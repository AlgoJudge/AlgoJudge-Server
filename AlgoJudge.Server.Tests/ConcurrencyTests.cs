using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The optimistic-concurrency tokens, one race each.
/// <para>
/// <b>Written against two <c>DbContext</c> instances rather than through the
/// API</b>, because the thing under test is the token: two readers of one row,
/// both deciding on what they read, both writing back. An HTTP test would prove
/// the handler's answer and could pass with no token at all — the two requests
/// would simply overwrite one another and both return 200.
/// </para>
/// <para>
/// Each test therefore asserts <b>two</b> things: that the second writer is
/// refused, and that the row never reaches the state only a lost update could
/// produce.
/// </para>
/// </summary>
[Collection("server")]
public class ConcurrencyTests(ServerFixture server)
{
    /// <summary>
    /// Starts the shared host once, so the database this class talks to
    /// directly has actually been migrated.
    /// <para>
    /// Every other suite gets this for free by making a request first. These
    /// tests open a <c>DbContext</c> before anything else does, and on an empty
    /// database that is a missing table rather than a race.
    /// </para>
    /// </summary>
    private async Task ReadyAsync()
    {
        using var warm = server.CreateClient();
        (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
    }

    /// <summary>The two seeded development accounts, whichever ids they have.</summary>
    private async Task<(string Source, string Target)> TwoAccountsAsync()
    {
        await using var context = server.NewContext();
        var ids = await context.Users.OrderBy(u => u.UserName).Select(u => u.Id).Take(2).ToListAsync();
        Assert.Equal(2, ids.Count);
        return (ids[0], ids[1]);
    }

    /* ── carrying one account's work onto another ──────────────────────────── */

    /// <summary>
    /// <b>The race this whole change exists for.</b> An undo checks that nothing
    /// has been anonymised; the sweeper checks that nothing has been undone.
    /// Both read, both decide, both write — and without a token both are right,
    /// leaving a merge that was given back <i>and</i> emptied.
    /// </summary>
    [Fact]
    public async Task An_undo_and_the_anonymiser_cannot_both_win()
    {
        await ReadyAsync();
        var (source, target) = await TwoAccountsAsync();
        var id = Guid.NewGuid();

        await using (var seed = server.NewContext())
        {
            seed.AccountMerges.Add(new AccountMerge
            {
                Id = id,
                SourceUserId = source,
                TargetUserId = target,
                MergedByUserId = target,
                AnonymiseAfter = DateTime.UtcNow.AddDays(-1),
                Moved = JsonSerializer.Serialize(new { }),
            });
            await seed.SaveChangesAsync();
        }

        await using var undo = server.NewContext();
        await using var sweep = server.NewContext();

        var undoing = await undo.AccountMerges.FirstAsync(m => m.Id == id);
        var sweeping = await sweep.AccountMerges.FirstAsync(m => m.Id == id);

        undoing.UndoneAt = DateTime.UtcNow;
        undoing.UndoneByUserId = target;
        await undo.SaveChangesAsync();

        sweeping.SourceAnonymisedAt = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => sweep.SaveChangesAsync());

        await using var after = server.NewContext();
        var merge = await after.AccountMerges.FirstAsync(m => m.Id == id);
        Assert.NotNull(merge.UndoneAt);
        Assert.Null(merge.SourceAnonymisedAt);
    }

    /* ── a deletion somebody stopped ───────────────────────────────────────── */

