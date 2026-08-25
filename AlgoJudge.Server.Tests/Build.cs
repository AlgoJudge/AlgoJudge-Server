using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit.Sdk;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Building a working activity, and a Runner to judge it.
/// <para>
/// Every test that needs one builds <b>its own</b>: the suite shares a database,
/// and a test that leans on the seed or on what another test left is a test that
/// fails depending on the order it ran in.
/// </para>
/// </summary>
public static class Build
{
    /// <summary>An activity with one open round and one problem worth 50, ready to submit to.</summary>
    public static async Task<(string Slug, string RoundId)> ActivityAsync(
        ServerFixture server, bool external = false, string[]? runnerTags = null,
        string problemType = "standard-io@1")
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();

        await PostAsync(admin, "/api/v1/activities", new
        {
            slug,
            name = "Test activity",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
            attachmentVisibility = new[] { new { name = "source", visibility = "participant" } },
            runnerTags = runnerTags ?? [],
        });

        var round = await PostAsync(admin, $"/api/v1/activities/{slug}/series", new
        {
            slug = "r1",
            name = "Round 1",
            startDate = DateTime.UtcNow.AddHours(-1).ToString("O"),
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
        });
        var roundId = round.GetProperty("id").GetString()!;

        var problem = await PostAsync(admin, "/api/v1/problems", new
        {
            slug = "p-" + slug.ToLowerInvariant(),
            name = "Test problem",
            type = problemType,
            external,
        });
        var problemId = problem.GetProperty("id").GetString()!;

        var statement = await UploadAsync(admin, "/api/v1/files", "content.md", "# Test\n");
        var package = await UploadAsync(admin, "/api/v1/files", "package.zip", "archive-" + slug);

        await PostAsync(admin, $"/api/v1/problems/{problemId}/versions", new
        {
            statements = new[] { new { fileId = statement } },
            config = new { format = "standard-io", version = 1, limits = new { timeMs = 1000, memoryBytes = 268435456 } },
            package = new { fileId = package },
        });

        await PostAsync(admin, $"/api/v1/series/{roundId}/problems", new
        {
            problemId, slug = "A", maxPoints = 50,
        });

        // The scheduler owns opening a round, so this opens it the way the
        // scheduler will rather than pretending the dates decide.
        await using (var context = server.NewContext())
        {
            var series = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            series.IsOpen = true;
            series.StartAnnouncedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        return (slug, roundId);
    }

    /// <summary>
    /// A second round in an activity that already has one, with a problem of
    /// its own so it can be asserted about.
    /// <para>
    /// <b>The shape no test had.</b> Every activity built here ran exactly one
    /// round, so nothing ever asked what a round does to its neighbours — which
    /// is the whole question an activity-scoped importance answers.
    /// </para>
    /// </summary>
    public static async Task<string> SecondRoundAsync(
        ServerFixture server, string activitySlug, string roundSlug = "r2",
        string[]? runnerTags = null)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var round = await PostAsync(admin, $"/api/v1/activities/{activitySlug}/series", new
        {
            slug = roundSlug,
            name = "Round " + roundSlug,
            startDate = DateTime.UtcNow.AddHours(-1).ToString("O"),
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            runnerTags = runnerTags ?? [],
        });
        var roundId = round.GetProperty("id").GetString()!;

        var problem = await PostAsync(admin, "/api/v1/problems", new
        {
            slug = $"p-{roundSlug}-{Guid.NewGuid():N}"[..24].ToLowerInvariant(),
            name = "Second problem",
            type = "standard-io@1",
        });
        var problemId = problem.GetProperty("id").GetString()!;

        var statement = await UploadAsync(admin, "/api/v1/files", "content.md", "# Second\n");
        var package = await UploadAsync(admin, "/api/v1/files", "package.zip", "archive-" + roundId);

        await PostAsync(admin, $"/api/v1/problems/{problemId}/versions", new
        {
            statements = new[] { new { fileId = statement } },
            config = new { format = "standard-io", version = 1, limits = new { timeMs = 1000, memoryBytes = 268435456 } },
            package = new { fileId = package },
        });

        await PostAsync(admin, $"/api/v1/series/{roundId}/problems", new
        {
            problemId, slug = "B", maxPoints = 50,
        });

        await using (var context = server.NewContext())
        {
            var series = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            series.IsOpen = true;
            series.StartAnnouncedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        return roundId;
    }

    public static async Task<string> PackageIdOfAsync(ServerFixture server, string activitySlug)
    {
        await using var context = server.NewContext();
        var assignment = await context.SeriesProblems
            .Include(sp => sp.Activity)
            .FirstAsync(sp => sp.Activity!.Slug == activitySlug);

        var reference = await context.FileReferences
            .FirstAsync(r => r.ProblemVersionId == assignment.PinnedProblemVersionId
                && r.Name == "package.zip");

        return reference.FileId.ToString();
    }

