using System.Net;
using System.Net.Sockets;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using DbFile = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Services
{
    public interface IExternalFetchService
    {
        /// <summary>Fetches an address into an ordinary file, or refuses it.</summary>
        Task<DbFile> FetchAsync(string? url, CancellationToken ct);
    }

    /// <summary>
    /// Fetching a document from a host the installation named.
    /// <para>
    /// <b>This is a Server making a request somebody else chose the target of</b>,
    /// which is the shape of every SSRF there has ever been. Four things stand
    /// between it and the inside of whatever network this runs in, and **each of
    /// them is load-bearing on its own**:
    /// </para>
    /// <list type="number">
    /// <item>the address is read and its host compared against the installation's
    /// list — <see cref="FetchTarget"/>, and it settles nothing about where that
    /// host actually is;</item>
    /// <item>the name is resolved <b>once</b> and the connection is made to an
    /// address that was checked — not to a name that was, which is a different
    /// promise and the one DNS rebinding breaks;</item>
    /// <item>redirects are refused outright, because a redirect is a second
    /// address chosen by the far end after every check was made about the
    /// first;</item>
    /// <item>the body is counted as it arrives, because <c>Content-Length</c> is
    /// a claim by the sender.</item>
    /// </list>
    /// <para>
    /// The Server learns nothing about what it fetched. The bytes become an
    /// ordinary <see cref="DbFile"/>, addressed by a checksum it computed itself,
    /// and no part of this knows or asks what is on the other end.
    /// </para>
    /// </summary>
    public class ExternalFetchService(
        ApplicationDbContext context,
        IFileService files,
        IPermissionService permissions
    ) : IExternalFetchService
    {
        /// <summary>
        /// What one document may weigh. A statement is a PDF; anything past this
        /// is either not one or is not something to pull through a Server on
        /// somebody's say-so.
        /// </summary>
        private const long MaxBytes = 32L * 1024 * 1024;

        public async Task<DbFile> FetchAsync(string? url, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemImportExternal, null, ct);

            var instance = await context.Instance.AsNoTracking().FirstOrDefaultAsync(ct);

            // The same switch that stops external work being handed out stops
            // external content being pulled in. One decision, both directions.
            if (instance is null || !instance.ExternalJudgingEnabled)
            {
                throw new ValidationException(
                    "This installation does not fetch content from elsewhere",
                    "fetch.disabled");
            }

            var decision = FetchTarget.Check(url, instance.ExternalFetchHosts);
            if (!decision.Allowed)
            {
                throw new ValidationException("That address may not be fetched", decision.Refusal);
            }

            using var response = await SendAsync(decision.Target!, ct);

            RefuseUnlessUsable(response.StatusCode);

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await using var counted = new CountedStream(body, MaxBytes);

            var staged = await files.StageAsync(counted, ct);

            // The checksum is the one the store computed while writing, so this
            // is the same commit an upload makes — there is no second path for
            // bytes that arrived this way.
            return await files.CommitAsync(
                staged, NameOf(decision.Target!), MediaTypeOf(response), staged.Key.Sha256, ct);
        }

        /// <summary>
        /// What the far end answered, and whether it is something to store.
        /// <para>
        /// <b>A redirect is refused rather than followed or re-checked.</b>
        /// Re-checking the new address is defensible and is also how this grows
        /// a second, subtler path to the same mistake — every guard above was
        /// made about the address the caller gave, and a redirect is one the far
        /// end chose afterwards. Refusing is one line and cannot rot.
        /// </para>
        /// <para>
        /// Public so it can be tested for what it is: a decision, not a network
        /// call. Everything else on this path needs a socket to exercise, which
        /// is exactly why this part does not.
        /// </para>
        /// </summary>
        public static void RefuseUnlessUsable(HttpStatusCode status)
        {
            if ((int)status is >= 300 and < 400)
            {
                throw new ValidationException(
                    "That address redirects, and a redirect is not followed", "fetch.redirect");
            }

            if ((int)status is < 200 or >= 300)
            {
                throw new ValidationException($"The host answered {(int)status}", "fetch.status");
            }
        }

        private static async Task<HttpResponseMessage> SendAsync(Uri target, CancellationToken ct)
        {
            try
            {
                return await Http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException e) when (Inside(e))
            {
                throw new ValidationException(
                    "That host resolves to an address inside this network", "fetch.host.inside");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ValidationException("That host could not be reached", "fetch.unreachable");
            }
        }

        private static bool Inside(Exception e)
        {
            for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is InsideTheNetworkException) return true;
            }
            return false;
        }

        /// <summary>A name to file it under, from the address and nothing else.</summary>
        private static string NameOf(Uri target)
        {
            var last = target.Segments.LastOrDefault()?.Trim('/') ?? "";
            var cleaned = new string(last.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
            return cleaned.Length is > 0 and <= 120 ? cleaned : "fetched";
        }

        /// <summary>
        /// The media type alone, without the parameters that came with it.
        /// <para>
        /// Taken from the far end because it is the only thing that knows, and
        /// treated as a label rather than as a fact: nothing here decides what to
        /// do based on it, and the Client renders by the problem type.
        /// </para>
        /// </summary>
        private static string MediaTypeOf(HttpResponseMessage response) =>
            response.Content.Headers.ContentType?.MediaType is { Length: > 0 } media
                ? media
                : "application/octet-stream";

        // ── The client, and the callback that is the whole point ─────────────

        private static readonly HttpClient Http = Build();

        private static HttpClient Build()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                // Nothing is reused across fetches: a pooled connection is a
                // connection whose address was checked for somebody else's
                // request, some minutes ago.
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = ConnectAsync,
            };

            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// Resolves once, keeps only the addresses that are out on the internet,
        /// and connects to those.
        /// <para>
        /// <b>The gap this closes is the whole reason it exists.</b> Letting the
        /// stack resolve the name itself would mean the address checked and the
        /// address connected to are two separate lookups, and a name whose owner
        /// answers differently the second time is a name that reaches inside.
        /// </para>
        /// </summary>
        private static async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext connection, CancellationToken ct)
        {
            var resolved = await Dns.GetHostAddressesAsync(connection.DnsEndPoint.Host, ct);
            var reachable = resolved.Where(PublicAddress.IsPublic).ToArray();

            if (reachable.Length == 0)
            {
                throw new InsideTheNetworkException(connection.DnsEndPoint.Host);
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(reachable, connection.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private sealed class InsideTheNetworkException(string host)
            : Exception($"{host} resolves only to addresses inside this network");
    }
}
