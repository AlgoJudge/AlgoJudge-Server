using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What happens on the way out.
/// <para>
/// Three of these cover settings that are <b>off by default in ASP.NET Core</b>
/// and were off here: a conditional request was answered with the whole file, a
/// range request was answered with the whole file, and the <c>ETag</c> that made
/// both possible was written as a bare header the framework never consulted.
/// Measured against the running stack on 2026-08-12 before any of it changed.
/// </para>
/// </summary>
[Collection("server-2")]
public class DownloadPathTests(ServerFixture server)
{
    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task<(HttpClient Client, string Id, byte[] Bytes)> StoredAsync(int size = 4096)
    {
        var client = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "payload.bin" },
            { new StringContent(Sha256Of(bytes)), "sha256" },
        };

        var response = await client.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (client, stored.GetProperty("id").GetString()!, bytes);
    }

    // ── what the response says the bytes are ────────────────────────────────

    /// <summary>
    /// Uploads with a declared type and, optionally, a name written into the
    /// part's own <c>Content-Disposition</c> so a shape the helpers cannot make
    /// can still be sent.
    /// </summary>
    private async Task<(HttpClient Client, string Id)> DeclaredAsync(
        string mimeType, string? fileName, string? rawDisposition = null)
    {
        var client = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var bytes = Encoding.UTF8.GetBytes("<script>alert(1)</script>");

        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(mimeType);
        part.Headers.TryAddWithoutValidation(
            "Content-Disposition",
            rawDisposition ?? $"form-data; name=\"file\"; filename=\"{fileName}\"");

        using var content = new MultipartFormDataContent
        {
            part,
            { new StringContent(Sha256Of(bytes)), "sha256" },
        };

        var response = await client.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (client, stored.GetProperty("id").GetString()!);
    }

    /// <summary>
    /// <b>The stored type is the uploader's own word for it</b> —
    /// <c>CommitAsync</c> only replaces a blank one — and both download endpoints
    /// used to hand it straight back. A file uploaded as <c>text/html</c> was
    /// served as <c>text/html</c>, on the API origin, from an endpoint that is
    /// anonymous for anything an instance document points at. The edge's
    /// <c>nosniff</c> is no help when the type is declared rather than guessed.
    /// </summary>
    [Fact]
    public async Task A_file_whose_type_is_not_one_we_render_is_served_as_bytes()
    {
        var (client, id) = await DeclaredAsync("text/html", "payload.html");

        var response = await client.GetAsync($"/api/v1/files/{id}");
        await Sign.Succeeded(response);

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    /// <summary>
    /// <c>MultipartUpload.HasFile</c> accepts either <c>filename</c> or
    /// <c>filename*</c>, but only the first was ever read — so a part carrying
    /// just the encoded form was stored with an empty name, and MVC omits
    /// <c>Content-Disposition</c> altogether when the download name is empty.
    /// That is what turned a declared type into a rendered page.
    /// </summary>
    [Fact]
    public async Task A_file_that_arrived_with_only_an_encoded_name_still_has_a_name()
    {
        var (client, id) = await DeclaredAsync(
            "text/html",
            fileName: null,
            rawDisposition: "form-data; name=\"file\"; filename*=UTF-8''payload.html");

        var response = await client.GetAsync($"/api/v1/files/{id}");
        await Sign.Succeeded(response);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);

        // **The name itself, not merely that there is one.** `NameOrDefault`
        // falls back to `{id}.bin`, so asserting non-emptiness passes just as
        // well against a Server that never read `filename*` at all — which is
        // the half of this fix that lives in `MultipartUpload`.
        Assert.Equal("payload.html", disposition.FileName?.Trim('"'));
    }

    /// <summary>
    /// A statement is read in an <c>&lt;object data&gt;</c>, and a browser
    /// honours <c>attachment</c> even there — so the one type that has to be
    /// shown in place says so.
    /// </summary>
    [Fact]
    public async Task A_statement_that_is_a_pdf_is_shown_rather_than_offered()
    {
        var (client, id) = await DeclaredAsync("application/pdf", "statement.pdf");

        var response = await client.GetAsync($"/api/v1/files/{id}");
        await Sign.Succeeded(response);

        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    /// <summary>
    /// <b>Both halves of the SVG answer are deliberate.</b> The type survives, so
    /// an instance logo still renders in an <c>&lt;img&gt;</c> — which ignores
    /// the disposition, and which <c>nosniff</c> would otherwise refuse if the
    /// type were collapsed to bytes. And it is never <c>inline</c>, so a
    /// top-level navigation to the same address downloads rather than running the
    /// script an SVG may carry.
    /// </summary>
    [Fact]
    public async Task A_mark_is_still_a_picture_and_still_not_a_page()
    {
        var (client, id) = await DeclaredAsync("image/svg+xml", "mark.svg");

        var response = await client.GetAsync($"/api/v1/files/{id}");
        await Sign.Succeeded(response);

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task A_caller_that_already_has_it_is_told_so()
    {
        var (client, id, bytes) = await StoredAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/files/{id}");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{Sha256Of(bytes)}\""));

        var response = await client.SendAsync(request);

        // 200 here means the ETag is a header nobody compares against — which is
        // what it was, because it was written onto the response rather than
        // handed to the File() overload that does the comparing.
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task A_download_can_be_resumed_from_the_middle()
    {
        var (client, id, bytes) = await StoredAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/files/{id}");
        request.Headers.Range = new RangeHeaderValue(1000, 1099);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());

        var range = response.Content.Headers.ContentRange!;
        Assert.Equal(1000, range.From);
        Assert.Equal(1099, range.To);
        Assert.Equal(bytes.LongLength, range.Length);

        // The bytes themselves, not just the status. A range served from the
        // wrong offset answers 206 too.
        var served = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Skip(1000).Take(100).ToArray(), served);
    }

    [Fact]
    public async Task A_range_past_the_end_is_refused_rather_than_invented()
    {
        var (client, id, bytes) = await StoredAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/files/{id}");
        request.Headers.Range = new RangeHeaderValue(bytes.LongLength + 10, bytes.LongLength + 20);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    /// <summary>
    /// A row naming a store this installation is not configured for.
    /// <para>
    /// The realistic way in is an operator retiring a store id while rows still
    /// name it — which §3 forbids and nothing can enforce. The answer has to say
    /// "this Server cannot reach it", not "it does not exist": a 404 sends
    /// somebody looking for a deletion bug instead of at their own configuration.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_file_in_a_store_nobody_configured_is_unavailable_and_not_missing()
    {
        var (client, id, _) = await StoredAsync();

        await using (var context = server.NewContext())
        {
            var file = await context.Files.FirstAsync(f => f.Id == Guid.Parse(id));
            file.StorageId = "retired";
            await context.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/v1/files/{id}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("storage.unavailable", problem.GetProperty("code").GetString());

        // A65c: not the store, not the backend, not a bucket, not a path.
        var body = problem.ToString();
        Assert.DoesNotContain("retired", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("postgres", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Runner's diagnostics come through the one file endpoint.
    /// <para>
    /// There was a second endpoint for these — <c>/runners/{id}/files/{id}</c> —
    /// until 2026-08-12. It asked the same question this path asks and was
    /// therefore a second way to the bytes, which §2 invariant 1 forbids. Going
    /// through <c>/files/{id}</c> is also what gives the panel a conditional
    /// request and a range, which the dedicated endpoint never had.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_runner_attachment_is_read_through_the_one_file_endpoint()
    {
        var runner = await Build.RunnerAsync(server);
        var log = "cpu MHz : 3600.000\nmodel name : something\n";
        var bytes = Encoding.UTF8.GetBytes(log);

        using var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "lscpu.txt" },
            { new StringContent(Sha256Of(bytes)), "sha256" },
        };
        var stored = await runner.Client.PostAsync("/api/v1/runner/files", upload);
        await Sign.Succeeded(stored);
        var fileId = (await stored.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var attached = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/files/attach", new { fileId, name = "lscpu.txt" });
        await Sign.Succeeded(attached);

        // Somebody holding runner:read, through the ordinary file endpoint.
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var response = await admin.GetAsync($"/api/v1/files/{fileId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(log, await response.Content.ReadAsStringAsync());

        // And everything the file endpoint gives, which the endpoint that used
        // to serve this did not: a conditional request is answered as one.
        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/files/{fileId}");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{Sha256Of(bytes)}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await admin.SendAsync(conditional)).StatusCode);
    }

    /// <summary>
    /// The boundary the removed endpoint was carrying, checked where it now
    /// lives: a Runner's log is operator material, and a participant is not an
    /// operator.
    /// </summary>
    [Fact]
    public async Task A_participant_cannot_read_a_Runners_diagnostics()
    {
        var runner = await Build.RunnerAsync(server);
        var bytes = Encoding.UTF8.GetBytes("model name : something private\n");

        using var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "lscpu.txt" },
            { new StringContent(Sha256Of(bytes)), "sha256" },
        };
        var stored = await runner.Client.PostAsync("/api/v1/runner/files", upload);
        await Sign.Succeeded(stored);
        var fileId = (await stored.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        await Sign.Succeeded(await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/files/attach", new { fileId, name = "lscpu.txt" }));

        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var response = await participant.GetAsync($"/api/v1/files/{fileId}");

        // 404 and not 403: a file id is opaque, and a 403 would confirm the
        // bytes exist.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