    /// <summary>
    /// Halting checks that the request is still <c>Pending</c>; the sweep checks
    /// the same thing before carrying it out. One of them has to lose.
    /// </summary>
    [Fact]
    public async Task A_halt_and_a_completion_cannot_both_win()
    {
        await ReadyAsync();

        var id = Guid.NewGuid();

        await using (var seed = server.NewContext())
        {
            seed.AccountDeletionRequests.Add(new AccountDeletionRequest
            {
                Id = id,
                Channel = DeletionChannel.Holder,
                RequestedAt = DateTime.UtcNow,
                ExecuteAfter = DateTime.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        await using var halt = server.NewContext();
        await using var sweep = server.NewContext();

        var halting = await halt.AccountDeletionRequests.FirstAsync(r => r.Id == id);
        var sweeping = await sweep.AccountDeletionRequests.FirstAsync(r => r.Id == id);

        halting.State = DeletionState.Halted;
        await halt.SaveChangesAsync();

        sweeping.State = DeletionState.Completed;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => sweep.SaveChangesAsync());

        await using var after = server.NewContext();
        Assert.Equal(
            DeletionState.Halted,
            (await after.AccountDeletionRequests.FirstAsync(r => r.Id == id)).State);
    }

    /* ── a migration somebody called off ───────────────────────────────────── */

    /// <summary>
    /// The advisory lock keeps two instances from moving files at once. It has
    /// never stood between the worker and an operator, and this is that gap.
    /// </summary>
    [Fact]
    public async Task A_cancel_is_not_lost_to_the_migrator()
    {
        await ReadyAsync();

        var id = Guid.NewGuid();

        await using (var seed = server.NewContext())
        {
            seed.StorageMigrations.Add(new StorageMigration
            {
                Id = id,
                TargetStoreId = "objects",
                State = StorageMigrationState.Running,
            });
            await seed.SaveChangesAsync();
        }

        await using var operatorSide = server.NewContext();
        await using var worker = server.NewContext();

        var cancelling = await operatorSide.StorageMigrations.FirstAsync(m => m.Id == id);
        var moving = await worker.StorageMigrations.FirstAsync(m => m.Id == id);

        cancelling.State = StorageMigrationState.Cancelled;
        cancelling.Detail = "called off by an operator";
        await operatorSide.SaveChangesAsync();

        // What the worker would have written on its next file.
        moving.FilesMoved += 1;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => worker.SaveChangesAsync());

        await using var after = server.NewContext();
        Assert.Equal(
            StorageMigrationState.Cancelled,
            (await after.StorageMigrations.FirstAsync(m => m.Id == id)).State);
    }

    /* ── the installation's own settings ───────────────────────────────────── */

    /// <summary>
    /// One row, and <b>two writers since 2026-08-28</b>: the manager panel, and
    /// the pre-configuration read from disk. The settings endpoint replaces the
    /// whole object, so without a token the later save puts every field back.
    /// </summary>
    [Fact]
    public async Task Two_settings_writers_do_not_erase_one_another()
    {
        await ReadyAsync();

        await using (var ensure = server.NewContext())
        {
            if (!await ensure.Instance.AnyAsync())
            {
                ensure.Instance.Add(new Instance());
                await ensure.SaveChangesAsync();
            }
        }

        await using var panel = server.NewContext();
        await using var apply = server.NewContext();

        var fromPanel = await panel.Instance.FirstAsync();
        var fromFile = await apply.Instance.FirstAsync();

        var chosen = "Set by the panel " + Guid.NewGuid().ToString("N")[..8];
        fromPanel.Name = chosen;
        await panel.SaveChangesAsync();

        fromFile.Name = "Set by the file";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => apply.SaveChangesAsync());

        await using var after = server.NewContext();
        Assert.Equal(chosen, (await after.Instance.FirstAsync()).Name);
    }

    /* ── approving and revoking a Runner ───────────────────────────────────── */

    /// <summary>
    /// <b>The one that is not a token, and the reason is worth knowing.</b>
    /// <c>Runners</c> cannot carry an optimistic-concurrency token:
    /// <c>LastSeenAt</c> is written on every claim, every renewal and every
    /// report, so a Runner with two requests in flight would collide with
    /// itself on ordinary traffic. The first attempt at this change put a token
    /// here and reddened nineteen tests.
    /// <para>
    /// So the race that mattered — approving a Runner somebody is revoking —
    /// is closed by a condition carried in the <c>UPDATE</c> itself. Revoked
    /// <b>between</b> the read and the write, which is the only arrangement that
    /// tells a compare-and-set from a read-then-write.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_approval_cannot_overtake_a_revocation()
    {
        await ReadyAsync();

        var id = Guid.NewGuid();

        await using (var seed = server.NewContext())
        {
            seed.Runners.Add(new Runner
            {
                Id = id,
                Name = "concurrency",
                Product = "AlgoJudge-Runner-Stub",
                PublicKey = Guid.NewGuid().ToString("N"),
                Fingerprint = Guid.NewGuid().ToString("N"),
                State = RunnerState.PendingApproval,
            });
            await seed.SaveChangesAsync();
        }

        var saboteur = new RevokeWhileApproving(server.ConnectionString);
        using var host = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseNpgsql(server.ConnectionString).AddInterceptors(saboteur));
            }));

        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // Armed only now, so signing in and everything before it is ordinary.
        saboteur.RunnerId = id;

        var response = await admin.PostAsync($"/api/v1/runners/{id}/approve", null);

        Assert.True(saboteur.Fired, "the race never happened, so this test proved nothing");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("runner.revoked", problem.GetProperty("code").GetString());

        await using var after = server.NewContext();
        Assert.Equal(RunnerState.Revoked, (await after.Runners.FirstAsync(r => r.Id == id)).State);
    }
}