    public static async Task<HttpClient> ParticipantAsync(ServerFixture server, string activitySlug)
    {
        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);
        var joined = await client.PostAsJsonAsync($"/api/v1/activities/{activitySlug}/enrolment", new { });
        await Sign.Succeeded(joined);
        return client;
    }

    /// <summary>
    /// Sends, and hands back whatever the Server said.
    /// <para>
    /// Its own method rather than a flag, for the reason <see cref="Sign"/> gives
    /// for the same split: refusal is what some tests are <b>asserting</b> — an
    /// allowance spent, a round shut — and <see cref="SubmitAsync"/> throws on
    /// one, which is right everywhere else and useless there.
    /// </para>
    /// </summary>
    public static async Task<HttpResponseMessage> TrySubmitAsync(
        HttpClient client, string activitySlug, string source, string problemSlug = "A")
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"type":"standard-io@1","language":"python3"}"""), "props" },
            { new StringContent(source), "code" },
            { new StringContent("main.py"), "fileName" },
            { new StringContent(checksum), "sha256" },
        };

        return await client.PostAsync(
            $"/api/v1/activities/{activitySlug}/problems/{problemSlug}/submissions", content);
    }

    public static async Task<JsonElement> SubmitAsync(
        HttpClient client, string activitySlug, string source, string problemSlug = "A")
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"type":"standard-io@1","language":"python3"}"""), "props" },
            { new StringContent(source), "code" },
            // Required for pasted source since 2026-08-22: the Server no longer
            // owns a table of language extensions, so only the sender can name
            // the file, and a name the Runner refuses is worse than no name.
            { new StringContent("main.py"), "fileName" },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync(
            $"/api/v1/activities/{activitySlug}/problems/{problemSlug}/submissions", content);
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<string> UploadAsync(
        HttpClient client, string path, string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", name },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync(path, content);
        await Sign.Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return stored.GetProperty("id").GetString()!;
    }

    /// <summary>A registered, approved Runner with a live token.</summary>
    /// <param name="host">
    /// Where the Runner's own calls go, when a test needs a host of its own —
    /// one carrying an interceptor, say. Registration and approval always go to
    /// the fixture, because they are the same database either way.
    /// </param>
    public static async Task<StubRunner> RunnerAsync(
        ServerFixture server, WebApplicationFactory<Program>? host = null, bool external = false,
        string[]? tags = null, string[]? problemTypes = null)
    {
        var anonymous = server.CreateClient();

        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());

        var registered = await PostAsync(anonymous, "/api/v1/runner/register", new
        {
            name = "stub",
            product = "AlgoJudge-Runner-Stub",
            version = "0.0.1",
            publicKey = pub,
            problemTypes = problemTypes ?? ["standard-io@1"],
            external,
            // Declared at registration, which is the only door they come in
            // through — the panel owns them from the next restart onwards.
            tags = tags ?? [],
        });

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var runnerId = registered.GetProperty("runnerId").GetString()!;
        await Sign.Succeeded(await admin.PostAsync($"/api/v1/runners/{runnerId}/approve", null));

        var stub = new StubRunner(
            (host ?? (WebApplicationFactory<Program>)server).CreateClient(),
            runnerId, registered.GetProperty("fingerprint").GetString()!, pub, priv);
        await stub.AuthenticateAsync();
        return stub;
    }

    /// <summary>
    /// Moves every field a copy is supposed to carry off the value a new entity
    /// already holds.
    ///
    /// <para>
    /// <b>Without this the copy tests prove nothing.</b> A dropped field leaves
    /// the property initializer's value in the copy, and a source still sitting
    /// on that value compares equal either way — which is exactly how nine
    /// fields were dropped under a passing suite. <c>CopiedFields.AssertCarried</c>
    /// refuses to run against a source that has not been through here.
    /// </para>
    ///
    /// <para>
    /// Written straight to the context rather than through the API: the point is
    /// to reach every stored field, including the pairs the write path refuses
    /// to accept together, and a copy has to carry those too.
    /// </para>
    /// </summary>
    public static async Task DistinctiveAsync(ServerFixture server, Guid activityId)
    {
        await using var context = server.NewContext();

        var activity = await context.Activities.FirstAsync(a => a.Id == activityId);
        activity.Type = "course@1";
        activity.RankingType = "points";
        activity.TimeZone = "Europe/Lisbon";
        activity.HasQuestions = false;
        activity.ScoreVisibility = ScoreVisibility.ManagersOnly;
        activity.ShowGroupMembers = true;
        activity.JoinPolicy = JoinPolicy.Open;
        activity.Unlisted = true;
        activity.HideEndedSeriesProblems = true;
        activity.Props = """{"marker":"activity"}""";
        activity.MaxUploadBytes = 12_345_678;
        activity.MaxAttachments = 5;
        activity.MaxSubmissionsPerProblem = 7;
        activity.RunnerTags = ["lab-north"];

        var assignments = await context.SeriesProblems
            .Where(sp => sp.ActivityId == activityId)
            .ToListAsync();
        foreach (var assignment in assignments)
        {
            assignment.Name = "Named for the copy";
            assignment.MaxPoints = 42;
            assignment.Config = """{"languages":["cpp17-gcc"]}""";
            assignment.Spec = """{"languages":["cpp17-gcc","python3"]}""";
            assignment.Props = """{"marker":"assignment"}""";
            assignment.MaxUploadBytes = 4096;
            assignment.MaxAttachments = 3;
            assignment.MaxSubmissions = 9;
        }

        // **The rounds are written without being tracked, and the rules as rows
        // of their own.** `Series` carries `xmin` as a concurrency token, so
        // loading one with its collection to add a child turns a fixture into a
        // fight with the row version. `LockdownTests` found this first.
        var rounds = await context.Series
            .Where(s => s.ActivityId == activityId)
            .Select(s => s.Id)
            .ToListAsync();

        foreach (var id in rounds)
        {
            context.SeriesAddressRules.Add(new SeriesAddressRule
            {
                SeriesId = id,
                Network = System.Net.IPNetwork.Parse("192.168.7.0/24"),
                Note = "the room",
            });

            await context.Series.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.HideProblemsWhilePaused, true)
                .SetProperty(s => s.RevealProblemCount, false)
                .SetProperty(s => s.Importance, SeriesImportance.Exam)
                .SetProperty(s => s.ImportanceScope, SeriesImportanceScope.Installation)
                .SetProperty(s => s.RestrictionsEnabled, false)
                .SetProperty(s => s.RunnerTags, new List<string> { "lab-north", "quiet" }));
        }

        await context.SaveChangesAsync();
    }
}

