using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The operator's own surface: who may reach it, and what it can do.
///
/// <para>
/// Everything here is about a door nobody signs in through. The rules worth
/// defending are the ones that have no screen to look at: that <b>both</b>
/// halves are required and neither alone opens it, that an installation with no
/// token configured has no admin surface at all, and that the account this
/// surface exists to rescue cannot be taken by somebody else in the meantime.
/// </para>
/// </summary>
[Collection("server-3")]
public class AdminSurfaceTests(ServerFixture server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Operator()
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        return client;
    }

    /// <summary>
    /// Puts the well-known development password back, whatever a test here left.
    ///
    /// <para>
    /// The whole suite signs in as this account. A test that changed its
    /// password and stopped there would fail every test that ran afterwards, for
    /// a reason none of them could report.
    /// </para>
    /// </summary>
    private async Task RestoreAdminPasswordAsync()
    {
        using var scope = server.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await users.FindByNameAsync(Seeder.AdminLogin);
        if (admin is null) return;

        var token = await users.GeneratePasswordResetTokenAsync(admin);
        await users.ResetPasswordAsync(admin, token, Seeder.DevAdminPassword);
        admin.AccessFailedCount = 0;
        admin.LockoutEnd = null;
        await users.UpdateAsync(admin);
    }

    // ── who may reach it ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>Both halves, and neither on its own.</b>
    ///
    /// <para>
    /// The loopback rule alone was the whole authorization until the token
    /// arrived, and it is not enough: anything with a foothold inside the
    /// container — including this Server's own process — is already on loopback.
    /// The token is the half a stolen foothold does not come with. So the test
    /// is not "does the right request work" but "does each wrong one fail".
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_takes_the_machine_and_the_token_and_answers_the_same_way_to_anything_else()
    {
        // Right machine, right token.
        var allowed = await Operator().GetAsync("/api/v1/admin/maintenance");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Right machine, no token at all.
        var silent = await server.CreateClient().GetAsync("/api/v1/admin/maintenance");
        Assert.Equal(HttpStatusCode.NotFound, silent.StatusCode);

        // Right machine, a token that is nearly right. The comparison is
        // constant time, and this is the case it exists for.
        var wrong = server.CreateClient();
        wrong.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken + "x");
        Assert.Equal(HttpStatusCode.NotFound, (await wrong.GetAsync("/api/v1/admin/maintenance")).StatusCode);

        // The right token from the wrong machine, claiming to be the right one.
        var forged = Operator();
        forged.DefaultRequestHeaders.Add(ServerFixture.PeerHeader, "203.0.113.7");
        forged.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.NotFound, (await forged.GetAsync("/api/v1/admin/maintenance")).StatusCode);
    }

    /// <summary>
    /// No token configured closes the whole group.
    ///
    /// <para>
    /// The direction matters: a missing setting has to <b>shut</b> the door. An
    /// installation that read "no token" as "no check" would ship an
    /// unauthenticated switch to whoever reached loopback first, and it would do
    /// it silently — which is why the Server also says so at start.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_installation_with_no_token_has_no_admin_surface_at_all()
    {
        var client = server.Closed();

        // Not only the switch: the whole group, including the endpoint whose
        // job is to let somebody back in.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/admin/maintenance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/v1/admin/maintenance?on=true", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/admin/password", new { password = "whatever-it-is" }, Json))
                .StatusCode);

        // And nothing was thrown on the way past.
        await using var context = server.NewContext();
        var state = await context.Maintenance.FirstOrDefaultAsync();
        Assert.True(state is null || state.Level == MaintenanceLevel.Open);
    }

    // ── the password ─────────────────────────────────────────────────────────

    /// <summary>
    /// The way into an installation nobody can sign in to.
    ///
    /// <para>
    /// A seeded <c>admin</c> has twenty characters nobody was told, so this is
    /// not a convenience — it is the only route in. What it must do is set the
    /// password, and what it must not do is say the password back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_sets_the_administrators_password_and_never_repeats_it()
    {
        const string chosen = "a-new-administrator-password";

        try
        {
            var response = await Operator().PostAsJsonAsync(
                "/api/v1/admin/password", new { password = chosen }, Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(Seeder.AdminLogin, body);
            // **Never echoed.** The caller already knows it, and everything else
            // that sees this document is a log or a screen recording.
            Assert.DoesNotContain(chosen, body);

            // The new one works.
            var signedIn = await Sign.InAsync(server, Seeder.AdminLogin, chosen);
            Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/account")).StatusCode);

            // And the old one does not.
            var stale = await Sign.TryInAsync(server, Seeder.AdminLogin, Seeder.DevAdminPassword);
            Assert.Null(stale);
        }
        finally
        {
            await RestoreAdminPasswordAsync();
        }
    }

    /// <summary>
    /// A refused password changes nothing.
    ///
    /// <para>
    /// The half-applied case is the dangerous one: an operator who typed
    /// something too short, was refused, and now cannot sign in with either the
    /// old password or the new one has an installation nobody can administer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_password_the_policy_refuses_leaves_the_old_one_working()
    {
        var refused = await Operator().PostAsJsonAsync(
            "/api/v1/admin/password", new { password = "short" }, Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var still = await Sign.TryInAsync(server, Seeder.AdminLogin, Seeder.DevAdminPassword);
        Assert.NotNull(still);
    }

    /// <summary>
    /// A locked-out administrator can be let back in.
    ///
    /// <para>
    /// Ten wrong guesses lock an account for an hour, and guessing is exactly
    /// what somebody does before they come looking for this endpoint. A reset
    /// that left the lockout standing would look like it had not worked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_clears_a_lockout_the_operator_earned_on_the_way_here()
    {
        const string chosen = "let-me-back-in-please";

        try
        {
            using (var scope = server.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var admin = await users.FindByNameAsync(Seeder.AdminLogin);
                admin!.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
                admin.AccessFailedCount = 10;
                await users.UpdateAsync(admin);
            }

            // Locked out, as the operator found it.
            Assert.Null(await Sign.TryInAsync(server, Seeder.AdminLogin, Seeder.DevAdminPassword));

            var reset = await Operator().PostAsJsonAsync(
                "/api/v1/admin/password", new { password = chosen }, Json);
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            Assert.NotNull(await Sign.TryInAsync(server, Seeder.AdminLogin, chosen));
        }
        finally
        {
            await RestoreAdminPasswordAsync();
        }
    }

    // ── the reserved login ───────────────────────────────────────────────────

    /// <summary>
    /// <b>Nobody else may be called <c>admin</c>.</b>
    ///
    /// <para>
    /// The name means something now: <c>/admin/password</c> resets the account
    /// that holds it, so whoever holds it holds the endpoint. Checked through
    /// the manager's own creation path and through a rename, because those are
    /// two different framework calls and the rule lives in one validator that
    /// both of them run.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("  admin  ")]
    public async Task No_second_account_may_take_the_administrators_login(string wanted)
    {
        var admin = await Sign.InAsync(server, Seeder.AdminLogin, Seeder.DevAdminPassword);

        var refused = await admin.PostAsJsonAsync("/api/v1/users", new { username = wanted }, Json);

        Assert.True(
            refused.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
            $"creating {wanted} answered {refused.StatusCode}");

        await using var context = server.NewContext();
        Assert.Equal(1, await context.Users.CountAsync(u => u.NormalizedUserName == "ADMIN"));
    }

    [Fact]
    public async Task An_ordinary_account_cannot_rename_itself_to_the_administrators_login()
    {
        var participant = await Sign.NewAccountAsync(server, "hopeful-" + Guid.NewGuid().ToString("N")[..8]);

        var refused = await participant.PutAsJsonAsync(
            "/api/v1/account", new { username = Seeder.AdminLogin }, Json);

        Assert.True(
            refused.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
            $"the rename answered {refused.StatusCode}");
    }

    /// <summary>
    /// And the administrator may not walk away from the name either — otherwise
    /// <c>/admin/password</c> points at an account that no longer exists and
    /// there is no session anywhere able to put it back.
    /// </summary>
    [Fact]
    public async Task The_administrator_cannot_rename_itself_away()
    {
        var admin = await Sign.InAsync(server, Seeder.AdminLogin, Seeder.DevAdminPassword);

        var refused = await admin.PutAsJsonAsync(
            "/api/v1/account", new { username = "not-the-admin-any-more" }, Json);

        Assert.True(
            refused.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
            $"the rename answered {refused.StatusCode}");

        await using var context = server.NewContext();
        Assert.True(await context.Users.AnyAsync(u => u.NormalizedUserName == "ADMIN"));
    }

    /// <summary>
    /// <b>A refused profile change used to answer 200.</b>
    /// <c>UpdateProfileAsync</c> threw away the <c>IdentityResult</c> of all
    /// three writes it makes, so a duplicate address was refused by
    /// <see cref="OptionalEmailValidator"/>, dropped on the floor, and the caller
    /// was handed a session document describing a change that had not happened.
    /// </summary>
    [Fact]
    public async Task A_profile_change_that_identity_refuses_is_not_reported_as_success()
    {
        var first = "prof-" + Guid.NewGuid().ToString("N")[..8];
        var second = "prof-" + Guid.NewGuid().ToString("N")[..8];
        await Sign.NewAccountAsync(server, first);
        var client = await Sign.NewAccountAsync(server, second);

        var taken = $"{first}@example.invalid";

        var refused = await client.PutAsJsonAsync("/api/v1/account", new { email = taken }, Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("account.profile", problem.GetProperty("code").GetString());

        // Scoped to the address this test made: the database is shared.
        await using var context = server.NewContext();
        Assert.Equal(
            1, await context.Users.CountAsync(u => u.NormalizedEmail == taken.ToUpperInvariant()));
    }

    /// <summary>
    /// The assertion the transaction exists for. <c>SetUserNameAsync</c> writes
    /// to the database itself, so checking the results without one would answer
    /// 422 for the address over a login that had already been renamed.
    /// </summary>
    [Fact]
    public async Task A_refused_profile_change_leaves_the_login_alone()
    {
        var first = "keep-" + Guid.NewGuid().ToString("N")[..8];
        var second = "keep-" + Guid.NewGuid().ToString("N")[..8];
        await Sign.NewAccountAsync(server, first);
        var client = await Sign.NewAccountAsync(server, second);

        var refused = await client.PutAsJsonAsync(
            "/api/v1/account",
            new { username = "renamed-" + Guid.NewGuid().ToString("N")[..8], email = $"{first}@example.invalid" },
            Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        await using var context = server.NewContext();
        Assert.True(await context.Users.AnyAsync(u => u.NormalizedUserName == second.ToUpperInvariant()));
    }

    /// <summary>A change nothing objects to still goes through.</summary>
    [Fact]
    public async Task An_ordinary_profile_change_still_succeeds()
    {
        var login = "ok-" + Guid.NewGuid().ToString("N")[..8];
        var client = await Sign.NewAccountAsync(server, login);

        var response = await client.PutAsJsonAsync(
            "/api/v1/account", new { firstName = "Zofia", lastName = "Nowak" }, Json);
        await Sign.Succeeded(response);

        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Zofia", session.GetProperty("firstName").GetString());
    }
}
