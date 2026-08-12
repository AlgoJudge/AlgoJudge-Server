using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The two amounts of detail storage may be reported in.
/// <para>
/// A65 and A65b are the same sentence read from both ends: anybody may learn
/// that storage is unwell, and only somebody standing on the machine may learn
/// which store, where it is, or what it said. The tests are mostly about the
/// second half, because the first is a promise about what is <b>not</b> in an
/// answer — and that is the kind of promise nothing but a test keeps.
/// </para>
/// </summary>
[Collection("server")]
public class StorageHealthTests(ServerFixture server)
{
    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task The_public_answer_says_one_word_and_names_nothing()
    {
        var anonymous = server.CreateClient();
        var health = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/health");

        var word = health.GetProperty("storage").GetString();
        Assert.Contains(word, new[] { "ok", "degraded" });

        // **Exactly these properties.** A65c is a promise about absence, so the
        // check has to be that nothing else is there — a store id added to this
        // document later would otherwise be disclosed to the internet and no
        // assertion would notice.
        var named = health.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.Contains("status", named);
        Assert.Contains("storage", named);
        // `maintenance` is present only while withdrawn, so the assertion is
        // that nothing *else* ever appears — a store id added to this document
        // later would be disclosed to the internet, and only this would notice.
        Assert.Empty(named.Except(["status", "maintenance", "storage"]));
    }

    /// <summary>
    /// A file whose store the configuration does not have.
    /// <para>
    /// The realistic way in is an operator retiring a store id while rows still
    /// name it, or a database restored somewhere the volumes did not follow.
    /// Either way the installation is not down — everything in a healthy store
    /// still serves — but somebody has to be told.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_store_that_files_name_and_nobody_configured_is_reported()
    {
        var retired = $"retired-{Guid.NewGuid():N}"[..20];
        var fileId = await StoreOneAsync();

        await using (var context = server.NewContext())
        {
            var file = await context.Files.FirstAsync(f => f.Id == fileId);
            file.StorageId = retired;
            await context.SaveChangesAsync();
        }

        try
        {
            var anonymous = server.CreateClient();
            var health = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/health");
            Assert.Equal("degraded", health.GetProperty("storage").GetString());

            var report = await Operator(server.CreateClient())
                .GetFromJsonAsync<JsonElement>("/api/v1/admin/storage");

            var unconfigured = report.GetProperty("unconfigured")
                .EnumerateArray().Select(id => id.GetString()).ToList();

            // The operator's answer names it. The public one did not.
            Assert.Contains(retired, unconfigured);
        }
        finally
        {
            // Put it back: the suite shares a database, and a permanently
            // degraded installation would be a state every later test ran under.
            await using var context = server.NewContext();
            var file = await context.Files.FirstAsync(f => f.Id == fileId);
            file.StorageId = Database.Models.File.InitialStorageId;
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task The_operator_surface_counts_what_each_store_holds()
    {
        await StoreOneAsync();

        var report = await Operator(server.CreateClient())
            .GetFromJsonAsync<JsonElement>("/api/v1/admin/storage");

        var stores = report.GetProperty("stores").EnumerateArray().ToList();
        Assert.NotEmpty(stores);

        var configured = stores.Single(store => store.GetProperty("isDefault").GetBoolean());
        Assert.True(configured.GetProperty("reachable").GetBoolean());
        Assert.True(configured.GetProperty("smokeTestPassed").GetBoolean());
        Assert.True(configured.GetProperty("files").GetInt64() > 0);
        Assert.True(configured.GetProperty("sizeBytes").GetInt64() > 0);
    }

    /// <summary>
    /// A store nothing has written to still appears, holding zero.
    /// <para>
    /// That zero is the whole point of the number (§11): it is how an operator
    /// answers "is it safe to switch this one off". A store that vanished from
    /// the report when it emptied would answer it by being missing, which reads
    /// identically to a configuration mistake.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_store_holding_nothing_still_says_so()
    {
        var volume = Path.Combine(Path.GetTempPath(), $"algojudge-empty-{Guid.NewGuid():N}");

        using var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Stores:pg:Kind", "postgres");
            builder.UseSetting("Storage:Stores:spare:Kind", "filesystem");
            builder.UseSetting("Storage:Stores:spare:Path", volume);
            // Named rather than inferred: with two stores the first key wins,
            // and which one that is should not depend on a dictionary.
            builder.UseSetting("Storage:Default", "pg");
        });

        try
        {
            var report = await Operator(host.CreateClient())
                .GetFromJsonAsync<JsonElement>("/api/v1/admin/storage");

            var spare = report.GetProperty("stores").EnumerateArray()
                .Single(store => store.GetProperty("id").GetString() == "spare");

            Assert.Equal(0, spare.GetProperty("files").GetInt64());
            Assert.Equal(0, spare.GetProperty("sizeBytes").GetInt64());
            Assert.False(spare.GetProperty("isDefault").GetBoolean());

            // And it was actually asked, rather than assumed well because it is
            // new: a filesystem store proves itself by writing and reading back.
            Assert.True(spare.GetProperty("smokeTestPassed").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(volume)) Directory.Delete(volume, recursive: true);
        }
    }

    /// <summary>
    /// The same refusal the rest of <c>/admin</c> gives, and for the same reason:
    /// a caller who is not on the machine cannot tell this endpoint from one that
    /// does not exist.
    /// </summary>
    [Fact]
    public async Task Where_the_files_are_is_not_a_question_from_the_network()
    {
        // **With the token**, so that what refuses is the interface and not a
        // missing header. Without it this would answer 404 anyway and prove
        // nothing about where the caller is standing.
        var client = Operator(server.CreateClient());
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/storage");
        request.Headers.Add(ServerFixture.PeerHeader, "203.0.113.7");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the same request from the machine itself is answered, so the test
        // above is about the address rather than about anything else being wrong.
        Assert.Equal(
            HttpStatusCode.OK,
            (await Operator(server.CreateClient()).GetAsync("/api/v1/admin/storage")).StatusCode);
    }

    /// <summary>
    /// The whole <c>/admin</c> group asks for both: the loopback interface and
    /// the configured token. The fixture arrives on loopback already; this is
    /// the other half.
    /// </summary>
    private static HttpClient Operator(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        return client;
    }

    private async Task<Guid> StoreOneAsync()
    {
        var client = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var bytes = Encoding.UTF8.GetBytes($"health {Guid.NewGuid()}");

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "health.txt" },
            { new StringContent(Sha256Of(bytes)), "sha256" },
        };

        var response = await client.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(stored.GetProperty("id").GetString()!);
    }
}
