using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Controllers;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Signing in through a provider, over tokens this test writes.
/// <para>
/// <b>No live Authentik is involved, and that is deliberate rather than a
/// shortcut.</b> What the framework's OIDC handler does — state, nonce, PKCE,
/// the code exchange, validating the <c>id_token</c> — is not this product's
/// code and is not what fails here. What is ours starts the moment a validated
/// principal exists: what its claims buy, whether an account appears, and what
/// happens to somebody's permissions when the answer is no.
/// </para>
/// <para>
/// So every test below hands <see cref="IFederatedSignInService"/> a principal
/// it built, which is exactly the object the handler would have produced.
/// </para>
/// </summary>
[Collection("server")]
public class FederatedSignInTests(ServerFixture server)
{
    [Fact]
    public async Task A_claim_path_reads_repeated_claims_and_a_nested_array()
    {
        using var scope = server.Services.CreateScope();
        var mapping = scope.ServiceProvider.GetRequiredService<IClaimMappingService>();

        // Authentik's shape: one claim per group.
        var repeated = Token("s", ("groups", "students"), ("groups", "lecturers"));
        Assert.Equal(["students", "lecturers"], mapping.ValuesAt(repeated, "groups"));

        // Keycloak's shape: one claim holding an object.
        var nested = Token("s", ("realm_access", """{"roles":["staff","dev"]}"""));
        Assert.Equal(["staff", "dev"], mapping.ValuesAt(nested, "realm_access.roles"));

        // A path that leads nowhere is a configuration mistake, not an exception
        // during somebody's sign-in.
        Assert.Empty(mapping.ValuesAt(nested, "realm_access.nothing"));
        Assert.Empty(mapping.ValuesAt(repeated, "roles"));
    }

    /// <summary>
    /// A first sign-in through a trusted provider creates the account and grants
    /// what the mapping says. Without it an account made in the directory has no
    /// counterpart here and nobody can do anything about it.
    /// </summary>
    [Fact]
    public async Task A_first_sign_in_provisions_the_account_and_grants_the_mapped_template()
    {
        var provider = await NewProviderAsync("jit", rules: [("lecturers", "manager")]);

        var outcome = await SignInAsync(provider, Token("jit-0001",
            ("groups", "lecturers"),
            ("preferred_username", "j.kowalski"),
            ("given_name", "Jan"),
            ("family_name", "Kowalski"),
            ("email", "j.kowalski@example.invalid"),
            ("email_verified", "true")));

        Assert.True(outcome.Admitted);
        Assert.NotNull(outcome.User);
        Assert.Equal("j.kowalski", outcome.User!.UserName);
        Assert.Equal("Jan", outcome.User.FirstName);
        Assert.True(outcome.User.EmailConfirmed);
        Assert.NotNull(outcome.User.ApprovedAt);

        // No password: that is what makes it not a local account, and therefore
        // what makes its profile the provider's.
        Assert.Null(outcome.User.PasswordHash);

        var contribution = await ContributionAsync(provider, outcome.User.Id);
        Assert.NotNull(contribution);
        Assert.Contains("submission:read:all", Parse(contribution!.Permissions));
        // The manager template does not carry it, and nothing may add it.
        Assert.DoesNotContain("system:administrator", Parse(contribution.Permissions));
    }

    /// <summary>
    /// **`deny` on a first sign-in creates nothing.** Provisioning somebody the
    /// mapping refuses would leave an account that can never be used and that an
    /// administrator has to explain.
    /// </summary>
    [Fact]
    public async Task Deny_refuses_a_first_sign_in_and_creates_no_account()
    {
        var provider = await NewProviderAsync("strict", rules: [("lecturers", "manager")]);

        var outcome = await SignInAsync(provider, Token("strict-0001",
            ("groups", "somebody-elses-group"),
            ("preferred_username", "nobody-here")));

        Assert.False(outcome.Admitted);
        Assert.Null(outcome.User);
        Assert.Equal("provider.unmapped", outcome.Reason);

        await using var context = server.NewContext();
        Assert.False(await context.Users.AnyAsync(u => u.UserName == "nobody-here"));
        Assert.False(await context.UserIdentities.AnyAsync(i => i.Subject == "strict-0001"));

        // Recorded anyway, because a refusal nobody can see is a support ticket.
        var attempt = await context.FederatedSignInAttempts
            .OrderByDescending(a => a.At)
            .FirstAsync(a => a.Subject == "strict-0001");
        Assert.Equal(FederatedSignInOutcome.Refused, attempt.Outcome);
        Assert.False(attempt.ChangedPermissions);
    }

