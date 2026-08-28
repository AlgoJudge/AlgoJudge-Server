using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Rotating the tool's own key, which is two acts and no schedule (decided
/// 2026-08-15).
///
/// <para>
/// <b>The overlap is the whole point.</b> A platform caches a key set and
/// refetches on its own terms, so a rotation that takes the old key out at the
/// same moment refuses everything signed before that refetch — grades stop
/// posting, in somebody else's installation, until they happen to look. So
/// rotating leaves the old key published and a separate, deliberate act closes
/// the window.
/// </para>
/// </summary>
[Collection("server-1")]
public class LtiKeyRotationTests(ServerFixture server)
{
    [Fact]
    public async Task Rotating_mints_a_new_key_and_keeps_publishing_the_old_one()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();

        var before = await KidsAsync(anonymous);
        Assert.NotEmpty(before);

        var rotated = await Post(await manager.PostAsync("/api/v1/lti/keys/rotate", null));
        var minted = rotated.GetProperty("kid").GetString()!;

        Assert.True(rotated.GetProperty("signing").GetBoolean(), "the new key is the one that signs");
        Assert.DoesNotContain(minted, before);

        // Both halves of the overlap, in the set a platform actually fetches.
        var after = await KidsAsync(anonymous);
        Assert.Contains(minted, after);
        foreach (var old in before)
        {
            Assert.Contains(old, after);
        }

        // And the old ones stopped signing, without leaving the set.
        var listed = await manager.GetFromJsonAsync<JsonElement>("/api/v1/lti/keys");
        var signing = listed.EnumerateArray()
            .Where(k => k.GetProperty("signing").GetBoolean())
            .Select(k => k.GetProperty("kid").GetString())
            .ToList();
        Assert.Equal([minted], signing);

        foreach (var key in listed.EnumerateArray()
                     .Where(k => k.GetProperty("kid").GetString() != minted))
        {
            Assert.NotNull(key.GetProperty("retiredAt").GetString());
        }
    }

    /// <summary>
    /// The second act, and the reason it is separate: only a person can tell that
    /// every platform has refetched.
    /// </summary>
    [Fact]
    public async Task Withdrawing_a_retired_key_takes_it_out_of_the_published_set()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var anonymous = server.CreateClient();

        var rotated = await Post(await manager.PostAsync("/api/v1/lti/keys/rotate", null));
        var minted = rotated.GetProperty("kid").GetString()!;

        var listed = await manager.GetFromJsonAsync<JsonElement>("/api/v1/lti/keys");
        var retired = listed.EnumerateArray()
            .First(k => k.GetProperty("kid").GetString() != minted)
            .GetProperty("kid").GetString()!;

        Assert.Contains(retired, await KidsAsync(anonymous));

        var withdrawn = await manager.PostAsync($"/api/v1/lti/keys/{retired}/withdraw", null);
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);

        var after = await KidsAsync(anonymous);
        Assert.DoesNotContain(retired, after);
        Assert.Contains(minted, after);

        // **And the private half is gone with it.** A withdrawn key has no use
        // left, and a private key nobody needs is only something to lose.
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlgoJudge.Server.Lti.Data.LtiDbContext>();
        Assert.False(await db.ToolKeys.AnyAsync(k => k.Kid == retired));
    }

    /// <summary>
    /// Withdrawing the key that signs would not be a rotation. It would be the
    /// tool going quiet: every signature it makes from then on verifies against
    /// nothing any platform holds.
    /// </summary>
    [Fact]
    public async Task The_key_that_signs_cannot_be_withdrawn()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var listed = await manager.GetFromJsonAsync<JsonElement>("/api/v1/lti/keys");
        var signing = listed.EnumerateArray()
            .First(k => k.GetProperty("signing").GetBoolean())
            .GetProperty("kid").GetString()!;

        var refused = await manager.PostAsync($"/api/v1/lti/keys/{signing}/withdraw", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lti.key.signing", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// Rotating is not something a participant does, and neither is reading the
    /// list. The set itself stays anonymous — that is what a platform fetches.
    /// </summary>
    [Fact]
    public async Task Rotation_is_behind_the_permission_that_governs_providers()
    {
        var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/lti/jwks.json")).StatusCode);

        var participant = await Sign.InAsync(
            server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

        foreach (var refused in new[]
                 {
                     await participant.GetAsync("/api/v1/lti/keys"),
                     await participant.PostAsync("/api/v1/lti/keys/rotate", null),
                     await participant.PostAsync("/api/v1/lti/keys/whatever/withdraw", null),
                 })
        {
            Assert.True(refused.StatusCode is HttpStatusCode.Forbidden,
                $"a participant reached {refused.RequestMessage?.RequestUri} with {(int)refused.StatusCode}");
        }
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private static async Task<JsonElement> Post(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The key ids a platform would actually see.</summary>
    private static async Task<IReadOnlyList<string>> KidsAsync(HttpClient anonymous)
    {
        var body = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/lti/jwks.json");
        return body.GetProperty("keys").EnumerateArray()
            .Select(k => k.GetProperty("kid").GetString()!)
            .ToList();
    }
}