/// <summary>
/// A Runner with no sandbox. The handshake, the claim and the report are the
/// whole Server-facing contract, and none of it needs to run code.
/// </summary>
public sealed class StubRunner(
    HttpClient client, string id, string fingerprint, string publicKey, Ed25519PrivateKeyParameters key)
{
    public HttpClient Client { get; } = client;
    public string Id { get; } = id;

    /// <summary>Kept so a test can register the same key again — which is what a restart is.</summary>
    public string PublicKey { get; } = publicKey;

    public async Task AuthenticateAsync()
    {
        var challenge = await Build.PostAsync(
            Client, "/api/v1/runner/auth/challenge", new { fingerprint });
        var nonce = challenge.GetProperty("nonce").GetString()!;

        var signer = new Ed25519Signer();
        signer.Init(true, key);
        var message = Encoding.UTF8.GetBytes(nonce);
        signer.BlockUpdate(message, 0, message.Length);
        var signature = Convert.ToBase64String(signer.GenerateSignature());

        var token = await Build.PostAsync(Client, "/api/v1/runner/auth/token", new
        {
            fingerprint, nonce, signature,
        });

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
    }

    public async Task<JsonElement?> TryClaimAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/runner/jobs/claim", new { leaseSeconds = 300 });
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Claims until this submission's job comes up. The queue is shared with
    /// every other test, and a Runner takes whatever is oldest — claiming past
    /// other people's work is what a real one does.
    /// </summary>
    public async Task<JsonElement> ClaimUntilAsync(string submissionId) =>
        await TryClaimForAsync(submissionId)
        ?? throw new XunitException($"No job for submission {submissionId} came up");

    public async Task<JsonElement?> TryClaimForAsync(string submissionId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var job = await TryClaimAsync();
            if (job is null) return null;
            if (job.Value.GetProperty("submissionId").GetString() == submissionId) return job;
        }
        return null;
    }

    public async Task<string> UploadAsync(string name, string text) =>
        await Build.UploadAsync(Client, "/api/v1/runner/files", name, text);

    /// <summary>
    /// Reports a result.
    ///
    /// <para>
    /// <b>`maxScore` is a parameter, and it has to be.</b> It defaulted to 100
    /// and nothing ever passed anything else, so every test in this suite
    /// reported on the one scale where a raw score and a percentage are the same
    /// number — which is the scale on which none of the scoring defects is
    /// visible.
    /// </para>
    /// </summary>
    public async Task<JsonElement> ReportAsync(
        string jobId, string leaseToken, double score = 100, string verdict = "Accepted",
        double maxScore = 100)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/runner/jobs/{jobId}/report", new
        {
            leaseToken, score, maxScore, verdict, runnerVersion = "0.0.1",
        });
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
