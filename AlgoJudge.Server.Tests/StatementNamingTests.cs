using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a statement is called, and why the extension is not decoration.
/// <para>
/// <b>The name is the only thing that carries the type.</b> The Client decides
/// how to draw a statement from its extension alone — Markdown is rendered,
/// anything else is drawn in a frame — so a name that disagrees with the bytes
/// sends a PDF to a Markdown parser.
/// </para>
/// <para>
/// That is what the UVa import did until 2026-08-26: it stores the archive's PDF
/// as the statement, and every statement was named <c>content.md</c> whatever it
/// held.
/// </para>
/// </summary>
[Collection("server-2")]
public class StatementNamingTests(ServerFixture server)
{
    private async Task<HttpClient> AdminAsync() =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>
    /// Uploads with a stated media type, which is what decides the name.
    /// <para>
    /// <c>Build.UploadAsync</c> sends none, so it cannot express this — and the
    /// media type is exactly what the external fetch records when it pulls a
    /// statement from an archive.
    /// </para>
    /// </summary>
    private static async Task<string> UploadAsync(
        HttpClient admin, string name, string mediaType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        using var content = new MultipartFormDataContent
        {
            { part, "file", name },
            {
                new StringContent(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
                "sha256"
            },
        };

        var response = await admin.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    private static async Task<string> ProblemAsync(HttpClient admin)
    {
        var made = await admin.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "p-naming-" + Guid.NewGuid().ToString("N")[..8],
            name = "Naming",
            type = "uva@1",
            external = true,
        });
        await Sign.Succeeded(made);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    /// <summary>Every statement on the newest version, by name.</summary>
    private static async Task<IReadOnlyList<string>> StatementsAsync(HttpClient admin, string problemId)
    {
        var versions = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/problems/{problemId}/versions");

        return versions.EnumerateArray()
            .OrderByDescending(v => v.GetProperty("version").GetInt32())
            .First()
            .GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)
            .Where(name => name.StartsWith("content", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// A PDF statement is called <c>content.pdf</c>.
    /// <para>
    /// The whole of the UVa import defect: the bytes were right, the reference
    /// was right, and the name said Markdown.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_pdf_statement_is_named_as_a_pdf()
    {
        var admin = await AdminAsync();
        var problemId = await ProblemAsync(admin);
        var file = await UploadAsync(admin, "100.pdf", "application/pdf", "%PDF-1.4 not really\n");

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/problems/{problemId}/versions",
            new { statements = new[] { new { fileId = file } } }));

        Assert.Equal(["content.pdf"], await StatementsAsync(admin, problemId));
    }

    /// <summary>And Markdown is still Markdown, which is every other statement.</summary>
    [Fact]
    public async Task A_markdown_statement_keeps_its_name()
    {
        var admin = await AdminAsync();
        var problemId = await ProblemAsync(admin);
        var file = await UploadAsync(admin, "content.md", "text/markdown", "# A problem\n");

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/problems/{problemId}/versions",
            new { statements = new[] { new { fileId = file } } }));

        Assert.Equal(["content.md"], await StatementsAsync(admin, problemId));
    }

    /// <summary>
    /// Anything this Server has no renderer for is Markdown, because that is what
    /// the Client does with a statement it cannot tell apart. A third extension
    /// would be a name nothing keys on.
    /// </summary>
    [Fact]
    public async Task Anything_else_is_named_as_markdown()
    {
        var admin = await AdminAsync();
        var problemId = await ProblemAsync(admin);
        var file = await UploadAsync(admin, "statement.txt", "text/plain", "plain words\n");

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/problems/{problemId}/versions",
            new { statements = new[] { new { fileId = file } } }));

        Assert.Equal(["content.md"], await StatementsAsync(admin, problemId));
    }

    /// <summary>
    /// The language still rides on the name, and now beside the right extension.
    /// </summary>
    [Fact]
    public async Task A_translated_pdf_carries_both_the_language_and_the_extension()
    {
        var admin = await AdminAsync();
        var problemId = await ProblemAsync(admin);
        var polish = await UploadAsync(admin, "pl.md", "text/markdown", "# Zadanie\n");
        var english = await UploadAsync(admin, "en.pdf", "application/pdf", "%PDF-1.4 also not\n");

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/problems/{problemId}/versions",
            new
            {
                statements = new[]
                {
                    new { fileId = polish, language = (string?)null },
                    new { fileId = english, language = (string?)"en" },
                },
            }));

        var names = (await StatementsAsync(admin, problemId)).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(["content-en.pdf", "content.md"], names);
    }
}
