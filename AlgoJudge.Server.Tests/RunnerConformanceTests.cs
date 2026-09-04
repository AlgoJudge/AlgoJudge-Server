using AlgoJudge.Server.Authorization;
using System.Diagnostics;
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
[Collection("server-1")]
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
    /// <summary>
    /// **The first infrastructure failure is a reason to try again, not a
    /// verdict.** A Runner cannot tell a broken package from a torn download,
    /// or a broken host from a bad second — and ending a submission on the
    /// first of those made a hiccup permanent and a manual rejudge the only way
    /// back. §6.
    /// </summary>
    [Fact]
    public async Task An_infrastructure_failure_is_queued_again_rather_than_failed()
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

        // No result is named, because none was stored: a stored one is what
        // makes a repeat answer `duplicate`, and it would hand the next
        // Runner's honest work back to it as a duplicate of this failure.
        var answer = JsonDocument.Parse(await reported.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("queued", answer.GetProperty("state").GetString());
        Assert.False(answer.TryGetProperty("resultId", out var named) &&
                     named.ValueKind is not JsonValueKind.Null);

        await using var context = server.NewContext();
        var stored = await context.EvaluationJobs
            .Include(j => j.Result)
            .FirstAsync(j => j.Id == jobId);

        Assert.Equal(EvaluationJobState.Queued, stored.State);
        Assert.Null(stored.Result);
        Assert.Null(stored.RunnerId);
        Assert.Null(stored.FinishedAt);
        Assert.Contains("checksum", stored.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);

        // And it does not count towards what this Runner has got through.
        var runnerRow = await context.Runners.FirstAsync(r => r.Id == Guid.Parse(runner.Id));
        Assert.Equal(0, runnerRow.CompletedJobs);
    }

    /// <summary>
    /// **And it stops travelling once the deliveries run out.** Retrying for
    /// ever is how one bad package stops an installation, which is what the cap
    /// in §5 is for; the last reason given is the one recorded. §6.
    /// </summary>
    [Fact]
    public async Task An_infrastructure_failure_is_final_once_the_deliveries_run_out()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(3)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        Guid jobId = Guid.Empty;

        // Five deliveries, each one claimed and each one failing the same way.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var job = await runner.ClaimUntilAsync(submissionId);
            jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);

            var reported = await runner.Client.PostAsJsonAsync(
                $"/api/v1/runner/jobs/{jobId}/report",
                new
                {
                    leaseToken = job.GetProperty("leaseToken").GetString(),
                    infrastructureFailure = true,
                    failureReason = $"the sandbox would not start ({attempt})",
                });
            await Sign.Succeeded(reported);
        }

        await using var context = server.NewContext();
        var stored = await context.EvaluationJobs
            .Include(j => j.Result)
            .FirstAsync(j => j.Id == jobId);

        Assert.Equal(EvaluationJobState.Failed, stored.State);
        Assert.Contains("(5)", stored.FailureReason ?? "");
        Assert.NotNull(stored.FinishedAt);

        // A result row is written at the end — it is what makes a repeated
        // report idempotent — and it carries **no score**, so `Scoring.Best`
        // skips it and no board ever sees a zero that reads as a wrong answer.
        Assert.NotNull(stored.Result);
        Assert.Null(stored.Result!.Score);
        Assert.Null(stored.Result.MaxScore);
    }

    /// <summary>
    /// **A Runner may ask to be told rather than to ask again.** With
    /// <c>waitSeconds</c> the Server holds the request open, and a submission
    /// arriving while it waits is handed over without another round trip.
    /// <para>
    /// The assertion is the *latency*, not the answer: without the nudge this
    /// would still pass, five seconds later, when the wait ran out. So the job
    /// has to arrive in a fraction of the wait for the test to mean anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_waiting_runner_is_handed_a_job_the_moment_one_exists()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var runner = await Build.RunnerAsync(server);

        // Empty the queue first, so what this waits for is the submission below
        // and not something another test left behind.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var drained = await runner.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
            if (drained.StatusCode == HttpStatusCode.NoContent) break;
            await Sign.Succeeded(drained);
        }

        var started = Stopwatch.StartNew();
        var waiting = runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 30 });

        // Long enough that the request is certainly parked in the wait rather
        // than still being routed, and short enough to leave the assertion room.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await Build.SubmitAsync(participant, slug, "print(4)\n");

        var answer = await waiting;
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"it took {started.Elapsed} to be handed a job that existed after half a "
                + "second, so it waited the wait out rather than being told");
    }

    /// <summary>
    /// **A nudge is a broadcast; a job is not.** The only Runner waiting cannot
    /// judge the type, so it is woken and handed nothing — and the job is still
    /// there afterwards for one that can.
    /// <para>
    /// This is the property the whole arrangement rests on and the one a
    /// broadcast could plausibly have broken: <c>Wake</c> carries no information
    /// about *which* job became claimable, so every waiter looks, and what keeps
    /// a job away from a Runner that cannot judge it is the claim's own filter
    /// rather than anything the signal knows.
    /// </para>
    /// <para>
    /// **Written so there is no race to win.** The obvious shape — both Runners
    /// waiting, assert the capable one gets it — passes against a Server with no
    /// type filter at all, because the capable Runner takes the job first
    /// anyway and the other one then finds an empty queue. Measured: with
    /// <c>p."Type" = ANY(...)</c> defeated it still went green. Only one Runner
    /// waits here, and it is the wrong one.
    /// </para>
    /// <para>
    /// The second half matters as much as the first. A <c>204</c> alone is also
    /// what a Server that lost the submission would return; the capable
    /// Runner's <c>200</c> afterwards is what says the job was withheld rather
    /// than dropped.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_waiting_runner_is_not_handed_a_type_it_did_not_declare()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var capable = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);
        var other = await Build.RunnerAsync(server, problemTypes: ["output-only@1"]);
        await DrainAsync(capable, other);

        // The wrong Runner, and nobody else, is listening.
        var waiting = other.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 5 });
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // `standard-io@1`, which is what `Build.ActivityAsync` publishes.
        await Build.SubmitAsync(participant, slug, "print(6)\n");

        var began = Stopwatch.StartNew();
        var refused = await waiting;
        Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);

        // It held the claim open to its deadline rather than answering at once,
        // so the empty answer is one the wait path produced.
        Assert.True(
            began.Elapsed >= TimeSpan.FromSeconds(3),
            $"the claim came back after {began.Elapsed.TotalSeconds:0.0}s, so it never waited");

        // Withheld, not lost.
        var taken = await capable.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    /// <summary>
    /// **A rejudge takes the attempt it replaces out of the queue.**
    /// <para>
    /// Nothing stopped a submission from having two claimable jobs, and
    /// <c>SKIP LOCKED</c> then correctly handed them to two Runners — the same
    /// source judged twice, and on an External Runner the same solution
    /// submitted twice to somebody else's site. The verdict survived it, because
    /// scoring reads the highest attempt, which is why this was invisible.
    /// </para>
    /// <para>
    /// The second claim's <c>204</c> is the assertion that matters. Checking the
    /// stored state alone would pass against a Server that marked the row and
    /// still handed it out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rejudge_supersedes_an_attempt_nobody_has_taken()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var runner = await Build.RunnerAsync(server);
        await DrainAsync(runner);

        var submitted = await Build.SubmitAsync(participant, slug, "print(11)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(
            await admin.PostAsync($"/api/v1/submissions/{submissionId}/rejudge", null));

        var detail = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/submissions/{submissionId}");
        var attempts = detail.GetProperty("attemptList").EnumerateArray().ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("queued", attempts[0].GetProperty("state").GetString());
        Assert.Equal("superseded", attempts[1].GetProperty("state").GetString());

        var taken = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);

        var second = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    /// <summary>
    /// **A rejudge does not take a job away from a Runner that is judging it —
    /// it waits behind.**
    /// <para>
    /// The other half, and the reason the fix is in two parts. A queued sibling
    /// can simply be superseded, because a queued job is by construction one
    /// nobody holds. A running one cannot: the sandboxing Runner's keeper never
    /// aborts an evaluation, so cancelling it would mean a sandbox finishing and
    /// discarding its work, and on an External Runner a real submission already
    /// spent on somebody else's account. So the *new* attempt waits, on the
    /// claim's own filter.
    /// </para>
    /// <para>
    /// And it must not wait for ever, which is the last clause: finishing the
    /// running attempt is what releases it, and that nudge had to be added —
    /// a completion used not to be work for anybody.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rejudge_waits_behind_the_attempt_a_runner_is_judging()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(12)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var holder = await Build.RunnerAsync(server);
        var job = await holder.ClaimUntilAsync(submissionId);
        var jobId = job.GetProperty("jobId").GetString()!;

        var waiter = await Build.RunnerAsync(server);
        await DrainAsync(waiter);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(
            await admin.PostAsync($"/api/v1/submissions/{submissionId}/rejudge", null));

        var detail = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/submissions/{submissionId}");
        var attempts = detail.GetProperty("attemptList").EnumerateArray().ToList();
        Assert.Equal("queued", attempts[0].GetProperty("state").GetString());
        // Untouched: it is being judged, and nothing here may discard that.
        Assert.Equal("running", attempts[1].GetProperty("state").GetString());

        var waited = await waiter.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.NoContent, waited.StatusCode);

        await Sign.Succeeded(await holder.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/report",
            new
            {
                leaseToken = job.GetProperty("leaseToken").GetString(),
                verdict = "accepted",
                score = 100,
                maxScore = 100,
            }));

        var released = await waiter.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
    }

    /// <summary>
    /// **A delivery nobody was ever heard about is given back.**
    /// <para>
    /// The claim is committed — running, leased, one of five attempts spent —
    /// before the answer to it is written, so an answer lost in between leaves a
    /// job the Runner cannot release because it never learned the lease token.
    /// The reaper is what recovers it, and until now it charged the participant
    /// for a handover that demonstrably did not happen. Five of those over a
    /// contest end with the submission failed and a message blaming the package.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_claim_the_runner_never_acknowledged_costs_no_delivery()
    {
        var (jobId, _, _) = await ClaimedAsync("print(13)\n");

        Assert.Equal(1, await DeliveriesAsync(jobId));
        await ExpireLeaseAsync(jobId);
        await SweepAsync();

        Assert.Equal(EvaluationJobState.Queued, await StateAsync(jobId));
        Assert.Equal(0, await DeliveriesAsync(jobId));
    }

    /// <summary>
    /// **And one that was acknowledged still costs its delivery.**
    /// <para>
    /// The twin, and it is not the same test twice: without it the refund could
    /// swallow every reclaim, which is exactly the case the delivery cap exists
    /// for — a job that kills every Runner it reaches would then be retried for
    /// ever. One renewal is enough to have been heard from.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_claim_that_was_acknowledged_still_costs_its_delivery()
    {
        var (jobId, runner, leaseToken) = await ClaimedAsync("print(14)\n");

        await Sign.Succeeded(await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/lease", new { leaseToken, leaseSeconds = 60 }));

        await ExpireLeaseAsync(jobId);
        await SweepAsync();

        Assert.Equal(EvaluationJobState.Queued, await StateAsync(jobId));
        Assert.Equal(1, await DeliveriesAsync(jobId));
    }

    /// <summary>
    /// **A key the Server already knows may not be re-declared by whoever can
    /// read it.**
    /// <para>
    /// Registration is anonymous because a Runner nobody has approved has no
    /// session to present. For a fingerprint the Server already has, that meant
    /// anyone could rewrite the row: the problem types and the external flag the
    /// claim pairs work on, and the name, product, version and host facts a
    /// manager reads when deciding whether to approve it — with the approval
    /// left exactly where it was. A public key is public by construction, and
    /// the panel hands it to anyone who may list Runners.
    /// </para>
    /// <para>
    /// **The claim is where this is checked, not the stored row.** A Server that
    /// answered the refusal and wrote the fields anyway would pass a row
    /// assertion written the other way round; what has to still be true is which
    /// work this Runner is handed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unsigned_re_registration_is_refused_and_the_queue_does_not_follow_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var runner = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);
        await DrainAsync(runner);

        var refused = await server.CreateClient().PostAsJsonAsync("/api/v1/runner/register", new
        {
            name = "impostor",
            product = "AlgoJudge-Runner-Stub",
            version = "6.6.6",
            publicKey = runner.PublicKey,
            // Both halves of the claim's pairing, rewritten.
            problemTypes = new[] { "output-only@1" },
            external = true,
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runner.nonce.unknown", problem.GetProperty("code").GetString());

        // The row stands, and this is the assertion that means it.
        await Build.SubmitAsync(participant, slug, "print(15)\n");
        var taken = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    /// <summary>
    /// **And a signed one still moves the queue**, because a restart reporting a
    /// changed configuration is what this endpoint is for.
    /// <para>
    /// The half that keeps the refusal above from being a fleet that cannot
    /// restart: an operator who edits <c>AJ_Runner__ProblemTypes</c> and
    /// restarts still gets what they asked for, having proved the private key is
    /// on the machine that is asking.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_signed_re_registration_changes_what_the_queue_hands_over()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        // Cannot judge what this activity publishes, to begin with.
        var runner = await Build.RunnerAsync(server, problemTypes: ["output-only@1"]);
        await Build.SubmitAsync(participant, slug, "print(16)\n");

        var nothing = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode);

        var (nonce, signature) = await runner.ProofAsync();
        await Build.PostAsync(server.CreateClient(), "/api/v1/runner/register", new
        {
            name = "stub",
            product = "AlgoJudge-Runner-Stub",
            version = "0.0.1",
            publicKey = runner.PublicKey,
            problemTypes = new[] { "standard-io@1" },
            nonce,
            signature,
        });

        var taken = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    /// <summary>
    /// **A nonce issued to another key does not travel.**
    /// <para>
    /// <c>auth/challenge</c> is anonymous and hands a nonce to anyone who names
    /// a fingerprint, so "the nonce exists" says nothing about who is presenting
    /// it. The check that makes it evidence lived only on the handshake until
    /// this endpoint started accepting one too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_nonce_issued_to_another_key_is_refused()
    {
        var runner = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);
        var other = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);

        // Somebody else's nonce, and this key's signature over it.
        var (nonce, _) = await other.ProofAsync();
        var (_, signature) = await runner.ProofAsync();

        var refused = await server.CreateClient().PostAsJsonAsync("/api/v1/runner/register", new
        {
            name = "stub",
            product = "AlgoJudge-Runner-Stub",
            version = "0.0.1",
            publicKey = runner.PublicKey,
            problemTypes = new[] { "output-only@1" },
            nonce,
            signature,
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runner.nonce.mismatch", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// Empties the queue of whatever the rest of the collection left behind, so
    /// a test that counts what one submission produces is counting that.
    /// </summary>
    private static async Task DrainAsync(params StubRunner[] runners)
    {
        foreach (var runner in runners)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var drained = await runner.Client.PostAsJsonAsync(
                    "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
                if (drained.StatusCode == HttpStatusCode.NoContent) break;
                await Sign.Succeeded(drained);
            }
        }
    }

    /// <summary>
    /// **One job, two Runners that both want it, and exactly one gets it.**
    /// <para>
    /// <c>FOR UPDATE OF j SKIP LOCKED LIMIT 1</c> is the whole mechanism, and it
    /// was true before a nudge existed. What is new is that both Runners now
    /// look at the *same instant* rather than whenever their own backoff
    /// happened to expire — so the two claims race in a way they previously
    /// could only do by coincidence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_submission_reaches_exactly_one_of_two_identical_runners()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var first = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);
        var second = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);

        await DrainAsync(first, second);

        var waiting = new[]
        {
            first.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 6 }),
            second.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 6 }),
        };
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await Build.SubmitAsync(participant, slug, "print(7)\n");

        var answers = await Task.WhenAll(waiting);
        var handed = answers.Count(a => a.StatusCode == HttpStatusCode.OK);
        var empty = answers.Count(a => a.StatusCode == HttpStatusCode.NoContent);

        // Reported together, because which of the two is wrong is the whole
        // diagnosis: two `200`s is a job judged twice, two `204`s is a job
        // nobody was given, and anything else is a claim that failed outright.
        var seen = string.Join(", ", answers.Select(a => a.StatusCode.ToString()));
        Assert.True(handed == 1 && empty == 1, $"the two claims answered {seen}");
    }

    /// <summary>
    /// **Rejudging one submission ends a held claim, as rejudging many always
    /// did.** Of the six places that queue work, this was the only one that
    /// queued it silently — and it is the one a manager uses, since a corrected
    /// package is tried on a single entry before a whole round.
    /// <para>
    /// The assertion is latency, like its neighbours: without the nudge the job
    /// is still handed over, twenty seconds later, when the wait runs out. A
    /// test that only checked the <c>200</c> would pass against the defect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rejudge_of_one_submission_ends_a_held_claim()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(8)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        // Taken by one Runner **and finished**, so the queue is empty when the
        // other waits and nothing of this submission is still running — a
        // rejudge queued behind a running attempt waits for it by design, and
        // this test is about the nudge rather than about that rule.
        var holder = await Build.RunnerAsync(server);
        var job = await holder.ClaimUntilAsync(submissionId);
        await Sign.Succeeded(await holder.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{job.GetProperty("jobId").GetString()}/report",
            new
            {
                leaseToken = job.GetProperty("leaseToken").GetString(),
                verdict = "accepted",
                score = 100,
                maxScore = 100,
            }));

        var waiter = await Build.RunnerAsync(server);
        await DrainAsync(waiter);

        var started = Stopwatch.StartNew();
        var waiting = waiter.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 20 });
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(
            await admin.PostAsync($"/api/v1/submissions/{submissionId}/rejudge", null));

        var answer = await waiting;
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"the rejudge took {started.Elapsed} to reach a held claim, so it waited the "
                + "wait out rather than being told");
    }

    /// <summary>
    /// **Reopening after a drain releases the backlog to Runners that are
    /// already listening.**
    /// <para>
    /// A drain is the one state where work reliably piles up: <c>ClaimAsync</c>
    /// answers everybody with nothing while report and release keep handing jobs
    /// back, so the queue grows behind a door every Runner has already found
    /// shut. They are all inside held claims. Without a nudge on the way out,
    /// the whole backlog waits out a deadline after the Server is open again —
    /// on the path an operator is watching, having just reopened it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reopening_after_a_drain_ends_a_held_claim()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(9)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var holder = await Build.RunnerAsync(server);
        var job = await holder.ClaimUntilAsync(submissionId);
        var jobId = job.GetProperty("jobId").GetString()!;

        var waiter = await Build.RunnerAsync(server);
        await DrainAsync(waiter);

        var operators = server.CreateClient();
        operators.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        var switched = await operators.PostAsJsonAsync(
            "/api/v1/admin/maintenance", new { on = true, reason = "a backup" });
        Assert.True(switched.IsSuccessStatusCode, await switched.Content.ReadAsStringAsync());

        try
        {
            // The backlog: handed back while the door is shut, so it stays
            // queued and nobody may take it. Submitting instead would not work —
            // the gate refuses that during a drain, which is the point of one.
            var released = await holder.Client.PostAsJsonAsync(
                $"/api/v1/runner/jobs/{jobId}/release",
                new { leaseToken = job.GetProperty("leaseToken").GetString() });
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

            var started = Stopwatch.StartNew();
            var waiting = waiter.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 20 });
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            await Sign.Succeeded(await operators.PostAsJsonAsync(
                "/api/v1/admin/maintenance", new { on = false, reason = (string?)null }));

            var answer = await waiting;
            started.Stop();

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.True(
                started.Elapsed < TimeSpan.FromSeconds(10),
                $"reopening took {started.Elapsed} to reach a held claim, so the backlog "
                    + "waited a deadline out after the Server was open again");
        }
        finally
        {
            await operators.PostAsJsonAsync(
                "/api/v1/admin/maintenance", new { on = false, reason = (string?)null });
        }
    }

    /// <summary>
    /// **Retagging a Runner into a pool that has work is a delivery.**
    /// <para>
    /// Both sides of the tag comparison are read at claim time rather than
    /// stamped on the job, so this Runner matches the queued work the instant
    /// the row is written — and would still have sat there until its own
    /// deadline, because a held claim looks again only when something tells it
    /// to. The specification advertises that retagging redirects work already
    /// waiting; without the nudge that is true only eventually.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retagging_a_runner_into_a_pool_ends_its_held_claim()
    {
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);
        var participant = await Build.ParticipantAsync(server, slug);

        // Untagged, so it is in the general pool and this work is not its.
        var waiter = await Build.RunnerAsync(server);
        await DrainAsync(waiter);

        await Build.SubmitAsync(participant, slug, "print(10)\n");

        var started = Stopwatch.StartNew();
        var waiting = waiter.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 20 });
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/runners/{waiter.Id}/tags", new { tags = new[] { "lab-a" } }));

        var answer = await waiting;
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"the retag took {started.Elapsed} to reach a held claim, so the Runner waited "
                + "its deadline out on a queue it already matched");
    }

    /// <summary>
    /// **A drain begun while a claim is held is seen by that claim.**
    /// <para>
    /// A request scope carries one <c>DbContext</c>, and a tracking query hands
    /// back the instance already in its change tracker rather than the row as it
    /// now stands. So a claim held across the switch read a snapshot taken
    /// before it — and could hand out a job **after** the drainer had watched
    /// the queue go quiet and closed the door, which is the one guarantee a
    /// drain offers an operator taking a backup.
    /// </para>
    /// <para>
    /// **The queue is refilled by a release rather than a submission**, because
    /// a draining Server refuses a participant's write — so the only way to put
    /// work back after the switch is a Runner handing a job back, which is
    /// allowed and is also what a fleet being restarted for that very
    /// maintenance does.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_claim_held_across_a_drain_hands_out_nothing()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(5)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        // One Runner takes the job, so the queue is empty when the other starts
        // waiting — and holds something it can give back later.
        var holder = await Build.RunnerAsync(server);
        var job = await holder.ClaimUntilAsync(submissionId);
        var jobId = job.GetProperty("jobId").GetString()!;

        var waiter = await Build.RunnerAsync(server);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var drained = await waiter.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
            if (drained.StatusCode == HttpStatusCode.NoContent) break;
            await Sign.Succeeded(drained);
        }

        var waiting = waiter.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 8 });
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var operators = server.CreateClient();
        operators.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        var switched = await operators.PostAsJsonAsync(
            "/api/v1/admin/maintenance", new { on = true, reason = "a backup" });
        Assert.True(switched.IsSuccessStatusCode, await switched.Content.ReadAsStringAsync());

        try
        {
            // Back on the queue, and the nudge wakes the held claim — which must
            // now find a Server that is draining rather than the one it saw when
            // the request began.
            var released = await holder.Client.PostAsJsonAsync(
                $"/api/v1/runner/jobs/{jobId}/release",
                new { leaseToken = job.GetProperty("leaseToken").GetString() });
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

            var answer = await waiting;
            Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        }
        finally
        {
            await operators.PostAsJsonAsync(
                "/api/v1/admin/maintenance", new { on = false, reason = (string?)null });
        }
    }

    /// <summary>
    /// **And an empty queue still answers 204, at the deadline and not before.**
    /// A Runner that asked to wait and was given nothing must not be told so
    /// immediately — that would turn the wait into a busy loop — nor held past
    /// what it asked for, which is what its own request timeout is set against.
    /// </summary>
    [Fact]
    public async Task A_waiting_runner_is_told_nothing_matched_when_the_wait_runs_out()
    {
        var runner = await Build.RunnerAsync(server);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var drained = await runner.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
            if (drained.StatusCode == HttpStatusCode.NoContent) break;
            await Sign.Succeeded(drained);
        }

        var started = Stopwatch.StartNew();
        var answer = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60, waitSeconds = 2 });
        started.Stop();

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        // The jitter takes a sixteenth off the top, so the floor is below two
        // seconds by that much and a little more for the clock.
        Assert.True(
            started.Elapsed > TimeSpan.FromMilliseconds(1500),
            $"it answered after {started.Elapsed}, which is not waiting");
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"it answered after {started.Elapsed}, which is past what it asked for");
    }

    /// <summary>
    /// **Asking for nothing is what every Runner did before this existed**, and
    /// it must still answer at once. The conformance sequence in §11 depends on
    /// it: every other case here claims without a wait and expects an immediate
    /// 204 on an empty queue.
    /// </summary>
    [Fact]
    public async Task A_claim_that_asks_for_no_wait_still_answers_at_once()
    {
        var runner = await Build.RunnerAsync(server);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var drained = await runner.Client.PostAsJsonAsync(
                "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
            if (drained.StatusCode == HttpStatusCode.NoContent) break;
            await Sign.Succeeded(drained);
        }

        var started = Stopwatch.StartNew();
        var answer = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        started.Stop();

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(5),
            $"an unasked-for wait of {started.Elapsed} appeared from somewhere");
    }

    /// <summary>
    /// **A Runner being stopped gives the job back, and it costs the
    /// participant nothing.** The job is queued again at once instead of
    /// waiting out a lease nobody is going to miss, and the delivery the claim
    /// counted is given back — an operator restarting a fleet must not spend a
    /// submission's attempts. §5.1.
    /// </summary>
    [Fact]
    public async Task A_released_job_is_queued_again_at_once_and_costs_no_delivery()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(3)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);
        var jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);

        var released = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/release",
            new { leaseToken = job.GetProperty("leaseToken").GetString() });
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

        await using (var context = server.NewContext())
        {
            var stored = await context.EvaluationJobs
                .Include(j => j.Result)
                .FirstAsync(j => j.Id == jobId);

            Assert.Equal(EvaluationJobState.Queued, stored.State);
            Assert.Null(stored.Result);
            Assert.Null(stored.LeaseToken);
            Assert.Null(stored.RunnerId);
            // The claim counted one and the release gave it back.
            Assert.Equal(0, stored.Deliveries);
            Assert.Equal(1, stored.Releases);
        }

        // And it is there to be taken: the same job, by whoever asks next.
        var again = await runner.ClaimUntilAsync(submissionId);
        Assert.Equal(jobId, Guid.Parse(again.GetProperty("jobId").GetString()!));
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

    /// <summary>Claims one job and answers what a Runner would then hold.</summary>
    private async Task<(Guid JobId, StubRunner Runner, string LeaseToken)> ClaimedAsync(string source)
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, source);

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        return (
            Guid.Parse(job.GetProperty("jobId").GetString()!),
            runner,
            job.GetProperty("leaseToken").GetString()!);
    }

    private async Task<int> DeliveriesAsync(Guid jobId)
    {
        using var scope = server.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await context.EvaluationJobs.Where(j => j.Id == jobId)
            .Select(j => j.Deliveries).FirstAsync();
    }

    /// <summary>Moves the lease into the past, which is the reaper's only input.</summary>
    private async Task ExpireLeaseAsync(Guid jobId)
    {
        using var scope = server.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.EvaluationJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                j => j.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
    }

    private async Task<EvaluationJobState> StateAsync(Guid jobId)
    {
        using var scope = server.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await context.EvaluationJobs.Where(j => j.Id == jobId)
            .Select(j => j.State).FirstAsync();
    }

    /// <summary>
    /// One pass of the reaper.
    /// <para>
    /// **What it answers is deliberately not asserted.** The reaper reclaims
    /// every expired lease in the installation, and the collections in this
    /// suite run against one database at the same time — so the count is
    /// whatever the rest of the suite happened to leave, and a test that
    /// insisted on one passed alone and failed in the run that matters.
    /// </para>
    /// </summary>
    private Task<int> SweepAsync() =>
        server.Services.GetRequiredService<LeaseReaper>().SweepAsync(default);
}
