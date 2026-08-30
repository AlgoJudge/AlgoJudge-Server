using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// An installation's own colours and typeface.
/// <para>
/// <b>Most of what is here is a refusal, and that is the point.</b> Every value
/// in a theme ends up inside a stylesheet the Client builds, and every face is a
/// file fetched by the browser of somebody who has not signed in. A field that
/// took anything but six hexadecimal digits would let a configuration file carry
/// CSS; a face taken on its declared type would let it carry anything at all.
/// </para>
/// </summary>
[Collection("server-3")]
public class ThemeTests(ServerFixture server)
{
    private const string Instance = "/api/v1/instance";

    /// <summary>The smallest thing that begins <c>wOF2</c>. Nothing renders it, and nothing has to.</summary>
    private static byte[] Woff2() =>
        [.. "wOF2"u8.ToArray(), .. Enumerable.Repeat((byte)0, 60)];

    [Fact]
    public async Task A_colour_that_is_not_six_hexadecimal_digits_is_refused()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // A keyword is a valid CSS colour and is refused anyway: the narrow rule
        // is what makes the wider one — that nothing else can get through —
        // possible to state at all.
        var keyword = await Put(admin, Values(light: """{ "primary": "red" }"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, keyword.StatusCode);
        Assert.Equal("theme.colour", await Code(keyword));

        // And the reason the rule exists.
        var css = await Put(admin, Values(light: """{ "primary": "#ffffff; } body { display: none" }"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, css.StatusCode);
        Assert.Equal("theme.colour", await Code(css));
    }

    [Fact]
    public async Task An_unknown_key_is_refused_and_named()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await Put(admin, new
        {
            fileId = await FileAsync(admin, "theme.yml", """
                format: algojudge-theme
                version: 1
                light:
                  primry: "#0050a9"
                """),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        // Read once: the response body is a stream, and a second read of it is
        // an ObjectDisposedException rather than the same answer again.
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("theme.key", problem.GetProperty("code").GetString());

        // Named, and the accepted ones listed. A typo quietly ignored is how a
        // configuration file comes to claim something that is not in force.
        var detail = problem.GetProperty("detail").GetString() ?? "";
        Assert.Contains("light.primry", detail);
        Assert.Contains("primary", detail);
    }

    [Fact]
    public async Task A_family_the_theme_ships_no_face_for_is_refused()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await Put(admin, new
        {
            theme = new { fontFamily = "Helvetica Neue" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("theme.font.undeclared", await Code(refused));

        // A generic name is the exception, because it resolves to something on
        // every machine by definition.
        var generic = await Put(admin, new { theme = new { fontFamily = "serif" } });
        await Sign.Succeeded(generic);
        await Clear(admin);
    }

    [Fact]
    public async Task A_face_is_refused_on_its_bytes_rather_than_on_its_name()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var pretender = await FileAsync(admin, "not-a-font.woff2", "GIF89a this is not a font");
        var refused = await admin.PostAsJsonAsync($"{Instance}/fonts", new
        {
            fileId = pretender,
            name = "not-a-font.woff2",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("font.format", await Code(refused));
    }

    [Fact]
    public async Task A_path_is_not_a_face_name()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var file = await BytesAsync(admin, "face.woff2", Woff2());
        var refused = await admin.PostAsJsonAsync($"{Instance}/fonts", new
        {
            fileId = file,
            name = "../../etc/face.woff2",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("font.name", await Code(refused));
    }

    [Fact]
    public async Task A_theme_naming_a_face_that_is_not_stored_is_refused()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await Put(admin, new
        {
            theme = new
            {
                fontFamily = "Absent Sans",
                fonts = new[] { new { family = "Absent Sans", file = "absent-400.woff2" } },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("theme.font.missing", await Code(refused));
    }

    /// <summary>
    /// The two doors reach one document. The panel's form sends values and this
    /// Server writes the YAML; an operator sends a file of their own. There is
    /// one thing in force either way, and it is downloadable.
    /// </summary>
    [Fact]
    public async Task The_form_and_a_file_publish_the_same_theme()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var fromForm = await Put(admin, Values(
            light: """{ "primary": "#0050A9", "navBackground": "#0050a9", "navText": "#ffffff" }""",
            dark: """{ "primary": "#4d94dc" }"""));
        await Sign.Succeeded(fromForm);

        var info = await fromForm.Content.ReadFromJsonAsync<JsonElement>();
        var theme = info.GetProperty("theme");

        // Normalised on the way in: a colour is stored in one case, so two
        // spellings of one colour cannot read as two values.
        Assert.Equal("#0050a9", theme.GetProperty("light").GetProperty("primary").GetString());
        Assert.Equal("#ffffff", theme.GetProperty("light").GetProperty("navText").GetString());
        Assert.Equal("#4d94dc", theme.GetProperty("dark").GetProperty("primary").GetString());

        // Untouched keys are **absent from the answer**, not null and not black:
        // the serialiser omits them, so a reader sees nothing where a key was
        // never set and draws the product's own. Thirty of the thirty-two were
        // never mentioned here.
        Assert.False(
            theme.GetProperty("light").TryGetProperty("body", out _),
            "a colour nobody set is not in the answer at all");

        // The file behind it is the one the panel offers back, and it says the
        // same thing.
        var written = await admin.GetStringAsync($"/api/v1/files/{theme.GetProperty("fileId").GetString()}");
        Assert.Contains("#0050a9", written);
        Assert.Contains("format: algojudge-theme", written);

        var again = await Put(admin, new { fileId = await FileAsync(admin, "theme.yml", written) });
        await Sign.Succeeded(again);
        var second = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("theme");
        Assert.Equal("#0050a9", second.GetProperty("light").GetProperty("primary").GetString());

        await Clear(admin);
    }

    [Fact]
    public async Task The_theme_is_readable_before_anybody_has_signed_in()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await Put(admin, Values(light: """{ "primary": "#116644" }""")));

        // The sign-in screen is drawn in the operator's colours, so this answer
        // has to carry them to somebody who has not signed in.
        using var anonymous = server.CreateClient();
        var info = await anonymous.GetFromJsonAsync<JsonElement>(Instance);
        Assert.Equal(
            "#116644",
            info.GetProperty("theme").GetProperty("light").GetProperty("primary").GetString());

        await Clear(admin);
    }

    [Fact]
    public async Task Withdrawing_the_theme_returns_the_installation_to_the_default()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await Put(admin, Values(light: """{ "primary": "#884400" }""")));

        var response = await admin.DeleteAsync($"{Instance}/theme");
        await Sign.Succeeded(response);

        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(
            info.TryGetProperty("theme", out _),
            "an installation that has withdrawn its theme draws the one the Client ships");
    }

    /// <summary>
    /// A theme naming a face that is not stored cannot be read, and a theme that
    /// cannot be read is no theme — so withdrawing one that is in use would turn
    /// deleting a file into every colour on the installation reverting.
    /// </summary>
    [Fact]
    public async Task A_face_the_published_theme_draws_with_cannot_be_withdrawn()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var file = await BytesAsync(admin, "in-use-400.woff2", Woff2());
        await Sign.Succeeded(await admin.PostAsJsonAsync($"{Instance}/fonts", new
        {
            fileId = file,
            name = "in-use-400.woff2",
        }));

        await Sign.Succeeded(await Put(admin, new
        {
            theme = new
            {
                fontFamily = "In Use",
                fonts = new[] { new { family = "In Use", file = "in-use-400.woff2", weight = 400 } },
            },
        }));

        var refused = await admin.DeleteAsync($"{Instance}/fonts/in-use-400.woff2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("font.inUse", await Code(refused));

        // Withdraw the theme first, and then it goes.
        await Sign.Succeeded(await admin.DeleteAsync($"{Instance}/theme"));
        await Sign.Succeeded(await admin.DeleteAsync($"{Instance}/fonts/in-use-400.woff2"));
    }

    /// <summary>A face travels with an address this Server built, never one anybody typed.</summary>
    [Fact]
    public async Task A_declared_face_reaches_the_reader_as_an_address()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var file = await BytesAsync(admin, "reader-700.woff2", Woff2());
        await Sign.Succeeded(await admin.PostAsJsonAsync($"{Instance}/fonts", new
        {
            fileId = file,
            name = "reader-700.woff2",
        }));

        var response = await Put(admin, new
        {
            theme = new
            {
                fontFamilyHeadings = "Reader",
                fonts = new[] { new { family = "Reader", file = "reader-700.woff2", weight = 700 } },
            },
        });
        await Sign.Succeeded(response);

        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        var face = info.GetProperty("theme").GetProperty("fonts").EnumerateArray().Single();
        Assert.Equal("Reader", face.GetProperty("family").GetString());
        Assert.Equal(700, face.GetProperty("weight").GetInt32());
        Assert.Equal("normal", face.GetProperty("style").GetString());
        Assert.Equal($"/api/v1/files/{file}", face.GetProperty("url").GetString());

        // And it is fetchable by somebody who has not signed in, because that is
        // who the login screen is for.
        using var anonymous = server.CreateClient();
        var fetched = await anonymous.GetAsync(face.GetProperty("url").GetString());
        await Sign.Succeeded(fetched);

        await Sign.Succeeded(await admin.DeleteAsync($"{Instance}/theme"));
        await Sign.Succeeded(await admin.DeleteAsync($"{Instance}/fonts/reader-700.woff2"));
    }

    [Fact]
    public async Task A_request_stating_both_doors_is_refused()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await Put(admin, new
        {
            fileId = await FileAsync(admin, "theme.yml", "format: algojudge-theme\nversion: 1\n"),
            theme = new { fontFamily = "serif" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("theme.input", await Code(refused));

        var neither = await Put(admin, new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, neither.StatusCode);
        Assert.Equal("theme.input", await Code(neither));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> Put(HttpClient client, object body) =>
        client.PutAsJsonAsync($"{Instance}/theme", body);

    private static async Task Clear(HttpClient client) =>
        await Sign.Succeeded(await client.DeleteAsync($"{Instance}/theme"));

    /// <summary>The form's body, written as the screen writes it.</summary>
    private static object Values(string? light = null, string? dark = null) => new
    {
        theme = JsonSerializer.Deserialize<JsonElement>($$"""
            { "light": {{light ?? "null"}}, "dark": {{dark ?? "null"}} }
            """),
    };

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString() ?? "";

    private static Task<string> FileAsync(HttpClient client, string name, string text) =>
        BytesAsync(client, name, Encoding.UTF8.GetBytes(text));

    private static async Task<string> BytesAsync(HttpClient client, string name, byte[] bytes)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", name },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return stored.GetProperty("id").GetString()!;
    }
}
