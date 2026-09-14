using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Who may read a stored file, addressed by its id.
/// <para>
/// <b>`/files/{id}` is the one address the screens do not control.</b> Every
/// other read arrives through a route that names an activity, a submission or a
/// problem, and is narrowed by it; this one names bytes. Three holes found by
/// the authorization audit of 2026-09-09 were all of that shape — a rule the
/// screens apply and this address did not.
/// </para>
/// </summary>
[Collection("server-3")]
public class FileAccessTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>
    /// A manager-scope file on the version an activity is judging.
    /// <para>
    /// Written straight onto the pinned version rather than published through a
    /// second version, because what is under test is the <b>read</b> rule and
    /// pinning would take the new version out of the job's reach.
    /// </para>
    /// </summary>
    private async Task<Guid> ModelSolutionOnAsync(string activitySlug)
    {
        await using var context = server.NewContext();
        var assignment = await context.SeriesProblems
            .Include(sp => sp.Activity)
            .FirstAsync(sp => sp.Activity!.Slug == activitySlug);

        var file = new Database.Models.File
        {
            Id = Guid.NewGuid(),
            Name = "model.cpp",
            MimeType = "text/plain",
            SizeBytes = 12,
            Sha256 = new string('a', 64),
            StorageId = "pg",
        };
        context.Files.Add(file);
        context.FileReferences.Add(new FileReference
        {
            FileId = file.Id,
            OwnerKind = FileOwnerKind.ProblemVersion,
            ProblemVersionId = assignment.PinnedProblemVersionId,
            Scope = FileScope.Manager,
            Name = "model.cpp",
        });
        await context.SaveChangesAsync();
        return file.Id;
    }

    /// <summary>
    /// <b>A Runner judging a problem may not read its model solution.</b>
    /// `FileService.CanReadProblemVersionAsync` says manager scope is "never a
    /// participant, never a Runner"; `RunnerService.MayReadAsync` matched the
    /// version and never the scope, so anything judging a problem could fetch
    /// the answer by id.
    /// </summary>
    [Fact]
    public async Task A_runner_judging_a_problem_may_not_read_its_model_solution()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var model = await ModelSolutionOnAsync(slug);

        var runner = await Build.RunnerAsync(server);
        await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);

        var read = await runner.Client.GetAsync($"/api/v1/runner/files/{model}");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    /// <summary>And it still reads the package, which is what it is there for.</summary>
    [Fact]
    public async Task And_still_reads_the_package_it_was_given_a_job_for()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var package = await Build.PackageIdOfAsync(server, slug);

        var runner = await Build.RunnerAsync(server);
        await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);

        var read = await runner.Client.GetAsync($"/api/v1/runner/files/{package}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// <b>A statement is not readable by file id while its round has not
    /// opened.</b> `ProblemService` treats `ISeriesGate.MayReadProblems` as the
    /// rule for whether a statement may be sent at all; the file address applied
    /// the lockdown and never the gate, so a round that never opened disclosed
    /// what it holds to anybody who could name the file.
    /// </summary>
    [Fact]
    public async Task A_statement_is_not_readable_while_its_round_has_not_opened()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        Guid statement;
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems
                .Include(sp => sp.Activity)
                .FirstAsync(sp => sp.Activity!.Slug == slug);
            statement = (await context.FileReferences
                .FirstAsync(r => r.ProblemVersionId == assignment.PinnedProblemVersionId
                    && r.Scope == FileScope.Participant)).FileId;

            // Back to never opened, the way a round waiting for the scheduler is.
            var round = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            round.IsOpen = false;
            round.StartAnnouncedAt = null;
            await context.SaveChangesAsync();
        }

        var read = await participant.GetAsync($"/api/v1/files/{statement}");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    /// <summary>
    /// <b>Attaching a file is a read grant, so the caller must be able to read
    /// it.</b> Existence was the whole test, which turned `problem:update` into
    /// a way to read anything by attaching its id to one's own version and then
    /// fetching it.
    /// </summary>
    [Fact]
    public async Task A_file_the_caller_cannot_read_may_not_be_attached()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        // Somebody else's upload: unreferenced, so only its uploader may read it.
        var participant = await Build.ParticipantAsync(server, slug);
        var theirs = await Build.UploadAsync(participant, "/api/v1/files", "notes.md", "not yours\n");

        Guid problemId;
        await using (var context = server.NewContext())
        {
            problemId = (await context.SeriesProblems
                .Include(sp => sp.Activity)
                .FirstAsync(sp => sp.Activity!.Slug == slug)).ProblemId;
        }

        // **A version this Server would otherwise publish.** Its own statement,
        // its own config: the only thing wrong with it is the attached file, so
        // a refusal can only be about that. Written with an empty statement list
        // first, which was refused for being empty — the test passed against the
        // unfixed Server, measured 2026-09-09, which is the whole reason this
        // one names the code it expects.
        var mine = await Build.UploadAsync(admin, "/api/v1/files", "content.md", "# Mine\n");
        var published = await admin.PostAsJsonAsync($"/api/v1/problems/{problemId}/versions", new
        {
            statements = new[] { new { fileId = mine } },
            config = new { format = "standard-io", version = 1, limits = new { timeMs = 1000, memoryBytes = 268435456 } },
            files = new[] { new { fileId = theirs, name = "stolen.md", scope = "participant" } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.StatusCode);
        var refusal = await published.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("file.missing", refusal.GetProperty("code").GetString());
    }

    /// <summary>The source of one submission, and the id it is fetched by.</summary>
    private static async Task<(string Id, string FileId)> SubmittedAsync(
        HttpClient participant, string slug)
    {
        var created = await Build.SubmitAsync(participant, slug, "int main() { return 0; }\n");
        var id = created.GetProperty("id").GetString()!;
        var detail = await Build.GetAsync(participant, $"/api/v1/activities/{slug}/submissions/{id}");
        var source = detail.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "source");
        return (id, source.GetProperty("fileId").GetString()!);
    }

    private static async Task<int> SourceRowsAsync(HttpClient client, string slug, string id) =>
        (await Build.GetAsync(client, $"/api/v1/activities/{slug}/submissions/{id}"))
            .GetProperty("files").EnumerateArray()
            .Count(f => f.GetProperty("name").GetString() == "source");

    /// <summary>
    /// <b>A round paused with its statements taken away takes the source with
    /// them.</b> A participant re-reading their own code during a pause called
    /// for a leak in the statement is the reading the hiding exists to stop, one
    /// door along.
    /// <para>
    /// Asserted at <b>both</b> doors, because either alone is a hole: the detail
    /// stops naming the file, and the file id stops answering. The reference is
    /// what the screen draws a button from; the bytes are what the button
    /// fetches, and a remembered id walks past the screen entirely.
    /// </para>
    /// <para>
    /// <b>Resuming gives it back</b>, which is what proves this is the gate
    /// asked at read time rather than a decision written down when the round
    /// stopped.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_paused_rounds_hidden_content_takes_its_source_with_it()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var (id, fileId) = await SubmittedAsync(participant, slug);

        Assert.Equal(1, await SourceRowsAsync(participant, slug, id));
        Assert.Equal(HttpStatusCode.OK, (await participant.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/pause", new { hideProblems = true }));

        Assert.Equal(0, await SourceRowsAsync(participant, slug, id));
        // 404 and not 403, as every other refusal at this address is: a file id
        // is opaque, and a 403 would confirm that the bytes exist.
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/resume", new { extendEnd = false }));

        Assert.Equal(1, await SourceRowsAsync(participant, slug, id));
        Assert.Equal(HttpStatusCode.OK, (await participant.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
    }

    /// <summary>
    /// <b>The other half of the same rule</b>, and a different field: an
    /// activity that hides the problems of finished rounds hides what was
    /// written for them too. Nothing about the submission changed — the round
    /// ended.
    /// </summary>
    [Fact]
    public async Task An_ended_rounds_source_goes_where_the_activity_hides_finished_rounds()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var (id, fileId) = await SubmittedAsync(participant, slug);

        await using (var context = server.NewContext())
        {
            var round = await context.Series.Include(s => s.Activity)
                .FirstAsync(s => s.Id == Guid.Parse(roundId));
            round.IsOpen = false;
            round.EndDate = DateTime.UtcNow.AddMinutes(-1);
            round.Activity!.HideEndedSeriesProblems = true;
            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await SourceRowsAsync(participant, slug, id));
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
    }

    /// <summary>
    /// <b>Staff are exempt, and by the key they already hold.</b> Whoever hid
    /// the round is the person who has to be able to see what happened in it —
    /// a manager who paused a contest for a leak and then could not read the
    /// answers already sent would have to resume it to investigate.
    /// </summary>
    [Fact]
    public async Task A_hidden_round_keeps_its_source_readable_by_staff()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var (_, fileId) = await SubmittedAsync(participant, slug);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/pause", new { hideProblems = true }));

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
    }

    /// <summary>
    /// <b>The evaluation log stays, and that is chosen rather than overlooked.</b>
    /// The rule is scoped to the name a submission's own bytes are stored under,
    /// so an attempt's log and per-test document are untouched — in a course
    /// they are the feedback, and the round ending is when somebody reads them.
    /// <para>
    /// A compiler error can quote a line of the source, so this is not airtight;
    /// it is the line the owner drew, and this test is where it is written down.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_hidden_round_keeps_the_evaluation_log_readable()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var (id, sourceId) = await SubmittedAsync(participant, slug);

        Guid logId;
        await using (var context = server.NewContext())
        {
            var job = await context.EvaluationJobs.FirstAsync(j => j.SubmissionId == Guid.Parse(id));
            var log = new Database.Models.File
            {
                Id = Guid.NewGuid(),
                Name = "log.txt",
                MimeType = "text/plain",
                SizeBytes = 7,
                Sha256 = new string('b', 64),
                StorageId = "pg",
            };
            context.Files.Add(log);
            context.FileReferences.Add(new FileReference
            {
                FileId = log.Id,
                OwnerKind = FileOwnerKind.Attempt,
                EvaluationJobId = job.Id,
                Scope = FileScope.Participant,
                Name = "log",
            });
            // The activity shares it, the way a course does.
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            context.AttachmentRules.Add(new AttachmentRule
            {
                ActivityId = activity.Id,
                Name = "log",
                Visibility = AttachmentVisibility.Participant,
            });
            await context.SaveChangesAsync();
            logId = log.Id;
        }

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/pause", new { hideProblems = true }));

        // Asked at `/meta`, which applies the identical rule and reads the row
        // rather than the bytes: this log is a reference planted without a blob
        // behind it, so the download would fail on storage and say nothing about
        // who may read it.
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/v1/files/{sourceId}/meta")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await participant.GetAsync($"/api/v1/files/{logId}/meta")).StatusCode);
    }
}
