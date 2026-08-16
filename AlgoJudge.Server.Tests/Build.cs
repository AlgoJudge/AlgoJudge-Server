using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
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
        ServerFixture server, bool external = false)
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
            languages = new[] { "python" },
            attachmentVisibility = new[] { new { name = "source", visibility = "participant" } },
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
            type = "standard-io@1",
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

    public static async Task<JsonElement> SubmitAsync(
        HttpClient client, string activitySlug, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new StringContent("python"), "language" },
            { new StringContent(source), "code" },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync(
            $"/api/v1/activities/{activitySlug}/problems/A/submissions", content);
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
        ServerFixture server, WebApplicationFactory<Program>? host = null, bool external = false)
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
            problemTypes = new[] { "standard-io@1" },
            external,
        });

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var runnerId = registered.GetProperty("runnerId").GetString()!;
        await Sign.Succeeded(await admin.PostAsync($"/api/v1/runners/{runnerId}/approve", null));

        var stub = new StubRunner(
            (host ?? (WebApplicationFactory<Program>)server).CreateClient(),
            runnerId, registered.GetProperty("fingerprint").GetString()!, priv);
        await stub.AuthenticateAsync();
        return stub;
    }
}

/// <summary>
/// A Runner with no sandbox. The handshake, the claim and the report are the
/// whole Server-facing contract, and none of it needs to run code.
/// </summary>
public sealed class StubRunner(
    HttpClient client, string id, string fingerprint, Ed25519PrivateKeyParameters key)
{
    public HttpClient Client { get; } = client;
    public string Id { get; } = id;

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

    public async Task<JsonElement> ReportAsync(
        string jobId, string leaseToken, double score = 100, string verdict = "Accepted")
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/runner/jobs/{jobId}/report", new
        {
            leaseToken, score, maxScore = 100, verdict, runnerVersion = "0.0.1",
        });
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
