using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Carrying one account's work onto another.
/// <para>
/// <b>What a person produced moves; what they did to somebody else's thing
/// stays.</b> Half of these tests are about the second half of that sentence —
/// a merge that quietly reattributed a manager's decisions would pass every
/// test about points and still be wrong.
/// </para>
/// <para>
/// <b>Nothing is removed.</b> Deletion in this product means emptying in place,
/// and a merge is no exception: the emptied account is anonymised when the undo
/// window closes, so the rows recording what it once did keep resolving.
/// </para>
/// </summary>
[Collection("server-3")]
public class AccountMergeTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>A signed-in account, and the id the merge endpoints take.</summary>
    private async Task<(HttpClient Client, string Id, string Login)> PersonAsync()
    {
        var login = "m-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);

        await using var context = server.NewContext();
        var id = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
        return (client, id, login);
    }

    private static async Task<HttpResponseMessage> MergeAsync(
        HttpClient by, string sourceId, string targetId) =>
        await by.PostAsJsonAsync(
            $"/api/v1/users/{sourceId}/merge", new { targetUserId = targetId });

    private static async Task<JsonElement> PreviewAsync(
        HttpClient by, string sourceId, string targetId)
    {
        var response = await by.PostAsJsonAsync(
            $"/api/v1/users/{sourceId}/merge-preview", new { targetUserId = targetId });
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The whole thing, done: two accounts in one activity and one merge.</summary>
    private async Task<(string Activity, string SourceId, string TargetId, JsonElement Merge)>
        MergedAsync()
    {
        var (activity, _) = await Build.ActivityAsync(server);
        var (source, sourceId, _) = await PersonAsync();
        var (target, targetId, _) = await PersonAsync();

        await Sign.Succeeded(await source.PostAsJsonAsync($"/api/v1/activities/{activity}/enrolment", new { }));
        await Sign.Succeeded(await target.PostAsJsonAsync($"/api/v1/activities/{activity}/enrolment", new { }));
        await Sign.Succeeded(await Build.TrySubmitAsync(source, activity, "print(1)\n"));

        var admin = await AdminAsync(server);
        var response = await MergeAsync(admin, sourceId, targetId);
        await Sign.Succeeded(response);

        return (activity, sourceId, targetId,
            await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    // ── what moves ──────────────────────────────────────────────────────────

    /// <summary>The work arrives, and the account it came from holds none of it.</summary>
    [Fact]
    public async Task The_work_moves_onto_the_target()
    {
        var (_, sourceId, targetId, _) = await MergedAsync();

        await using var context = server.NewContext();
        Assert.Equal(0, await context.Submissions.CountAsync(s => s.UserId == sourceId));
        Assert.Equal(1, await context.Submissions.CountAsync(s => s.UserId == targetId));
    }

    /// <summary>
    /// <b>One row on the board, not two.</b> Both accounts were enrolled, and a
    /// merge that moved the grant on top of the target's own would leave the
    /// person competing against themselves.
    /// </summary>
    [Fact]
    public async Task The_board_shows_one_contestant()
    {
        var (activity, sourceId, targetId, _) = await MergedAsync();

        await using (var context = server.NewContext())
        {
            var grants = await context.Grants
                .CountAsync(g => g.UserId == targetId && g.Activity!.Slug == activity);
            Assert.Equal(1, grants);
            Assert.Equal(0, await context.Grants
                .CountAsync(g => g.UserId == sourceId && g.Activity!.Slug == activity));
        }

        var admin = await AdminAsync(server);
        var board = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{activity}/results");
        var rows = board.GetProperty("contestants").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToList();

        Assert.Contains(targetId, rows);
        Assert.DoesNotContain(sourceId, rows);
    }

    /// <summary>
    /// <b>A decision the merged account itself made still names it.</b>
    /// <para>
    /// The first version of this test had an <i>administrator</i> exclude the
    /// submission and then merged the participant — which proves only that a
    /// merge does not rewrite an unrelated column. Sabotaging the rule did not
    /// bite it. What has to hold is stronger: when the account being merged away
    /// is the one that ruled, its ruling stays where it is. Its row survives
    /// anonymised precisely so that it can.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_decision_the_source_made_is_not_reattributed()
    {
        var (activity, _) = await Build.ActivityAsync(server);
        var (participant, _, _) = await PersonAsync();
        var (staff, staffId, _) = await PersonAsync();
        var (_, targetId, _) = await PersonAsync();

        await Sign.Succeeded(await participant.PostAsJsonAsync(
            $"/api/v1/activities/{activity}/enrolment", new { }));
        var sent = await Build.SubmitAsync(participant, activity, "print(1)\n");
        var submissionId = Guid.Parse(sent.GetProperty("id").GetString()!);

        // The account about to be merged away is the one that rules on it.
        await using (var context = server.NewContext())
        {
            var activityId = (await context.Activities.FirstAsync(a => a.Slug == activity)).Id;
            context.Grants.Add(new Grant
            {
                UserId = staffId,
                ActivityId = activityId,
                IsSystem = true,
                Permissions = """["submission:exclude","submission:read:all","activity:read"]""",
            });
            await context.SaveChangesAsync();
        }

        await Sign.Succeeded(await staff.PostAsJsonAsync(
            $"/api/v1/submissions/{submissionId}/excluded",
            new { excluded = true, reason = "Poza konkursem" }));

        var admin = await AdminAsync(server);
        await Sign.Succeeded(await MergeAsync(admin, staffId, targetId));

        await using var after = server.NewContext();
        var submission = await after.Submissions.FirstAsync(s => s.Id == submissionId);

        Assert.Equal(staffId, submission.ExcludedByUserId);
        Assert.NotEqual(targetId, submission.ExcludedByUserId);
    }

    /// <summary>
    /// An upload nothing points at yet is readable by whoever made it, and after
    /// a merge that is the target. Leaving the stamp behind strands it: the
    /// account that could read it is blocked, and no other check matches.
    /// </summary>
    [Fact]
    public async Task An_unattached_upload_follows_the_person()
    {
        var (person, personId, _) = await PersonAsync();
        var (_, targetId, _) = await PersonAsync();

        var admin = await AdminAsync(server);
        var fileId = Guid.Parse(await Build.UploadAsync(admin, "/api/v1/files", "note.md", "# hi\n"));

        await using (var context = server.NewContext())
        {
            var file = await context.Files.FirstAsync(f => f.Id == fileId);
            file.UploadedByUserId = personId;
            await context.SaveChangesAsync();
        }

        await Sign.Succeeded(await MergeAsync(admin, personId, targetId));

        await using var after = server.NewContext();
        Assert.Equal(targetId, (await after.Files.FirstAsync(f => f.Id == fileId)).UploadedByUserId);
        Assert.NotNull(person);
    }

    // ── what the emptied account becomes ────────────────────────────────────

    /// <summary>
    /// <b>Blocked now, not at its next sign-in.</b> `LockoutEnd` is checked when
    /// somebody signs in, so without `BlockedGate` a merged-away account carries
    /// on working for as long as its cookie survives — half an hour by default.
    /// </summary>
    [Fact]
    public async Task The_emptied_account_stops_working_at_once()
    {
        var (activity, _) = await Build.ActivityAsync(server);
        var (source, sourceId, _) = await PersonAsync();
        var (_, targetId, _) = await PersonAsync();

        await Sign.Succeeded(await source.PostAsJsonAsync($"/api/v1/activities/{activity}/enrolment", new { }));
        // Signed in and working before the merge, with the cookie it keeps.
        Assert.True((await source.GetAsync("/api/v1/activities")).IsSuccessStatusCode);

        var admin = await AdminAsync(server);
        await Sign.Succeeded(await MergeAsync(admin, sourceId, targetId));

        var refused = await source.GetAsync("/api/v1/activities");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("account.blocked", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// It is <b>anonymised</b>, never removed — the rows that say what it once
    /// did still name it, and they have to keep resolving.
    /// </summary>
    [Fact]
    public async Task The_window_closing_empties_the_account_rather_than_removing_it()
    {
        var (_, sourceId, _, merge) = await MergedAsync();
        var mergeId = Guid.Parse(merge.GetProperty("id").GetString()!);

        // The window, brought forward rather than waited out.
        await using (var context = server.NewContext())
        {
            await context.AccountMerges.Where(m => m.Id == mergeId)
                .ExecuteUpdateAsync(u => u.SetProperty(
                    m => m.AnonymiseAfter, DateTime.UtcNow.AddMinutes(-1)));
        }

        var swept = await SweepAsync();
        Assert.True(swept >= 1, "the sweep emptied nothing");

        await using var after = server.NewContext();
        var user = await after.Users.FirstOrDefaultAsync(u => u.Id == sourceId);
        Assert.NotNull(user);
        Assert.True(user!.Anonymized, "the account was left as it was");
        Assert.Null(user.Email);
    }

    private async Task<int> SweepAsync()
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAccountMergeService>()
            .SweepAsync(CancellationToken.None);
    }

    // ── undo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>What moved, and only what moved.</b> Once the ids are the same the
    /// target's own work is indistinguishable from what arrived, so an undo that
    /// took everything would empty the wrong account.
    /// </summary>
    [Fact]
    public async Task Undo_returns_what_moved_and_leaves_the_targets_own_alone()
    {
        var (activity, _) = await Build.ActivityAsync(server);
        var (source, sourceId, _) = await PersonAsync();
        var (target, targetId, _) = await PersonAsync();

        await Sign.Succeeded(await source.PostAsJsonAsync($"/api/v1/activities/{activity}/enrolment", new { }));
        await Sign.Succeeded(await target.PostAsJsonAsync($"/api/v1/activities/{activity}/enrolment", new { }));

        await Sign.Succeeded(await Build.TrySubmitAsync(source, activity, "print(1)\n"));
        var theirOwn = await Build.SubmitAsync(target, activity, "print(2)\n");
        var theirOwnId = Guid.Parse(theirOwn.GetProperty("id").GetString()!);

        var admin = await AdminAsync(server);
        var response = await MergeAsync(admin, sourceId, targetId);
        await Sign.Succeeded(response);
        var merge = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/users/merges/{merge.GetProperty("id").GetString()}/undo", new { }));

        await using var context = server.NewContext();
        Assert.Equal(1, await context.Submissions.CountAsync(s => s.UserId == sourceId));
        Assert.Equal(targetId,
            (await context.Submissions.FirstAsync(s => s.Id == theirOwnId)).UserId);

        // And the account works again.
        var user = await context.Users.FirstAsync(u => u.Id == sourceId);
        Assert.Null(user.LockoutEnd);
    }

    /// <summary>
    /// The grant a collision dropped is the one thing an undo builds rather than
    /// moves, because its row is gone.
    /// </summary>
    [Fact]
    public async Task Undo_gives_back_the_grant_the_collision_dropped()
    {
        var (activity, sourceId, _, merge) = await MergedAsync();

        var admin = await AdminAsync(server);
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/users/merges/{merge.GetProperty("id").GetString()}/undo", new { }));

        await using var context = server.NewContext();
        Assert.Equal(1, await context.Grants
            .CountAsync(g => g.UserId == sourceId && g.Activity!.Slug == activity));
    }

    /// <summary>Once the account has been emptied there is nothing to give back.</summary>
    [Fact]
    public async Task Undo_is_refused_after_the_window()
    {
        var (_, _, _, merge) = await MergedAsync();
        var mergeId = Guid.Parse(merge.GetProperty("id").GetString()!);

        await using (var context = server.NewContext())
        {
            await context.AccountMerges.Where(m => m.Id == mergeId)
                .ExecuteUpdateAsync(u => u.SetProperty(
                    m => m.AnonymiseAfter, DateTime.UtcNow.AddMinutes(-1)));
        }
        await SweepAsync();

        var admin = await AdminAsync(server);
        var refused = await admin.PostAsJsonAsync($"/api/v1/users/merges/{mergeId}/undo", new { });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("merge.window.closed", await refused.Content.ReadAsStringAsync());
    }

    // ── what is refused ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>The escalation this would otherwise be.</b> Grants move with the work,
    /// so without this somebody holding <c>user:merge</c> merges an
    /// administrator into their own account and inherits their permissions.
    /// </summary>
    [Fact]
    public async Task An_account_with_system_permissions_is_refused()
    {
        var (_, sourceId, _) = await PersonAsync();
        var (_, targetId, _) = await PersonAsync();

        await using (var context = server.NewContext())
        {
            context.Grants.Add(new Grant
            {
                UserId = sourceId,
                Permissions = """["activity:create"]""",
            });
            await context.SaveChangesAsync();
        }

        var admin = await AdminAsync(server);
        var refused = await MergeAsync(admin, sourceId, targetId);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("merge.blocked", await refused.Content.ReadAsStringAsync());

        // And the preview says so before anybody presses anything.
        var preview = await PreviewAsync(admin, sourceId, targetId);
        Assert.NotEmpty(preview.GetProperty("blockers").EnumerateArray());
    }

    /// <summary>An account cannot be merged into itself.</summary>
    [Fact]
    public async Task Merging_an_account_into_itself_is_refused()
    {
        var (_, id, _) = await PersonAsync();
        var admin = await AdminAsync(server);

        var refused = await MergeAsync(admin, id, id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("merge.same", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// <b><c>user:merge</c>, and <c>user:update</c> is not enough.</b> This hands
    /// one person's work to another and must not arrive with ordinary account
    /// editing.
    /// </summary>
    [Fact]
    public async Task It_takes_a_permission_of_its_own()
    {
        var (_, sourceId, _) = await PersonAsync();
        var (_, targetId, _) = await PersonAsync();
        var (manager, managerId, _) = await PersonAsync();

        await using (var context = server.NewContext())
        {
            context.Grants.Add(new Grant
            {
                UserId = managerId,
                Permissions = """["user:read:all","user:update","user:create"]""",
            });
            await context.SaveChangesAsync();
        }

        var refused = await MergeAsync(manager, sourceId, targetId);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>The preview changes nothing, which is what makes it a preview.</summary>
    [Fact]
    public async Task The_preview_moves_nothing()
    {
        var (activity, _) = await Build.ActivityAsync(server);
        var (source, sourceId, _) = await PersonAsync();
        var (_, targetId, _) = await PersonAsync();

        await Sign.Succeeded(await source.PostAsJsonAsync($"/api/v1/activities/{activity}/enrolment", new { }));
        await Sign.Succeeded(await Build.TrySubmitAsync(source, activity, "print(1)\n"));

        var admin = await AdminAsync(server);
        var preview = await PreviewAsync(admin, sourceId, targetId);

        Assert.Equal(1, preview.GetProperty("submissions").GetInt32());
        Assert.Equal(1, preview.GetProperty("activities").GetInt32());

        await using var context = server.NewContext();
        Assert.Equal(1, await context.Submissions.CountAsync(s => s.UserId == sourceId));
        Assert.Null((await context.Users.FirstAsync(u => u.Id == sourceId)).LockoutEnd);
    }
}
