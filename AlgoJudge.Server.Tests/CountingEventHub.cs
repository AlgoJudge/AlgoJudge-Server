using System.Collections.Concurrent;
using System.Net.WebSockets;
using AlgoJudge.Server.Realtime;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Counts what was announced, so "exactly once" can be asserted rather than
/// hoped for.
///
/// <para>
/// The real hub writes to sockets, and a test that watches rows instead can only
/// see that a marker ended up set — which is equally true of a round announced
/// once and one announced twice by two Server instances at the same moment. The
/// event is the thing a person actually receives, so it is the thing to count.
/// </para>
/// </summary>
public sealed class CountingEventHub : IEventHub
{
    private readonly ConcurrentBag<(string Type, object Data)> sent = [];

    public IReadOnlyList<(string Type, object Data)> Sent => sent.ToList();

    private readonly ConcurrentBag<(string Type, IReadOnlyList<string> To)> addressed = [];

    /// <summary>
    /// Who each announcement was addressed to.
    /// <para>
    /// Beside <see cref="Sent"/> rather than folded into it: several tests read
    /// that tuple and counting announcements is a different question from asking
    /// who hears one. This one exists for the second — the scheduler used to
    /// resolve its own audience, and got a different answer from every other
    /// caller.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Type, IReadOnlyList<string> To)> Addressed => addressed.ToList();

    public Task SendToUsersAsync(
        IEnumerable<string> userIds, string type, object data, CancellationToken ct = default)
    {
        // Once per send, not once per recipient: the question is how many times
        // the Server decided to announce something, not how many people heard it.
        sent.Add((type, data));
        addressed.Add((type, userIds.ToList()));
        return Task.CompletedTask;
    }

    public Task SendToUserAsync(string userId, string type, object data, CancellationToken ct = default)
    {
        sent.Add((type, data));
        addressed.Add((type, new List<string> { userId }));
        return Task.CompletedTask;
    }

    // Nothing here holds a socket, and nothing under test asks it to.
    public EventConnection Add(string userId, WebSocket socket) =>
        throw new NotSupportedException("this hub counts announcements and holds no sockets");

    public void Remove(EventConnection connection) { }

    public int ConnectionsFor(string userId) => 0;
}
