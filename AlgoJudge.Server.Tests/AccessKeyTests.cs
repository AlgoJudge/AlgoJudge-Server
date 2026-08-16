using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A secret this installation holds for a service it talks to.
/// <para>
/// <b>The only secret in the product that comes back out</b>, because the thing
/// that needs it runs in a manager's browser. That makes the permission on the
/// endpoint the whole of the protection, and these are about exactly that.
/// </para>
/// </summary>
[Collection("server")]
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
    /// It does come back through the one endpoint written to hand it out — and
    /// that endpoint is the exception, so it is the thing worth asserting.
    /// </summary>
    [Fact]
    public async Task The_key_is_handed_to_somebody_who_may_import()
    {
        var admin = await AdminAsync();
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance/access-keys/uvaexplorer", new { value = "handed-over" }));

        var answer = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/instance/access-keys/uvaexplorer/value");

        Assert.Equal("uvaexplorer", answer.GetProperty("name").GetString());
        Assert.Equal("handed-over", answer.GetProperty("value").GetString());
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

    /// An empty value is how an installation stops holding a secret.
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
}
