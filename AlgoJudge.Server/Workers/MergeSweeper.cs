using AlgoJudge.Server.Services;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Removes the accounts a merge emptied, once nobody can undo it any more.
    /// <para>
    /// <b>Its own worker rather than a second job in the deletion sweep</b>, for
    /// the reason every other one here is its own: a sweep that fails takes only
    /// its own work down with it, and the log then says which.
    /// </para>
    /// </summary>
    public class MergeSweeper(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<MergeSweeper> logger
    ) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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
                    logger.LogError(e, "The merge sweep failed");
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

        internal async Task<int> SweepAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var merges = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            return await merges.SweepAsync(ct);
        }
    }
}
