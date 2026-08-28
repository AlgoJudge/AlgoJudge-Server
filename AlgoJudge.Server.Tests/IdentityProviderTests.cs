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
    /// its initialiser, a rule reached through a *tracked* parent arrives with a
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
                new { claimValue = "staff", templateName = "manager" },
                new { claimValue = "students", templateName = "participant" },
            }));
        await Sign.Succeeded(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // One value keeps its rule, one changes template, one goes, one arrives.
        var edited = await admin.PutAsJsonAsync($"/api/v1/identity/providers/{id}",
            Registration("editable", new[]
            {
                new { claimValue = "staff", templateName = "participant" },
                new { claimValue = "guests", templateName = "participant" },
            }));
        await Sign.Succeeded(edited);

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/identity/providers/{id}");
        var rules = read.GetProperty("mappingRules").EnumerateArray()
            .ToDictionary(r => r.GetProperty("claimValue").GetString()!,
                          r => r.GetProperty("templateName").GetString());

        Assert.Equal(["guests", "staff"], rules.Keys.OrderBy(k => k));
        Assert.Equal("participant", rules["staff"]);

        // Again, with the same body: an edit that changes nothing is the one an
        // operator makes by pressing Save twice.
        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/identity/providers/{id}",
            Registration("editable", new[]
            {
                new { claimValue = "staff", templateName = "participant" },
                new { claimValue = "guests", templateName = "participant" },
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

        var template = await admin.PostAsJsonAsync("/api/v1/permission-templates", new
        {
            name = "back-door",
            permissions = new[] { "system:administrator" },
        });
        await Sign.Succeeded(template);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("with-a-back-door", new[]
            {
                new { claimValue = "staff", templateName = "back-door" },
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
            permissions = new[] { "provider:manage", "template:read" },
        });
        await Sign.Succeeded(granted);

        // `manager` carries `submission:read:all` and much else this account does
        // not hold.
        var refused = await operatorClient.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("over-reaching", new[]
            {
                new { claimValue = "lecturers", templateName = "manager" },
            }));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("provider.rule.excess", await Code(refused));

        // And what they *do* hold goes through, so the guard is not simply
        // refusing everything.
        var narrow = await admin.PostAsJsonAsync("/api/v1/permission-templates", new
        {
            name = "just-templates",
            permissions = new[] { "template:read" },
        });
        await Sign.Succeeded(narrow);

        var allowed = await operatorClient.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("within-reach", new[]
            {
                new { claimValue = "lecturers", templateName = "just-templates" },
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

        var template = await admin.PostAsJsonAsync("/api/v1/permission-templates", new
        {
            name = "mapped-set",
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(template);
        var templateId = (await template.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers",
            Registration("maps-a-template", new[]
            {
                new { claimValue = "students", templateName = "mapped-set" },
            }));
        await Sign.Succeeded(created);

        var refused = await admin.DeleteAsync($"/api/v1/permission-templates/{templateId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("template.mapped", await Code(refused));

        // A rename has to reach the rule, or the provider goes on naming
        // something that no longer answers.
        var renamed = await admin.PutAsJsonAsync($"/api/v1/permission-templates/{templateId}", new
        {
            name = "mapped-set-renamed",
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(renamed);

        await using var context = server.NewContext();
        var rule = await context.IdentityProviderMappingRules
            .Include(r => r.Provider)
            .FirstAsync(r => r.Provider!.Slug == "maps-a-template");
        Assert.Equal("mapped-set-renamed", rule.TemplateName);
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
                new { claimValue = "staff", templateName = "participant" },
                new { claimValue = "staff", templateName = "participant" },
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
