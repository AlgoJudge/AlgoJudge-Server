using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Api.Contracts;

namespace AlgoJudge.Server.Realtime
{
    /// <summary>
    /// One open socket.
    /// <para>
    /// A session is neither a person nor a browser tab: one sign-in may hold
    /// several sockets, and the users screen shows the <b>count</b>. That count
    /// is taken from this registry rather than stored, because a number written
    /// to a row survives a crash and then tells the screen somebody is present
    /// who left hours ago.
    /// </para>
    /// </summary>
    public sealed class EventConnection(string userId, WebSocket socket)
    {
        public string UserId { get; } = userId;
        public WebSocket Socket { get; } = socket;
        public Guid Id { get; } = Utils.Uuid.New();

        // Not disposed, and deliberately: disposing it races an in-flight send,
        // whose `finally` would then throw `ObjectDisposedException` out of a
        // `finally`. `SemaphoreSlim` only needs disposal once `AvailableWaitHandle`
        // has been read, and nothing here reads it.
        private readonly SemaphoreSlim writeLock = new(1, 1);

        /// <summary>
        /// Sends one frame, or gives up. Serialised per connection: a WebSocket
        /// permits one send at a time, and two events racing would interleave
        /// into a frame nobody can parse.
        /// <para>
        /// <b>Bounded by <paramref name="deadline"/>, which is the whole point.</b>
        /// A client that opens a socket and stops draining its receive window
        /// used to park this <c>await</c> for ever — and with the fan-out below
        /// awaiting each recipient in turn, one such client stopped delivery to
        /// everybody. `SeriesScheduler` awaits its tick inline on a token that
        /// only fires at shutdown, so it stopped rounds opening at all.
        /// </para>
        /// </summary>
        public async Task<bool> SendAsync(
            ReadOnlyMemory<byte> payload, TimeSpan deadline, CancellationToken ct)
        {
            if (Socket.State != WebSocketState.Open) return false;

            // The `TimeSpan` overload answers `false` rather than throwing, and
            // the wait is outside the `try` — so a timeout here must not fall
            // through to the `finally` and release a semaphore it never took.
            if (!await writeLock.WaitAsync(deadline, ct)) return false;

            try
            {
                if (Socket.State != WebSocketState.Open) return false;

                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(deadline);

                await Socket.SendAsync(payload, WebSocketMessageType.Text, true, bounded.Token);
                return true;
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A socket that died between the state check and the send is an
                // ordinary event, not a fault. Reconnect is a refetch, so nothing
                // is lost by dropping it.
                return false;
            }
            finally
            {
                writeLock.Release();
            }
        }
    }

    /// <summary>
    /// Who is connected, and how an event reaches them.
    /// <para>
    /// <b>There are no subscriptions.</b> The Client does not ask for anything;
    /// the Server sends an event to a session only if that session may read what
    /// the event names, judged by the same permission model that guards the REST
    /// endpoint carrying the same data. A subscribe protocol would restate the
    /// permission model on the wire, and a client that can ask is a client that
    /// can ask for something it may not have.
    /// </para>
    /// <para>
    /// Nothing here is durable. REST is the source of persistent state and this
    /// only says something changed, so there is no replay, no cursor and no
    /// catch-up buffer — a screen that missed an event refetches.
    /// </para>
    /// </summary>
    public interface IEventHub
    {
        EventConnection Add(string userId, WebSocket socket);
        void Remove(EventConnection connection);

        /// <summary>How many sockets this user holds, right now.</summary>
        int ConnectionsFor(string userId);

        /// <summary>Sends to one user's sockets.</summary>
        Task SendToUserAsync(string userId, string type, object data, CancellationToken ct = default);

        /// <summary>
        /// Sends to several users at once. The caller has already decided who may
        /// read it — that decision is authorization and does not belong here.
        /// </summary>
        Task SendToUsersAsync(IEnumerable<string> userIds, string type, object data, CancellationToken ct = default);
    }

    public class EventHub(ILogger<EventHub> logger, IConfiguration configuration) : IEventHub
    {
        /// <summary>
        /// How long one frame may take before its reader is dropped.
        /// <para>
        /// A setting rather than a constant so an operator on a slow link has a
        /// lever. Five seconds is far beyond any healthy client and far below
        /// anything a person waits through, and nothing here is durable — the
        /// screen that missed an event refetches, which is what makes dropping a
        /// reader the right answer rather than a loss.
        /// </para>
        /// </summary>
        private readonly TimeSpan deadline = TimeSpan.FromSeconds(
            configuration.GetValue("Events:SendTimeoutSeconds", 5.0));

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, EventConnection>> byUser = new();

        public EventConnection Add(string userId, WebSocket socket)
        {
            var connection = new EventConnection(userId, socket);
            byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, EventConnection>())[connection.Id] = connection;
            return connection;
        }

