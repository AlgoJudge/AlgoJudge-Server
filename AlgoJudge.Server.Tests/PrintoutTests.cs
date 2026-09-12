using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Print requests, and the three things that would leak somebody's source.
/// <para>
/// The feature exists to be <b>delegated</b> — one person at a printer who holds
/// nothing else — so most of what is worth asserting here is a refusal. The
/// positive path is short and the negatives are the point.
/// </para>
/// <para>
/// <b>None of this is reachable from the browser suite.</b> The Client's fake has
/// no <c>FileReference</c> model at all, so who may read a printout's bytes is
/// answerable only here.
/// </para>
/// </summary>
[Collection("server-2")]
public class PrintoutTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    private async Task<(HttpClient Client, string Id)> AccountAsync()
    {
        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);
        await using var context = server.NewContext();
        var id = (await context.Users.AsNoTracking().FirstAsync(u => u.UserName == login)).Id;
        return (client, id);
    }

    private async Task<Guid> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id;
    }

    /// <summary>The module is off by default, so every test that sends turns it on.</summary>
    private async Task OpenPrintoutsAsync(string slug)
    {
        await using var context = server.NewContext();
        var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
        activity.HasPrintouts = true;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Somebody who holds <c>printout:manage</c> on one activity and nothing
    /// anywhere else. This is the person the feature is for.
    /// </summary>
    private async Task<HttpClient> OperatorOfAsync(string slug)
    {
        var admin = await AdminAsync(server);
        var (client, id) = await AccountAsync();

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            activityId = (await ActivityIdAsync(slug)).ToString(),
            permissions = new[] { Permissions.PrintoutManage },
        }));

        return client;
    }

    /// <summary>
    /// The one printout in this activity. **Scoped, because the fixture's
    /// database is shared across the collection** and "the first row" is
    /// whichever test ran first.
    /// </summary>
    private async Task<Guid> PrintoutInAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Printouts
            .AsNoTracking()
            .Include(x => x.Activity)
            .FirstAsync(x => x.Activity!.Slug == slug)).Id;
    }

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static MultipartFormDataContent Body(
        string code, string fileName, string? sha256 = null, string? title = null, string? submissionId = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(code), "code" },
            { new StringContent(fileName), "fileName" },
            { new StringContent(sha256 ?? Digest(code)), "sha256" },
        };
        if (title is not null) form.Add(new StringContent(title), "title");
        if (submissionId is not null) form.Add(new StringContent(submissionId), "submissionId");
        return form;
    }

    private static Task<HttpResponseMessage> AskAsync(
        HttpClient client, string slug, string code = "print(1)\n", string fileName = "main.py",
        string? sha256 = null, string? submissionId = null) =>
        client.PostAsync(
            $"/api/v1/activities/{slug}/printouts",
            Body(code, fileName, sha256, submissionId: submissionId));

    // ── The module switch ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_activity_that_does_not_take_print_requests_refuses_them()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var refused = await AskAsync(participant, slug);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("printouts.disabled", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// Turning the module off stops new requests and leaves the queue alone.
    /// <para>
    /// The asymmetry is deliberate: paper already asked for is still owed to
    /// somebody, and a switch that emptied the queue would strand it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Closing_the_module_leaves_what_was_already_asked_for()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        var participant = await Build.ParticipantAsync(server, slug);
        await Sign.Succeeded(await AskAsync(participant, slug));

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.HasPrintouts = false;
            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await AskAsync(participant, slug)).StatusCode);

        var queue = await Build.GetAsync(
            await OperatorOfAsync(slug), "/api/v1/printouts?page=1&pageSize=100");
        Assert.Equal(1, queue.GetProperty("total").GetInt32());
    }

    // ── The three leaks ───────────────────────────────────────────────────────

    /// <summary>
    /// <b>A request may only name your own submission.</b> Without this a
    /// participant posts a competitor's submission id, the operator prints it,
    /// and the paper goes to whoever is standing at the printer.
    /// </summary>
    [Fact]
    public async Task A_print_request_may_not_name_somebody_elses_submission()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);

        var author = await Build.ParticipantAsync(server, slug);
        var theirs = await Build.SubmitAsync(author, slug, "print('mine')\n");
        var submissionId = theirs.GetProperty("id").GetString();

        var other = await Build.ParticipantAsync(server, slug);
        var refused = await AskAsync(other, slug, submissionId: submissionId);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("printout.submission.notYours", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// The requester's list is their own. Every enrolled participant holds
    /// <c>printout:request</c>, so an unfiltered query would hand each of them
    /// everybody else's file names and titles.
    /// </summary>
    [Fact]
    public async Task A_participant_reads_their_own_requests_and_no_others()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);

        var first = await Build.ParticipantAsync(server, slug);
        var second = await Build.ParticipantAsync(server, slug);
        await Sign.Succeeded(await AskAsync(first, slug, fileName: "first.py"));
        await Sign.Succeeded(await AskAsync(second, slug, fileName: "second.py"));

        var mine = await Build.GetAsync(second, $"/api/v1/activities/{slug}/printouts?page=1&pageSize=100");
        var names = mine.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("fileName").GetString())
            .ToList();

        Assert.Equal(["second.py"], names);
    }

    /// <summary>
    /// A sheet and a resolve are asked at the printout's <b>own</b> activity.
    /// An operator trusted with one room has no business with another's.
    /// </summary>
    [Fact]
    public async Task An_operator_reaches_no_printout_outside_their_activity()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var (theirs, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(theirs);

        var participant = await Build.ParticipantAsync(server, theirs);
        await Sign.Succeeded(await AskAsync(participant, theirs));

        var printoutId = await PrintoutInAsync(theirs);

        var elsewhere = await OperatorOfAsync(mine);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await elsewhere.GetAsync($"/api/v1/printouts/{printoutId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await elsewhere.PostAsJsonAsync(
                $"/api/v1/printouts/{printoutId}/resolve", new { outcome = "printed" })).StatusCode);
    }

    /// <summary>
    /// And the same for the <b>bytes</b>, which is a different rule in a
    /// different file.
    /// <para>
    /// <c>/files/{id}</c> is the one address no screen controls: it names bytes
    /// rather than an activity, so <c>FileService</c> has to ask the question
    /// again. Asking it <i>anywhere</i> rather than at the printout's own
    /// activity would let one room's printer operator read another room's exam
    /// answers, and every other assertion here would stay green.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_operator_reads_no_printout_bytes_outside_their_activity()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var (theirs, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(theirs);

        await Sign.Succeeded(await AskAsync(await Build.ParticipantAsync(server, theirs), theirs));
        var printoutId = await PrintoutInAsync(theirs);

        Guid fileId;
        await using (var context = server.NewContext())
        {
            fileId = (await context.FileReferences.AsNoTracking()
                .FirstAsync(r => r.PrintoutId == printoutId)).FileId;
        }

        // Holds the key — in the wrong room.
        var elsewhere = await OperatorOfAsync(mine);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        // Holds nothing at all.
        var (bystander, _) = await AccountAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await bystander.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        // And the one who should.
        var printer = await OperatorOfAsync(theirs);
        Assert.Equal(HttpStatusCode.OK, (await printer.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
    }

    // ── The delegation, which is the point ────────────────────────────────────

    /// <summary>
    /// One key, one surface. A grant carrying <c>printout:manage</c> and nothing
    /// else opens the queue and refuses the rest of the panel.
    /// </summary>
    [Fact]
    public async Task The_printer_operator_reaches_the_queue_and_nothing_else()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var printer = await OperatorOfAsync(slug);

        Assert.Equal(HttpStatusCode.OK, (await printer.GetAsync("/api/v1/printouts?page=1&pageSize=20")).StatusCode);

        foreach (var closed in new[]
                 {
                     "/api/v1/submissions?page=1&pageSize=20",
                     "/api/v1/questions?page=1&pageSize=20",
                     "/api/v1/users?page=1&pageSize=20",
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await printer.GetAsync(closed)).StatusCode);
        }

        // **And the one that does not refuse.** `/manager/activities` narrows on
        // `activity:update`, so an operator gets 200 and an empty page rather
        // than a refusal — which is exactly why the printouts filter has an
        // endpoint of its own instead of borrowing this one. Pinned here so that
        // if it ever starts answering, somebody notices the filter could have
        // been simpler.
        var managed = await Build.GetAsync(printer, "/api/v1/manager/activities?page=1&pageSize=20");
        Assert.Empty(managed.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// The queue narrows rather than refusing, and carries no other activity's
    /// rows — the defect PR #102 fixed for three other lists, which this feature
    /// is more exposed to because the operator's grant is on an activity by
    /// construction.
    /// </summary>
    [Fact]
    public async Task The_queue_is_narrowed_rather_than_refused()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var (theirs, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(mine);
        await OpenPrintoutsAsync(theirs);

        await Sign.Succeeded(await AskAsync(
            await Build.ParticipantAsync(server, mine), mine, fileName: "mine.py"));
        await Sign.Succeeded(await AskAsync(
            await Build.ParticipantAsync(server, theirs), theirs, fileName: "theirs.py"));

        var page = await Build.GetAsync(
            await OperatorOfAsync(mine), "/api/v1/printouts?page=1&pageSize=100");
        var names = page.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("fileName").GetString())
            .ToList();

        Assert.Contains("mine.py", names);
        Assert.DoesNotContain("theirs.py", names);
    }

    /// <summary>
    /// The filter's options come from the printing key, not from
    /// <c>activity:update</c> — an operator asking the panel's summary would be
    /// handed an empty list with no error to notice.
    /// </summary>
    [Fact]
    public async Task The_filter_offers_the_activities_the_operator_prints_for()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var (theirs, _) = await Build.ActivityAsync(server);

        var page = await Build.GetAsync(await OperatorOfAsync(mine), "/api/v1/printouts/activities");
        var slugs = page.EnumerateArray().Select(row => row.GetProperty("slug").GetString()).ToList();

        Assert.Contains(mine, slugs);
        Assert.DoesNotContain(theirs, slugs);
    }

    // ── The sheet, and disposal ───────────────────────────────────────────────

    /// <summary>
    /// The whole loop: asked for, carried on the sheet with who and when, then
    /// resolved — after which the source is gone and the row still says so.
    /// </summary>
    [Fact]
    public async Task Resolving_a_request_takes_the_source_with_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        var participant = await Build.ParticipantAsync(server, slug);
        await Sign.Succeeded(await AskAsync(participant, slug, "print('paper')\n", "answer.py"));

        var printoutId = await PrintoutInAsync(slug);
        Guid fileId;
        await using (var context = server.NewContext())
        {
            fileId = (await context.FileReferences.AsNoTracking()
                .FirstAsync(r => r.PrintoutId == printoutId)).FileId;
        }

        var printer = await OperatorOfAsync(slug);

        var sheet = await Build.GetAsync(printer, $"/api/v1/printouts/{printoutId}");
        Assert.Equal("print('paper')\n", sheet.GetProperty("source").GetString());
        Assert.Equal(slug, sheet.GetProperty("printout").GetProperty("activitySlug").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            sheet.GetProperty("printout").GetProperty("requestedByName").GetString()));
        Assert.Equal(
            Digest("print('paper')\n"),
            sheet.GetProperty("printout").GetProperty("sha256").GetString());

        // The bytes are readable by the operator right up to the confirm.
        Assert.Equal(HttpStatusCode.OK, (await printer.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        await Sign.Succeeded(await printer.PostAsJsonAsync(
            $"/api/v1/printouts/{printoutId}/resolve", new { outcome = "printed" }));

        // Gone from the store, and gone from that address.
        await using (var context = server.NewContext())
        {
            Assert.False(await context.Files.AnyAsync(f => f.Id == fileId));
            Assert.False(await context.FileReferences.AnyAsync(r => r.PrintoutId == printoutId));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await printer.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        // **200 and no source, never 404.** The printout still exists and its
        // record is the audit trail.
        //
        // The whole API omits nulls rather than writing them, so the assertion is
        // absence — and `sourceDisposedAt` below is what carries the fact, which
        // is why the Client reads that and not the missing field.
        var after = await Build.GetAsync(printer, $"/api/v1/printouts/{printoutId}");
        Assert.False(after.TryGetProperty("source", out _));
        Assert.Equal("printed", after.GetProperty("printout").GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            after.GetProperty("printout").GetProperty("sourceDisposedAt").GetString()));
    }

    /// <summary>A second confirm is a conflict, not a second disposal.</summary>
    [Fact]
    public async Task A_resolved_request_cannot_be_resolved_again()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        await Sign.Succeeded(await AskAsync(await Build.ParticipantAsync(server, slug), slug));

        var printoutId = await PrintoutInAsync(slug);

        var printer = await OperatorOfAsync(slug);
        await Sign.Succeeded(await printer.PostAsJsonAsync(
            $"/api/v1/printouts/{printoutId}/resolve", new { outcome = "discarded" }));

        var again = await printer.PostAsJsonAsync(
            $"/api/v1/printouts/{printoutId}/resolve", new { outcome = "printed" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>
    /// Deleting an account takes its waiting sheets with it.
    /// <para>
    /// Anonymisation reaches the <i>name</i> for free, through the <c>User</c>
    /// navigation. It reaches nothing at all of the source, which is the half
    /// that would otherwise sit in the queue for whoever is next at the printer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_disposes_of_its_waiting_sheets()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);

        var participant = await Build.ParticipantAsync(server, slug);
        await Sign.Succeeded(await AskAsync(participant, slug, "print('theirs')\n", "theirs.py"));

        var printoutId = await PrintoutInAsync(slug);
        Guid fileId;
        string userId;
        await using (var context = server.NewContext())
        {
            var printout = await context.Printouts.AsNoTracking().FirstAsync(x => x.Id == printoutId);
            userId = printout.RequestedByUserId;
            fileId = (await context.FileReferences.AsNoTracking()
                .FirstAsync(r => r.PrintoutId == printoutId)).FileId;
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var printouts = scope.ServiceProvider.GetRequiredService<IPrintoutService>();
            Assert.Equal(1, await printouts.DisposeOfEveryOutstandingAsync(userId, CancellationToken.None));
        }

        await using (var context = server.NewContext())
        {
            Assert.False(await context.Files.AnyAsync(f => f.Id == fileId));
            var after = await context.Printouts.AsNoTracking().FirstAsync(x => x.Id == printoutId);
            Assert.Equal(PrintoutState.Discarded, after.State);
            Assert.NotNull(after.SourceDisposedAt);
        }
    }

    // ── Two people, one printer ───────────────────────────────────────────────

    /// <summary>
    /// Taking a row marks it, so the other operator can see it is somebody's.
    /// <para>
    /// This is the whole of what the state prevents: not a race on the row, but
    /// two people each opening a sheet believing they are alone with the queue.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Taking_a_row_shows_the_other_operator_who_has_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        await Sign.Succeeded(await AskAsync(await Build.ParticipantAsync(server, slug), slug));
        var printoutId = await PrintoutInAsync(slug);

        var first = await OperatorOfAsync(slug);
        var second = await OperatorOfAsync(slug);

        var claimed = await Build.PostAsync(first, $"/api/v1/printouts/{printoutId}/claim", new { });
        Assert.Equal("printing", claimed.GetProperty("state").GetString());
        Assert.True(claimed.GetProperty("claimedByMe").GetBoolean());

        // The same row, read by the other one.
        var theirs = await Build.GetAsync(second, "/api/v1/printouts?page=1&pageSize=100");
        var row = theirs.GetProperty("items").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == printoutId.ToString());

        Assert.Equal("printing", row.GetProperty("state").GetString());
        Assert.False(row.GetProperty("claimedByMe").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("claimedByName").GetString()));
    }

    /// <summary>
    /// And it can be taken over, because the alternative is a page stranded
    /// behind somebody who went home.
    /// </summary>
    [Fact]
    public async Task A_row_somebody_walked_away_from_can_be_taken_over()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        await Sign.Succeeded(await AskAsync(await Build.ParticipantAsync(server, slug), slug));
        var printoutId = await PrintoutInAsync(slug);

        await Build.PostAsync(await OperatorOfAsync(slug), $"/api/v1/printouts/{printoutId}/claim", new { });

        var second = await OperatorOfAsync(slug);
        var taken = await Build.PostAsync(second, $"/api/v1/printouts/{printoutId}/claim", new { });
        Assert.True(taken.GetProperty("claimedByMe").GetBoolean());

        // And handed back, which returns it to the queue rather than resolving it.
        var released = await Build.PostAsync(second, $"/api/v1/printouts/{printoutId}/release", new { });
        Assert.Equal("requested", released.GetProperty("state").GetString());
        Assert.False(released.TryGetProperty("claimedByName", out _), "and nobody holds it");
    }

    /// <summary>A resolved row is nobody's to take.</summary>
    [Fact]
    public async Task A_resolved_row_cannot_be_claimed()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        await Sign.Succeeded(await AskAsync(await Build.ParticipantAsync(server, slug), slug));
        var printoutId = await PrintoutInAsync(slug);

        var printer = await OperatorOfAsync(slug);
        await Sign.Succeeded(await printer.PostAsJsonAsync(
            $"/api/v1/printouts/{printoutId}/resolve", new { outcome = "printed" }));

        var refused = await printer.PostAsJsonAsync($"/api/v1/printouts/{printoutId}/claim", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    // ── The upload rules ──────────────────────────────────────────────────────

    /// <summary>
    /// **A file part keeps its bytes, newlines included.**
    /// <para>
    /// A browser normalises every newline in a multipart *text* field to CRLF
    /// before it leaves, so source picked from disk and hashed as it is on disk
    /// never matched what arrived — a 422 on a file nothing was wrong with. The
    /// file part is what makes the checksum mean what it says.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_picked_file_arrives_with_the_bytes_it_had()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        var participant = await Build.ParticipantAsync(server, slug);

        // Unix newlines, which is what a text field would have turned into CRLF.
        var source = "int main() {\n    return 0;\n}\n";
        var bytes = Encoding.UTF8.GetBytes(source);

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", "program (1).cpp");
        form.Add(new StringContent(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), "sha256");

        await Sign.Succeeded(await participant.PostAsync($"/api/v1/activities/{slug}/printouts", form));

        var printoutId = await PrintoutInAsync(slug);
        var sheet = await Build.GetAsync(await OperatorOfAsync(slug), $"/api/v1/printouts/{printoutId}");

        Assert.Equal(source, sheet.GetProperty("source").GetString());
        Assert.Equal("program (1).cpp", sheet.GetProperty("printout").GetProperty("fileName").GetString());
    }

    /// <summary>
    /// A checksum that does not match what arrived stores nothing — no row and
    /// no bytes, because the refusal is inside <c>CommitAsync</c>.
    /// </summary>
    [Fact]
    public async Task A_declared_checksum_that_does_not_match_stores_nothing()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        var participant = await Build.ParticipantAsync(server, slug);

        int filesBefore;
        await using (var context = server.NewContext())
        {
            filesBefore = await context.Files.CountAsync();
        }

        var refused = await AskAsync(participant, slug, sha256: new string('b', 64));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await using (var context = server.NewContext())
        {
            Assert.Equal(filesBefore, await context.Files.CountAsync());
            Assert.False(await context.Printouts.AnyAsync(x => x.Activity!.Slug == slug));
        }
    }

    /// <summary>
    /// Three waiting is enough. A fourth says so rather than joining the line.
    /// </summary>
    [Fact]
    public async Task A_person_may_only_have_so_many_requests_waiting()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await OpenPrintoutsAsync(slug);
        var participant = await Build.ParticipantAsync(server, slug);

        for (var i = 0; i < 3; i++)
        {
            await Sign.Succeeded(await AskAsync(participant, slug, fileName: $"a{i}.py"));
        }

        var refused = await AskAsync(participant, slug, fileName: "a3.py");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("printout.tooMany", problem.GetProperty("code").GetString());
    }
}
