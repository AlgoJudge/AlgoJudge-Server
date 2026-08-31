using System.Net.WebSockets;
using System.Text;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A socket that accepts a frame and never finishes taking it — a client that
/// connected and then stopped draining its receive window.
/// <para>
/// This is the shape the fan-out had no answer for: <c>EventConnection.SendAsync</c>
/// awaited the socket with no deadline, and <c>SendToUsersAsync</c> awaited each
/// recipient in turn, so one of these stopped delivery to everybody. The worst
/// caller was <c>SeriesScheduler</c>, which awaits its tick inline on a token
/// that only fires at shutdown — so no round opened or closed until a restart.
/// </para>
/// </summary>
public sealed class StallingSocket : WebSocket
{
    /// <summary>Whether the hub gave up on it and closed it.</summary>
    public bool Aborted { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;

    public override void Abort() => Aborted = true;

    public override Task CloseAsync(
        WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;

    public override Task CloseOutputAsync(
        WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken ct) =>
        Task.FromException<WebSocketReceiveResult>(
            new NotSupportedException("nothing under test reads from this socket"));

    /// <summary>Never completes, until whoever is waiting stops waiting.</summary>
    public override Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct);
}

/// <summary>
/// A socket that takes every frame at once and remembers it, so a test can say
/// what a healthy recipient received while somebody else was stalling.
/// </summary>
public sealed class RecordingSocket : WebSocket
{
    private readonly List<string> frames = [];

    public IReadOnlyList<string> Frames
    {
        get { lock (frames) return [.. frames]; }
    }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => WebSocketState.Open;

    public override void Abort()
    {
    }

    public override Task CloseAsync(
        WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;

    public override Task CloseOutputAsync(
        WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken ct) =>
        Task.FromException<WebSocketReceiveResult>(
            new NotSupportedException("nothing under test reads from this socket"));

    public override Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    {
        lock (frames) frames.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        return Task.CompletedTask;
    }
}