    /// <summary>
    /// **The one most likely to be built backwards.** A token with nothing
    /// mapped, under <c>deny</c>, refuses the sign-in <b>and still withdraws</b>
    /// what the provider granted before.
    /// <para>
    /// Get the order wrong and somebody removed from the directory's staff group
    /// keeps every right they had here, for ever, by the simple method of never
    /// signing in again. Nothing would look broken.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deny_withdraws_what_it_granted_before_even_though_it_refuses()
    {
        var provider = await NewProviderAsync("revoking", rules: [("lecturers", "manager")]);

        var first = await SignInAsync(provider, Token("revoking-0001",
            ("groups", "lecturers"), ("preferred_username", "was-a-lecturer")));
        Assert.True(first.Admitted);
        Assert.NotNull(await ContributionAsync(provider, first.User!.Id));

        // The same person, no longer in the group.
        var second = await SignInAsync(provider, Token("revoking-0001",
            ("groups", "alumni"), ("preferred_username", "was-a-lecturer")));

        Assert.False(second.Admitted);
        Assert.Equal("provider.unmapped", second.Reason);

        // The account survives — this is not a deletion — and the contribution
        // is gone.
        await using var context = server.NewContext();
        Assert.True(await context.Users.AnyAsync(u => u.Id == first.User.Id));
        Assert.Null(await ContributionAsync(provider, first.User.Id));

        var attempt = await context.FederatedSignInAttempts
            .OrderByDescending(a => a.At)
            .FirstAsync(a => a.Subject == "revoking-0001");
        Assert.Equal(FederatedSignInOutcome.Refused, attempt.Outcome);
        Assert.True(attempt.ChangedPermissions);
    }

    [Fact]
    public async Task DefaultTemplate_admits_what_deny_would_have_refused()
    {
        var provider = await NewProviderAsync("welcoming",
            rules: [("lecturers", "manager")],
            unmapped: "defaultTemplate",
            defaultTemplate: "participant");

        var outcome = await SignInAsync(provider, Token("welcoming-0001",
            ("groups", "nothing-we-map"), ("preferred_username", "a-newcomer")));

        Assert.True(outcome.Admitted);
        var contribution = await ContributionAsync(provider, outcome.User!.Id);
        Assert.Contains("submission:create", Parse(contribution!.Permissions));
        Assert.DoesNotContain("submission:read:all", Parse(contribution.Permissions));
    }

    /// <summary>
    /// The contribution is rewritten from the mapping at every sign-in — so a
    /// promotion in the directory arrives here, and nothing accumulates.
    /// </summary>
    [Fact]
    public async Task Every_sign_in_rewrites_the_contribution()
    {
        var provider = await NewProviderAsync("rewriting",
            rules: [("students", "participant"), ("lecturers", "manager")]);

        var first = await SignInAsync(provider, Token("rewriting-0001",
            ("groups", "students"), ("preferred_username", "moves-up")));
        Assert.DoesNotContain("submission:read:all",
            Parse((await ContributionAsync(provider, first.User!.Id))!.Permissions));

        await SignInAsync(provider, Token("rewriting-0001",
            ("groups", "lecturers"), ("preferred_username", "moves-up")));
        Assert.Contains("submission:read:all",
            Parse((await ContributionAsync(provider, first.User.Id))!.Permissions));

        // Back down again — a union that only ever grew would be a promotion
        // nobody could undo.
        await SignInAsync(provider, Token("rewriting-0001",
            ("groups", "students"), ("preferred_username", "moves-up")));
        Assert.DoesNotContain("submission:read:all",
            Parse((await ContributionAsync(provider, first.User.Id))!.Permissions));

        // One contribution from this provider, however many sign-ins.
        await using var context = server.NewContext();
        Assert.Equal(1, await context.Grants.CountAsync(
            g => g.UserId == first.User.Id && g.SourceProviderId == provider));
    }

