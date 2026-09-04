using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// A nudge, so a Runner holding a <c>claim</c> open learns that work exists
    /// without asking again.
    /// <para>
    /// **This is not a socket, and it does not hand anything out.** Every job
    /// still leaves through <c>ClaimAsync</c>, one at a time, under
    /// <c>FOR UPDATE SKIP LOCKED</c> — so a nudge cannot deliver a job twice,
    /// and a nudge that is lost costs nothing but the wait it would have cut
    /// short. That is what lets the whole arrangement be optimistic: the
    /// deadline is the mechanism and the signal is only an optimisation of
    /// *when* a Runner looks.
    /// </para>
    /// <para>
    /// **In this process only.** A second Server instance would not see these,
    /// and the decision of 2026-08-27 says what the answer will be when there
    /// is one: PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c>, not Redis. Until then a
    /// missed nudge degrades to the wait, which is exactly the behaviour every
    /// installation has today.
    /// </para>
    /// </summary>
    public interface IQueueSignal
    {
        /// <summary>Says that something may now be claimable.</summary>
        void Wake();

        /// <summary>
        /// The signal as it stands **now**, to be awaited after a look.
        /// <para>
        /// Separate from the waiting because the order is what makes the wait
        /// safe: a look that finds an empty queue, and only then starts
        /// listening, is deaf for exactly as long as it takes to roll back a
        /// transaction and unwind — and a submission committed in that window
        /// nudges nobody. Taking the signal first turns that window into a
        /// nudge already delivered, so the wait ends at once and looks again.
        /// </para>
        /// </summary>
        IQueueNudge Capture();
    }

    /// <summary>
    /// One captured signal. Waiting on it answers whether a nudge came rather
    /// than the time running out — and a nudge that landed **after the capture
    /// and before the wait** counts, which is the entire point of holding one.
    /// </summary>
    public interface IQueueNudge
    {
        Task<bool> WaitAsync(TimeSpan within, CancellationToken ct);
    }

    public sealed class QueueSignal : IQueueSignal
    {
        /// <summary>
        /// **Replaced rather than reset.** A completed source stays completed,
        /// so the one every waiter is holding is swapped for a fresh one at the
        /// moment it is completed. A Runner that captures afterwards takes the
        /// new one and is not handed a nudge that has already been and gone.
        /// </summary>
        private TaskCompletionSource waiting = Fresh();

        private static TaskCompletionSource Fresh() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Wake() =>
            Interlocked.Exchange(ref waiting, Fresh()).TrySetResult();

        public IQueueNudge Capture() => new Nudge(Volatile.Read(ref waiting).Task);

        private sealed class Nudge(Task nudged) : IQueueNudge
        {
            public async Task<bool> WaitAsync(TimeSpan within, CancellationToken ct)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(within);
                try
                {
                    await nudged.WaitAsync(deadline.Token);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // Told apart deliberately: the client going away is not the
                    // same as the wait running out, and only the first should
                    // end the request rather than answer it.
                    ct.ThrowIfCancellationRequested();
                    return false;
                }
            }
        }
    }
}
