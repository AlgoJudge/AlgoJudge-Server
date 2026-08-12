using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Storage;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// §13.2, and the specification calls it the most important test on its list:
/// <b>memory must not grow with the number of concurrent 128 MiB transfers.</b>
/// <para>
/// <b>Allocation, not the heap at rest.</b> A buffering Server allocates a whole
/// file per transfer and then hands it to the collector, so a measurement taken
/// after everything finished would find a tidy heap and prove nothing. What
/// separates streaming from buffering is how many bytes were allocated on the
/// way — <c>GC.GetTotalAllocatedBytes</c> counts exactly that, and it does not
/// care when they were freed.
/// </para>
/// <para>
/// <b>In the storage collection, which xUnit runs alone.</b> The counter is
/// process-wide: another suite allocating beside this one would be measured as
/// part of it.
/// </para>
/// </summary>
[Collection("storage")]
public sealed class MemoryTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>The package ceiling: the largest thing this product accepts.</summary>
    private const long FileBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Enough that a per-transfer buffer would be unmistakable — three files is
    /// 384 MiB, against a streamed path that should allocate a rounding error.
    /// </summary>
    private const int Concurrent = 3;

    private PostgreSqlContainer container = null!;
    private WebApplicationFactory<Program> host = null!;
    private string volume = "";

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder()
            .WithImage("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();
        await container.StartAsync();

        volume = Path.Combine(Path.GetTempPath(), $"algojudge-memory-{Guid.NewGuid():N}");

        host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DbConnectionString", container.GetConnectionString());
            builder.UseSetting("Admin:Token", "memory-tests");
            // A volume rather than the database: the claim is about the upload
            // and download paths, which are the same whichever store is
            // configured, and a filesystem store keeps 384 MiB of test out of a
            // PostgreSQL transaction log.
            builder.UseSetting("Storage:Stores:objects:Kind", "filesystem");
            builder.UseSetting("Storage:Stores:objects:Path", volume);
            builder.UseSetting("Storage:Default", "objects");
        });

        // Force the host to build now, so nothing below measures startup.
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Database.ApplicationDbContext>()
            .Database.CanConnectAsync();
    }

    public async Task DisposeAsync()
    {
        host.Dispose();
        await container.DisposeAsync();
        if (Directory.Exists(volume)) Directory.Delete(volume, recursive: true);
    }

    /// <summary>
    /// The storage path itself: hashing, spooling and writing, then reading back.
    /// <para>
    /// <b>The tightest of the three, because it is entirely ours.</b> Measured at
    /// <b>0 MiB per 128 MiB</b> on 2026-08-13 — the buffers are reused and
    /// nothing holds a file.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_storage_path_does_not_hold_a_file()
    {
        using var scope = host.Services.CreateScope();
        var stores = scope.ServiceProvider.GetRequiredService<IBlobStoreRegistry>();
        var store = stores.Default;

        // One first, so that anything allocated once — JIT, pooled buffers — is
        // paid for before the measurement starts.
        await store.WriteAsync(Guid.NewGuid(), new PatternStream(FileBytes), CancellationToken.None);

        var before = Settled();

        var written = await Task.WhenAll(Enumerable.Range(0, Concurrent).Select(async _ =>
        {
            var id = Guid.NewGuid();
            var result = await store.WriteAsync(id, new PatternStream(FileBytes), CancellationToken.None);
            return new BlobKey(id, result.Sha256);
        }));

        await Task.WhenAll(written.Select(async key =>
        {
            await using var reading = await store.OpenReadAsync(key, CancellationToken.None);
            await reading.CopyToAsync(Stream.Null);
        }));

        var moved = FileBytes * Concurrent * 2;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("storage path", moved, allocated);

        Assert.True(
            allocated < moved / 8,
            $"the storage path allocated {Mib(allocated)} MiB while moving {Mib(moved)} MiB");
    }

    /// <summary>
    /// Reading an upload off the wire: our parsing, over a body on disk.
    /// <para>
    /// <b>Deliberately not through the test host.</b> <c>TestServer</c> is not
    /// Kestrel — its in-memory request transport allocated <b>three times</b> the
    /// bytes it carried when this was measured, which would drown everything the
    /// product does. What is under test here is the reader this product chose,
    /// and it came to <b>8 MiB per 128 MiB</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Parsing_an_upload_does_not_hold_it()
    {
        var body = Path.Combine(Path.GetTempPath(), $"algojudge-body-{Guid.NewGuid():N}");

        try
        {
            string boundary;
            await using (var writing = new FileStream(body, FileMode.CreateNew, FileAccess.Write))
            using (var content = Multipart())
            {
                boundary = BoundaryOf(content);
                await content.CopyToAsync(writing);
            }

            await using var reading = new FileStream(body, FileMode.Open, FileAccess.Read);
            var before = Settled();

            var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, reading);
            while (await reader.ReadNextSectionAsync() is { } section)
            {
                // Through the same two wrappers the endpoint puts on it, so the
                // ceiling and the checksum are in the measurement too.
                await using var limited = new LimitedStream(section.Body, UploadLimits.Package);
                await using var hashing = new HashingStream(limited);
                await hashing.CopyToAsync(Stream.Null);
            }

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Report("upload parsing", FileBytes, allocated);

            Assert.True(
                allocated < FileBytes / 4,
                $"parsing allocated {Mib(allocated)} MiB for a {Mib(FileBytes)} MiB upload");
        }
        finally
        {
            if (System.IO.File.Exists(body)) System.IO.File.Delete(body);
        }
    }

    /// <summary>
    /// End to end, through the API, on three connections at once.
    /// <para>
    /// <b>The loosest threshold of the three, and the reason is measured.</b>
    /// Most of what this counts is <c>TestServer</c>'s own request transport —
    /// 384 MiB carried per 128 MiB uploaded, against 8 MiB for the parsing and 0
    /// for the store. So the number here says more about the harness than about
    /// the product, and a threshold tight enough to be interesting would be a
    /// threshold about xUnit.
    /// </para>
    /// <para>
    /// It is still worth running: a return to holding whole files would add
    /// several copies per transfer on top of that, which two and a half times
    /// the bytes moved still catches. Measured at <b>1.5×</b> on 2026-08-13.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_transfers_through_the_API_do_not_hold_files()
    {
        var client = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await UploadAsync(client);

        var before = Settled();

        var ids = await Task.WhenAll(
            Enumerable.Range(0, Concurrent).Select(_ => UploadAsync(client)));
        await Task.WhenAll(ids.Select(id => DownloadAsync(client, id)));

        var moved = FileBytes * Concurrent * 2;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("end to end", moved, allocated);

        Assert.True(
            allocated < moved * 5 / 2,
            $"allocated {Mib(allocated)} MiB while moving {Mib(moved)} MiB, "
            + "which is the shape of a path that holds whole files");
    }

    private static long Mib(long bytes) => bytes / (1024 * 1024);

    private void Report(string what, long moved, long allocated) =>
        output.WriteLine(
            $"{what,-15} moved {Mib(moved),5} MiB, allocated {Mib(allocated),5} MiB "
            + $"({(double)allocated / moved:P0})");

    private static MultipartFormDataContent Multipart()
    {
        var content = new MultipartFormDataContent();
        var file = new StreamContent(new PatternStream(FileBytes));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "package.zip");
        content.Add(new StringContent(PatternStream.Sha256Of(FileBytes)), "sha256");
        return content;
    }

    private static string BoundaryOf(MultipartFormDataContent content) =>
        Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(
            Microsoft.Net.Http.Headers.MediaTypeHeaderValue
                .Parse(content.Headers.ContentType!.ToString()).Boundary).Value!;

    /// <summary>
    /// The heap after everything is finished, with the collector given every
    /// chance. Two passes because the first may only queue finalizers.
    /// </summary>
    private static long Settled()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalAllocatedBytes(precise: true);
    }

    /// <summary>
    /// Uploads 128 MiB the Client's way — the file first, its checksum after —
    /// without ever holding the file.
    /// </summary>
    private static async Task<string> UploadAsync(HttpClient client)
    {
        using var content = Multipart();

        var response = await client.PostAsync("/api/v1/files", content);
        await Sign.Succeeded(response);

        var stored = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return stored.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Reads it back into nothing. <c>ReadAsByteArrayAsync</c> here would
    /// allocate the file in the test and measure the test.
    /// </summary>
    private static async Task DownloadAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync(
            $"/api/v1/files/{id}", HttpCompletionOption.ResponseHeadersRead);
        await Sign.Succeeded(response);

        await using var body = await response.Content.ReadAsStreamAsync();
        await body.CopyToAsync(Stream.Null);
    }

    /// <summary>
    /// 128 MiB that exist only while they are being read. A <c>byte[]</c> here
    /// would put the very allocation under test into the test.
    /// </summary>
    private sealed class PatternStream(long length) : Stream
    {
        private long position;

        /// <summary>What a stream of this length hashes to, computed the same way.</summary>
        public static string Sha256Of(long length)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];

            for (long written = 0; written < length;)
            {
                var take = (int)Math.Min(buffer.Length, length - written);
                for (var i = 0; i < take; i++) buffer[i] = (byte)((written + i) % 251);
                hash.AppendData(buffer, 0, take);
                written += take;
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - position);
            for (var i = 0; i < take; i++) buffer[offset + i] = (byte)((position + i) % 251);
            position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var take = (int)Math.Min(buffer.Length, length - position);
            for (var i = 0; i < take; i++) buffer.Span[i] = (byte)((position + i) % 251);
            position += take;
            return ValueTask.FromResult(take);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
