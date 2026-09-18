using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a request may leave out of the modules object.
/// <para>
/// <b>An archive exported before a module existed must still import.</b> The
/// exchange archive is written in the browser and carries the activity's
/// modules verbatim, so one written before 2026-09-12 names <c>questions</c>
/// and nothing else. While both members were <c>required</c> the serializer
/// refused it with a 400 before any handler ran — and omitting the object
/// entirely was accepted all along, which made the contract strict about the
/// one shape an older export writes and lenient about the one it never does.
/// </para>
/// </summary>
[Collection("server-2")]
public class ActivityModulesTests(ServerFixture server)
{
    private static object Input(string slug, object? modules) => modules is null
        ? new { slug, name = "Modules", type = "contest@1", rankingType = "icpc", timeZone = "Europe/Warsaw" }
        : new { slug, name = "Modules", type = "contest@1", rankingType = "icpc", timeZone = "Europe/Warsaw", modules };

    private static string NewSlug() => "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();

    [Fact]
    public async Task An_object_naming_one_module_is_accepted_and_the_other_takes_its_default()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = NewSlug();

        // Exactly what a bundle exported before printouts existed carries.
        var response = await admin.PostAsJsonAsync("/api/v1/activities", Input(slug, new { questions = true }));
        Assert.True(response.IsSuccessStatusCode,
            $"a request naming only `questions` was refused with {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var modules = created.GetProperty("modules");
        Assert.True(modules.GetProperty("questions").GetBoolean());
        // Not merely absent: printouts is opt-in, so the answer is `false`.
        Assert.False(modules.GetProperty("printouts").GetBoolean());
    }

    [Fact]
    public async Task A_module_an_update_does_not_name_keeps_the_value_it_had()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = NewSlug();
        await Build.PostAsync(admin, "/api/v1/activities", Input(slug, new { questions = true, printouts = true }));

        // Names one module. The other is not mentioned, so it is not being
        // asked about -- which is what every optional field on this input means.
        var response = await admin.PutAsJsonAsync($"/api/v1/activities/{slug}", Input(slug, new { questions = false }));
        await Sign.Succeeded(response);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        var modules = updated.GetProperty("modules");
        Assert.False(modules.GetProperty("questions").GetBoolean());
        Assert.True(modules.GetProperty("printouts").GetBoolean());
    }

    [Fact]
    public async Task An_answer_still_names_every_module()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = NewSlug();

        // **The read contract did not become lenient with the write one.** A
        // reader must never have to guess what an absent member meant, and an
        // answer that omitted one would say nothing about which -- the Server
        // omits nulls when it writes.
        await Build.PostAsync(admin, "/api/v1/activities", Input(slug, null));
        var read = await Build.GetAsync(admin, $"/api/v1/manager/activities/{slug}");
        var modules = read.GetProperty("modules");
        Assert.Equal(JsonValueKind.True, modules.GetProperty("questions").ValueKind);
        Assert.Equal(JsonValueKind.False, modules.GetProperty("printouts").ValueKind);
    }
}
