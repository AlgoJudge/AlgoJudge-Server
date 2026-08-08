using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The manager panel's refusals.
/// <para>
/// Almost every rule here is about <b>when a change is refused</b> — that is
/// what makes the panel safe to hand to somebody running a contest, and it is
/// the part a screen cannot enforce on its own.
/// </para>
/// </summary>
[Collection("server")]
public class PanelTests(ServerFixture server)
{
    /// <summary>
    /// Nobody may grant a permission they do not themselves hold. Without this
    /// the model is decorative: anybody who could edit a grant could write
    /// `system:administrator` into it.
    /// </summary>
    [Fact]
    public async Task Nobody_grants_what_they_do_not_hold()
    {
        var manager = await Sign.NewAccountAsync(server, "limited-manager");

        // A manager of the seeded activity, with the manager set and nothing more.
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync("DEV-2026");
        var managerId = await UserIdAsync("limited-manager");

        var granted = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            activityId,
            permissions = new[] { "activity:read", "grant:read:all", "grant:update" },
        });
        await Sign.Succeeded(granted);

        // A target of this test's own. Setting a grant <b>replaces</b> the whole
        // set for that user in that scope — pointing this at the shared seeded
        // participant would strip their `submission:create` and break every
        // other test that submits.
        await Sign.NewAccountAsync(server, "grant-target");
        var targetId = await UserIdAsync("grant-target");

        // They may hand on what they hold…
        var allowed = await manager.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = targetId,
            activityId,
            permissions = new[] { "activity:read" },
        });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // …and not what they do not.
        var refused = await manager.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = targetId,
            activityId,
            permissions = new[] { "activity:read", "system:administrator" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("grant.excess", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_grant_carrying_a_staff_permission_is_systemic_whatever_the_caller_says()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync("DEV-2026");
        await Sign.NewAccountAsync(server, "quiet-jury");

        var response = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = await UserIdAsync("quiet-jury"),
            activityId,
            // A staff permission, and an explicit claim that it is not systemic.
            permissions = new[] { "activity:read", "submission:read:all" },
            isSystem = false,
        });
        await Sign.Succeeded(response);

        var grant = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The Server decides. A jury member counted among the competitors is a
        // bug, not a preference.
        Assert.True(grant.GetProperty("isSystem").GetBoolean());
    }

    [Fact]
    public async Task A_built_in_template_cannot_be_deleted()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var templates = await admin.GetFromJsonAsync<JsonElement>("/api/v1/permission-templates");
        var builtIn = templates.EnumerateArray().First(t => t.GetProperty("isBuiltIn").GetBoolean());

        var response = await admin.DeleteAsync(
            $"/api/v1/permission-templates/{builtIn.GetProperty("id").GetString()}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("template.builtIn", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_permission_is_refused_rather_than_stored()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var response = await admin.PostAsJsonAsync("/api/v1/permission-templates", new
        {
            name = "invented-" + Guid.NewGuid().ToString("N")[..6],
            permissions = new[] { "activity:read", "problem:teleport" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("template.permission.unknown", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// Detaching is refused once anything has been submitted: the submissions
    /// point at the assignment, and a standing computed from them would develop
    /// a hole.
    /// </summary>
    [Fact]
    public async Task An_assignment_with_submissions_cannot_be_detached()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);
        await Sign.SubmitAsync(participant, "python", "print('detach')\n");

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var series = await admin.GetFromJsonAsync<JsonElement>("/api/v1/manager/activities/DEV-2026/series");
        var assignment = series.EnumerateArray().First()
            .GetProperty("problems").EnumerateArray().First();

        var response = await admin.DeleteAsync(
            $"/api/v1/series-problems/{assignment.GetProperty("id").GetString()}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("assignment.hasSubmissions", problem.GetProperty("code").GetString());
    }

    /// <summary>A rejudge adds an attempt and never overwrites a result.</summary>
    [Fact]
    public async Task A_rejudge_adds_an_attempt_and_keeps_the_previous_one()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);
        var submitted = await Sign.SubmitAsync(participant, "python", "print('rejudge')\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var response = await admin.PostAsync($"/api/v1/submissions/{submissionId}/rejudge", null);
        await Sign.Succeeded(response);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/submissions/{submissionId}");
        var attempts = detail.GetProperty("attemptList").EnumerateArray().ToList();

        Assert.Equal(2, attempts.Count);
        // Newest first, and the older one is still there.
        Assert.Equal(2, attempts[0].GetProperty("attempt").GetInt32());
        Assert.Equal(1, attempts[1].GetProperty("attempt").GetInt32());
    }

    /// <summary>
    /// A shift is a delta so two managers reacting to the same delay do not lose
    /// one another's move — and everything downstream of the start moves with it.
    /// </summary>
    [Fact]
    public async Task Shifting_a_series_moves_its_freeze_and_its_window_too()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);

        var round = await Post(admin, $"/api/v1/activities/{slug}/series", new
        {
            slug = "r1",
            name = "R1",
            startDate = "2026-09-01T10:00:00Z",
            endDate = "2026-09-01T15:00:00Z",
            rankingFreezeAt = "2026-09-01T14:00:00Z",
            rankingVisibleFrom = "2026-09-01T10:00:00Z",
        });
        var roundId = round.GetProperty("id").GetString()!;

        var shifted = await Post(admin, $"/api/v1/series/{roundId}/shift", new { minutes = 90 });

        Assert.Equal("2026-09-01T11:30:00.0000000Z", shifted.GetProperty("startDate").GetString());
        Assert.Equal("2026-09-01T16:30:00.0000000Z", shifted.GetProperty("endDate").GetString());
        // A round delayed by ninety minutes whose freeze stayed at the old wall
        // clock would freeze the wrong hour.
        Assert.Equal("2026-09-01T15:30:00.0000000Z", shifted.GetProperty("rankingFreezeAt").GetString());
        Assert.Equal("2026-09-01T11:30:00.0000000Z", shifted.GetProperty("rankingVisibleFrom").GetString());
    }

    [Fact]
    public async Task Pausing_shuts_the_round_and_resuming_can_give_the_time_back()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);

        var round = await Post(admin, $"/api/v1/activities/{slug}/series", new
        {
            slug = "r1",
            name = "R1",
            startDate = DateTime.UtcNow.AddHours(-1).ToString("O"),
            endDate = DateTime.UtcNow.AddHours(1).ToString("O"),
        });
        var roundId = round.GetProperty("id").GetString()!;

        // The scheduler owns opening, so this opens it the way the scheduler will.
        await using (var context = server.NewContext())
        {
            var series = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            series.IsOpen = true;
            series.StartAnnouncedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        var paused = await Post(admin, $"/api/v1/series/{roundId}/pause", new { hideProblems = true });
        Assert.False(paused.GetProperty("isOpen").GetBoolean());
        Assert.NotNull(paused.GetProperty("pausedAt").GetString());

        // Pausing twice is refused: it would silently move the moment the pause
        // began, and that is what "give the time back" is measured from.
        var again = await admin.PostAsJsonAsync($"/api/v1/series/{roundId}/pause", new { hideProblems = true });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var before = DateTime.Parse(round.GetProperty("endDate").GetString()!).ToUniversalTime();
        var resumed = await Post(admin, $"/api/v1/series/{roundId}/resume", new { extendEnd = true });

        Assert.True(resumed.GetProperty("isOpen").GetBoolean());
        Assert.False(resumed.TryGetProperty("pausedAt", out _), "a resumed round carries no pause");
        var after = DateTime.Parse(resumed.GetProperty("endDate").GetString()!).ToUniversalTime();
        Assert.True(after >= before, "the interruption is given back, so the end does not move earlier");
    }

    [Fact]
    public async Task Duplicating_a_problem_copies_only_the_newest_version()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var problems = await admin.GetFromJsonAsync<JsonElement>("/api/v1/problems?pageSize=50");
        var seeded = problems.GetProperty("items").EnumerateArray()
            .First(p => p.GetProperty("slug").GetString() == "sum");

        var copy = await Post(admin, $"/api/v1/problems/{seeded.GetProperty("id").GetString()}/duplicate", new { });

        Assert.EndsWith("(copy)", copy.GetProperty("name").GetString());
        Assert.Equal(1, copy.GetProperty("versionCount").GetInt32());
        // A copy is a draft. Inheriting an instance-wide visibility would
        // publish it by accident.
        Assert.Equal("private", copy.GetProperty("visibility").GetString());
        Assert.Equal(0, copy.GetProperty("attachedCount").GetInt32());
    }

    [Fact]
    public async Task Temporary_accounts_come_back_once_with_readable_passwords()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var prefix = "room-" + Guid.NewGuid().ToString("N")[..6];

        var created = await Post(admin, "/api/v1/users/temporary", new { prefix, count = 3 });
        var accounts = created.EnumerateArray().ToList();

        Assert.Equal(3, accounts.Count);
        Assert.Equal($"{prefix}-001", accounts[0].GetProperty("username").GetString());
        Assert.Equal($"{prefix}-003", accounts[2].GetProperty("username").GetString());

        foreach (var account in accounts)
        {
            var password = account.GetProperty("password").GetString()!;
            Assert.Equal(12, password.Length);
            // Read off a paper slip and typed by somebody in a hurry: no
            // character pair nobody can tell apart.
            Assert.DoesNotContain(password, c => "0O1lI".Contains(c));
        }

        // Running it again continues the numbering rather than colliding.
        var more = await Post(admin, "/api/v1/users/temporary", new { prefix, count = 2 });
        Assert.Equal($"{prefix}-004", more.EnumerateArray().First().GetProperty("username").GetString());
    }

    [Fact]
    public async Task Blocking_is_a_lockout_and_not_a_second_flag()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.NewAccountAsync(server, "gets-blocked");
        var id = await UserIdAsync("gets-blocked");

        var blocked = await Post(admin, $"/api/v1/users/{id}/blocked", new { blocked = true, reason = "testing" });
        Assert.NotNull(blocked.GetProperty("blockedAt").GetString());
        Assert.Equal("testing", blocked.GetProperty("blockedReason").GetString());

        await using (var context = server.NewContext())
        {
            var user = await context.Users.FirstAsync(u => u.Id == id);
            // One fact, in one place: Identity's own lockout, which is what
            // actually stops the sign-in.
            Assert.NotNull(user.LockoutEnd);
        }

        var unblocked = await Post(admin, $"/api/v1/users/{id}/blocked", new { blocked = false });
        Assert.False(unblocked.TryGetProperty("blockedAt", out _), "an unblocked account carries no block");
    }

    [Fact]
    public async Task An_instance_document_is_added_as_a_revision_rather_than_replacing_the_last()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var first = await UploadAsync(admin, "terms.md", "# Terms\n\nVersion one.\n");
        await Post(admin, "/api/v1/instance/documents/terms", new
        {
            statements = new[] { new { fileId = first } },
            validFrom = "2026-01-01T00:00:00Z",
        });

        var second = await UploadAsync(admin, "terms.md", "# Terms\n\nVersion two.\n");
        var info = await Post(admin, "/api/v1/instance/documents/terms", new
        {
            statements = new[] { new { fileId = second } },
            validFrom = "2026-02-01T00:00:00Z",
        });

        // The reader is served the newest revision whose date has passed.
        var current = info.GetProperty("documents").EnumerateArray()
            .Single(d => d.GetProperty("kind").GetString() == "terms");
        Assert.Equal(second, current.GetProperty("fileId").GetString());

        // And the earlier one is still answerable: "which policy was in force in
        // January" is a question somebody is owed an answer to.
        var history = await admin.GetFromJsonAsync<JsonElement>("/api/v1/instance/documents/terms");
        var revisions = history.EnumerateArray().ToList();
        Assert.True(revisions.Count >= 2);
        Assert.Contains(revisions, r => r.GetProperty("fileId").GetString() == first);
    }

    [Fact]
    public async Task Withdrawing_a_document_leaves_its_revisions_readable()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var file = await UploadAsync(admin, "cookies.md", "# Cookies\n");
        await Post(admin, "/api/v1/instance/documents/cookies", new
        {
            statements = new[] { new { fileId = file } },
        });

        var response = await admin.DeleteAsync("/api/v1/instance/documents/cookies");
        await Sign.Succeeded(response);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain(
            info.GetProperty("documents").EnumerateArray(),
            d => d.GetProperty("kind").GetString() == "cookies");

        var history = await admin.GetFromJsonAsync<JsonElement>("/api/v1/instance/documents/cookies");
        Assert.NotEmpty(history.EnumerateArray().ToList());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<JsonElement> Post(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> UploadAsync(HttpClient client, string name, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", name },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return stored.GetProperty("id").GetString()!;
    }

    private static async Task<string> NewActivityAsync(HttpClient admin)
    {
        var slug = "PANEL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await Post(admin, "/api/v1/activities", new
        {
            slug,
            name = "Panel test",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
            languages = new[] { "python" },
        });
        return slug;
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
        return activity.Id.ToString();
    }

    private async Task<string> UserIdAsync(string login)
    {
        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.UserName == login);
        return user.Id;
    }
}
