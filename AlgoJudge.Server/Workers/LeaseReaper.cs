using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Gives back jobs whose lease ran out.
    /// <para>
    /// This is the whole recovery story. The Runner is stateless apart from a
    /// package cache, so one that dies mid-job resumes nothing — nobody is
    /// coming back for that work. The Server reclaims it when the lease expires,
    /// which makes the deadline a <b>correctness mechanism</b> rather than a
    /// safety net: it has to outlast the slowest real evaluation, or this will
    /// hand a job to a second Runner while the first is still working on it.
    /// </para>
    /// <para>
    /// A job that keeps being reclaimed is a job that keeps killing Runners.
    /// Past the delivery cap it is failed with a reason rather than returned to
    /// the queue, because retrying it for ever is how one bad package stops an
    /// installation.
    /// </para>
    /// </summary>
    public class LeaseReaper(
        IServiceScopeFactory scopes,
        IQueueSignal queue,
        TimeProvider clock,
        ILogger<LeaseReaper> logger
    ) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            using var timer = new PeriodicTimer(Interval, clock);

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stopping);
                    await SweepTrialsAsync(stopping);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed sweep is not a failed process. The next one runs
                    // in thirty seconds, and a worker that took the host down
                    // with it would turn a transient database blip into an
                    // outage.
                    logger.LogError(e, "Lease sweep failed");
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
        /// The same recovery, for trials.
        /// <para>
        /// Swept beside jobs rather than by a second worker: it is the same
        /// deadline doing the same thing, and a trial whose lease expired with
        /// nobody to reclaim it would sit `running` for ever — visible to
        /// whoever asked for it, and counting against their ceiling.
        /// </para>
        /// </summary>
        internal async Task<int> SweepTrialsAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var trials = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var reclaimed = await trials.ReapAsync(ct);
            if (reclaimed > 0) logger.LogWarning("Reclaimed {Count} trials whose lease ran out", reclaimed);
            return reclaimed;
        }

        internal async Task<int> SweepAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var submissions = scope.ServiceProvider.GetRequiredService<ISubmissionService>();

            var now = clock.GetUtcNow().UtcDateTime;

            var expired = await context.EvaluationJobs
                .Where(j => j.State == EvaluationJobState.Running
                    && j.LeaseExpiresAt != null
                    && j.LeaseExpiresAt <= now)
                .ToListAsync(ct);

            if (expired.Count == 0) return 0;

            foreach (var job in expired)
            {
                // The token goes first. A Runner that wakes up and reports
                // against the old lease is then refused rather than allowed to
                // overwrite whoever has the job now.
                job.LeaseToken = null;
                job.LeaseExpiresAt = null;
                job.LeaseSeconds = null;
                job.RunnerId = null;
                job.ClaimedAt = null;

                if (job.Deliveries >= RunnerService.DeliveryCap)
                {
                    job.State = EvaluationJobState.Failed;
                    job.FinishedAt = now;
                    job.FailureReason =
                        $"Given up after {job.Deliveries} deliveries without a result";

                    logger.LogWarning(
                        "Job {Job} failed after {Deliveries} deliveries", job.Id, job.Deliveries);
                }
                else
                {
                    job.State = EvaluationJobState.Queued;
                    logger.LogInformation(
                        "Job {Job} reclaimed after {Deliveries} deliveries", job.Id, job.Deliveries);
                }
            }

            await context.SaveChangesAsync(ct);
            // These went back to the queue, and a Runner waiting for work is
            // the one thing that can act on it.
            queue.Wake();

            // Announced after the save, so a screen that refetches on the event
            // reads the state this sweep has already committed.
            foreach (var job in expired) await submissions.AnnounceAsync(job.SubmissionId, ct);

            return expired.Count;
        }
    }
}
