using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Sending somebody's work to a service this installation does not run.
/// <para>
/// <b>Three booleans and no cleverness.</b> A problem says whether judging it
/// leaves the building, a Runner says whether it forwards, and the installation
/// says whether it allows any of it. The Server pairs the first two by equality
/// and gates the whole thing on the third; it never reads a problem type, never
/// learns which archive is on the far end, and treats every external problem
/// alike. If any test here needed to name one, the design would be wrong.
/// </para>
/// </summary>
[Collection("server")]
public class ExternalJudgingTests(ServerFixture server)
{
    /// <summary>
    /// The switch, set the way each test needs it and never inherited.
    /// <para>
    /// Written rather than read-then-written: a test that assumed the state a
    /// previous test left would pass or fail by running order, and the suite
    /// shares one database.
    /// </para>
    /// </summary>
    private async Task AllowExternalAsync(bool allowed)
    {
        // **The host first, and this is not decoration.** `NewContext` opens its
        // own connection and knows nothing about the application, so calling it
        // before anything has started the host reaches a database the host has
        // not migrated yet. The failure then reads `relation "Instance" does not
        // exist`, which sounds like a missing migration and is really an
        // ordering mistake. Every other test here happens to sign in first.
        server.CreateClient().Dispose();

        await using var context = server.NewContext();
        var instance = await context.Instance.FirstOrDefaultAsync();
        if (instance is null)
        {
            instance = new Instance();
            context.Instance.Add(instance);
        }
        instance.ExternalJudgingEnabled = allowed;
        // The list goes back to what an installation ships with, so no test here
        // depends on what another one left behind.
        instance.ExternalFetchHosts = ["onlinejudge.org"];
        await context.SaveChangesAsync();
    }

    /// <summary>Names the hosts this installation will fetch from.</summary>
    private async Task AllowHostsAsync(params string[] hosts)
    {
        await using var context = server.NewContext();
        var instance = await context.Instance.FirstAsync();
        instance.ExternalFetchHosts = [.. hosts];
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// <b>The failure that would matter most.</b> A Runner that forwards
    /// submissions must never be handed a problem this installation judges
    /// itself — that would send somebody's work out of the building with nobody
    /// having chosen it, which is worse than the case the switch was written for.
    /// <para>
    /// The switch is deliberately <b>on</b>. With it off this test would pass
    /// without proving anything, because the gate would refuse the claim before
    /// the pairing was ever consulted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_local_problem_is_never_handed_to_a_runner_that_forwards_work()
    {
        await AllowExternalAsync(true);

        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var id = submitted.GetProperty("id").GetString()!;

        var forwarding = await Build.RunnerAsync(server, external: true);
        Assert.Null(await forwarding.TryClaimForAsync(id));

        // And the job was there to be taken. Without this the assertion above is
        // satisfied by an empty queue, which would prove nothing at all.
        var local = await Build.RunnerAsync(server);
        Assert.NotNull(await local.TryClaimForAsync(id));
    }

    /// The other half of the same equality, and it has to be an equality: a rule
    /// that only guarded one direction would leave the other to chance.
    [Fact]
    public async Task An_external_problem_is_not_handed_to_a_runner_that_judges_here()
    {
        await AllowExternalAsync(true);

        var (slug, _) = await Build.ActivityAsync(server, external: true);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var id = submitted.GetProperty("id").GetString()!;

        var local = await Build.RunnerAsync(server);
        Assert.Null(await local.TryClaimForAsync(id));

        var forwarding = await Build.RunnerAsync(server, external: true);
        Assert.NotNull(await forwarding.TryClaimForAsync(id));
    }

    /// <summary>
    /// The switch, doing the only thing it does.
    /// <para>
    /// Nothing is refused, revoked or failed while it is off — the Runner is
    /// handed an empty queue, exactly as a draining Server hands one out, and the
    /// work waits. Turning it on lets the queue drain, which is what makes this
    /// a decision an operator can take on a Tuesday rather than a migration.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_external_runner_is_given_nothing_until_the_installation_says_yes()
    {
        await AllowExternalAsync(false);

        var (slug, _) = await Build.ActivityAsync(server, external: true);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var id = submitted.GetProperty("id").GetString()!;

        var forwarding = await Build.RunnerAsync(server, external: true);
        Assert.Null(await forwarding.TryClaimAsync());

        await AllowExternalAsync(true);
        Assert.NotNull(await forwarding.TryClaimForAsync(id));
    }

    /// <summary>
    /// A trial means "run this package here and time it", which a Runner that
    /// forwards submissions cannot do — it has no sandbox, and the service on the
    /// far end judges an answer rather than measuring somebody's model solutions.
    /// </summary>
    [Fact]
    public async Task A_runner_that_forwards_work_measures_no_trials()
    {
        await AllowExternalAsync(true);

        // A real file, because the claim refuses a trial whose package is gone
        // and marks it failed on the way past — a made-up id produces a trial
        // nobody can take, which would satisfy the assertion below for entirely
        // the wrong reason.
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var package = await Build.UploadAsync(
            admin, "/api/v1/files", "package.zip", "trial-" + Guid.NewGuid());

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
            context.Trials.Add(new Trial
            {
                ActivityId = activity.Id,
                UserId = "external-judging-tests",
                PackageFileId = Guid.Parse(package),
                ProblemType = "standard-io@1",
            });
            await context.SaveChangesAsync();
        }

        var forwarding = await Build.RunnerAsync(server, external: true);
        var refused = await forwarding.Client.PostAsJsonAsync(
            "/api/v1/runner/trials/claim", new { leaseSeconds = 300 });
        Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);

