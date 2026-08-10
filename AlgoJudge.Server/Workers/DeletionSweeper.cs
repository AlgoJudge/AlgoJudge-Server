using AlgoJudge.Server.Services;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Carries out the deletion requests whose window has closed.
    /// <para>
    /// Only the provider's back channel ever waits: a person who asks to leave
    /// is here and gets an answer now. What waits is a request from a directory
    /// about somebody who cannot speak for themselves any more, and the wait is
    /// <b>an administrator's day to stop it</b>.
    /// </para>
    /// <para>
    /// A minute is often enough. The window is twenty-four hours, so the cost of
    /// checking is one indexed query against a table that is nearly always empty
    /// — and the cost of a longer interval is that "stopped in time" and
    /// "stopped too late" are decided by when the timer happened to fire.
    /// </para>
    /// </summary>
    public class DeletionSweeper(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<DeletionSweeper> logger
    ) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            using var timer = new PeriodicTimer(Interval, clock);

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stopping);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A sweep that throws must not end the worker. The next tick
                    // finds the same rows still due, which is the whole recovery
                    // story for anything transient.
                    logger.LogError(e, "The deletion sweep failed");
                }

                try
                {
                    await timer.WaitForNextTickAsync(stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// One pass. Internal so a test can run it against a clock it turns,
        /// rather than waiting a day for a window to close.
        /// </summary>
        internal async Task<int> SweepAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var deletion = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
            return await deletion.SweepAsync(ct);
        }
    }
}
