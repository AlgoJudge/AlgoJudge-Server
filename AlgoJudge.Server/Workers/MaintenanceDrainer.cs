using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Decides when draining is over, and closes the door.
    /// <para>
    /// Throwing the switch stops new work; this is what notices that the old
    /// work has finished. Between the two, an operator waiting to back up a
    /// database has a definite moment to wait for rather than a guess.
    /// </para>
    /// <para>
    /// <b>Both queues, always.</b> `EvaluationJobs` and `Trials` each hold
    /// `Running` rows with their own leases, and a drain that looked at one
    /// would announce silence while a Runner was still writing.
    /// </para>
    /// </summary>
    public class MaintenanceDrainer(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        TimeProvider clock,
        ILogger<MaintenanceDrainer> logger
    ) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long draining may last before the door is closed anyway.
        /// <para>
        /// Five minutes by default. **Nothing is lost when it fires**: an
        /// abandoned job keeps its lease, the lease expires, and the reaper puts
        /// it back on the queue — so the cost of forcing is one evaluation done
        /// twice, and the cost of never forcing is an operator held hostage by a
        /// single wedged Runner.
        /// </para>
        /// </summary>
        private TimeSpan ForceAfter =>
            TimeSpan.FromSeconds(configuration.GetValue("Maintenance:ForceAfterSeconds", 300));

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            using var timer = new PeriodicTimer(Interval, clock);

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stopping);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed pass is not a failed process, as elsewhere: the
                    // next one is five seconds away, and a worker that took the
                    // host down would turn a database blip into the outage it
                    // was trying to schedule.
                    logger.LogError(e, "The maintenance drain failed");
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
        /// One pass. Returns whether it closed the door on this pass.
        /// <para>
        /// Internal so a test can drive it against a turned clock rather than
        /// waiting five real minutes for the forced transition.
        /// </para>
        /// </summary>
        internal async Task<bool> TickAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();

            var state = await maintenance.StateAsync(ct);
            if (state.Level != MaintenanceLevel.Draining) return false;

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var running =
                await context.EvaluationJobs.CountAsync(j => j.State == EvaluationJobState.Running, ct)
                + await context.Trials.CountAsync(t => t.State == EvaluationJobState.Running, ct);

            var now = clock.GetUtcNow().UtcDateTime;
            var waited = state.RequestedAt is { } since ? now - since : TimeSpan.Zero;
            var forced = waited >= ForceAfter;

            if (running > 0 && !forced)
            {
                logger.LogInformation(
                    "Draining: {Running} still running, {Waited:0} s so far", running, waited.TotalSeconds);
                return false;
            }

            if (forced && running > 0)
            {
                // Said at warning level because it is the one outcome an
                // operator should look into afterwards: something did not
                // finish, and its lease is what will return it.
                logger.LogWarning(
                    "Closing with {Running} still running after {Waited:0} s; their leases will requeue them",
                    running, waited.TotalSeconds);
            }

            // The service announces; this only decides when.
            await maintenance.CloseAsync(ct);
            logger.LogInformation("Maintenance is now closed");
            return true;
        }

    }
}
