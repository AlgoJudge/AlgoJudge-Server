using System.Diagnostics;
using AlgoJudge.Server.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What one bad reader costs everybody else.
/// <para>
/// No fixture and no database: the hub is a singleton over a dictionary of
/// sockets, and what is under test is how long it is prepared to wait. Going
/// through a real host would prove the endpoint works and say nothing about the
/// deadline, which is the thing that was missing.
/// </para>
/// </summary>
public class EventFanOutTests
{
    private static EventHub Hub(double seconds) =>
        new(
            NullLogger<EventHub>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Events:SendTimeoutSeconds"] = seconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                })
                .Build());

    /// <summary>
    /// The whole defect, in one assertion each: the fan-out must finish, the
    /// stalled reader must be gone, its socket must be closed rather than left
    /// registered-but-open, and the healthy reader must still have been told.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_recipient_that_never_takes_a_frame_is_dropped_rather_than_holding_the_fan_out()
    {
        var hub = Hub(seconds: 1);

        var stalled = new StallingSocket();
        var healthy = new RecordingSocket();
        hub.Add("stalled", stalled);
        hub.Add("healthy", healthy);

        var clock = Stopwatch.StartNew();
        await hub.SendToUsersAsync(["stalled", "healthy"], "somethingChanged", new { });
        clock.Stop();

        // Generous, because this is not a benchmark: without the deadline it
        // does not finish at all, and the test dies on its own timeout instead.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(20),
            $"the fan-out took {clock.Elapsed}, so it waited on the stalled reader");

        Assert.Equal(0, hub.ConnectionsFor("stalled"));
        Assert.True(
            stalled.Aborted,
            "the stalled socket was unregistered but left open, so its connection is still held");

        Assert.Single(healthy.Frames);
        Assert.Equal(1, hub.ConnectionsFor("healthy"));
    }

    /// <summary>
    /// The cost of a stalled reader must not scale with how many people are
    /// behind it. Sequential sends made it one deadline each; the fan-out is
    /// bounded once.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Several_stalled_recipients_cost_one_deadline_between_them()
    {
        var hub = Hub(seconds: 2);

        var stalled = Enumerable.Range(0, 5).Select(_ => new StallingSocket()).ToList();
        for (var i = 0; i < stalled.Count; i++) hub.Add($"stalled-{i}", stalled[i]);

        var clock = Stopwatch.StartNew();
        await hub.SendToUsersAsync(
            [.. Enumerable.Range(0, stalled.Count).Select(i => $"stalled-{i}")],
            "somethingChanged",
            new { });
        clock.Stop();

        // Five sequential two-second deadlines would be ten; one is two.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(8),
            $"five stalled readers took {clock.Elapsed}, so they were waited on one after another");

        Assert.All(stalled, socket => Assert.True(socket.Aborted));
    }

    /// <summary>
    /// The ordinary case, so the deadline cannot be mistaken for something that
    /// drops healthy readers as well.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Everybody_who_takes_the_frame_keeps_their_connection()
    {
        var hub = Hub(seconds: 1);

        var first = new RecordingSocket();
        var second = new RecordingSocket();
        hub.Add("first", first);
        hub.Add("second", second);

        await hub.SendToUsersAsync(["first", "second"], "somethingChanged", new { });

        Assert.Single(first.Frames);
        Assert.Single(second.Frames);
        Assert.Contains("somethingChanged", first.Frames[0]);
        Assert.Equal(1, hub.ConnectionsFor("first"));
        Assert.Equal(1, hub.ConnectionsFor("second"));
    }
}
