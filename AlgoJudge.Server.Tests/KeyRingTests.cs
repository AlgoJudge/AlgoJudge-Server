using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The keys that encrypt a session cookie, shared and durable.
/// <para>
/// <b>Two hosts, one database, one cookie.</b> That is the subject: a cookie
/// minted by one instance has to be readable by another, which the framework's
/// default ring — process-local and not durable — could not do. Nothing here
/// restarts anything; a second host on the same database is the same question
/// asked more cheaply, and it is the one a load balancer asks on every request.
/// </para>
/// <para>
/// <b>The cookie test alone does not prove the store, and the sabotage said
/// so.</b> With <c>PersistKeysToDbContext</c> removed it still passed: the
/// default ring persists to a directory under the machine's profile, and two
/// hosts in one test process share that directory exactly as they would share a
/// table. What pins the keys to the database is
/// <see cref="KeyRingStoreTests.The_ring_follows_the_database_and_not_the_machine"/>,
/// which gives one host a database of its own. The cookie tests stay because
/// they are the promise in the words it was made in.
/// </para>
/// </summary>
[Collection("server-2")]
public class KeyRingTests(ServerFixture server)
{
    /// <summary>
    /// This product's own "who am I", not `MapIdentityApi`'s `manage/info`.
    /// <para>
    /// Measured 2026-08-27 against the development stack: the framework's
    /// endpoint throws <c>NotSupportedException("Users must have an email")</c>
    /// for an account without an address, and this product has accounts without
    /// one on purpose — the seeded administrator is one. A 500 there would have
    /// read as the key ring failing.
    /// </para>
    /// </summary>
    private const string Info = "/api/v1/account";

