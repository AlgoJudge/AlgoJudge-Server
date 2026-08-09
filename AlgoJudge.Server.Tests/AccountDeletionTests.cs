using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AlgoJudge.Server.Controllers;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The three ways an account can be asked to go, and the cascade they share.
/// <para>
/// They are not variants of one endpoint. <c>POST /account/delete</c> is the
/// local account's and asks for a password; <c>POST /account/deletion-requests</c>
/// belongs to an account owned by a provider and asks for nothing; and the back
/// channel is a directory saying somebody is gone, with nobody present to
/// confirm it. What they share is what happens afterwards, which is why that
/// part lives in one place.
/// </para>
/// </summary>
[Collection("server")]
public class AccountDeletionTests(ServerFixture server)
{
    /// <summary>
    /// **A webhook is retried, and three deliveries must remove one account
    /// once.** Without idempotency the second delivery opens a second window,
    /// and halting the first stops meaning anything.
    /// </summary>
    [Fact]
    public async Task The_back_channel_is_idempotent_on_its_request_id()
    {
        var (providerId, secret) = await NewProviderWithChannelAsync("idem");
        var person = await FederatedPersonAsync(providerId, "idem-0001", "idem-person");

        var first = await ReportAsync(providerId, secret, "idem-0001", "request-42");
        var second = await ReportAsync(providerId, secret, "idem-0001", "request-42");
        var third = await ReportAsync(providerId, secret, "idem-0001", "request-42");

        await Sign.Succeeded(first);
        await Sign.Succeeded(second);
        await Sign.Succeeded(third);

        // One row, and the same answer every time.
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(id, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(id, (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());

        await using var context = server.NewContext();
        Assert.Equal(1, await context.AccountDeletionRequests.CountAsync(r => r.RequestId == "request-42"));

        // And nothing has happened yet — that is what the window is.
        Assert.True(await context.UserIdentities.AnyAsync(i => i.UserId == person));
    }

    /// <summary>
    /// The window belongs to an administrator, not to the person: their account
    /// at the provider is gone, so they cannot sign in to cancel.
    /// </summary>
    [Fact]
    public async Task A_machine_request_waits_a_day_and_an_administrator_can_stop_it()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (providerId, secret) = await NewProviderWithChannelAsync("windowed");
        var person = await FederatedPersonAsync(providerId, "win-0001", "win-person");

        var reported = await ReportAsync(providerId, secret, "win-0001", "request-window");
        await Sign.Succeeded(reported);
        var body = await reported.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("pending", body.GetProperty("state").GetString());
        var requestedAt = DateTimeOffset.Parse(body.GetProperty("requestedAt").GetString()!);
        var executeAfter = DateTimeOffset.Parse(body.GetProperty("executeAfter").GetString()!);
        Assert.True(executeAfter - requestedAt >= TimeSpan.FromHours(23));

        // A sweep now does nothing: the window is open.
        Assert.Equal(0, await SweepAsync());

        var id = body.GetProperty("id").GetString();
        var halted = await admin.PostAsJsonAsync($"/api/v1/account-deletion-requests/{id}/halt", new { });
        await Sign.Succeeded(halted);
        Assert.Equal("halted", (await halted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("state").GetString());

        // Even once the window has passed, a halted request is not carried out.
        await using (var context = server.NewContext())
        {
            await context.AccountDeletionRequests
                .Where(r => r.RequestId == "request-window")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExecuteAfter, DateTime.UtcNow.AddHours(-1)));
        }

        Assert.Equal(0, await SweepAsync());

        await using (var context = server.NewContext())
        {
            Assert.True(await context.UserIdentities.AnyAsync(i => i.UserId == person));
            Assert.False((await context.Users.FirstAsync(u => u.Id == person)).Anonymized);
        }

        // And a window that has closed cannot be reopened: what it held has
        // already happened, or been stopped.
        var again = await admin.PostAsJsonAsync($"/api/v1/account-deletion-requests/{id}/halt", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>
    /// Once the window closes, the cascade runs: the link goes, that provider's
    /// contribution goes with it, and — nothing else remaining — the account is
    /// emptied rather than deleted, because results still name it.
    /// </summary>
    [Fact]
    public async Task A_closed_window_removes_the_link_and_empties_what_is_left()
    {
        var (providerId, secret) = await NewProviderWithChannelAsync("closing");
        var person = await FederatedPersonAsync(providerId, "close-0001", "close-person");

        await using (var context = server.NewContext())
        {
            Assert.NotNull(await context.Grants.FirstOrDefaultAsync(
                g => g.UserId == person && g.SourceProviderId == providerId));
        }

        await Sign.Succeeded(await ReportAsync(providerId, secret, "close-0001", "request-closing"));
        await CloseTheWindowAsync("request-closing");

        Assert.Equal(1, await SweepAsync());

        await using (var context = server.NewContext())
        {
            var user = await context.Users.FirstAsync(u => u.Id == person);
            Assert.True(user.Anonymized);
            Assert.StartsWith("deleted-", user.UserName);
            Assert.Null(user.Email);

            Assert.False(await context.UserIdentities.AnyAsync(i => i.UserId == person));
            // A directory that no longer knows somebody must not go on granting
            // them permissions.
            Assert.Null(await context.Grants.FirstOrDefaultAsync(
                g => g.UserId == person && g.SourceProviderId == providerId));
        }
    }

    /// <summary>
    /// **A webhook that can silence an administrator is an attack vector.** An
    /// account holding system-scope permissions is never emptied automatically;
    /// it goes to somebody to decide about.
    /// </summary>
    [Fact]
    public async Task An_account_holding_system_permissions_is_never_emptied_automatically()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (providerId, secret) = await NewProviderWithChannelAsync("cannot-silence");
        var person = await FederatedPersonAsync(providerId, "silence-0001", "important-person");

        // Given something by hand, which no provider can take back.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = person,
            permissions = new[] { "system:administrator" },
        }));

        await Sign.Succeeded(await ReportAsync(providerId, secret, "silence-0001", "request-silence"));
        await CloseTheWindowAsync("request-silence");
        Assert.Equal(1, await SweepAsync());

        await using var context = server.NewContext();

        var user = await context.Users.FirstAsync(u => u.Id == person);
        Assert.False(user.Anonymized);
        Assert.Equal("important-person", user.UserName);

        // The link is gone all the same — the provider's statement is honoured
        // as far as it goes — and the rest is somebody's decision.
        Assert.False(await context.UserIdentities.AnyAsync(i => i.UserId == person));

        var request = await context.AccountDeletionRequests.FirstAsync(r => r.RequestId == "request-silence");
        Assert.Equal(DeletionState.NeedsAttention, request.State);
    }

    /// <summary>
    /// **Two providers vouching for one person are two accounts here**, and that
    /// is the point of keying on issuer plus <c>sub</c>: a second directory
    /// saying "this is jan.kowalski" must not be handed the account the first
    /// one made. Joining them is a linking flow somebody has to design and
    /// somebody has to consent to; it does not happen by coincidence of a name.
    /// <para>
    /// The address collides — the same person really does have one address — and
    /// the second account is provisioned <b>without</b> it rather than not at
    /// all. Before this was handled, the second sign-in threw.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_providers_vouching_for_one_person_do_not_share_an_account()
    {
        var first = await NewProviderWithChannelAsync("two-ways-a");
        var second = await NewProviderWithChannelAsync("two-ways-b");

        var one = await FederatedPersonAsync(first.Id, "both-0001", "two-ways-person");
        var other = await SignInFederatedAsync(second.Id, "both-0002", "two-ways-person");

        Assert.True(other.Admitted);
        Assert.NotEqual(one, other.User!.Id);

        await using var context = server.NewContext();
        Assert.Equal(1, await context.UserIdentities.CountAsync(i => i.UserId == one));
        Assert.Equal(1, await context.UserIdentities.CountAsync(i => i.UserId == other.User.Id));

        // The first account keeps the address; the second was created without
        // one, because addresses stay unique here and it is not the key.
        Assert.Equal("two-ways-person@example.invalid",
            (await context.Users.FirstAsync(u => u.Id == one)).Email);
        Assert.Null((await context.Users.FirstAsync(u => u.Id == other.User.Id)).Email);
    }

    /// <summary>
    /// The other half of the cascade: a link goes and the account stays, because
    /// something else still admits its holder.
    /// <para>
    /// Reached here through a local password, which is the way an account can
    /// hold two ways in today. A second provider link would be the other, and
    /// there is no flow that creates one — recorded as an open question rather
    /// than tested into existence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Removing_a_link_leaves_an_account_that_still_has_a_password()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (providerId, secret) = await NewProviderWithChannelAsync("keeps-password");
        var person = await FederatedPersonAsync(providerId, "keeps-0001", "keeps-person");

        // An administrator issues one for it, which is what makes the account
        // this installation's as well as the directory's. The endpoint mints the
        // password itself; nobody chooses one on somebody else's behalf.
        await Sign.Succeeded(await admin.PostAsJsonAsync($"/api/v1/users/{person}/password", new { }));

        await Sign.Succeeded(await ReportAsync(providerId, secret, "keeps-0001", "request-keeps"));
        await CloseTheWindowAsync("request-keeps");
        Assert.Equal(1, await SweepAsync());

        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.Id == person);

        Assert.False(user.Anonymized);
        Assert.Equal("keeps-person", user.UserName);
        Assert.False(await context.UserIdentities.AnyAsync(i => i.UserId == person));

        var request = await context.AccountDeletionRequests.FirstAsync(r => r.RequestId == "request-keeps");
        Assert.Equal(DeletionState.Completed, request.State);
        Assert.Contains("another way to sign in", request.Detail);
    }

    /// <summary>
    /// Every refusal on the back channel is a 404, including a wrong secret.
    /// A 401 would confirm that this provider id is real and that the channel is
    /// open on it — the first thing somebody probing would want to know.
    /// </summary>
    [Fact]
    public async Task The_back_channel_answers_404_to_everything_it_refuses()
    {
        var (providerId, secret) = await NewProviderWithChannelAsync("guarded");

        Assert.Equal(HttpStatusCode.NotFound,
            (await ReportAsync(providerId, "not-the-secret", "x", "r1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ReportAsync(providerId, null, "x", "r2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ReportAsync(Guid.NewGuid(), secret, "x", "r3")).StatusCode);

        // A subject nobody here has is accepted and recorded, for the same
        // reason: answering differently would let a provider enumerate who has
        // an account in this installation.
        var unknown = await ReportAsync(providerId, secret, "nobody-we-know", "r4");
        await Sign.Succeeded(unknown);
        Assert.Equal("completed",
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
    }

    /// <summary>
    /// The two user channels do not overlap: a local account is sent to the form
    /// that asks for its password, and an account with no link is not offered
    /// de-registration.
    /// </summary>
    [Fact]
    public async Task A_local_account_is_not_offered_the_federated_channel()
    {
        var person = await Sign.NewAccountAsync(server, "purely-local");

        var refused = await person.PostAsJsonAsync("/api/v1/account/deletion-requests", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("account.deletion.notFederated",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>The queue is not open to whoever asks.</summary>
    [Fact]
    public async Task The_queue_needs_a_permission()
    {
        var nobody = await Sign.NewAccountAsync(server, "queue-outsider");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await nobody.GetAsync("/api/v1/account-deletion-requests")).StatusCode);
    }

    // ── the plumbing ─────────────────────────────────────────────────────────

    private const string Secret = "back-channel-secret-for-the-suite";

    private async Task<(Guid Id, string Secret)> NewProviderWithChannelAsync(string slug)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug,
            displayName = slug,
            issuer = $"https://{slug}.example.invalid",
            clientId = "algojudge",
            clientSecret = "client-secret-for-the-suite",
            deletionSecret = Secret,
            deletionChannelEnabled = true,
            claimPath = "groups",
            mappingRules = new[] { new { claimValue = "students", templateName = "participant" } },
        });
        await Sign.Succeeded(created);

        return (Guid.Parse((await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!), Secret);
    }

    /// <summary>Signs somebody in through a provider, so there is a link to remove.</summary>
    private async Task<string> FederatedPersonAsync(Guid providerId, string subject, string login)
    {
        var outcome = await SignInFederatedAsync(providerId, subject, login);
        Assert.True(outcome.Admitted);
        return outcome.User!.Id;
    }

    private async Task<FederatedSignIn> SignInFederatedAsync(Guid providerId, string subject, string login)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("groups", "students"),
            new Claim("preferred_username", login),
            new Claim("email", $"{login}@example.invalid"),
        ], "oidc-test");

        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFederatedSignInService>()
            .CompleteAsync(providerId, new ClaimsPrincipal(identity), default);
    }

    private async Task<HttpResponseMessage> ReportAsync(
        Guid providerId, string? secret, string subject, string requestId)
    {
        var client = server.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/identity/providers/{providerId}/deletion-requests")
        {
            Content = JsonContent.Create(new
            {
                subject,
                requestId,
                requestedAt = DateTimeOffset.UtcNow.ToString("O"),
            }),
        };
        if (secret is not null) request.Headers.Add(ProviderDeletionController.SecretHeader, secret);

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Moves a request's window into the past. The alternative is waiting a day,
    /// and the alternative to that is a shorter window in production.
    /// </summary>
    private async Task CloseTheWindowAsync(string requestId)
    {
        await using var context = server.NewContext();
        await context.AccountDeletionRequests
            .Where(r => r.RequestId == requestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExecuteAfter, DateTime.UtcNow.AddMinutes(-1)));
    }

    private async Task<int> SweepAsync()
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountDeletionService>()
            .SweepAsync(default);
    }
}