        public void Remove(EventConnection connection)
        {
            if (!byUser.TryGetValue(connection.UserId, out var connections)) return;
            connections.TryRemove(connection.Id, out _);
            // Left in place when empty rather than removed under a lock: an empty
            // bag costs a few bytes, and removing it races with an arriving
            // connection for the same user.
        }

        /// <summary>
        /// Drops a connection that would not take a frame.
        /// <para>
        /// <see cref="Remove"/> alone only unregisters, and after a send that ran
        /// out of time the frame is half-written — so the socket is unusable and
        /// leaving it open holds a connection nobody can send on.
        /// <c>Abort</c> makes the reader in <c>EventSocket</c> throw, which its
        /// own catch already treats as the ordinary end of a socket.
        /// </para>
        /// </summary>
        private void Evict(EventConnection connection)
        {
            Remove(connection);

            try
            {
                connection.Socket.Abort();
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
            {
                // Already gone, which is the outcome wanted.
            }

            logger.LogDebug("Dropped a connection for {User} that would not take a frame", connection.UserId);
        }

        public int ConnectionsFor(string userId) =>
            byUser.TryGetValue(userId, out var connections)
                ? connections.Values.Count(c => c.Socket.State == WebSocketState.Open)
                : 0;

        public Task SendToUserAsync(string userId, string type, object data, CancellationToken ct = default) =>
            SendToUsersAsync([userId], type, data, ct);

        public async Task SendToUsersAsync(
            IEnumerable<string> userIds, string type, object data, CancellationToken ct = default)
        {
            var envelope = new EventEnvelopeDto { Type = type, Data = data };
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);

            var recipients = userIds.Distinct().ToList();

            // **Every recipient at once, and bounded once.** Awaiting them in
            // turn made the cost of a stalled client the deadline times the
            // number of recipients ahead of everybody else; in parallel it is one
            // deadline for the whole fan-out. Each connection holds its own lock,
            // and `Values` is a snapshot, so evicting while walking is safe.
            var sends = recipients
                .Where(byUser.ContainsKey)
                .SelectMany(userId => byUser[userId].Values)
                .Select(async connection =>
                    (Connection: connection, Sent: await connection.SendAsync(payload, deadline, ct)))
                .ToList();

            foreach (var (connection, sent) in await Task.WhenAll(sends))
            {
                if (!sent) Evict(connection);
            }

            logger.LogDebug("Dispatched {Type} to {Count} recipients", type, recipients.Count);
        }
    }

    /// <summary>
    /// The socket endpoint itself.
    /// <para>
    /// Authenticated by the same session cookie as every other request. No token
    /// in the query string: it would end up in proxy logs.
    /// </para>
    /// </summary>
    public static class EventSocket
    {
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(25);

        public static void MapEventSocket(this WebApplication app, string path)
        {
            app.Map(path, async (HttpContext http, IEventHub hub, ILoggerFactory loggers) =>
            {
                if (!http.WebSockets.IsWebSocketRequest)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var userId = http.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userId))
                {
                    // A connection nobody is signed in for is refused at the
                    // handshake, which is where the Client already expects it.
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                var logger = loggers.CreateLogger("EventSocket");
                using var socket = await http.WebSockets.AcceptWebSocketAsync();
                var connection = hub.Add(userId, socket);
                logger.LogDebug("Socket open for {UserId}", userId);

                try
                {
                    await PumpAsync(socket, http.RequestAborted);
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException)
                {
                    // A socket that goes away is the normal end of one.
                }
                finally
                {
                    hub.Remove(connection);
                    logger.LogDebug("Socket closed for {UserId}", userId);
                }
            });
        }

        /// <summary>
        /// Reads until the peer goes away, and pings while it waits.
        /// <para>
        /// The Client sends nothing — there are no subscribe frames — so this
        /// exists to notice a half-open socket. One that died silently and stayed
        /// counted would make the users screen lie about who is present.
        /// </para>
        /// </summary>
        private static async Task PumpAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[4 * 1024];
            using var pings = new PeriodicTimer(PingInterval);

            var reading = socket.ReceiveAsync(buffer, ct);

            while (socket.State == WebSocketState.Open)
            {
                var ticked = pings.WaitForNextTickAsync(ct).AsTask();
                var finished = await Task.WhenAny(reading, ticked);

                if (finished == reading)
                {
                    var result = await reading;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        return;
                    }
                    // Anything the Client sends is ignored on purpose.
                    reading = socket.ReceiveAsync(buffer, ct);
                }
                else
                {
                    var ping = Encoding.UTF8.GetBytes("""{"type":"ping","data":{}}""");
                    await socket.SendAsync(ping, WebSocketMessageType.Text, true, ct);
                }
            }
        }
    }
}
