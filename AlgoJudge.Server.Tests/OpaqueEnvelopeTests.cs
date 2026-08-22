using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The gate in front of everything the Server stores and never reads.
///
/// <para>
/// <b>All of this was specified on 2026-08-07 and implemented on 2026-08-22.</b>
/// `docs/specs/OPAQUE_DOCUMENTS.md` has required an envelope that is an envelope
/// and a ceiling since it was accepted; only <c>extra</c>'s 2 kB existed, in one
/// method. A manager could store a hundred megabytes of JSON on an assignment —
/// the denial-of-service surface that specification was written to close, left
/// open by its own implementation.
/// </para>
/// <para>
/// Neither check reads the document. One asks whether the envelope is an
/// envelope, the other how big it is. What is inside stays the problem type's
/// business, which is the entire point of the field.
/// </para>
/// </summary>
[Collection("server")]
public class OpaqueEnvelopeTests(ServerFixture server)
{
    // ── the envelope ────────────────────────────────────────────────────────

    /// <summary>
    /// An array is not an envelope, and the refusal says which it got.
    /// </summary>
    [Theory]
    [InlineData("[1, 2, 3]", "an array")]
    [InlineData("\"a string\"", "a string")]
    [InlineData("42", "a number")]
    [InlineData("true", "a boolean")]
    public async Task A_config_that_is_not_an_object_is_refused_and_named(string json, string named)
    {
        var (_, roundId) = await Build.ActivityAsync(server);

        var refused = await AttachAsync(roundId, json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var (code, detail) = await RefusalOf(refused);
        Assert.Equal("opaque.notAnObject", code);
        // The message names what arrived, because "invalid" tells a manager
        // nothing about which of their fields to look at.
        Assert.Contains(named, detail);
    }

    /// <summary>
    /// An object is what the rule asks for, so it goes in.
    /// </summary>
    [Fact]
    public async Task An_object_is_stored_without_being_read()
    {
        var (_, roundId) = await Build.ActivityAsync(server);

        var accepted = await AttachAsync(roundId, "{\"whatever\": {\"the\": [\"type\", \"wants\"]}}");

        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());

        // Stored verbatim: the nesting an array *inside* the envelope is fine,
        // because the rule is about the envelope and not about the contents.
        var round = Guid.Parse(roundId);
        await using var context = server.NewContext();
        var stored = await context.SeriesProblems.FirstAsync(sp => sp.SeriesId == round && sp.Slug == "B");
        Assert.Contains("wants", stored.Config!);
    }

    /// <summary>
    /// Absent stays absent, and never becomes <c>{}</c>.
    /// </summary>
    [Fact]
    public async Task An_absent_config_is_null_rather_than_an_empty_object()
    {
        var (_, roundId) = await Build.ActivityAsync(server);

        var accepted = await AttachAsync(roundId, "null");
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());