        // The control: a trial was queued and is claimable by a Runner that runs
        // things. Without it the assertion above passes on an empty table.
        var local = await Build.RunnerAsync(server);
        var taken = await local.Client.PostAsJsonAsync(
            "/api/v1/runner/trials/claim", new { leaseSeconds = 300 });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    /// <summary>
    /// <b>Silence is not a decision.</b> The settings endpoint replaces the
    /// whole object, so a request written before this field existed omits it —
    /// and reading that omission as "off" would close the door under an
    /// installation that had opened it, while somebody was saving something
    /// else entirely.
    /// </summary>
    [Fact]
    public async Task Saving_other_settings_does_not_turn_external_judging_off()
    {
        await AllowExternalAsync(true);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PutAsJsonAsync("/api/v1/instance", new
        {
            localRegistrationEnabled = false,
            requireEmail = false,
            requireConfirmedEmail = false,
            showLogo = true,
            showLocalSignIn = true,
            accountDeletionEnabled = true,
        }));

        await using var context = server.NewContext();
        var instance = await context.Instance.FirstAsync();
        Assert.True(instance.ExternalJudgingEnabled);
    }

    /// <summary>
    /// A reserved slug namespace belongs to whoever may import into it.
    /// <para>
    /// Not secrecy — collision. Two problems called <c>Imported-100</c>, one imported
    /// and one typed in by hand, is a tangle nobody can undo afterwards. The
    /// reserved list is configuration, so the Server refuses the name without
    /// ever learning what an archive is.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_reserved_slug_namespace_belongs_to_whoever_may_import()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var login = "importer-" + Guid.NewGuid().ToString("N")[..8];
        var person = await Sign.NewAccountAsync(server, login);

        string personId;
        await using (var context = server.NewContext())
        {
            personId = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
        }

        // Enough to make problems, and nothing more.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "problem:create" },
        }));

        var refused = await person.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "Imported-100",
            name = "Hand-made, in somebody else's namespace",
            type = "standard-io@1",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("reserved", await refused.Content.ReadAsStringAsync());

        // A name outside the namespace is nobody's business but theirs.
        var allowed = await person.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "ordinary-" + Guid.NewGuid().ToString("N")[..8],
            name = "Anything else",
            type = "standard-io@1",
        });
        await Sign.Succeeded(allowed);

        // And with the import permission the namespace opens.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "problem:create", "problem:import:external" },
        }));

        var imported = await person.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "Imported-" + Guid.NewGuid().ToString("N")[..8],
            name = "Imported",
            type = "standard-io@1",
            external = true,
        });
        await Sign.Succeeded(imported);
    }

    // ── Fetching content from a host the installation named ─────────────────

    /// <summary>
    /// **The switch governs both directions.** It decides whether work may go
    /// out, and it decides whether content may be pulled in — one decision, so
    /// an operator cannot close one door and leave the other standing open
    /// without noticing.
    /// </summary>
    [Fact]
    public async Task Nothing_is_fetched_while_external_judging_is_off()
    {
        await AllowExternalAsync(false);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var refused = await admin.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://onlinejudge.org/external/1/100.pdf",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("fetch.disabled", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A host nobody put on the list, refused before a packet is sent. The
    /// address here is deliberately one that <b>ends with</b> an allowed name.
    /// </summary>
    [Fact]
    public async Task A_host_the_installation_never_named_is_refused()
    {
        await AllowExternalAsync(true);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var refused = await admin.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://onlinejudge.org.example.invalid/100.pdf",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("fetch.host.notAllowed", await refused.Content.ReadAsStringAsync());
    }

    /// An address literal names a machine the list never mentioned.
    [Fact]
    public async Task An_address_literal_is_refused_before_anything_is_sent()
    {
        await AllowExternalAsync(true);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var refused = await admin.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://169.254.169.254/latest/meta-data/",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("fetch.url.address", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Fetching is a grant, not something every manager has. Checked before the
    /// switch, so somebody without it cannot learn the installation's setting by
    /// reading which refusal came back.
    /// </summary>
    [Fact]
    public async Task Fetching_needs_the_import_permission()
    {
        await AllowExternalAsync(true);

        var login = "fetcher-" + Guid.NewGuid().ToString("N")[..8];
        var person = await Sign.NewAccountAsync(server, login);

        var refused = await person.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://onlinejudge.org/external/1/100.pdf",
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// **The rebinding guard, exercised with a real name and no hostile DNS.**
    /// <para>
    /// <c>localhost</c> is a name, not an address literal, so it passes every
    /// check a string can make — and it resolves to the loopback interface,
    /// which is exactly the shape of a host whose owner points it inside. The
    /// refusal therefore comes from the one place that can see it: the moment of
    /// connecting, after the name has been resolved.
    /// </para>
    /// <para>
    /// This is the test that proves the callback is wired in at all. Without it
    /// the address checks are a function nobody calls.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_host_that_resolves_inside_is_refused_at_the_socket()
    {
        await AllowExternalAsync(true);
        await AllowHostsAsync("localhost");

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var refused = await admin.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://localhost/statement.pdf",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("fetch.host.inside", await refused.Content.ReadAsStringAsync());
    }

    /// The list an operator edits, read back as they left it.
    [Fact]
    public async Task The_allowlist_is_readable_and_replaceable_by_a_manager()
    {
        await AllowExternalAsync(true);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var shipped = await admin.GetFromJsonAsync<JsonElement>("/api/v1/instance/external-content");
        Assert.True(shipped.GetProperty("enabled").GetBoolean());
        Assert.Contains(
            "onlinejudge.org",
            shipped.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));

        // Tidied only in ways that cannot change which host is meant: blanks
        // dropped, whitespace trimmed, the same host named twice collapsed.
        var saved = await admin.PutAsJsonAsync("/api/v1/instance/external-content", new
        {
            hosts = new[] { "  example.invalid  ", "", "EXAMPLE.invalid", "second.invalid" },
        });
        await Sign.Succeeded(saved);

        var back = await admin.GetFromJsonAsync<JsonElement>("/api/v1/instance/external-content");
        var hosts = back.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()).ToArray();
        Assert.Equal(["example.invalid", "second.invalid"], hosts);

        // And the list is what fetching consults, not a copy of it.
        var refused = await admin.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://onlinejudge.org/external/1/100.pdf",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("fetch.host.notAllowed", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// An installation that allows nothing fetches nothing — the empty list has
    /// to mean what it says, because it is the state an operator reaches by
    /// removing what the product shipped.
    /// </summary>
    [Fact]
    public async Task An_emptied_allowlist_fetches_nothing()
    {
        await AllowExternalAsync(true);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/external-content", new { hosts = Array.Empty<string>() }));

        var refused = await admin.PostAsJsonAsync("/api/v1/files/fetch", new
        {
            url = "https://onlinejudge.org/external/1/100.pdf",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("fetch.host.notAllowed", await refused.Content.ReadAsStringAsync());
    }

    /// The destinations are not something every signed-in person may read.
    [Fact]
    public async Task Reading_the_allowlist_needs_the_instance_permission()
    {
        await AllowExternalAsync(true);

        var person = await Sign.NewAccountAsync(
            server, "curious-" + Guid.NewGuid().ToString("N")[..8]);
        var refused = await person.GetAsync("/api/v1/instance/external-content");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