    /// <summary>
    /// **Unreachable through a mapping, in every configuration** — including one
    /// reached by writing the rule first and editing the template afterwards.
    /// The edit is refused; and if a template ever carries it anyway, the
    /// mapping strips it rather than trusting that it cannot happen.
    /// </summary>
    [Fact]
    public async Task No_claim_grants_administrator_however_the_template_got_it()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/permission-templates", new
        {
            name = "innocent-at-first",
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(created);
        var templateId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var provider = await NewProviderAsync("late-escalation",
            rules: [("staff", "innocent-at-first")]);

        // The edit that would have smuggled it in.
        var refused = await admin.PutAsJsonAsync($"/api/v1/permission-templates/{templateId}", new
        {
            name = "innocent-at-first",
            permissions = new[] { "activity:read", "system:administrator" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("template.mapped.administrator",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // And the belt to that pair of braces: written past the API, it is still
        // stripped when the mapping is used.
        await using (var context = server.NewContext())
        {
            var template = await context.PermissionTemplates.FirstAsync(t => t.Name == "innocent-at-first");
            template.Permissions = """["activity:read","system:administrator"]""";
            await context.SaveChangesAsync();
        }

        var outcome = await SignInAsync(provider, Token("late-0001",
            ("groups", "staff"), ("preferred_username", "not-an-admin")));

        Assert.True(outcome.Admitted);
        var contribution = await ContributionAsync(provider, outcome.User!.Id);
        Assert.Equal(["activity:read"], Parse(contribution!.Permissions));
    }

    /// <summary>
    /// **A provider never inherits an account by name.** The federated key is
    /// issuer plus <c>sub</c> for exactly this reason: a directory that could
    /// hand us a `preferred_username` matching somebody's login would otherwise
    /// be handing itself that person's account.
    /// </summary>
    [Fact]
    public async Task A_provider_never_takes_over_an_existing_login()
    {
        await Sign.NewAccountAsync(server, "already-here");
        var existingId = await UserIdAsync("already-here");

        var provider = await NewProviderAsync("impersonating", rules: [("students", "participant")]);

        var outcome = await SignInAsync(provider, Token("impersonating-0001",
            ("groups", "students"), ("preferred_username", "already-here")));

        Assert.True(outcome.Admitted);
        Assert.NotEqual(existingId, outcome.User!.Id);
        Assert.NotEqual("already-here", outcome.User.UserName);
        Assert.StartsWith("already-here-", outcome.User.UserName);

        // The account that was already here is untouched — same id, still local,
        // and holding nothing this provider granted.
        await using var context = server.NewContext();
        var existing = await context.Users.FirstAsync(u => u.Id == existingId);
        Assert.Equal("already-here", existing.UserName);
        Assert.NotNull(existing.PasswordHash);
        Assert.Null(await ContributionAsync(provider, existingId));
    }

    /// <summary>
    /// A disabled provider is a statement about the registration, not about
    /// anybody's permissions. Turning one off to reconfigure it must not demote
    /// everybody who signed in through it.
    /// </summary>
    [Fact]
    public async Task A_disabled_provider_admits_nobody_and_changes_nothing()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var provider = await NewProviderAsync("switched-off", rules: [("students", "participant")]);

        var first = await SignInAsync(provider, Token("off-0001",
            ("groups", "students"), ("preferred_username", "signed-in-once")));
        Assert.True(first.Admitted);

        await using (var context = server.NewContext())
        {
            var row = await context.IdentityProviders.FirstAsync(p => p.Id == provider);
            row.Enabled = false;
            await context.SaveChangesAsync();
        }

        var second = await SignInAsync(provider, Token("off-0001",
            ("groups", "students"), ("preferred_username", "signed-in-once")));

        Assert.False(second.Admitted);
        Assert.Equal("provider.disabled", second.Reason);
        // Still holding what it granted: nothing was said about their rights.
        Assert.NotNull(await ContributionAsync(provider, first.User!.Id));
    }

    /// <summary>
    /// The rule of 2026-08-04 — "an SSO account may change none of its own
    /// profile fields" — moves out of the Client's disabled inputs and into the
    /// API, where a terminal cannot ignore it.
    /// </summary>
    [Fact]
    public async Task An_account_owned_by_a_provider_is_read_only_on_the_server()
    {
        var person = await Sign.NewAccountAsync(server, "becomes-federated");

        Assert.True((await person.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("isLocal").GetBoolean());

        // What an account looks like once its local credential is gone and the
        // provider owns it. Written directly so the session survives: going
        // through `UserManager` would roll the security stamp and sign them out,
        // which is not the state being tested.
        await using (var context = server.NewContext())
        {
            var user = await context.Users.FirstAsync(u => u.UserName == "becomes-federated");
            user.PasswordHash = null;
            await context.SaveChangesAsync();
        }

        Assert.False((await person.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("isLocal").GetBoolean());

        var refused = await person.PutAsJsonAsync("/api/v1/account", new { firstName = "Renamed" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("account.federated",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // And the local deletion channel is not theirs either — that is what
        // de-registering the link is for.
        var deletion = await person.PostAsJsonAsync("/api/v1/account/delete", new { password = "whatever" });
        Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
    }

    /// <summary>
    /// The place a provider sends somebody back to is reachable, and it is this
    /// product's code that answers there.
    /// <para>
    /// <b>Found by a live sign-in on 2026-08-10, not by any test above.</b> The
    /// challenge used to hand the handler a hand-written
    /// <c>/identity/providers/…/signed-in</c>, which omits the path base the API
    /// is served under. Every earlier step then worked perfectly — the person
    /// signed in at the provider, consented, and was redirected back with a
    /// valid token — and the journey ended on a 404, with nothing in the
    /// sign-in log, because the action that writes that log never ran.
    /// </para>
    /// <para>
    /// So this asks for the landing path the way a browser arrives at it and
    /// insists on a refusal <b>from the controller</b>: a routing 404 and a
    /// controller that refuses look nothing alike, and only one of them means
    /// the address is real.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_landing_endpoint_is_served_under_the_api_path_base()
    {
        const string slug = "landing";
        await NewProviderAsync(slug, rules: []);

        var browser = server.CreateClient(new() { AllowAutoRedirect = false });
        var arrived = await browser.GetAsync(
            $"/api/v1/identity/providers/{slug}/signed-in?returnUrl=%2Factivities");

        // Nobody is signed in and no external ticket exists, so the controller
        // refuses — and a refusal is a redirect carrying a reason, because the
        // browser is mid-journey and there is nobody to read a JSON body.
        Assert.Equal(HttpStatusCode.Redirect, arrived.StatusCode);
        var sentTo = arrived.Headers.Location!.ToString();
        Assert.Contains("/login?", sentTo);
        Assert.Contains("error=provider.ticket.missing", sentTo);

        // And the same path without the base is nobody's: that is precisely the
        // address the defect generated.
        var nowhere = await browser.GetAsync($"/identity/providers/{slug}/signed-in");
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);

        // The half the two requests above cannot see: **what the challenge tells
        // the provider to come back to.** It travels inside the handler's
        // encrypted state, so no response header carries it and no round trip
        // reveals it — which is why the literal survived every test and was
        // caught by a person watching a browser. Asking the action directly is
        // the only place it is observable.
        using var scope = server.Services.CreateScope();
        var request = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        request.Request.PathBase = "/api/v1";
        request.Request.Host = new HostString("algojudge.test");
        request.Request.RouteValues["controller"] = "FederatedSignIn";

        // Without an endpoint on the request, `IUrlHelperFactory` hands back the
        // pre-endpoint-routing helper, which has no router here and throws. A
        // real request always carries one by the time an action runs.
        request.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            EndpointMetadataCollection.Empty, "the request under test"));

        var context = new ActionContext(request, new RouteData(request.Request.RouteValues),
            new ControllerActionDescriptor { ControllerName = "FederatedSignIn", ActionName = "Challenge" });

        var controller = new FederatedSignInController(
            scope.ServiceProvider.GetRequiredService<IProviderRegistry>(),
            scope.ServiceProvider.GetRequiredService<IFederatedSignInService>(),
            scope.ServiceProvider.GetRequiredService<SignInManager<User>>())
        {
            ControllerContext = new ControllerContext(context),
            Url = scope.ServiceProvider.GetRequiredService<IUrlHelperFactory>().GetUrlHelper(context),
        };

        var challenge = Assert.IsType<ChallengeResult>(controller.Challenge(slug, "/activities"));

        Assert.Equal($"/api/v1/identity/providers/{slug}/signed-in?returnUrl=%2Factivities",
            challenge.Properties!.RedirectUri);
    }

    // ── the plumbing these tests are made of ──────────────────────────────────

    private static ClaimsPrincipal Token(string subject, params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity([new Claim("sub", subject)], "oidc-test");
        foreach (var (type, value) in claims) identity.AddClaim(new Claim(type, value));
        return new ClaimsPrincipal(identity);
    }

    private async Task<FederatedSignIn> SignInAsync(Guid providerId, ClaimsPrincipal principal)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFederatedSignInService>()
            .CompleteAsync(providerId, principal, default);
    }

    private async Task<Guid> NewProviderAsync(
        string slug,
        (string Value, string Template)[] rules,
        string? unmapped = null,
        string? defaultTemplate = null)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug,
            displayName = slug,
            issuer = $"https://{slug}.example.invalid",
            clientId = "algojudge",
            clientSecret = "secret-for-the-suite",
            claimPath = "groups",
            unmappedBehavior = unmapped,
            defaultTemplateName = defaultTemplate,
            mappingRules = rules.Select(r => new { claimValue = r.Value, templateName = r.Template }),
        });
        await Sign.Succeeded(created);

        return Guid.Parse((await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!);
    }

    private async Task<Grant?> ContributionAsync(Guid providerId, string userId)
    {
        await using var context = server.NewContext();
        return await context.Grants.FirstOrDefaultAsync(
            g => g.UserId == userId && g.ActivityId == null && g.SourceProviderId == providerId);
    }

    private static IReadOnlyList<string> Parse(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private async Task<string> UserIdAsync(string login)
    {
        await using var context = server.NewContext();
        return (await context.Users.FirstAsync(u => u.UserName == login)).Id;
    }
}
