using AlgoJudge.Server.Services;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The nudge, and the one ordering that makes it worth having.
///
/// <para>
/// **A signal is only ever an optimisation of *when* a Runner looks** — every
/// job still leaves through <c>ClaimAsync</c> under
/// <c>FOR UPDATE SKIP LOCKED</c>, so nothing here can deliver a job twice and a
/// nudge that is genuinely lost costs one wait of latency. That is exactly why
/// these are worth pinning: the failure is invisible, it looks like the queue
/// simply being slow, and the only thing separating "fast" from "25 seconds" is
/// which side of a look the signal was taken on.
/// </para>
/// <para>
/// No fixture and no database. The two cases below are the whole contract, and
/// they are the two that a plausible implementation gets backwards.
/// </para>
/// </summary>
public class QueueSignalTests
{
    /// <summary>
    /// **A nudge that lands while the caller is looking is not lost.**
    /// <para>
    /// This is the window the capture exists for. A claim looks at the queue,
    /// finds nothing, and only then starts listening; between the look's own
    /// snapshot and the listening there is a rollback, a disposal and a return,
    /// and a submission committed in there would fire its nudge into an empty
    /// room. Taking the signal *before* the look turns that same window into a
    /// nudge already delivered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_nudge_between_the_capture_and_the_wait_is_still_delivered()
    {
        var signal = new QueueSignal();

        var nudge = signal.Capture();
        // The look happens here, and finds nothing.
        signal.Wake();

        Assert.True(await nudge.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    /// <summary>
    /// **A nudge that has already been and gone is not delivered again.**
    /// <para>
    /// The other half, and the reason the source is replaced rather than reset:
    /// without it a capture would inherit a completed task for ever and every
    /// wait would return at once, turning a held claim back into the busy poll
    /// the whole arrangement replaced. The generous five seconds above and the
    /// short deadline here are deliberate — this one has to prove a timeout, so
    /// it must not be able to pass by being slow.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_nudge_taken_before_the_capture_is_not_waiting_for_it()
    {
        var signal = new QueueSignal();

        signal.Wake();
        var nudge = signal.Capture();

        Assert.False(await nudge.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None));
    }
}