        var round = Guid.Parse(roundId);
        await using var context = server.NewContext();
        var stored = await context.SeriesProblems.FirstAsync(sp => sp.SeriesId == round && sp.Slug == "B");
        // An empty object is not a second way of saying nothing. Two spellings
        // of one fact drift, and a reader has to guard against both.
        Assert.Null(stored.Config);
    }

    // ── the ceiling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Over 256 kB is refused, and refused rather than trimmed.
    /// </summary>
    [Fact]
    public async Task A_config_over_the_ceiling_is_refused_and_nothing_is_stored()
    {
        var (_, roundId) = await Build.ActivityAsync(server);

        // Comfortably over, and valid JSON: what is under test is the size, not
        // the parser.
        var padding = new string('x', 300 * 1024);
        var refused = await AttachAsync(roundId, $"{{\"padding\": \"{padding}\"}}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("opaque.tooLarge", (await RefusalOf(refused)).Code);

        // **Refused, never truncated.** Half a JSON document is not a document,
        // and a Server that trimmed one would be storing something no renderer
        // can read while reporting success.
        //
        // Scoped to this round, not to the database: the suite shares one, and a
        // sibling test stores a "B" of its own. Asserting globally made this
        // fail on the order it ran in — which is the trap `Build`'s own comment
        // warns about.
        var round = Guid.Parse(roundId);
        await using var context = server.NewContext();
        Assert.False(await context.SeriesProblems.AnyAsync(sp => sp.SeriesId == round && sp.Config != null));
    }

    /// <summary>
    /// Just under it goes in, so the ceiling is a ceiling and not a ban.
    /// </summary>
    [Fact]
    public async Task A_config_just_under_the_ceiling_is_accepted()
    {
        var (_, roundId) = await Build.ActivityAsync(server);

        var padding = new string('x', 200 * 1024);
        var accepted = await AttachAsync(roundId, $"{{\"padding\": \"{padding}\"}}");

        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
    }

    // ── the third limit the Server exists to enforce ─────────────────────────

    /// <summary>
    /// An assignment that accepts no attachments accepts no submission.
    ///
    /// <para>
    /// <c>MaxAttachments</c> was stored, projected, editable in the panel and
    /// read by nothing until 2026-08-22 — a promise the code did not keep,
    /// against a specification that names the attachment count as a column
    /// precisely because the Server polices it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_assignment_accepting_no_attachments_refuses_a_submission()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxAttachments = 0;
            await context.SaveChangesAsync();
        }

        var refused = await SubmitRawAsync(participant, slug, "print(1)\n");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("submission.attachments", (await RefusalOf(refused)).Code);
    }

    /// <summary>
    /// One attachment is what the form sends, so one is enough to pass.
    /// </summary>
    [Fact]
    public async Task An_assignment_accepting_one_attachment_takes_the_submission()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxAttachments = 1;
            await context.SaveChangesAsync();
        }

        var accepted = await SubmitRawAsync(participant, slug, "print(1)\n");

        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
    }

    // ── the chain, merged in depth ──────────────────────────────────────────

    /// <summary>
    /// An assignment that narrows one option keeps the version's others.
    ///
    /// <para>
    /// <b>Rewritten 2026-08-22, an hour after it was written.</b> It proved a
    /// deep merge in the Server, of the problem version's configuration under
    /// the assignment's. There is no version layer any more: the chain is the
    /// package and the assignment, the Runner performs the one merge that
    /// remains, and what this can still prove — and has to — is that the
    /// Server hands the document over <b>whole</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_assignments_configuration_reaches_the_runner_entire()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);

        // Scoped to this round rather than taken with `FirstAsync()`: the suite
        // shares a database, and an unfiltered first row is whichever test ran
        // before this one.
        var round = Guid.Parse(roundId);
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems.FirstAsync(sp => sp.SeriesId == round);
            assignment.Config = """{"limits":{"timeMs":250,"memoryBytes":536870912},"languages":["python3"]}""";
            await context.SaveChangesAsync();
        }

        var participant = await Build.ParticipantAsync(server, slug);
        var submission = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submission.GetProperty("id").GetString()!);

        var config = job.GetProperty("config");
        var limits = config.GetProperty("limits");
        Assert.Equal(250, limits.GetProperty("timeMs").GetInt32());
        Assert.Equal(536870912, limits.GetProperty("memoryBytes").GetInt64());

        // Not a member the Server knows anything about, which is the point: it
        // arrives because nothing here reads or rebuilds the document.
        Assert.Equal(
            new[] { "python3" },
            config.GetProperty("languages").EnumerateArray().Select(l => l.GetString()).ToArray());
    }

    /// <summary>
    /// The language left the Server on 2026-08-22, and this is what took its
    /// place: whatever the participant declared travels to the Runner unread.
    ///
    /// <para>
    /// The Server used to refuse a language the activity did not list. It cannot
    /// — the language is one member of an opaque document now — and the refusal
    /// is the Runner's, against the allowed set in the assignment's `config`.
    /// What must not happen is the document being lost on the way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task What_the_participant_declared_reaches_the_runner_unread()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submission = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submission.GetProperty("id").GetString()!);

        var props = job.GetProperty("props");
        Assert.Equal("python3", props.GetProperty("language").GetString());
        Assert.Equal("standard-io@1", props.GetProperty("type").GetString());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches a second problem to the round, carrying the config under test.
    ///
    /// <para>
    /// The attach endpoint rather than an edit, because that is the write path
    /// the guard sits on and because a fresh assignment leaves the first one
    /// alone — the suite shares a database.
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> AttachAsync(string roundId, string configJson)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var problem = await Build.PostAsync(admin, "/api/v1/problems", new
        {
            slug = "q-" + Guid.NewGuid().ToString("N")[..8],
            name = "Second problem",
            type = "standard-io@1",
        });
        var problemId = problem.GetProperty("id").GetString()!;

        var body = $$"""
            { "problemId": "{{problemId}}", "slug": "B", "maxPoints": 10, "config": {{configJson}} }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await admin.PostAsync($"/api/v1/series/{roundId}/problems", content);
    }

    private static async Task<HttpResponseMessage> SubmitRawAsync(
        HttpClient client, string slug, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"type":"standard-io@1","language":"python3"}"""), "props" },
            { new StringContent("main.py"), "fileName" },
            { new StringContent(source), "code" },
            { new StringContent(checksum), "sha256" },
        };

        return await client.PostAsync(
            $"/api/v1/activities/{slug}/problems/A/submissions", content);
    }

    /// <summary>
    /// The whole refusal, read once.
    ///
    /// <para>
    /// <b>Once</b> is the point: an <c>HttpContent</c> is a stream, and asking
    /// it for the code and then for the message read it twice and threw on the
    /// second. The first version of these tests did exactly that, and the four
    /// that failed had already proved the refusal correct before failing to
    /// read it.
    /// </para>
    /// </summary>
    private static async Task<(string Code, string Detail)> RefusalOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            body.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "",
            body.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "");
    }
}
