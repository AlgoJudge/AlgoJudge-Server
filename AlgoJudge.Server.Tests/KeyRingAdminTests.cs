using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The operator's commands for the key ring.
/// <para>
/// <b>They exist because the documented recipe was raw SQL.</b> Getting an
/// encrypted key onto an installation that already had a plaintext one meant
/// deleting rows by hand — the kind of instruction <c>aj-admin</c> exists to
/// remove, and one that destroys the record rather than revoking it.
/// </para>
/// <para>
/// <b>Every destructive test builds a database of its own.</b> Revoking signs
/// out every session the ring covers, and the suite shares one — a revoke
/// against it would fail whatever else happened to be signed in, in whichever
/// test the scheduler had running at the time.
/// </para>
/// </summary>
[Collection("server-2")]
public class KeyRingAdminTests(ServerFixture server)
{
    private const string Ring = "/api/v1/admin/keyring";

    private static HttpClient Operator(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A host with a ring nothing else in the suite is signed in against.</summary>
    private async Task<WebApplicationFactory<Program>> OwnRingAsync(
        Action<IWebHostBuilder>? more = null)
    {
        var connectionString = await ScratchDatabase.CreateAsync(server);
        return server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
            more?.Invoke(builder);
        });
    }

    private static async Task<string> SignInAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true",
            new { email = Database.Seeder.DevAdminLogin, password = Database.Seeder.DevAdminPassword });

        Assert.True(response.IsSuccessStatusCode, $"signing in returned {(int)response.StatusCode}");
        return string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
    }

    private static async Task<HttpStatusCode> StillSignedInAsync(
        WebApplicationFactory<Program> host, string cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account");
        request.Headers.Add("Cookie", cookies);
        var response = await host
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Status_says_what_is_in_force_and_what_is_stored()
    {
        var report = await ReadAsync(await Operator(server).GetAsync(Ring));

        Assert.Equal(KeyRing.Database, report.GetProperty("kind").GetString());
        Assert.Equal(KeyRing.ApplicationName, report.GetProperty("applicationName").GetString());

        // The suite configures no certificate, so its keys are plaintext — which
        // is the state a database backup raises a question about, and the answer
        // an operator comes here for.
        var keys = report.GetProperty("keys").EnumerateArray().ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal("plaintext", key.GetProperty("storage").GetString()));
        Assert.All(keys, key => Assert.True(key.GetProperty("readable").GetBoolean()));
    }

    /// <summary>
    /// It is inside the guarded group: with the token it answers, without it
    /// there is nothing there — the same 404 everything else in <c>/admin</c>
    /// gives, which does not confirm the endpoint exists.
    /// <para>
    /// <b>Both halves, in one test, and the sabotage is why.</b> Asserting only
    /// the 404 passed with the endpoint moved out of the group entirely — a
    /// route that does not exist answers 404 as convincingly as one that is
    /// being protected. The 200 is what makes the 404 mean anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_answers_the_operator_and_nobody_else()
    {
        Assert.Equal(HttpStatusCode.OK, (await Operator(server).GetAsync(Ring)).StatusCode);

        var anonymous = await server
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .GetAsync(Ring);
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);

        // And the token has to be the right one, not merely present.
        var wrong = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        wrong.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, "not-the-token");
        Assert.Equal(HttpStatusCode.NotFound, (await wrong.GetAsync(Ring)).StatusCode);
    }

    [Fact]
    public async Task Rotating_writes_a_key_and_signs_nobody_out()
    {
        using var host = await OwnRingAsync();
        var cookies = await SignInAsync(host);

        var before = (await ReadAsync(await Operator(host).GetAsync(Ring)))
            .GetProperty("keys").GetArrayLength();

        var created = await ReadAsync(
            await Operator(host).PostAsync($"{Ring}/rotate", null));

        Assert.True(created.GetProperty("isActive").GetBoolean());

        var after = (await ReadAsync(await Operator(host).GetAsync(Ring)))
            .GetProperty("keys").GetArrayLength();
        Assert.Equal(before + 1, after);

        // **The whole reason rotate is a separate command from revoke.**
        Assert.Equal(HttpStatusCode.OK, await StillSignedInAsync(host, cookies));
    }

    [Fact]
    public async Task Revoking_signs_everybody_out()
    {
        using var host = await OwnRingAsync();
        var cookies = await SignInAsync(host);
        Assert.Equal(HttpStatusCode.OK, await StillSignedInAsync(host, cookies));

        var revoked = await ReadAsync(await Operator(host).PostAsync(
            $"{Ring}/revoke?confirm=revoke&reason=a+test", null));

        Assert.True(revoked.GetProperty("revoked").GetInt32() >= 1);
        Assert.Equal("a test", revoked.GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, await StillSignedInAsync(host, cookies));
    }

    [Fact]
    public async Task Revoking_without_the_word_is_refused()
    {
        using var host = await OwnRingAsync();
        var cookies = await SignInAsync(host);

        foreach (var query in new[] { "", "?confirm=true", "?confirm=yes", "?confirm=REVOKE" })
        {
            var response = await Operator(host).PostAsync($"{Ring}/revoke{query}", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("keyring.confirm", await response.Content.ReadAsStringAsync());
        }

        // And nothing happened to anybody while it was being refused.
        Assert.Equal(HttpStatusCode.OK, await StillSignedInAsync(host, cookies));
    }

    /// <summary>
    /// <b>The validation, and the reason this endpoint is worth having.</b> A
    /// certificate dropped instead of kept leaves keys nobody can read, and
    /// until now the only symptom was everybody being signed out.
    /// </summary>
    [Fact]
    public async Task A_key_no_configured_certificate_can_read_is_reported()
    {
        using var certificate = Certificate.Scratch();
        var connectionString = await ScratchDatabase.CreateAsync(server);

        // A ring written under a certificate…
        using (var encrypting = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Path", certificate.Path);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Password", Certificate.Password);
        }))
        {
            await ReadAsync(await Operator(encrypting).PostAsync($"{Ring}/rotate", null));
        }

        // …and read by a Server that no longer has it.
        using var without = server.WithWebHostBuilder(
            builder => builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString));

        var report = await ReadAsync(await Operator(without).GetAsync(Ring));

        var unreadable = report.GetProperty("keys").EnumerateArray()
            .Where(key => !key.GetProperty("readable").GetBoolean())
            .ToList();
        Assert.NotEmpty(unreadable);
        Assert.All(unreadable, key => Assert.Equal("encrypted", key.GetProperty("storage").GetString()));

        var problems = report.GetProperty("problems").EnumerateArray()
            .Select(problem => problem.GetString() ?? "").ToList();
        Assert.Contains(problems, problem => problem.Contains("cannot be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// The measured trap: configuring a certificate over an existing ring
    /// changes nothing until it rotates, and nothing said so.
    /// <para>
    /// <b>And what rotating does not fix, which this test was written to miss
    /// and did.</b> The first version asserted that rotating cleared every
    /// complaint about plaintext; it does not, because the old plaintext key is
    /// still there and still usable — which is the very thing that lets every
    /// open session survive a rotate. Two problems are reported, and only a
    /// revoke clears the second.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_certificate_over_a_plaintext_key_says_to_rotate_and_rotating_fixes_it()
    {
        using var certificate = Certificate.Scratch();
        var connectionString = await ScratchDatabase.CreateAsync(server);

        // A plaintext ring first, written by a Server with no certificate.
        using (var plain = server.WithWebHostBuilder(
            builder => builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString)))
        {
            await ReadAsync(await Operator(plain).PostAsync($"{Ring}/rotate", null));
        }

        using var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Path", certificate.Path);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Password", Certificate.Password);
        });

        static List<string> Problems(JsonElement report) => report.GetProperty("problems")
            .EnumerateArray().Select(problem => problem.GetString() ?? "").ToList();

        const string Minted = "key new cookies are minted under is";
        const string Dump = "a database dump still carries";

        var before = Problems(await ReadAsync(await Operator(host).GetAsync(Ring)));
        Assert.Contains(before, p => p.Contains(Minted, StringComparison.Ordinal));
        Assert.Contains(before, p => p.Contains(Dump, StringComparison.Ordinal));

        var rotated = await ReadAsync(await Operator(host).PostAsync($"{Ring}/rotate", null));
        Assert.Equal("encrypted", rotated.GetProperty("storage").GetString());

        // Rotating fixes what it can: new cookies are minted under an encrypted
        // key. It does **not** remove the plaintext one, which is still usable —
        // and still in any backup taken today.
        var after = Problems(await ReadAsync(await Operator(host).GetAsync(Ring)));
        Assert.DoesNotContain(after, p => p.Contains(Minted, StringComparison.Ordinal));
        Assert.Contains(after, p => p.Contains(Dump, StringComparison.Ordinal));

        // And revoking clears the rest, at the price it names.
        await ReadAsync(await Operator(host).PostAsync($"{Ring}/revoke?confirm=revoke", null));

        var revoked = Problems(await ReadAsync(await Operator(host).GetAsync(Ring)));
        Assert.DoesNotContain(revoked, p => p.Contains(Dump, StringComparison.Ordinal));
    }

    /// <summary>
    /// With the keys in memory there is no stored ring, and a command that
    /// reported success would have changed nothing the running Server reads.
    /// </summary>
    [Fact]
    public async Task With_the_keys_in_memory_it_says_so_and_refuses_to_act()
    {
        using var host = server.WithWebHostBuilder(
            builder => builder.UseSetting(KeyRing.KindSetting, KeyRing.Ephemeral));

        var report = await ReadAsync(await Operator(host).GetAsync(Ring));
        Assert.Equal(KeyRing.Ephemeral, report.GetProperty("kind").GetString());
        Assert.Empty(report.GetProperty("keys").EnumerateArray());
        Assert.NotEmpty(report.GetProperty("problems").EnumerateArray());

        foreach (var path in new[] { $"{Ring}/rotate", $"{Ring}/revoke?confirm=revoke" })
        {
            var response = await Operator(host).PostAsync(path, null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("keyring.ephemeral", await response.Content.ReadAsStringAsync());
        }
    }
}
