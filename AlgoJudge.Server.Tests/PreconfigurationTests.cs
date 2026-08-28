using System.Net;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using FileOwnerKind = AlgoJudge.Server.Database.Models.FileOwnerKind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// An installation stood up from files on disk.
/// <para>
/// <b>Every test that applies anything builds a database of its own.</b> The
/// suite shares one, and an apply against it would rename the installation
/// under whichever other test happened to be reading it.
/// </para>
/// </summary>
[Collection("server-1")]
public class PreconfigurationTests(ServerFixture server) : IDisposable
{
    private const string Config = "/api/v1/admin/config";

    private readonly List<string> directories = [];

    public void Dispose()
    {
        foreach (var directory in directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a test's own scratch directory, gone or held; neither matters */ }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The committed <c>preconfig.example</c>, found by walking up from the test
    /// binary — the repository root is not a path the test host knows.
    /// </summary>
    private static string Shipped()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, "preconfig.example");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            $"preconfig.example is not above {AppContext.BaseDirectory}");
    }

    /// <summary>A directory holding one configuration file and whatever else is asked for.</summary>
    private string Directory_(string yaml, params (string Path, string Content)[] files)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "algojudge-preconfig", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        directories.Add(directory);

        File.WriteAllText(Path.Combine(directory, "algojudge.yml"), yaml);

        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return directory;
    }

    private static string Yaml(string name, string? extra = null) =>
        $"""
        format: algojudge-preconfiguration
        version: 1

        instance:
          name: "{name}"
        {extra}
        """;

    private const string Page = "---\nversion: 1\n---\n\n# Witamy\n";

    /// <summary>A fresh installation: a database of its own, migrated and empty.</summary>
    private async Task<(WebApplicationFactory<Program> Host, string ConnectionString)> FreshAsync(
        string? directory)
    {
        var connectionString = await ScratchDatabase.CreateAsync(server);
        var host = server.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
            if (directory is not null)
            {
                builder.UseSetting(
                    AlgoJudge.Server.Preconfiguration.PreconfigurationFile.PathSetting, directory);
            }
        });

        return (host, connectionString);
    }

    /// <summary>
    /// A database of its own that has <b>already been started once</b>, so a
    /// host built on it afterwards is no longer fresh and applies nothing at
    /// start. This is how the endpoint is reached with a directory the start
    /// itself would have refused.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Host, string ConnectionString)> SeededAsync(
        string directory)
    {
        var (bare, connectionString) = await FreshAsync(null);
        using (var warm = bare.CreateClient())
        {
            (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        }

        return (bare.WithWebHostBuilder(builder => builder.UseSetting(
            AlgoJudge.Server.Preconfiguration.PreconfigurationFile.PathSetting, directory)),
            connectionString);
    }

    private static HttpClient Operator(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The refusal message, for the tests that assert on what it says.</summary>
    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return body;
    }

    /* ── the promise of the item ───────────────────────────────────────────── */

    /// <summary>
    /// The whole of §5 in one walk: an installation nobody has touched takes
    /// what the files say, without anybody opening the panel.
    /// </summary>
    [Fact]
    public async Task A_fresh_installation_takes_what_the_files_say()
    {
        var name = "Preconfigured " + Guid.NewGuid().ToString("N")[..8];
        var directory = Directory_(Yaml(name), ("pages/welcome.md", Page));

        var (host, connectionString) = await FreshAsync(directory);
        using var client = host.CreateClient();

        var info = await ReadAsync(await client.GetAsync("/api/v1/instance"));
        Assert.Equal(name, info.GetProperty("name").GetString());

        Assert.Contains(
            info.GetProperty("documents").EnumerateArray(),
            document => document.GetProperty("kind").GetString() == "welcome");

        await using var context = ScratchDatabase.Context(connectionString);
        Assert.Equal(1, await WelcomeAsync(connectionString));
    }

    /// <summary>
    /// How many revisions of the welcome page there are.
    /// <para>
    /// Named rather than counted across every document: the development seed
    /// publishes four legal pages of its own, so a total says nothing about
    /// what this feature did.
    /// </para>
    /// </summary>
    private static async Task<int> WelcomeAsync(string connectionString)
    {
        await using var context = ScratchDatabase.Context(connectionString);
        return await context.FileReferences.CountAsync(reference =>
            reference.OwnerKind == FileOwnerKind.InstanceDocument && reference.Name == "welcome");
    }

    /// <summary>
    /// <b>And an installation that already exists is left alone.</b> This is the
    /// half that makes the other safe: a file re-read on every boot would undo
    /// whatever an administrator had chosen since.
    /// </summary>
    [Fact]
    public async Task An_installation_that_already_exists_is_left_alone()
    {
        var name = "Never applied " + Guid.NewGuid().ToString("N")[..8];
        var directory = Directory_(Yaml(name), ("pages/welcome.md", Page));

        // The suite's own database: seeded, with users, and therefore not fresh.
        // **Started first, deliberately.** If this class runs before anything
        // else touches the fixture, the host below would be the one meeting an
        // empty database — and would apply, which is what this denies.
        using (var warm = server.CreateClient())
        {
            (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        }

        var host = server.WithWebHostBuilder(builder => builder.UseSetting(
            AlgoJudge.Server.Preconfiguration.PreconfigurationFile.PathSetting, directory));

        using var client = host.CreateClient();
        var info = await ReadAsync(await client.GetAsync("/api/v1/instance"));

        // **Read as "may be absent".** An unnamed installation omits the field
        // entirely, and another test in the suite clears the name — so asking
        // for the property outright makes this test depend on what ran before
        // it. The full run found that; the class on its own never did.
        var current = info.TryGetProperty("name", out var named) ? named.GetString() : null;
        Assert.NotEqual(name, current);

        // And the change is still waiting, which proves the file was readable
        // and the start simply declined to act on it.
        using var admin = Operator(host);
        var plan = await ReadAsync(await admin.GetAsync(Config));
        Assert.Contains(
            plan.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("target").GetString() == "instance.name");
    }

    /* ── applying, and applying again ──────────────────────────────────────── */

    /// <summary>
    /// <b>The revision test.</b> Publishing <i>adds</i> a revision, so an apply
    /// that republished whatever it found would grow a privacy policy's history
    /// by one entry per run — destroying exactly the history the versioning
    /// exists to keep. The checksum comparison is what stops it.
    /// </summary>
    [Fact]
    public async Task Applying_twice_changes_nothing_the_second_time()
    {
        var directory = Directory_(Yaml("Twice"), ("pages/welcome.md", Page));
        var (host, connectionString) = await FreshAsync(directory);
        using var admin = Operator(host);

        // The first apply already happened at start; this is the second and third.
        var second = await ReadAsync(await admin.PostAsync($"{Config}/apply", null));
        Assert.Empty(second.GetProperty("changes").EnumerateArray());

        var third = await ReadAsync(await admin.GetAsync(Config));
        Assert.Empty(third.GetProperty("changes").EnumerateArray());

        Assert.Equal(1, await WelcomeAsync(connectionString));
    }

    /// <summary>A page that has changed is published as a revision, superseding the last.</summary>
    [Fact]
    public async Task A_changed_page_supersedes_the_last()
    {
        var directory = Directory_(Yaml("Revised"), ("pages/welcome.md", Page));
        var (host, connectionString) = await FreshAsync(directory);
        using var admin = Operator(host);

        File.WriteAllText(
            Path.Combine(directory, "pages", "welcome.md"),
            "---\nversion: 1\n---\n\n# Witamy ponownie\n");

        var applied = await ReadAsync(await admin.PostAsync($"{Config}/apply", null));
        Assert.Contains(
            applied.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("target").GetString() == "document.welcome");

        await using var context = ScratchDatabase.Context(connectionString);
        var references = await context.FileReferences
            .Where(reference => reference.OwnerKind == FileOwnerKind.InstanceDocument
                && reference.Name == "welcome")
            .ToListAsync();

        Assert.Equal(2, references.Count);
        Assert.Equal(1, references.Count(reference => reference.SupersededAt != null));
    }

    /// <summary>
    /// A setting the file does not state is left as it was, never reset.
    /// <para>
    /// <b>Two flags, and they are chosen rather than picked.</b> One ships
    /// <c>true</c> and is moved to <c>false</c>, the other the other way round —
    /// so neither "reset to the shipped default" nor "reset to the type's
    /// default" can pass. The first draft of this test used one flag and a
    /// sabotage walked straight through it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_setting_absent_from_the_file_is_left_alone()
    {
        var directory = Directory_(Yaml("Partial"));
        var (host, connectionString) = await FreshAsync(directory);

        // Started, so there is an instance row to change. The apply at start
        // set the name; nothing it did touched the flag below.
        using (var warm = host.CreateClient())
        {
            (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        }

        await using (var seeded = ScratchDatabase.Context(connectionString))
        {
            var instance = await seeded.Instance.FirstAsync();
            instance.ShowLocalSignIn = false;          // ships true
            instance.ExternalJudgingEnabled = true;    // ships false
            await seeded.SaveChangesAsync();
        }

        using var admin = Operator(host);
        await ReadAsync(await admin.PostAsync($"{Config}/apply", null));

        await using var context = ScratchDatabase.Context(connectionString);
        var after = await context.Instance.FirstAsync();
        Assert.False(after.ShowLocalSignIn);
        Assert.True(after.ExternalJudgingEnabled);
    }

    /// <summary>
    /// A document the directory does not carry stays published. Apply adds; it
    /// never withdraws.
    /// </summary>
    [Fact]
    public async Task It_never_withdraws_what_the_files_do_not_mention()
    {
        var directory = Directory_(
            Yaml("Additive"), ("pages/welcome.md", Page), ("pages/privacy.md", Page));

        var (host, connectionString) = await FreshAsync(directory);
        using var admin = Operator(host);

        File.Delete(Path.Combine(directory, "pages", "privacy.md"));
        var applied = await ReadAsync(await admin.PostAsync($"{Config}/apply", null));
        Assert.Empty(applied.GetProperty("changes").EnumerateArray());

        await using var context = ScratchDatabase.Context(connectionString);
        Assert.Equal(1, await context.FileReferences.CountAsync(reference =>
            reference.OwnerKind == FileOwnerKind.InstanceDocument
            && reference.Name == "privacy"
            && reference.SupersededAt == null));
    }

    /// <summary>The mark travels the same way, and is compared the same way.</summary>
    [Fact]
    public async Task A_mark_is_published_from_the_directory()
    {
        const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"></svg>";
        var directory = Directory_(Yaml("Marked"), ("logo.svg", Svg));

        var (host, _) = await FreshAsync(directory);
        using var client = host.CreateClient();

        var info = await ReadAsync(await client.GetAsync("/api/v1/instance"));
        Assert.Equal(
            "image/svg+xml", info.GetProperty("logo").GetProperty("mimeType").GetString());

        using var admin = Operator(host);
        var plan = await ReadAsync(await admin.GetAsync(Config));
        Assert.Empty(plan.GetProperty("changes").EnumerateArray());
    }

    /// <summary>The dry run writes nothing. It is the only reason to trust it.</summary>
    [Fact]
    public async Task The_plan_changes_nothing()
    {
        var directory = Directory_(Yaml("Planned"), ("pages/welcome.md", Page));

        // Started once without a path, so the start applied nothing and there
        // is something left for a plan to describe.
        var (host, connectionString) = await SeededAsync(directory);
        using var admin = Operator(host);
        var plan = await ReadAsync(await admin.GetAsync(Config));

        Assert.False(plan.GetProperty("applied").GetBoolean());
        Assert.NotEmpty(plan.GetProperty("changes").EnumerateArray());

        Assert.Equal(0, await WelcomeAsync(connectionString));
    }

    /* ── what it refuses ───────────────────────────────────────────────────── */

    [Fact]
    public async Task An_unknown_version_is_refused()
    {
        var directory = Directory_("format: algojudge-preconfiguration\nversion: 2\n");
        var (host, _) = await SeededAsync(directory);
        using var admin = Operator(host);

        var body = await RefusalAsync(await admin.GetAsync(Config));
        Assert.Contains("version 2", body);
    }

    /// <summary>
    /// A typo is named rather than ignored. A key silently dropped is how a
    /// configuration file comes to claim something that is not in force.
    /// </summary>
    [Fact]
    public async Task An_unknown_key_is_refused_and_named()
    {
        var directory = Directory_(Yaml("Typo", "  requireEmial: true"));
        var (host, _) = await SeededAsync(directory);
        using var admin = Operator(host);

        var body = await RefusalAsync(await admin.GetAsync(Config));
        Assert.Contains("requireEmial", body);
        Assert.Contains("requireEmail", body);
    }

    [Fact]
    public async Task A_page_whose_name_is_not_a_kind_is_refused()
    {
        var directory = Directory_(Yaml("Stray"), ("pages/regulamin.md", Page));
        var (host, _) = await SeededAsync(directory);
        using var admin = Operator(host);

        var body = await RefusalAsync(await admin.GetAsync(Config));
        Assert.Contains("regulamin.md", body);
        Assert.Contains("accessibility", body);
    }

    /// <summary>
    /// <b>An unresolved variable stops the apply.</b> Storing the text of a
    /// variable name would leave an installation whose settings look configured
    /// and are not — worse than one that refuses.
    /// </summary>
    [Fact]
    public async Task An_unresolved_variable_is_refused_rather_than_stored()
    {
        var variable = "AJ_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var directory = Directory_(Yaml($"${{{variable}}}"));
        var (host, _) = await SeededAsync(directory);
        using var admin = Operator(host);

        var body = await RefusalAsync(await admin.GetAsync(Config));
        Assert.Contains(variable, body);
    }

    /// <summary>And a variable that resolves is what actually lands in the database.</summary>
    [Fact]
    public async Task A_resolved_variable_is_what_is_stored()
    {
        var variable = "AJ_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var value = "Resolved " + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, value);

        try
        {
            var directory = Directory_(Yaml($"${{{variable}}}"));
            var (host, _) = await FreshAsync(directory);
            using var client = host.CreateClient();

            var info = await ReadAsync(await client.GetAsync("/api/v1/instance"));
            Assert.Equal(value, info.GetProperty("name").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// Front matter is a <b>warning</b>, not a refusal: the Client will not
    /// render the page, and the operator should hear that — but the Server does
    /// not parse stored content and does not start here.
    /// </summary>
    [Fact]
    public async Task A_page_with_no_front_matter_is_warned_about_and_still_applied()
    {
        var directory = Directory_(Yaml("Unversioned"), ("pages/welcome.md", "# Witamy\n"));
        var (host, connectionString) = await FreshAsync(directory);
        using var admin = Operator(host);

        var plan = await ReadAsync(await admin.GetAsync(Config));
        Assert.Contains(
            plan.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("front matter"));

        Assert.Equal(1, await WelcomeAsync(connectionString));
    }

    /// <summary>
    /// <b>A first start it cannot read does not happen.</b> Discovered by
    /// writing the tests above: a fresh installation applies at start, so a
    /// directory it refuses is a deployment that stops rather than one that
    /// comes up half configured. That is the right way round — the only start
    /// this can happen on is the one somebody is watching, and every later
    /// restart reads nothing at all.
    /// </summary>
    [Fact]
    public async Task A_fresh_installation_it_cannot_read_does_not_start()
    {
        var directory = Directory_(Yaml("Broken", "  requireEmial: true"));
        var (host, _) = await FreshAsync(directory);

        var refused = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("requireEmial", Flatten(refused));
    }

    private static string Flatten(Exception error) =>
        error.InnerException is { } inner ? $"{error.Message} {Flatten(inner)}" : error.Message;

    /// <summary>An installation that names no directory is told so, by name.</summary>
    [Fact]
    public async Task Without_a_path_it_says_so()
    {
        var (host, _) = await FreshAsync(null);
        using var admin = Operator(host);

        var body = await RefusalAsync(await admin.GetAsync(Config));
        Assert.Contains("Preconfiguration:Path", body);
    }

    /* ── the door ──────────────────────────────────────────────────────────── */

    /// <summary>
    /// Both halves, in one test. Written this way because "404 without a token"
    /// alone passes just as well with the endpoint deleted — which is exactly
    /// what a sabotage of it proved.
    /// </summary>
    [Fact]
    public async Task It_answers_the_operator_and_nobody_else()
    {
        var directory = Directory_(Yaml("Guarded"));
        var (host, _) = await FreshAsync(directory);

        using (var admin = Operator(host))
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Config)).StatusCode);
        }

        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(Config)).StatusCode);

        using var wrong = host.CreateClient();
        wrong.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, "not-the-token");
        Assert.Equal(HttpStatusCode.NotFound, (await wrong.GetAsync(Config)).StatusCode);
    }

    /// <summary>
    /// The committed example is read by this Server, not merely by a human.
    /// A template that stops parsing is a template every new installation
    /// copies.
    /// </summary>
    [Fact]
    public async Task The_shipped_example_is_readable()
    {
        var directory = Shipped();

        var (host, _) = await FreshAsync(directory);
        using var client = host.CreateClient();

        var info = await ReadAsync(await client.GetAsync("/api/v1/instance"));
        Assert.Equal("AlgoJudge (development)", info.GetProperty("name").GetString());

        using var admin = Operator(host);
        var plan = await ReadAsync(await admin.GetAsync(Config));
        Assert.Empty(plan.GetProperty("changes").EnumerateArray());
        Assert.Empty(plan.GetProperty("warnings").EnumerateArray());
    }
}
