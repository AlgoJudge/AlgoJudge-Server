using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A secret this installation holds for a service it talks to.
/// <para>
/// <b>The only secret in the product that comes back out</b>, which makes the
/// permission on the endpoint the whole of the protection, and most of these are
/// about exactly that.
/// </para>
/// <para>
/// <b>Since 2026-08-26 the stored key is not what comes back for
/// `uvaexplorer`</b> — it is exchanged for an hourly token and the long-lived key
/// stays in this process. The tests at the end are about that exchange, and about
/// its two failure shapes: no key at all, which is anonymous browsing, and a key
/// that could not be spent, which is a refusal.
/// </para>
/// </summary>
[Collection("server-2")]
public class AccessKeyTests(ServerFixture server)
{
    private async Task<HttpClient> AdminAsync() =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// Set, listed by name, and the value never travels back on that path.
    [Fact]
    public async Task Setting_a_key_answers_with_its_name_and_never_its_value()
    {
        var admin = await AdminAsync();

        var listed = await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/uvaexplorer", new { value = "the-secret-itself" });
        await Sign.Succeeded(listed);

        var body = await listed.Content.ReadAsStringAsync();
        Assert.Contains("uvaexplorer", body);
        Assert.DoesNotContain("the-secret-itself", body);

        var again = await admin.GetStringAsync("/api/v1/instance/access-keys");
        Assert.Contains("uvaexplorer", again);
        Assert.DoesNotContain("the-secret-itself", again);
    }

    /// <summary>
    /// A credential does come back through the one endpoint written to hand one
    /// out — and that endpoint is the exception, so it is the thing worth
    /// asserting.
    /// <para>
    /// <b>This asserted the stored value until 2026-08-26</b>, when the stored
    /// key stopped being what comes back for this name. It goes through the stub
    /// archive now: without one it would have reached the real
    /// <c>uvaexplorer.algojudge.app</c> from a test run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_credential_is_handed_to_somebody_who_may_import()
    {
        var (admin, _) = await ArchiveAsync();

        var answer = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/instance/access-keys/uvaexplorer/value");

        Assert.Equal("uvaexplorer", answer.GetProperty("name").GetString());
        Assert.Equal(FakeExplorer.Token, answer.GetProperty("value").GetString());
    }

