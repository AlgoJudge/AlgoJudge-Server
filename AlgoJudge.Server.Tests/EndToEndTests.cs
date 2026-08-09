using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The path the whole milestone exists for: a manager creates an activity and a
/// problem, a participant submits, the Server queues a job, a stub Runner claims
/// it and reports — idempotently — and the submission ends up carrying a verdict.
/// </summary>
[Collection("server")]
public class EndToEndTests(ServerFixture server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Health_answers_only_under_the_api_path()
    {
        var client = server.CreateClient();

        var underPrefix = await client.GetAsync("/api/v1/health");
        var atRoot = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, underPrefix.StatusCode);
        // UsePathBase only strips the prefix; it does not require it. This is
        // the guard that makes the fixed prefix mean something.
        Assert.Equal(HttpStatusCode.NotFound, atRoot.StatusCode);
    }

    [Fact]
    public async Task A_failure_is_problem_json_with_a_code()
    {
        var client = server.CreateClient();

        var response = await client.GetAsync("/api/v1/nope");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_instance_is_readable_without_signing_in()
    {
        var client = server.CreateClient();

        var instance = await client.GetFromJsonAsync<JsonElement>("/api/v1/instance");

        // Shipped off: accounts are created by an organiser or arrive by SSO.
        Assert.False(instance.GetProperty("localRegistrationEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Array, instance.GetProperty("documents").ValueKind);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_the_account()
    {
        var client = server.CreateClient();
        var response = await client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The whole milestone, on an activity this test builds for itself.
    /// <para>
    /// Its own activity rather than the seeded one, for two reasons. It makes the
    /// test independent of what every other test in the collection has submitted
    /// — they share a database — and it exercises the half of the stated
    /// criterion the seed would otherwise skip: <b>a manager creates an activity
    /// and a problem</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manager_builds_an_activity_a_participant_submits_and_a_runner_judges_it()
    {
        var slug = "E2E-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var admin = await SignInAsync(Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // ── a manager builds it ──────────────────────────────────────────────
        var activity = await Post(admin, "/api/v1/activities", new
        {
            slug,
            name = "End to end",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
            languages = new[] { "python" },
            attachmentVisibility = new[] { new { name = "source", visibility = "participant" } },
        });
        Assert.Equal(slug, activity.GetProperty("slug").GetString());

        var round = await Post(admin, $"/api/v1/activities/{slug}/series", new
        {
            slug = "r1",
            name = "Round 1",
            startDate = DateTime.UtcNow.AddHours(-1).ToString("O"),
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
        });
        var roundId = round.GetProperty("id").GetString()!;

        var problem = await Post(admin, "/api/v1/problems", new
        {
            slug = "sum-" + slug.ToLowerInvariant(),
            name = "Sum",
            type = "standard-io@1",
        });
        var problemId = problem.GetProperty("id").GetString()!;

        // The statement and the package are uploaded first and published by
        // reference — a version is not built up after the fact.
        var statement = await UploadAsync(admin, "content.md", "# Sum\n\nAdd them.\n");
        var package = await UploadAsync(admin, "package.zip", "pretend-archive");

        var version = await Post(admin, $"/api/v1/problems/{problemId}/versions", new
        {
            note = "First",
            statements = new[] { new { fileId = statement } },
            config = new { format = "standard-io", version = 1, limits = new { timeMs = 2000, memoryBytes = 268435456 } },
            package = new { fileId = package },
        });
        Assert.Equal(1, version.GetProperty("version").GetInt32());
        Assert.True(version.GetProperty("hasPackage").GetBoolean());

        // Attaching pins the current version and says what it is worth here.
        var attached = await Post(admin, $"/api/v1/series/{roundId}/problems", new
        {
            problemId,
            slug = "A",
            maxPoints = 50,
        });
        var assignment = Assert.Single(attached.GetProperty("problems").EnumerateArray().ToList());
        Assert.Equal(version.GetProperty("id").GetString(), assignment.GetProperty("pinnedProblemVersionId").GetString());

        // The round is shut until the scheduler opens it, so this test opens it
        // the way the scheduler will.
        await using (var context = server.NewContext())
        {
            var series = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            series.IsOpen = true;
            series.StartAnnouncedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // ── a participant joins and submits ──────────────────────────────────
        var participant = await Sign.NewAccountAsync(server, "e2e-" + slug.ToLowerInvariant());
        var joined = await participant.PostAsJsonAsync($"/api/v1/activities/{slug}/enrolment", new { });
        await Sign.Succeeded(joined);

        var series2 = await participant.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/series");
        var visible = Assert.Single(series2.EnumerateArray().ToList());
        Assert.True(visible.GetProperty("isOpen").GetBoolean());
        var column = Assert.Single(visible.GetProperty("problems").EnumerateArray().ToList());
        Assert.Equal("untouched", column.GetProperty("status").GetString());

        const string source = "print(sum(map(int, input().split())))\n";
        var submitted = await SubmitToAsync(participant, slug, "python", source);
        var submissionId = submitted.GetProperty("id").GetString()!;
        Assert.Equal("queued", submitted.GetProperty("state").GetString());
        // Nothing has judged it, so there is no score — and absent is not zero.
        // Absent, not null: an unjudged submission has no score, and the contract
        // says a value that is not there is left out rather than written as
        // `null` — which the Client's `!== undefined` guards would let through.
        Assert.False(submitted.TryGetProperty("score", out _));

        // ── a Runner registers, is approved, and takes this job ─────────────
        var runner = await RegisterRunnerAsync();
        var job = await runner.ClaimUntilAsync(submissionId);

        Assert.Equal("standard-io@1", job.GetProperty("problemType").GetString());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Contains(
            job.GetProperty("files").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "source");
        // The merged configuration chain, which the Server carried without
        // reading either layer.
        Assert.Equal(2000, job.GetProperty("config").GetProperty("limits").GetProperty("timeMs").GetInt32());

        var running = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{submissionId}");
        Assert.Equal("running", running.GetProperty("state").GetString());

        // ── it reports, and reporting again changes nothing ─────────────────
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        var accepted = await runner.ReportAsync(jobId, leaseToken, score: 80, verdict: "Accepted");
        Assert.False(accepted.GetProperty("duplicate").GetBoolean());

        var repeat = await runner.ReportAsync(jobId, leaseToken, score: 80, verdict: "Accepted");
        Assert.True(repeat.GetProperty("duplicate").GetBoolean());
        Assert.Equal(
            accepted.GetProperty("resultId").GetString(),
            repeat.GetProperty("resultId").GetString());

        await using (var context = server.NewContext())
        {
            var forThisJob = await context.Results.CountAsync(r => r.EvaluationJobId == Guid.Parse(jobId));
            Assert.Equal(1, forThisJob);
        }

        // ── and the participant sees a verdict in this round's scale ────────
        var finished = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{submissionId}");

        Assert.Equal("completed", finished.GetProperty("state").GetString());
        Assert.Equal("Accepted", finished.GetProperty("verdict").GetString());
        // The Runner marked 80 of 100; the assignment is worth 50 here.
        Assert.Equal(40, finished.GetProperty("score").GetDouble());
        Assert.Equal(50, finished.GetProperty("maxScore").GetDouble());
    }

    [Fact]
    public async Task A_second_runner_finds_nothing_once_the_queue_is_empty()
    {
        var runner = await RegisterRunnerAsync();

        // Drain whatever other tests left, then ask once more.
        while ((await runner.TryClaimAsync()) is not null) { }

        var response = await runner.RawClaimAsync();
        // Nothing to do is a normal state, not an error.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_unapproved_runner_gets_no_token()
    {
        var client = server.CreateClient();
        var (publicKey, _) = NewKeyPair();

        var registered = await Post(client, "/api/v1/runner/register", new
        {
            name = "unapproved",
            publicKey,
            problemTypes = new[] { "standard-io@1" },
        });
        Assert.Equal("pendingApproval", registered.GetProperty("state").GetString());

        var fingerprint = registered.GetProperty("fingerprint").GetString()!;
        var challenge = await Post(client, "/api/v1/runner/auth/challenge", new { fingerprint });

        var response = await client.PostAsJsonAsync("/api/v1/runner/auth/token", new
        {
            fingerprint,
            nonce = challenge.GetProperty("nonce").GetString(),
            signature = "not-a-signature",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runner.notApproved", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_nonce_cannot_be_spent_twice()
    {
        var runner = await RegisterRunnerAsync();
        var (nonce, signature) = await runner.SignChallengeAsync();

        var first = await runner.RedeemAsync(nonce, signature);
        var second = await runner.RedeemAsync(nonce, signature);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // Single use, taken out of the table before it is checked — so even the
        // Runner that made the signature cannot replay it.
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task A_language_the_activity_does_not_accept_is_refused()
    {
        var participant = await SignInAsync(Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

        var content = Multipart("rust", "fn main() {}\n");
        var response = await participant.PostAsync(
            "/api/v1/activities/DEV-2026/problems/A/submissions", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("submission.language", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_file_whose_checksum_does_not_match_is_not_stored()
    {
        var manager = await SignInAsync(Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent("hello"u8.ToArray()), "file", "hello.txt" },
            { new StringContent(new string('0', 64)), "sha256" },
        };

        var response = await manager.PostAsync("/api/v1/files", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("checksum_mismatch", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// `MapIdentityApi` maps `/register` unconditionally, so an installation
    /// that declares registration closed accepted registrations anyway — and the
    /// account could then sign in. The setting has to be a rule, not a label.
    /// </summary>
    [Fact]
    public async Task Registration_is_refused_when_the_instance_says_it_is_closed()
    {
        var client = server.CreateClient();

        var instance = await client.GetFromJsonAsync<JsonElement>("/api/v1/instance");
        Assert.False(instance.GetProperty("localRegistrationEnabled").GetBoolean());

        var response = await client.PostAsJsonAsync("/api/v1/identity/register", new
        {
            email = "intruder@example.invalid",
            password = "twelve-characters-at-least",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("registration.closed", problem.GetProperty("code").GetString());

        // And nothing was created, so nothing can sign in with it.
        var signIn = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true",
            new { email = "intruder@example.invalid", password = "twelve-characters-at-least" });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    /// <summary>
    /// There is no mail sender in v1, so there is no password reset. An endpoint
    /// that exists and cannot work invites a screen to promise something nothing
    /// will deliver.
    /// </summary>
    [Theory]
    [InlineData("forgotPassword")]
    [InlineData("resetPassword")]
    [InlineData("resendConfirmationEmail")]
    public async Task Everything_that_needs_mail_is_refused(string endpoint)
    {
        var client = server.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/identity/{endpoint}", new { email = "someone@example.invalid" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mail.unavailable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_participant_cannot_read_the_manager_view()
    {
        var participant = await SignInAsync(Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);
        var response = await participant.GetAsync("/api/v1/manager/activities/DEV-2026");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_bypasses_every_check()
    {
        var admin = await SignInAsync(Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // The administrator holds no activity grant at all, and still reads it:
        // system:administrator is checked first and bypasses everything.
        var activity = await admin.GetFromJsonAsync<JsonElement>("/api/v1/manager/activities/DEV-2026");
        Assert.Equal("DEV-2026", activity.GetProperty("slug").GetString());

        // Staff are excluded from the participant count — read from the grants,
        // never stored. Compared against the grants rather than a fixed number,
        // because other tests in this collection enrol accounts of their own.
        await using var context = server.NewContext();
        var expected = await context.Grants.CountAsync(g =>
            g.Activity!.Slug == "DEV-2026" && !g.IsSystem && g.State == GrantState.Active);

        Assert.Equal(expected, activity.GetProperty("participantCount").GetInt32());
        Assert.True(expected >= 1);

        // And the manager grant the seed created is systemic, so it is not in it.
        var systemic = await context.Grants.CountAsync(g =>
            g.Activity!.Slug == "DEV-2026" && g.IsSystem && g.State == GrantState.Active);
        Assert.True(systemic >= 1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpClient> SignInAsync(string login, string password)
    {
        var client = server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true", new { email = login, password });
        await Succeeded(response);
        return client;
    }

    private static MultipartFormDataContent Multipart(string language, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new MultipartFormDataContent
        {
            { new StringContent(language), "language" },
            { new StringContent(source), "code" },
            { new StringContent(checksum), "sha256" },
        };
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient client, string language, string source) =>
        await SubmitToAsync(client, "DEV-2026", language, source);

    private static async Task<JsonElement> SubmitToAsync(
        HttpClient client, string activitySlug, string language, string source)
    {
        using var content = Multipart(language, source);
        var response = await client.PostAsync(
            $"/api/v1/activities/{activitySlug}/problems/A/submissions", content);
        await Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Uploads bytes and answers with the stored file's id.</summary>
    private static async Task<string> UploadAsync(HttpClient client, string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", name },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync("/api/v1/files", content);
        await Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return stored.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> Post(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        await Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Fails with the body, not just the status.
    /// <para>
    /// `EnsureSuccessStatusCode` says "500" and nothing else, which for a
    /// problem+json API throws away the one thing that would have identified the
    /// fault. Every assertion of success in this suite goes through here.
    /// </para>
    /// </summary>
    internal static async Task Succeeded(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw new XunitException(
            $"{(int)response.StatusCode} {response.ReasonPhrase} from "
            + $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}\n{body}");
    }

    private static (string PublicKey, Ed25519PrivateKeyParameters Private) NewKeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        return (Convert.ToBase64String(pub.GetEncoded()), priv);
    }

    private async Task<StubRunner> RegisterRunnerAsync()
    {
        var anonymous = server.CreateClient();
        var (publicKey, priv) = NewKeyPair();

        var registered = await Post(anonymous, "/api/v1/runner/register", new
        {
            name = "stub",
            product = "AlgoJudge-Runner-Stub",
            version = "0.0.1",
            publicKey,
            problemTypes = new[] { "standard-io@1" },
            machine = new { os = "linux", cores = 2 },
        });

        var fingerprint = registered.GetProperty("fingerprint").GetString()!;

        // Approval is an administrator's act, and nothing is evaluated before it.
        var admin = await SignInAsync(Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var runnerId = registered.GetProperty("runnerId").GetString()!;
        var approved = await admin.PostAsync($"/api/v1/runners/{runnerId}/approve", null);
        await Succeeded(approved);

        var stub = new StubRunner(server.CreateClient(), fingerprint, priv);
        await stub.AuthenticateAsync();
        return stub;
    }

    /// <summary>
    /// A Runner with no sandbox: it does the handshake, claims and reports. That
    /// is the whole Server-facing contract, and none of it needs to run code.
    /// </summary>
    private sealed class StubRunner(HttpClient client, string fingerprint, Ed25519PrivateKeyParameters key)
    {
        public async Task<(string Nonce, string Signature)> SignChallengeAsync()
        {
            var challenge = await Post(client, "/api/v1/runner/auth/challenge", new { fingerprint });
            var nonce = challenge.GetProperty("nonce").GetString()!;

            var signer = new Ed25519Signer();
            signer.Init(true, key);
            var message = Encoding.UTF8.GetBytes(nonce);
            signer.BlockUpdate(message, 0, message.Length);
            return (nonce, Convert.ToBase64String(signer.GenerateSignature()));
        }

        public Task<HttpResponseMessage> RedeemAsync(string nonce, string signature) =>
            client.PostAsJsonAsync("/api/v1/runner/auth/token", new { fingerprint, nonce, signature });

        public async Task AuthenticateAsync()
        {
            var (nonce, signature) = await SignChallengeAsync();
            var response = await RedeemAsync(nonce, signature);
            await Succeeded(response);

            var token = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("token").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public Task<HttpResponseMessage> RawClaimAsync() =>
            client.PostAsJsonAsync("/api/v1/runner/jobs/claim", new { leaseSeconds = 120 });

        public async Task<JsonElement?> TryClaimAsync()
        {
            var response = await RawClaimAsync();
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            await Succeeded(response);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async Task<JsonElement> ClaimAsync() =>
            await TryClaimAsync() ?? throw new InvalidOperationException("Expected a job to claim");

        /// <summary>
        /// Claims until this submission's job comes up.
        /// <para>
        /// The queue is shared with every other test in the collection, and a
        /// Runner takes whatever is oldest. Claiming past other people's work is
        /// what a real Runner does; taking the first job and asserting it is ours
        /// is what makes a suite order-dependent.
        /// </para>
        /// </summary>
        public async Task<JsonElement> ClaimUntilAsync(string submissionId)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var job = await TryClaimAsync();
                if (job is null) break;
                if (job.Value.GetProperty("submissionId").GetString() == submissionId) return job.Value;
            }
            throw new XunitException($"No job for submission {submissionId} came up");
        }

        public async Task<JsonElement> ReportAsync(string jobId, string leaseToken, double score, string verdict)
        {
            var response = await client.PostAsJsonAsync($"/api/v1/runner/jobs/{jobId}/report", new
            {
                leaseToken,
                score,
                maxScore = 100,
                verdict,
                runnerVersion = "0.0.1",
                extra = new { cycles = 12345 },
            });
            await Succeeded(response);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }
}
