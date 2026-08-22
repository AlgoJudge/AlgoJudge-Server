using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What happens on the way in: one pass, a real ceiling, and nothing left behind
/// when the answer is no.
/// </summary>
[Collection("server")]
public class UploadPathTests(ServerFixture server)
{
    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// <para>
    /// The Client's own form appends <c>file</c> before <c>sha256</c>
    /// (<c>FileApiHttp.upload</c>), and a streamed reader sees the parts in that
    /// order — so an implementation that needed the checksum first would refuse
    /// every real upload while passing a test that sent them the other way round.
    /// Both orders, therefore, and neither is a contract.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_checksum_may_arrive_on_either_side_of_the_file(bool checksumFirst)
    {
        var client = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var bytes = Encoding.UTF8.GetBytes($"either side {checksumFirst}");

        using var content = new MultipartFormDataContent();
        if (checksumFirst) content.Add(new StringContent(Sha256Of(bytes)), "sha256");
        content.Add(new ByteArrayContent(bytes), "file", "a.txt");
        if (!checksumFirst) content.Add(new StringContent(Sha256Of(bytes)), "sha256");

        var response = await client.PostAsync("/api/v1/files", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Sha256Of(bytes), stored.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task A_file_that_is_not_what_it_claimed_leaves_nothing_at_all()
    {
        var client = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var bytes = Encoding.UTF8.GetBytes("the bytes");

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "wrong.txt" },
            // A checksum of something else entirely.
            { new StringContent(Sha256Of(Encoding.UTF8.GetBytes("not the bytes"))), "sha256" },
        };

        var before = await BlobCountAsync();
        var response = await client.PostAsync("/api/v1/files", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("checksum_mismatch", problem.GetProperty("code").GetString());

        // **Not one row and not one blob.** The bytes were written before the
        // checksum could be compared — they had to be, to be hashed in one pass —
        // so "stores nothing" is a promise about cleaning up, not about never
        // having touched the store.
        Assert.Equal(before, await BlobCountAsync());
        Assert.Equal(0, await server.NewContext().Files.CountAsync(f => f.Sha256 == Sha256Of(bytes)));
    }

    [Fact]
    public async Task A_submission_larger_than_the_ceiling_is_refused_and_stored_nowhere()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        // Over the 8 MiB submission ceiling, and well under the 128 MiB one that
        // applies to a package — so this fails only if the limit is per endpoint.
        var oversized = new byte[UploadLimits.Submission + 64 * 1024];
        Random.Shared.NextBytes(oversized);

        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"type":"standard-io@1","language":"python3"}"""), "props" },
            { new StringContent("main.py"), "fileName" },
            { new ByteArrayContent(oversized), "file", "big.py" },
            { new StringContent(Sha256Of(oversized)), "sha256" },
        };

        var before = await BlobCountAsync();
        var response = await participant.PostAsync(
            $"/api/v1/activities/{slug}/problems/A/submissions", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(before, await BlobCountAsync());
    }

    /// <summary>
    /// <para>
    /// The bytes go down before the rules are asked — they have to, because
    /// hashing them in one pass means reading them off the socket first. So every
    /// refusal after that point has to clean up after itself, and a closed round
    /// is the refusal a participant is most likely to meet.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_submission_the_rules_refuse_leaves_no_bytes_behind()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var source = "print('refused')\n";
        var bytes = Encoding.UTF8.GetBytes(source);

        using var content = new MultipartFormDataContent
        {
            // **A refusal that happens after the bytes are staged.** It used
            // to be a language the activity did not accept; the Server stopped
            // reading languages on 2026-08-22, and what still refuses on this
            // side of staging is the envelope rule — a document that is not a
            // document.
            { new StringContent("[1, 2, 3]"), "props" },
            { new StringContent("main.py"), "fileName" },
            { new StringContent(source), "code" },
            { new StringContent(Sha256Of(bytes)), "sha256" },
        };

        var blobsBefore = await BlobCountAsync();
        var rowsBefore = await FileRowCountAsync();

        var response = await participant.PostAsync(
            $"/api/v1/activities/{slug}/problems/A/submissions", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(blobsBefore, await BlobCountAsync());

        // **The row, as well as the bytes.** Counting blobs alone made this test
        // blind to the failure it exists to catch: `CommitAsync` writes a `Files`
        // row and saves it, while the controller's `catch` deletes only the blob.
        // A refusal after that point therefore left a row pointing at bytes that
        // no longer exist — and every assertion here still passed. Proven by
        // moving the check back after the commit, which this now fails on and
        // did not before.
        Assert.Equal(rowsBefore, await FileRowCountAsync());
    }

    /// <summary>
    /// How many blobs the default store is holding.
    /// <para>
    /// Counted in the database because the development configuration stores them
    /// there. It is the one place in the suite that knows which backend is
    /// configured, and it is a measurement rather than a decision — nothing in
    /// the Server branches on it.
    /// </para>
    /// </summary>
    /// <summary>Rows in `Files`, which is not the same question as how many blobs.</summary>
    private async Task<long> FileRowCountAsync()
    {
        await using var context = server.NewContext();
        return await context.Files.LongCountAsync();
    }

    private async Task<long> BlobCountAsync()
    {
        await using var context = server.NewContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT count(*) FROM ""FileContents""";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// The ceiling that rides on the stream, on its own.
/// <para>
/// It exists beside <c>[RequestSizeLimit]</c> rather than instead of it, and a
/// sabotage run on 2026-08-12 showed which of the two actually refuses an
/// oversized submission: the attribute does, first. That makes this one a
/// backstop — and a backstop nobody exercises is a backstop nobody knows is
/// broken, so it is exercised here directly.
/// </para>
/// <para>
/// The difference between them is real: the attribute bounds the whole request
/// body, framing and fields included, while this bounds the file. A future
/// endpoint that raises the first without thinking about the second still has
/// this.
/// </para>
/// </summary>
public class LimitedStreamTests
{
    [Fact]
    public async Task Reading_past_the_ceiling_stops_rather_than_truncating()
    {
        var bytes = new byte[4096];
        await using var limited = new AlgoJudge.Server.Storage.LimitedStream(
            new MemoryStream(bytes), maxBytes: 1000);

        // Not a short read: a short read looks exactly like the end of a smaller
        // file, and the upload would be stored as a truncation of itself.
        var refused = await Assert.ThrowsAsync<PayloadTooLargeException>(async () =>
        {
            using var sink = new MemoryStream();
            await limited.CopyToAsync(sink);
        });

        Assert.Equal(1000, refused.LimitBytes);
    }

    [Fact]
    public async Task Exactly_the_ceiling_is_allowed_through()
    {
        var bytes = new byte[1000];
        Random.Shared.NextBytes(bytes);

        await using var limited = new AlgoJudge.Server.Storage.LimitedStream(
            new MemoryStream(bytes), maxBytes: 1000);
        using var sink = new MemoryStream();
        await limited.CopyToAsync(sink);

        // A ceiling that refused the largest allowed file would be an off-by-one
        // nobody notices until somebody's package is exactly 128 MiB.
        Assert.Equal(bytes, sink.ToArray());
    }
}