    /// And to nobody else. This is the whole of the protection.
    [Fact]
    public async Task Somebody_who_may_not_import_is_refused_the_key()
    {
        var admin = await AdminAsync();
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/uvaexplorer", new { value = "not-for-you" }));

        var person = await Sign.NewAccountAsync(
            server, "keyless-" + Guid.NewGuid().ToString("N")[..8]);
        var refused = await person.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// **A key nobody has decided who may read is read by nobody but an
    /// administrator.** The next key here is expected to be a model provider's,
    /// and falling back to a manager permission would hand it out on the day it
    /// was added, before anyone had chosen.
    /// </summary>
    [Fact]
    public async Task A_key_with_no_gate_of_its_own_falls_to_the_administrator()
    {
        var admin = await AdminAsync();
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/some-future-model", new { value = "expensive" }));

        // The administrator may, because the administrator may everything.
        var mine = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/instance/access-keys/some-future-model/value");
        Assert.Equal("expensive", mine.GetProperty("value").GetString());

        // Somebody holding only the import permission may not: that permission
        // is about problems, and this key is not.
        var login = "importer-only-" + Guid.NewGuid().ToString("N")[..8];
        var person = await Sign.NewAccountAsync(server, login);
        string personId;
        await using (var context = server.NewContext())
        {
            personId = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
        }
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "problem:import:external" },
        }));

        var refused = await person.GetAsync("/api/v1/instance/access-keys/some-future-model/value");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// An empty value is how an installation stops holding a secret.
    /// <para>
    /// <b>The 404 is load-bearing.</b> It is how the Client learns to open the
    /// picker with no credential at all and browse the public archive. Answering
    /// 200 with an empty value instead would look tidier and would leave the
    /// browser holding a credential the picker cannot use.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_empty_value_removes_the_key()
    {
        var admin = await AdminAsync();
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/uvaexplorer", new { value = "briefly" }));
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/uvaexplorer", new { value = "  " }));

        var gone = await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // ── the exchange ────────────────────────────────────────────────────────

    /// <summary>
    /// A host whose archive is this stub, with the key already stored.
    /// </summary>
    private async Task<(HttpClient Admin, FakeExplorer Archive)> ArchiveAsync()
    {
        var archive = new FakeExplorer();
        var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(Services.AccessKeyMinting.OriginSetting, FakeExplorer.Origin);
            builder.ConfigureTestServices(services => services
                .AddHttpClient(nameof(Services.AccessKeyMinting))
                .ConfigurePrimaryHttpMessageHandler(() => archive));
        });

        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/uvaexplorer", new { value = LongLived }));

        return (admin, archive);
    }

    private const string LongLived = "uexpl_the_installations_own_key";

    /// <summary>
    /// The browser is given the hourly token, and <b>never the stored key</b>.
    /// <para>
    /// The second assertion is the one this whole change exists for: the picker
    /// puts whatever it is given into an iframe address, so a stored key in this
    /// answer is a stored key in a URL.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_stored_key_is_exchanged_and_never_handed_over()
    {
        var (admin, archive) = await ArchiveAsync();

        var response = await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value");
        await Sign.Succeeded(response);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(FakeExplorer.Token, body);
        Assert.DoesNotContain(LongLived, body);
        Assert.DoesNotContain("uexpl_", body);

        // And it went where the document says, carrying the stored key.
        Assert.Equal("/api/access/token", Assert.Single(archive.Paths));
        Assert.Equal(LongLived, Assert.Single(archive.Bearers));
    }

    /// <summary>
    /// The answer says when the token dies, or a caller caches it past its death
    /// with no way to find out.
    /// </summary>
    [Fact]
    public async Task The_answer_carries_the_instant_the_token_dies()
    {
        var (admin, _) = await ArchiveAsync();

        var answer = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/instance/access-keys/uvaexplorer/value");

        var expires = answer.GetProperty("expiresAt").GetString();
        Assert.False(string.IsNullOrWhiteSpace(expires));
        Assert.Equal(
            new DateTime(2026, 8, 26, 13, 0, 0, DateTimeKind.Utc),
            DateTime.Parse(expires!, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    /// <summary>
    /// Nothing is cached: two asks are two exchanges.
    /// <para>
    /// A cache shared between managers would be wrong for a few minutes near
    /// every expiry, and would not be shared at all once this Server runs as more
    /// than one process.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_ask_is_its_own_exchange()
    {
        var (admin, archive) = await ArchiveAsync();

        await Sign.Succeeded(await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value"));
        await Sign.Succeeded(await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value"));

        Assert.Equal(2, archive.Paths.Count);
    }

    /// <summary>
    /// Each refusal keeps its own code, because the Client writes a different
    /// sentence for each and the manager can act on only some of them.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "accessKey.rejected", HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Forbidden, "accessKey.originRefused", HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests, "accessKey.tokenLimit", HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "accessKey.mintFailed", HttpStatusCode.BadGateway)]
    public async Task A_refused_exchange_keeps_its_reason(
        HttpStatusCode said, string code, HttpStatusCode answered)
    {
        var (admin, archive) = await ArchiveAsync();
        archive.Status = said;

        var response = await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value");

        Assert.Equal(answered, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        // **Never the stored key, on any path out of here.**
        Assert.DoesNotContain("uexpl_", body);
    }

    /// <summary>
    /// An archive that cannot be reached is a refusal, not a fallback.
    /// <para>
    /// Handing back the stored key when the exchange fails would restore exactly
    /// the leak this arrangement removes, on the day nobody was watching.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unreachable_archive_does_not_fall_back_to_the_stored_key()
    {
        var (admin, archive) = await ArchiveAsync();
        archive.Unreachable = true;

        var response = await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("accessKey.mintFailed", body);
        Assert.DoesNotContain(LongLived, body);
    }

    /// <summary>
    /// A body with no usable token is the same outcome as a refusal. Passing one
    /// on would send the browser a credential the picker cannot use, and the
    /// failure would show as an empty archive rather than as a message.
    /// </summary>
    [Theory]
    [InlineData("""{"accessToken":""}""")]
    [InlineData("""{"tokenType":"Bearer"}""")]
    [InlineData("not json at all")]
    public async Task An_answer_with_no_usable_token_is_refused(string body)
    {
        var (admin, archive) = await ArchiveAsync();
        archive.Body = body;

        var response = await admin.GetAsync("/api/v1/instance/access-keys/uvaexplorer/value");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("accessKey.mintFailed", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A name nothing mints still answers with what is stored, unchanged. The
    /// exchange is one service's arrangement, not a new rule for every key.
    /// </summary>
    [Fact]
    public async Task A_key_nothing_mints_still_answers_with_what_is_stored()
    {
        var admin = await AdminAsync();
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/some-future-model", new { value = "stored-as-is" }));

        var answer = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/instance/access-keys/some-future-model/value");

        Assert.Equal("stored-as-is", answer.GetProperty("value").GetString());
        // Absent rather than null: a value with no death has no field, which is
        // the shape the Client's `expiresAt?` is written against.
        Assert.False(answer.TryGetProperty("expiresAt", out _));
    }
}
