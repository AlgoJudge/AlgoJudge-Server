using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The Server–Runner protocol, clause by clause.
///
/// <para>
/// The specification is
/// <c>AlgoJudge-Design/specifications/server-runner/SERVER_RUNNER_API.md</c>,
/// accepted 2026-08-08 and written from this implementation. These are what make
/// it an agreement rather than a description: a second implementation of that
/// document should pass the same sequence, and a change here that the document
/// does not describe is a change to the contract.
/// </para>
/// <para>
/// Distinct from <c>RunnerTests</c>, which covers the lease and what a Runner may
/// reach through it. This covers the <b>handshake and its refusals</b> — the part
/// the 2025-03-27 proposal left open and nothing exercised end to end.
/// </para>
/// </summary>
[Collection("server")]
public class RunnerConformanceTests(ServerFixture server)
{
    /// <summary>A key pair, and the two things the protocol does with it.</summary>
    private sealed class Identity
    {
        private readonly Ed25519PrivateKeyParameters key;

        public Identity()
        {
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            key = (Ed25519PrivateKeyParameters)pair.Private;
            PublicKey = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
        }

        /// <summary>The raw 32 bytes the Server stores, base64 — not an SPKI wrapper.</summary>
        public string PublicKey { get; }

        /// <summary>Ed25519 over the UTF-8 bytes of the nonce, base64.</summary>
        public string Sign(string nonce)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, key);
            var message = Encoding.UTF8.GetBytes(nonce);
            signer.BlockUpdate(message, 0, message.Length);
            return Convert.ToBase64String(signer.GenerateSignature());
        }
    }

    private async Task<(HttpClient Client, string Id, string Fingerprint)> RegisterAsync(
        Identity identity, string name)
    {
        var anonymous = server.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/runner/register", new
        {
            name,
            product = "AlgoJudge-Runner-Conformance",
            version = "0.0.1",
            publicKey = identity.PublicKey,
            problemTypes = new[] { "standard-io@1" },
        });
        await Sign.Succeeded(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (server.CreateClient(),
            body.GetProperty("runnerId").GetString()!,
            body.GetProperty("fingerprint").GetString()!);
    }

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
    }

    // ── §3 registration ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("dG9vLXNob3J0", "runner.key.length")]
    [InlineData("not base64 at all !!", "runner.key.malformed")]
    public async Task A_key_that_is_not_thirty_two_bytes_is_refused(string publicKey, string expected)
    {
        var response = await server.CreateClient().PostAsJsonAsync("/api/v1/runner/register", new
        {
            name = "bad-key",
            publicKey,
            problemTypes = new[] { "standard-io@1" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(expected, await CodeOf(response));
    }

    // ── §4 authentication ───────────────────────────────────────────────────

    /// <summary>
    /// Approval is the whole of what approval means: registering announces a
    /// Runner, and nothing is evaluated until somebody says so.
    /// </summary>
    [Fact]
    public async Task A_registered_but_unapproved_runner_is_refused_a_token()
    {
        var identity = new Identity();
        var (client, _, fingerprint) = await RegisterAsync(identity, "unapproved");

        var challenge = await client.PostAsJsonAsync(
            "/api/v1/runner/auth/challenge", new { fingerprint });
        await Sign.Succeeded(challenge);
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nonce").GetString()!;

        var token = await client.PostAsJsonAsync("/api/v1/runner/auth/token", new
        {
            fingerprint,
            nonce,
            signature = identity.Sign(nonce),
        });

        Assert.Equal(HttpStatusCode.Forbidden, token.StatusCode);
        Assert.Equal("runner.notApproved", await CodeOf(token));
    }

    /// <summary>
    /// A nonce is spent by being used. Without this, one captured exchange is
    /// replayable for ever — which is the replay protection the 2025-03-27
    /// proposal listed as an open question.
    /// </summary>
    [Fact]
    public async Task A_nonce_is_single_use()
    {
        var identity = new Identity();
        var (client, id, fingerprint) = await RegisterAsync(identity, "replay");

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsync($"/api/v1/runners/{id}/approve", null));

        var challenge = await client.PostAsJsonAsync(
            "/api/v1/runner/auth/challenge", new { fingerprint });
        await Sign.Succeeded(challenge);
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nonce").GetString()!;
        var signature = identity.Sign(nonce);

        var first = await client.PostAsJsonAsync("/api/v1/runner/auth/token", new
        {
            fingerprint, nonce, signature,
        });
        await Sign.Succeeded(first);

        // The very same exchange, replayed.
        var replay = await client.PostAsJsonAsync("/api/v1/runner/auth/token", new
        {
            fingerprint, nonce, signature,
        });

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal("runner.nonce.unknown", await CodeOf(replay));
    }

    /// <summary>
    /// Holding the fingerprint is not holding the key. A signature that does not
    /// verify is the one thing between an attacker who read a registration and a
    /// token.
    /// </summary>
    [Fact]
    public async Task A_signature_from_the_wrong_key_is_refused()
    {
        var identity = new Identity();
        var (client, id, fingerprint) = await RegisterAsync(identity, "impostor");

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsync($"/api/v1/runners/{id}/approve", null));

        var challenge = await client.PostAsJsonAsync(
            "/api/v1/runner/auth/challenge", new { fingerprint });
        await Sign.Succeeded(challenge);
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nonce").GetString()!;

        var token = await client.PostAsJsonAsync("/api/v1/runner/auth/token", new
        {
            fingerprint,
            nonce,
            // Somebody else's key over the right nonce.
            signature = new Identity().Sign(nonce),
        });

        Assert.Equal(HttpStatusCode.Forbidden, token.StatusCode);
        Assert.Equal("runner.signature", await CodeOf(token));
    }

    // ── §5 claiming, and §6 reporting ───────────────────────────────────────

    /// <summary>
    /// An empty queue answers 204, not an error and not an empty object: there
    /// being nothing to do is the ordinary state of a Runner.
    /// </summary>
    [Fact]
    public async Task An_empty_queue_answers_no_content()
    {
        var runner = await Build.RunnerAsync(server);

        // Drain whatever other tests have left, then ask once more.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var response = await runner.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
            if (response.StatusCode == HttpStatusCode.NoContent) return;
            await Sign.Succeeded(response);
        }

        Assert.Fail("the queue never emptied, so the 204 path was not reached");
    }

    /// <summary>
    /// Reporting is idempotent on the lease token, and the second answer says so
    /// rather than pretending to be the first. A Runner that reported and then
    /// lost the connection may safely resend.
    /// </summary>
    [Fact]
    public async Task Reporting_twice_answers_the_same_result_and_says_it_is_a_repeat()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        var first = await runner.ReportAsync(jobId, leaseToken);
        var again = await runner.ReportAsync(jobId, leaseToken);

        Assert.Equal(
            first.GetProperty("resultId").GetString(),
            again.GetProperty("resultId").GetString());
        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(again.GetProperty("duplicate").GetBoolean(),
            "a repeat has to say so, or a Runner cannot tell it from a first report");
    }

    /// <summary>
    /// A job whose lease ran out goes back to the queue, and the Runner that was
    /// holding it is refused. Both halves matter: without the first the work is
    /// lost with the machine, and without the second a Runner that woke up late
    /// would overwrite whoever has the job now.
    /// </summary>
    [Fact]
    public async Task A_lease_that_expired_is_reclaimed_and_the_old_holder_is_refused()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(2)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        // Expire it where the clock cannot be waited out.
        await using (var context = server.NewContext())
        {
            var held = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            held.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }

        using (var scope = server.Services.CreateScope())
        {
            var reaped = await scope.ServiceProvider
                .GetRequiredService<LeaseReaper>().SweepAsync(default);
            Assert.True(reaped > 0, "the sweep found nothing to reclaim");
        }

        await using (var context = server.NewContext())
        {
            var held = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            Assert.Equal(EvaluationJobState.Queued, held.State);
            Assert.Null(held.RunnerId);
        }

        var late = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/report",
            new { leaseToken, score = 100, maxScore = 100, verdict = "Accepted" });

        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        Assert.Equal("runner.lease.stale", await CodeOf(late));
    }

    /// <summary>
    /// Renewing a lease never brings its deadline closer.
    /// <para>
    /// It used to. A Runner that claimed for ten minutes and then said "still
    /// working" asking for one minute moved its own deadline eight minutes
    /// earlier — so the obvious implementation, a short ping on a short
    /// interval, left itself a permanent sixty-second margin, and one pause
    /// longer than that handed the job to a second Runner while the first was
    /// still computing. Found by a Rust Runner's conformance test asserting the
    /// opposite and failing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Renewing_a_lease_never_shortens_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(4)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;
        var granted = DateTime.Parse(
            job.GetProperty("leaseExpiresAt").GetString()!,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind);

        // The floor is sixty seconds and the claim above took the ten-minute
        // default, so this asks for far less than is already held.
        var response = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/lease",
            new { leaseToken, leaseSeconds = 60 });
        await Sign.Succeeded(response);

        var renewed = DateTime.Parse(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("leaseExpiresAt").GetString()!,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind);

        // On the microsecond, not on the tick. PostgreSQL stores `timestamp` to
        // the microsecond and .NET keeps hundred-nanosecond ticks, so a deadline
        // that has been through the database comes back up to nine ticks earlier
        // than the one the claim returned straight from memory — with nothing
        // having moved. A client that compares these exactly is comparing a
        // stored value against an unstored one.
        Assert.True(
            renewed >= granted.AddMicroseconds(-1),
            $"renewing moved the deadline from {granted:O} back to {renewed:O}");
    }

    /// <summary>
    /// An infrastructure failure is not a wrong answer. Recording it as a zero
    /// would be a lie about the solution — the submission was never judged.
    /// </summary>
    [Fact]
    public async Task An_infrastructure_failure_is_not_scored_as_a_wrong_answer()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(3)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);

        var reported = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/report",
            new
            {
                leaseToken = job.GetProperty("leaseToken").GetString(),
                infrastructureFailure = true,
                failureReason = "package checksum mismatch",
            });
        await Sign.Succeeded(reported);

        await using var context = server.NewContext();
        var stored = await context.EvaluationJobs
            .Include(j => j.Result)
            .FirstAsync(j => j.Id == jobId);

        Assert.Equal(EvaluationJobState.Failed, stored.State);
        Assert.Contains("checksum", stored.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);

        // A result row is still written — it is what makes a repeated report
        // idempotent — but it carries **no score**, and `Scoring.Best` skips a
        // result without one. So the attempt is on record and the board never
        // sees a zero that would read as a wrong answer.
        Assert.NotNull(stored.Result);
        Assert.Null(stored.Result!.Score);
        Assert.Null(stored.Result.MaxScore);

        // And it does not count towards what this Runner has got through.
        var runnerRow = await context.Runners.FirstAsync(r => r.Id == stored.RunnerId);
        Assert.Equal(0, runnerRow.CompletedJobs);
    }
    /// <summary>
    /// A Runner says what it awarded **and** what it awarded it out of, and both
    /// are read.
    /// <para>
    /// Every reader but the grade export assumed a scale of a hundred, so a
    /// Runner marking out of one — which is what a problem judged elsewhere does,
    /// since an external judge gives no partial information — had its accepted
    /// answer rescaled as `round(1 / 100 × 50)`. That is zero. The export
    /// meanwhile sent fifty, so a board and a gradebook showed two different
    /// truths about one submission.
    /// </para>
    /// <para>
    /// Found on 2026-08-16 by the second Runner implementation, which is what
    /// `PROJECT_CONTEXT.md` §32 asks for one for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_runner_marking_out_of_one_is_not_read_as_a_hundredth()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(3)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);

        var reported = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{Guid.Parse(job.GetProperty("jobId").GetString()!)}/report",
            new
            {
                leaseToken = job.GetProperty("leaseToken").GetString(),
                score = 1.0,
                maxScore = 1.0,
                verdict = "Accepted",
            });
        await Sign.Succeeded(reported);

        var read = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{submissionId}");

        Assert.Equal(50, read.GetProperty("score").GetDouble());
        Assert.Equal(50, read.GetProperty("maxScore").GetDouble());

        // And the whole of the scale is a solve, not a partial: the assignment's
        // point value must not decide what "solved" means.
        var rounds = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/series");
        var mine = rounds.EnumerateArray()
            .SelectMany(round => round.GetProperty("problems").EnumerateArray())
            .First(problem => problem.GetProperty("slug").GetString() == "A");
        Assert.Equal("solved", mine.GetProperty("status").GetString());
        Assert.Equal(50, mine.GetProperty("bestScore").GetDouble());
    }

}
