using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Registering an identity provider, and the guards that stop a claim minting
/// privilege.
/// <para>
/// Two things here are not ordinary CRUD tests and are the reason this file
/// exists: a stored secret must never come back out, and a mapping must never
/// reach a permission the person writing it does not hold. Both are rules a
/// screen cannot enforce, and both fail silently if they are wrong — a leaked
/// secret looks like a working panel, and an over-broad mapping looks like a
/// working sign-in.
/// </para>
/// </summary>
[Collection("server-2")]
public class IdentityProviderTests(ServerFixture server)
{
    /// <summary>Distinctive enough that finding it in a response is unambiguous.</summary>
    private const string Secret = "client-secret-that-must-never-come-back-8f3a2c";

    private static object Registration(string slug, object? rules = null) => new
    {
        slug,
        displayName = "University SSO",
        issuer = "https://auth.example.invalid/application/o/algojudge",
        clientId = "algojudge",
        clientSecret = Secret,
        claimPath = "groups",
        mappingRules = rules ?? Array.Empty<object>(),
    };

    /// <summary>
    /// A provider that already has mapping rules can be edited.
    ///
    /// <para>
    /// **Found by a testbed, not by this suite, on 2026-08-11.** Every update of
    /// a provider carrying rules answered 500 —
    /// `DbUpdateConcurrencyException: expected to affect 1 row(s), but actually
    /// affected 0`. The service emptied the collection and refilled it with new
    /// objects, and because every entity in this schema assigns its own key in
    /// its initializer, a rule reached through a *tracked* parent arrives with a
    /// non-default `Id` and is taken for a row that already exists: EF wrote
    /// `UPDATE` where it needed `INSERT`, and the update matched nothing.
    /// </para>
    /// <para>
    /// The creation path never showed it, which is why the suite was quiet —
    /// there the parent is `Add`ed and the whole graph goes in as new. So this
    /// test edits **twice**: once to change a rule, once more to prove the
    /// second edit is not the one that breaks.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_provider_with_rules_can_be_edited_more_than_once()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("editable", new[]
            {
                new { claimValue = "staff", roleName = "manager" },
                new { claimValue = "students", roleName = "participant" },
            }));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // One value keeps its rule, one changes template, one goes, one arrives.
        var edited = await admin.PutAsJsonAsync($"/api/v1/identity/providers/{id}",
            Registration("editable", new[]
            {
                new { claimValue = "staff", roleName = "participant" },
                new { claimValue = "guests", roleName = "participant" },
            }));
        await Sign.Succeeded(edited);

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/identity/providers/{id}");
        var rules = read.GetProperty("mappingRules").EnumerateArray()
            .ToDictionary(r => r.GetProperty("claimValue").GetString()!,
                          r => r.GetProperty("roleName").GetString());

        Assert.Equal(["guests", "staff"], rules.Keys.OrderBy(k => k));
        Assert.Equal("participant", rules["staff"]);

        // Again, with the same body: an edit that changes nothing is the one an
        // operator makes by pressing Save twice.
        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/identity/providers/{id}",
            Registration("editable", new[]
            {
                new { claimValue = "staff", roleName = "participant" },
                new { claimValue = "guests", roleName = "participant" },
            })));
    }

    /// <summary>
    /// **The headline rule.** A secret goes in and nothing gives it back — not
    /// the creation response, not the list, not a re-read, not an update.
    /// <para>
    /// Asserted against the <b>raw response text</b> rather than against a
    /// parsed field, because a field-by-field assertion only checks the fields
    /// somebody thought of. This one fails if a secret ever appears anywhere in
    /// any of those bodies, including in a field added later by somebody who did
    /// not read this file.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stored_secret_never_comes_back()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", Registration("secretive"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var bodies = new List<string> { await Read(created) };
        bodies.Add(await Read(await admin.GetAsync("/api/v1/identity/providers")));
        bodies.Add(await Read(await admin.GetAsync($"/api/v1/identity/providers/{id}")));
        bodies.Add(await Read(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Registration("secretive"))));

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        }

        // …and it really was stored, so the test above is not passing because
        // nothing was saved.
        await using var context = server.NewContext();
        var stored = await context.IdentityProviders.FirstAsync(p => p.Slug == "secretive");
        Assert.Equal(Secret, stored.ClientSecret);

        // What the panel gets instead: whether one is set.
        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/identity/providers/{id}");
        Assert.True(read.GetProperty("hasClientSecret").GetBoolean());
    }

    /// <summary>
    /// An update that carries no secret keeps the stored one. The alternative —
    /// treating absence as "clear it" — would make a panel that round-trips its
    /// own empty field unconfigure a working provider on every save.
    /// </summary>
    [Fact]
    public async Task An_update_without_a_secret_keeps_the_stored_one()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", Registration("keeps-secret"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var updated = await admin.PutAsJsonAsync($"/api/v1/identity/providers/{id}", new
        {
            slug = "keeps-secret",
            displayName = "Renamed",
            issuer = "https://auth.example.invalid/application/o/algojudge",
            clientId = "algojudge",
            // no clientSecret
        });
        await Sign.Succeeded(updated);

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", body.GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("hasClientSecret").GetBoolean());

        await using var context = server.NewContext();
        var stored = await context.IdentityProviders.FirstAsync(p => p.Slug == "keeps-secret");
        Assert.Equal(Secret, stored.ClientSecret);
    }

    [Fact]
    public async Task The_whole_surface_needs_provider_manage()
    {
        var nobody = await Sign.NewAccountAsync(server, "provider-outsider");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await nobody.GetAsync("/api/v1/identity/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await nobody.PostAsJsonAsync("/api/v1/identity/providers", Registration("sneaky"))).StatusCode);
    }

    /// <summary>
    /// **No claim may ever grant `system:administrator`** — in any
    /// configuration, including one an administrator writes for themselves. It
    /// is not enough that the shipped templates do not carry it: an installation
    /// can invent a template, and that template would otherwise turn a directory
    /// group into a way of becoming an administrator here.
    /// </summary>
    [Fact]
    public async Task No_claim_may_grant_administrator()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var template = await admin.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "back-door",
            permissions = new[] { "system:administrator" },
        });
        await Sign.Succeeded(template);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("with-a-back-door", new[]
            {
                new { claimValue = "staff", roleName = "back-door" },
            }));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("provider.rule.administrator", await Code(refused));

        // Nothing was half-written.
        await using var context = server.NewContext();
        Assert.False(await context.IdentityProviders.AnyAsync(p => p.Slug == "with-a-back-door"));
    }

    /// <summary>
    /// The grant rule, applied to the path a claim takes: holding
    /// `provider:manage` must not be a way of granting yourself anything by
    /// mapping a group you are in onto a template you could not otherwise assign.
    /// </summary>
    [Fact]
    public async Task Nobody_maps_onto_what_they_do_not_hold()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var operatorClient = await Sign.NewAccountAsync(server, "provider-operator");

        var granted = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = await UserIdAsync("provider-operator"),
            permissions = new[] { "provider:manage", "role:read" },
        });
        await Sign.Succeeded(granted);

        // `manager` carries `submission:read:all` and much else this account does
        // not hold.
        var refused = await operatorClient.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("over-reaching", new[]
            {
                new { claimValue = "lecturers", roleName = "manager" },
            }));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("provider.rule.excess", await Code(refused));

        // And what they *do* hold goes through, so the guard is not simply
        // refusing everything.
        var narrow = await admin.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "just-templates",
            permissions = new[] { "role:read" },
        });
        await Sign.Succeeded(narrow);

        var allowed = await operatorClient.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("within-reach", new[]
            {
                new { claimValue = "lecturers", roleName = "just-templates" },
            }));
        await Sign.Succeeded(allowed);
    }

    /// <summary>
    /// A template a mapping rule names cannot be deleted, and renaming it takes
    /// the rule with it.
    /// <para>
    /// This is the one place anything points at a template. A grant does not —
    /// choosing one copies its permissions and nothing points back afterwards —
    /// and the difference matters: deleting a template leaves grants alone and
    /// would leave a mapping rule granting nothing, silently, at the next
    /// sign-in.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_mapped_template_cannot_be_deleted_and_follows_a_rename()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var template = await admin.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "mapped-set",
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(template);
        var templateId = (await template.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("maps-a-template", new[]
            {
                new { claimValue = "students", roleName = "mapped-set" },
            }));
        await Sign.Succeeded(created);

        var refused = await admin.DeleteAsync($"/api/v1/roles/{templateId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("role.mapped", await Code(refused));

        // A rename has to reach the rule, or the provider goes on naming
        // something that no longer answers.
        var renamed = await admin.PutAsJsonAsync($"/api/v1/roles/{templateId}", new
        {
            name = "mapped-set-renamed",
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(renamed);

        await using var context = server.NewContext();
        var rule = await context.IdentityProviderMappingRules
            .Include(r => r.Provider)
            .FirstAsync(r => r.Provider!.Slug == "maps-a-template");
        Assert.Equal("mapped-set-renamed", rule.RoleName);
    }

    /// <summary>
    /// The issuer is half the federated key and the origin every token is
    /// validated against. Over plain HTTP on a real network, whoever answers
    /// first decides who your users are — so it is refused, with loopback
    /// exempted so a development Authentik can be registered.
    /// </summary>
    [Theory]
    [InlineData("http://auth.example.invalid", "provider.issuer.insecure")]
    [InlineData("auth.example.invalid", "provider.issuer.invalid")]
    [InlineData("", "provider.issuer.required")]
    // **A scheme with no host used to ride in on the loopback exemption.** The
    // rule was "https, or anything on loopback", and `Uri.IsLoopback` is true of
    // a `file:` URL precisely because it has no host to be somewhere else.
    [InlineData("file:///etc/passwd", "provider.issuer.insecure")]
    [InlineData("ftp://auth.example.invalid", "provider.issuer.insecure")]
    public async Task An_issuer_is_https_or_loopback(string issuer, string code)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug = "bad-issuer",
            displayName = "Bad",
            issuer,
            clientId = "algojudge",
            clientSecret = Secret,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(code, await Code(refused));
    }

    [Fact]
    public async Task Loopback_is_allowed_over_http_so_a_development_provider_can_be_registered()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug = "local-authentik",
            displayName = "Local Authentik",
            issuer = "http://localhost:9000/application/o/algojudge",
            clientId = "algojudge",
            clientSecret = Secret,
        });

        await Sign.Succeeded(created);
    }

    /// <summary>
    /// An open back channel with no secret is an endpoint anybody may post an
    /// account deletion to.
    /// </summary>
    [Fact]
    public async Task The_deletion_channel_cannot_be_opened_without_a_secret()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug = "open-channel",
            displayName = "Open",
            issuer = "https://auth.example.invalid",
            clientId = "algojudge",
            clientSecret = Secret,
            deletionChannelEnabled = true,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("provider.deletionSecret.required", await Code(refused));
    }

    /// <summary>
    /// Two rules for one claim value is a question about ordering, and this
    /// model deliberately has no answer to it.
    /// </summary>
    [Fact]
    public async Task One_claim_value_is_mapped_once()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("mapped-twice", new[]
            {
                new { claimValue = "staff", roleName = "participant" },
                new { claimValue = "staff", roleName = "participant" },
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("provider.rule.duplicate", await Code(refused));
    }

    /// <summary>
    /// Deleting a provider that people sign in through decides something about
    /// their accounts, not about the registration. Disabling is the reversible
    /// act, and it is what the refusal points at.
    /// </summary>
    [Fact]
    public async Task A_provider_with_linked_accounts_is_not_deleted()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.NewAccountAsync(server, "federated-person");

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", Registration("has-people"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // Written directly: linking happens at sign-in, which does not exist yet.
        await using (var context = server.NewContext())
        {
            context.UserIdentities.Add(new UserIdentity
            {
                UserId = await UserIdAsync("federated-person"),
                ProviderId = Guid.Parse(id!),
                Subject = "sub-0001",
            });
            await context.SaveChangesAsync();
        }

        var refused = await admin.DeleteAsync($"/api/v1/identity/providers/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("provider.linked", await Code(refused));

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/identity/providers/{id}");
        Assert.Equal(1, read.GetProperty("linkedAccounts").GetInt32());

        // An empty one goes, so the refusal is about the links and not about
        // deletion being broken.
        var empty = await admin.PostAsJsonAsync("/api/v1/identity/providers", Registration("has-nobody"));
        await Sign.Succeeded(empty);
        var emptyId = (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/identity/providers/{emptyId}")).StatusCode);
    }

    /// <summary>
    /// A claim path is dotted names and never an expression — that is where
    /// "configuration, not code" is enforced rather than merely intended.
    /// </summary>
    [Theory]
    [InlineData("groups")]
    [InlineData("realm_access.roles")]
    public async Task A_claim_path_may_be_a_dotted_name(string path) =>
        await Sign.Succeeded(await (await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword))
            .PostAsJsonAsync("/api/v1/identity/providers", new
            {
                slug = "path-" + Guid.NewGuid().ToString("N")[..8],
                displayName = "Paths",
                issuer = "https://auth.example.invalid",
                clientId = "algojudge",
                clientSecret = Secret,
                claimPath = path,
            }));

    [Theory]
    [InlineData("groups[0]")]
    [InlineData("groups..roles")]
    [InlineData("user.groups | first")]
    public async Task A_claim_path_is_not_an_expression(string path)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug = "expressive",
            displayName = "Expressive",
            issuer = "https://auth.example.invalid",
            clientId = "algojudge",
            clientSecret = Secret,
            claimPath = path,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("provider.claimPath.invalid", await Code(refused));
    }

    /// <summary>The settings body a caller written before the redirect existed sends.</summary>
    private static object LegacySettings() => new
    {
        localRegistrationEnabled = false,
        requireEmail = false,
        requireConfirmedEmail = false,
        showLogo = true,
        showLocalSignIn = true,
        accountDeletionEnabled = true,
    };

    /// <summary>The same, plus a sign-in redirect. Blank clears it.</summary>
    private static object SettingsRedirecting(string slug) => new
    {
        localRegistrationEnabled = false,
        requireEmail = false,
        requireConfirmedEmail = false,
        showLogo = true,
        showLocalSignIn = true,
        accountDeletionEnabled = true,
        signInRedirectProvider = slug,
    };

    /// <summary>
    /// Both columns at once.
    /// <para>
    /// Stated separately because the two are separate: an installation may take
    /// its sign-ins from a directory and still run its own sign-ups. Nothing
    /// touched the register column before this overload existed.
    /// </para>
    /// </summary>
    private static object SettingsRedirecting(string slug, string register) => new
    {
        localRegistrationEnabled = false,
        requireEmail = false,
        requireConfirmedEmail = false,
        showLogo = true,
        showLocalSignIn = true,
        accountDeletionEnabled = true,
        signInRedirectProvider = slug,
        registerRedirectProvider = register,
    };

    /// <summary>The panel's own read of the two columns, unfiltered.</summary>
    private static async Task<JsonElement> RedirectsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/instance/redirects");
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>One half of that answer. Absent is a real answer for two of three.</summary>
    private static (string State, string? Slug, string? Name) Half(
        JsonElement redirects, string which)
    {
        var half = redirects.GetProperty(which);
        return (
            half.GetProperty("state").GetString()!,
            half.TryGetProperty("slug", out var slug) ? slug.GetString() : null,
            half.TryGetProperty("displayName", out var name) ? name.GetString() : null);
    }

    /// <summary>Absent is a real answer here, so this must not be a GetProperty.</summary>
    private static string? RedirectOn(JsonElement instance) =>
        instance.TryGetProperty("signInRedirectProvider", out var value) ? value.GetString() : null;

    private static object Provider(string slug, bool enabled) => new
    {
        slug,
        displayName = "University SSO",
        issuer = "https://auth.example.invalid/application/o/algojudge",
        clientId = "algojudge",
        clientSecret = Secret,
        claimPath = "groups",
        enabled,
        mappingRules = Array.Empty<object>(),
    };

    /// <summary>
    /// A redirect survives a save that does not mention it.
    ///
    /// <para>
    /// **This is the whole reason the field is read with `is { }` rather than the
    /// way `Name` is read.** The settings endpoint replaces the entire object, and
    /// three callers that predate this field omit it — including the manager
    /// panel's own form. Under the `Name` reading, an administrator toggling
    /// "Show the mark" would switch the sign-in path off and nothing would say so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_redirect_survives_a_save_that_does_not_mention_it()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            "/api/v1/identity/providers", Registration("redirect-kept")));

        await Sign.Succeeded(
            await admin.PutAsJsonAsync("/api/v1/instance", SettingsRedirecting("redirect-kept")));
        Assert.Equal("redirect-kept",
            RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        // The older body, unchanged, exactly as it is sent elsewhere in this suite.
        await Sign.Succeeded(await admin.PutAsJsonAsync("/api/v1/instance", LegacySettings()));

        Assert.Equal("redirect-kept",
            RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));
    }

    /// <summary>
    /// An empty string is how an operator turns the redirect off.
    ///
    /// <para>
    /// The other half of the test above: absent has to mean *leave it alone*, so
    /// something else has to mean *clear*, or the setting could be switched on
    /// and never off again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_empty_string_clears_the_redirect()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            "/api/v1/identity/providers", Registration("redirect-cleared")));

        await Sign.Succeeded(
            await admin.PutAsJsonAsync("/api/v1/instance", SettingsRedirecting("redirect-cleared")));
        Assert.Equal("redirect-cleared",
            RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        await Sign.Succeeded(
            await admin.PutAsJsonAsync("/api/v1/instance", SettingsRedirecting("")));

        Assert.Null(RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));
    }

    /// <summary>
    /// A redirect may only name a provider that would sign somebody in.
    ///
    /// <para>
    /// **The disabled case is the one worth the test.** A slug that is not a slug
    /// and a slug nobody registered are obvious; a provider that exists and is
    /// switched off looks fine in the panel and answers 404 at the challenge, so
    /// accepting it would put every visitor on a dead end with no way back to the
    /// form except knowing about `?admin=true`. Nothing else in this suite would
    /// notice the `Enabled` half of that check going missing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_redirect_must_name_a_provider_that_signs_people_in()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("redirect-refused"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // Registered, and then switched off.
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("redirect-refused", enabled: false)));

        foreach (var slug in new[] { "nobody-registered-this", "NOT A SLUG", "redirect-refused" })
        {
            var refused = await admin.PutAsJsonAsync(
                "/api/v1/instance", SettingsRedirecting(slug));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("instance.signInRedirect.unknown", await Code(refused));
        }
    }

    /// <summary>
    /// Disabling a provider stops the redirect and does not forget it.
    ///
    /// <para>
    /// **The lockout test.** The column keeps what an operator wrote and the
    /// answer is filtered against the providers actually on offer, so switching a
    /// provider off on a Monday morning leaves the sign-in screen drawing itself
    /// rather than sending everybody to a 404 — and switching it back on restores
    /// the redirect without anybody having to remember what it named.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Disabling_a_provider_takes_the_redirect_out_of_force_and_does_not_forget_it()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("redirect-forgotten"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("redirect-forgotten")));
        Assert.Equal("redirect-forgotten",
            RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("redirect-forgotten", enabled: false)));

        // Off the answer…
        Assert.Null(RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        // …and still in the column, which is what makes turning it back on free.
        await using (var context = server.NewContext())
        {
            var stored = await context.Instance.AsNoTracking().FirstAsync();
            Assert.Equal("redirect-forgotten", stored.SignInRedirectProvider);
        }

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("redirect-forgotten", enabled: true)));

        Assert.Equal("redirect-forgotten",
            RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        // Left as it was found, so nothing after this reads a redirect it did not set.
        await Sign.Succeeded(
            await admin.PutAsJsonAsync("/api/v1/instance", SettingsRedirecting("")));
    }

    /// <summary>
    /// **The panel is told what the public answer withholds.**
    /// <para>
    /// The screen that writes these two columns has to be able to read them, and
    /// `getInstanceInfo` cannot tell it: that answer is filtered to what a
    /// signed-out visitor should act on. Both halves are asserted here, in one
    /// test, so neither can be relaxed without the other going red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_panel_is_told_the_redirect_the_public_answer_hides()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("panel-reads-it"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("panel-reads-it")));
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("panel-reads-it", enabled: false)));

        var (state, slug, name) = Half(await RedirectsAsync(admin), "signIn");
        Assert.Equal("disabled", state);
        Assert.Equal("panel-reads-it", slug);
        Assert.Equal("University SSO", name);

        // The other half of the same fact: a visitor still learns nothing.
        Assert.Null(RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        await Sign.Succeeded(
            await admin.PutAsJsonAsync("/api/v1/instance", SettingsRedirecting("")));
    }

    /// <summary>
    /// **Four states, and each reported as itself.**
    /// <para>
    /// `unregistered` is the one that matters and the one a shortcut would lose:
    /// pre-configuration writes a slug before any provider exists, so an
    /// installation arrives in that state at its first start. Computing the
    /// answer from `Enabled` alone collapses it into `disabled` and the screen
    /// says something false about a provider nobody has registered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_state_a_redirect_can_be_in_is_reported_as_itself()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        Assert.Equal("none", Half(await RedirectsAsync(admin), "signIn").State);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("four-states"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("four-states", "four-states")));
        Assert.Equal("inForce", Half(await RedirectsAsync(admin), "signIn").State);
        Assert.Equal("inForce", Half(await RedirectsAsync(admin), "register").State);

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("four-states", enabled: false)));
        Assert.Equal("disabled", Half(await RedirectsAsync(admin), "signIn").State);

        // Removed entirely, which leaves the column naming nobody — the state
        // pre-configuration ships in.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/identity/providers/{id}")).StatusCode);

        var gone = Half(await RedirectsAsync(admin), "signIn");
        Assert.Equal("unregistered", gone.State);
        Assert.Equal("four-states", gone.Slug);
        Assert.Null(gone.Name);

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("", "")));
        Assert.Equal("none", Half(await RedirectsAsync(admin), "signIn").State);
        Assert.Equal("none", Half(await RedirectsAsync(admin), "register").State);
    }

    /// <summary>Only the permission that writes these columns may read them.</summary>
    [Fact]
    public async Task Only_instance_update_reads_the_redirects()
    {
        var nobody = await Sign.NewAccountAsync(server, "redirect-outsider");
        var anonymous = server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/instance/redirects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await nobody.GetAsync("/api/v1/instance/redirects")).StatusCode);
    }

    /// <summary>
    /// **A save that says nothing about a redirect leaves it alone**, and that is
    /// the contract the panel depends on to stop destroying one.
    /// <para>
    /// The panel seeds its form from the filtered public answer, so while a
    /// provider is switched off it cannot see the slug it holds. It therefore
    /// omits the field rather than sending what it can see — and absent has to go
    /// on meaning *leave it alone* for that to be safe, including on a request
    /// that changes something else entirely.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_save_that_omits_the_redirect_keeps_it_while_the_provider_is_off()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("omitted-survives"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("omitted-survives", "omitted-survives")));
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("omitted-survives", enabled: false)));

        // Somebody saves the tab for an unrelated reason. The body states no
        // redirect at all, exactly as the panel now does.
        await Sign.Succeeded(await admin.PutAsJsonAsync("/api/v1/instance", LegacySettings()));

        var after = await RedirectsAsync(admin);
        Assert.Equal(("disabled", "omitted-survives"),
            (Half(after, "signIn").State, Half(after, "signIn").Slug));
        Assert.Equal(("disabled", "omitted-survives"),
            (Half(after, "register").State, Half(after, "register").Slug));

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("omitted-survives", enabled: true)));
        Assert.Equal("omitted-survives",
            RedirectOn(await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/instance")));

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("", "")));
    }

    /// <summary>
    /// **Blank still clears, whatever state the redirect is in.** Absent meaning
    /// *leave alone* must not turn into "a redirect whose provider is off cannot
    /// be removed": the operator chose the empty option and that is a decision.
    /// </summary>
    [Fact]
    public async Task A_redirect_whose_provider_is_off_can_still_be_cleared()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("cleared-while-off"));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("cleared-while-off")));
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/identity/providers/{id}", Provider("cleared-while-off", enabled: false)));

        await Sign.Succeeded(await admin.PutAsJsonAsync(
            "/api/v1/instance", SettingsRedirecting("")));

        Assert.Equal("none", Half(await RedirectsAsync(admin), "signIn").State);
    }

    private static async Task<string> Read(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private async Task<string> UserIdAsync(string login)
    {
        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.UserName == login);
        return user.Id;
    }
}