    /// <summary>
    /// Signs in and hands back the cookies as a browser would send them.
    /// <para>
    /// Cookie handling is <b>off</b> on purpose: the point is to carry the
    /// cookie to a different host, and a client that manages its own jar keeps
    /// it where a test cannot reach.
    /// </para>
    /// </summary>
    private static async Task<string> SignInForCookiesAsync(
        WebApplicationFactory<Program> host, string login)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true",
            new { email = login, password = Sign.Password });

        Assert.True(response.IsSuccessStatusCode, $"signing in returned {(int)response.StatusCode}");

        var cookies = response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])
            .ToArray();

        Assert.NotEmpty(cookies);
        return string.Join("; ", cookies);
    }

    private static async Task<HttpResponseMessage> WhoAmIAsync(
        WebApplicationFactory<Program> host, string cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Info);
        request.Headers.Add("Cookie", cookies);

        return await host
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .SendAsync(request);
    }

    private async Task<string> SomebodyAsync()
    {
        var login = "kr-" + Guid.NewGuid().ToString("N")[..10];
        (await Sign.NewAccountAsync(server, login)).Dispose();
        return login;
    }

    /// <summary>One instance mints, another accepts.</summary>
    [Fact]
    public async Task A_cookie_from_one_host_is_accepted_by_another()
    {
        var cookies = await SignInForCookiesAsync(server, await SomebodyAsync());

        // A second host: its own service provider, its own Data Protection
        // registration, the same database.
        using var second = server.WithWebHostBuilder(_ => { });

        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(second, cookies)).StatusCode);
    }

    /// <summary>
    /// <b>And by a host deployed from somewhere else on disk.</b> Data Protection
    /// mixes an application discriminator into every purpose, and with nothing
    /// setting one that discriminator is the <i>content root</i> — so two
    /// containers built from different paths would silently not share a ring
    /// while sharing the table, the certificate and everything else.
    /// <c>SetApplicationName</c> is the one line that stops it, and this is what
    /// holds that line in place.
    /// <para>
    /// The content root rather than the application name: renaming the
    /// application sends MVC looking for an assembly of that name, which fails
    /// long before any of this.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cookie_is_accepted_by_a_host_rooted_somewhere_else()
    {
        var cookies = await SignInForCookiesAsync(server, await SomebodyAsync());

        var elsewhere = Directory.CreateTempSubdirectory("algojudge-root-").FullName;
        try
        {
            using var moved = server.WithWebHostBuilder(
                builder => builder.UseContentRoot(elsewhere));

            Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(moved, cookies)).StatusCode);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>
    /// And the same walk with the keys in memory, which must fail — otherwise
    /// nothing above is saying anything about a shared ring.
    /// </summary>
    [Fact]
    public async Task An_ephemeral_key_ring_does_not_travel()
    {
        var login = await SomebodyAsync();

        using var first = server.WithWebHostBuilder(
            builder => builder.UseSetting(KeyRing.KindSetting, KeyRing.Ephemeral));
        using var second = server.WithWebHostBuilder(
            builder => builder.UseSetting(KeyRing.KindSetting, KeyRing.Ephemeral));

        var cookies = await SignInForCookiesAsync(first, login);

        // Readable where it was minted…
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(first, cookies)).StatusCode);

        // …and nowhere else, which is what this Server did by accident until
        // 2026-08-27.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(second, cookies)).StatusCode);
    }

    /// <summary>The ring is in the database, not in a directory nobody backs up.</summary>
    [Fact]
    public async Task The_key_ring_is_written_to_the_database()
    {
        await SignInForCookiesAsync(server, await SomebodyAsync());

        await using var context = server.NewContext();
        Assert.NotEmpty(await context.DataProtectionKeys.ToListAsync());
    }

    /// <summary>
    /// A kind this Server does not implement stops it, and the refusal names the
    /// setting — Redis being the one somebody will actually try.
    /// </summary>
    [Fact]
    public void An_unimplemented_kind_refuses_to_start()
    {
        using var host = server.WithWebHostBuilder(
            builder => builder.UseSetting(KeyRing.KindSetting, "redis"));

        var refusal = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(KeyRing.KindSetting, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("redis", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ephemeral</c> outside Development is an installation that signs
    /// everybody out on every restart, and it presents as flakiness rather than
    /// as configuration — so it is refused rather than warned about.
    /// </summary>
    [Fact]
    public void Ephemeral_refuses_to_start_outside_development()
    {
        using var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(KeyRing.KindSetting, KeyRing.Ephemeral);
            builder.UseEnvironment("Production");
        });

        var refusal = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(KeyRing.KindSetting, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(KeyRing.Database, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A certificate with nothing to encrypt is a contradiction, and guessing
    /// which half was meant would leave an installation believing its keys are
    /// protected while none are being stored.
    /// </summary>
    [Fact]
    public void A_certificate_with_an_ephemeral_ring_refuses_to_start()
    {
        using var certificate = Certificate.Scratch();

        using var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(KeyRing.KindSetting, KeyRing.Ephemeral);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Path", certificate.Path);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Password", Certificate.Password);
        });

        var refusal = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains(KeyRing.CertificatesSetting, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A certificate that is not there stops the Server while somebody is still
    /// watching the deployment, rather than at the first sign-in.
    /// <para>
    /// <b>And it says the file is missing, not that the password is wrong.</b>
    /// The sabotage found this: without the explicit check the refusal still
    /// happens — <c>X509Certificate2</c> throws on a path that is not there and
    /// the catch turns it into the wrong-password message — so the operator is
    /// sent to check a password over a file that was never opened. The check
    /// earns its place by which of the two answers comes out, which is why this
    /// test asserts on that rather than on there being a refusal at all.
    /// </para>
    /// </summary>
    [Fact]
    public void A_certificate_that_is_not_there_says_so()
    {
        var missing = Path.Combine(Path.GetTempPath(), "algojudge-no-such-certificate.pfx");

        using var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Path", missing);
            builder.UseSetting($"{KeyRing.CertificatesSetting}:0:Password", Certificate.Password);
        });

        var refusal = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(KeyRing.CertificatesSetting, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(missing, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", refusal.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Which store the keys actually come out of — asked with a second database, the
/// one thing a machine-wide directory cannot fake.
/// </summary>
[Collection("server-2")]
public class KeyRingStoreTests(ServerFixture server)
{
    private const string Secret = "a session cookie, in miniature";

    private static IDataProtector Protector(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("key ring test");

    /// <summary>
    /// <b>The test that pins the store.</b> A host given a database of its own
    /// must not read what the suite's host protected — and if the keys were
    /// coming from the machine's profile directory, as the framework's default
    /// arranges, it would, because both hosts run on one machine.
    /// </summary>
    [Fact]
    public async Task The_ring_follows_the_database_and_not_the_machine()
    {
        var elsewhere = await ScratchDatabase.CreateAsync(server);

        using var sameDatabase = server.WithWebHostBuilder(_ => { });
        using var otherDatabase = server.WithWebHostBuilder(builder => builder.UseSetting(
            "ConnectionStrings:DbConnectionString", elsewhere));

        var payload = Protector(server).Protect(Secret);

        Assert.Equal(Secret, Protector(sameDatabase).Unprotect(payload));
        Assert.Throws<CryptographicException>(() => Protector(otherDatabase).Unprotect(payload));
    }

    /// <summary>
    /// The optional certificate, on a ring nobody else has written to.
    /// <para>
    /// <b>Its own database, and that is not fussiness.</b> Data Protection
    /// creates a key only when it finds none it can use — so against the suite's
    /// shared database it would find the plaintext key another test already
    /// made, use it, and encrypt nothing. The test would pass having asserted
    /// nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Keys_are_encrypted_with_the_configured_certificate()
    {
        using var certificate = Certificate.Scratch();
        var connectionString = await ScratchDatabase.CreateAsync(server);

        var payload = Using(connectionString, certificate.Path,
            provider => provider.CreateProtector("key ring test").Protect(Secret));

        await using (var context = ScratchDatabase.Context(connectionString))
        {
            var xml = Assert.Single(await context.DataProtectionKeys.ToListAsync()).Xml ?? "";

            Assert.Contains("encryptedKey", xml, StringComparison.OrdinalIgnoreCase);

            // The plaintext form carries the key material in a `<value>` under
            // `<masterKey>`. Its absence is the assertion; the presence check
            // above would pass on a document carrying both.
            Assert.DoesNotContain("<masterKey", xml, StringComparison.OrdinalIgnoreCase);
        }

        // A second provider, the same certificate, reads it back — which is what
        // "every certificate listed decrypts" has to mean in practice.
        var recovered = Using(connectionString, certificate.Path,
            provider => provider.CreateProtector("key ring test").Unprotect(payload));

        Assert.Equal(Secret, recovered);
    }

    /// <summary>
    /// A provider built the way <see cref="KeyRing"/> builds one, against a
    /// database of this test's own. No web host: what is under test is the
    /// registration, and a host would only add a seeder to wait for.
    /// </summary>
    private static TResult Using<TResult>(
        string connectionString, string certificatePath, Func<IDataProtectionProvider, TResult> what)
    {
        var settings = new Dictionary<string, string?>
        {
            [KeyRing.KindSetting] = KeyRing.Database,
            [$"{KeyRing.CertificatesSetting}:0:Path"] = certificatePath,
            [$"{KeyRing.CertificatesSetting}:0:Password"] = Certificate.Password,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

        KeyRing.Add(
            services,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new DevelopmentEnvironment());

        using var provider = services.BuildServiceProvider();
        return what(provider.GetRequiredService<IDataProtectionProvider>());
    }

    /// <summary>Development, because that is all <see cref="KeyRing"/> asks of it.</summary>
    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AlgoJudge.Server.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// An empty database beside the suite's, on the container it already runs.
/// <para>
/// A database rather than a second container: <c>StorageCollection</c> exists
/// because five containers at once made a timing-sensitive test fail, and what
/// these tests need is a separate key ring, not a separate server.
/// </para>
/// <para>
/// Migrated by the migrations under test, so this also says that the new one
/// produces a table Data Protection can use.
/// </para>
/// </summary>
public static class ScratchDatabase
{
    public static async Task<string> CreateAsync(ServerFixture server)
    {
        var name = "keyring_" + Guid.NewGuid().ToString("N")[..12];

        await using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($@"CREATE DATABASE ""{name}""", connection);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString)
        {
            Database = name,
        }.ConnectionString;

        await using var context = Context(connectionString);
        await context.Database.MigrateAsync();

        return connectionString;
    }

    public static ApplicationDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
}

/// <summary>
/// A self-signed certificate on disk, for the tests that need one.
/// <para>
/// Generated rather than committed: a certificate with its private key in a
/// repository is one somebody eventually uses somewhere real.
/// </para>
/// </summary>
public sealed class Certificate : IDisposable
{
    public const string Password = "certificate-password-for-tests";

    public string Path { get; private init; } = "";

    public static Certificate Scratch()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=algojudge-key-ring-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "algojudge-keyring-" + Guid.NewGuid().ToString("N")[..12] + ".pfx");

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return new Certificate { Path = path };
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* a temporary file the run is done with */ }
    }
}
