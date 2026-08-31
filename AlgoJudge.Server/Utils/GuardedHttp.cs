using System.Net;
using System.Net.Sockets;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that checks the address it is about to
    /// connect to, rather than the name it was given.
    /// <para>
    /// <b>Checking a name and then connecting is two lookups.</b> A host whose
    /// owner answers differently the second time reaches inside — DNS rebinding,
    /// and the window is minutes wide. So the check rides the connect callback,
    /// applies to the address actually being dialled, and applies again to every
    /// hop of a redirect.
    /// </para>
    /// <para>
    /// The predicate is a parameter because the two callers want different
    /// answers: fetching a statement off the internet may reach nothing private
    /// (<see cref="PublicAddress.IsPublic"/>), while reaching a paired LMS may
    /// reach the operator's own network but never the cloud metadata service
    /// (<see cref="PublicAddress.IsPublicOrPrivateNetwork"/>).
    /// </para>
    /// </summary>
    public static class GuardedHttp
    {
        public static SocketsHttpHandler Handler(Func<IPAddress, bool> mayConnect) =>
            new()
            {
                // A redirect is a second address, chosen by whoever answered the
                // first. The callback below would check it — but a redirect can
                // also change the scheme, and the caller's own https rule cannot
                // ride along. Refused outright, as the cheaper half of the same
                // argument.
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                // Nothing is reused across fetches: a pooled connection is a
                // connection whose address was checked for somebody else's
                // request, some minutes ago.
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = async (connection, ct) =>
                {
                    var resolved = await Dns.GetHostAddressesAsync(connection.DnsEndPoint.Host, ct);
                    var reachable = resolved.Where(mayConnect).ToArray();

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
                },
            };

        /// <summary>
        /// Whether a failure was this guard refusing, rather than the network.
        /// The handler's exception arrives wrapped, so the chain is walked.
        /// </summary>
        public static bool Refused(Exception e)
        {
            for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is InsideTheNetworkException) return true;
            }
            return false;
        }

        public sealed class InsideTheNetworkException(string host)
            : Exception($"{host} resolves only to addresses this Server may not reach");
    }
}
