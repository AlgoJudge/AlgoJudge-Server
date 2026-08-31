using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a manager may do to somebody else's account.
/// <para>
/// <c>PUT /users/{id}</c> had no test at all, and wrote <c>Email</c> and
/// <c>NormalizedEmail</c> straight onto the entity — so
/// <c>OptionalEmailValidator</c> never saw them and the "an address stays unique
/// when there is one" rule was enforced on every path but the one an
/// administrator uses.
/// </para>
/// </summary>
[Collection("server-2")]
public class ManagedUserTests(ServerFixture server)
{
    [Fact]
    public async Task A_manager_cannot_move_one_persons_address_onto_another()
    {
        var (firstId, firstAddress) = await PersonAsync();
        var (secondId, _) = await PersonAsync();

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var response = await admin.PutAsJsonAsync(
            $"/api/v1/users/{secondId}", new { email = firstAddress });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user.update", problem.GetProperty("code").GetString());

        // Scoped to the address this test made: the database is shared, and a
        // global count would depend on the order the suite ran in.
        await using var context = server.NewContext();
        Assert.Equal(
            1,
            await context.Users.CountAsync(u => u.NormalizedEmail == firstAddress.ToUpperInvariant()));
        Assert.NotEqual(firstAddress, (await context.Users.FirstAsync(u => u.Id == secondId)).Email);
    }

    /// <summary>
    /// The consequence the refusal exists to prevent, said out loud. A row with
    /// a duplicate address makes <c>ResetPasswordAsync</c> fail — it runs the
    /// whole validator chain — so the panel's password button stopped working
    /// for that account, and told the manager the <b>other</b> person's address
    /// while doing it.
    /// </summary>
    [Fact]
    public async Task A_refused_address_leaves_the_password_button_working()
    {
        var (_, firstAddress) = await PersonAsync();
        var (secondId, _) = await PersonAsync();

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await admin.PutAsJsonAsync($"/api/v1/users/{secondId}", new { email = firstAddress });

        var reset = await admin.PostAsync($"/api/v1/users/{secondId}/password", null);
        await Sign.Succeeded(reset);

        var body = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("password").GetString()));
    }

    /// <summary>An address that is nobody else's still goes through.</summary>
    [Fact]
    public async Task A_free_address_is_still_accepted()
    {
        var (id, _) = await PersonAsync();
        var wanted = $"moved-{Guid.NewGuid():N}@example.invalid";

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var response = await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { email = wanted });
        await Sign.Succeeded(response);

        await using var context = server.NewContext();
        var stored = await context.Users.FirstAsync(u => u.Id == id);
        Assert.Equal(wanted, stored.Email);

        // Written through the manager, so the lookup key is the normaliser's
        // answer rather than `ToUpperInvariant`, and the confirmation is cleared.
        Assert.Equal(wanted.ToUpperInvariant(), stored.NormalizedEmail);
        Assert.False(stored.EmailConfirmed);
    }

    /// <summary>
    /// <b>The marker says which session is reading, not who is.</b> It compared
    /// the row's owner to the caller, so every one of somebody's own sessions
    /// came back marked — and the marker exists so an operator does not end the
    /// session they are working from.
    /// </summary>
    [Fact]
    public async Task Exactly_one_session_is_the_one_asking()
    {
        // Two sign-ins, two cookies, two sessions for one account.
        var first = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        string adminId;
        await using (var context = server.NewContext())
        {
            adminId = (await context.Users.FirstAsync(u => u.UserName == Seeder.AdminLogin)).Id;
        }

        var sessions = (await Build.GetAsync(first, $"/api/v1/users/{adminId}/sessions"))
            .EnumerateArray()
            .ToList();

        Assert.True(sessions.Count >= 2, $"expected several sessions, got {sessions.Count}");
        Assert.Single(sessions, s => s.GetProperty("isCurrent").GetBoolean());
    }

    private async Task<(string Id, string Address)> PersonAsync()
    {
        var login = "mu-" + Guid.NewGuid().ToString("N")[..10];
        await Sign.NewAccountAsync(server, login);

        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.UserName == login);
        return (user.Id, user.Email!);
    }
}
