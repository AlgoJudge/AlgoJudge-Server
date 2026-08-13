using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    public interface IGradeSyncService
    {
        /// <summary>
        /// Brings the intended grades for one placement up to date with what
        /// AlgoJudge holds. Writes nothing to the platform.
        /// </summary>
        Task<int> RefreshAsync(ResourceLink link, CancellationToken ct);
    }

    /// <summary>
    /// What the gradebook <b>should</b> say, worked out from what this Server
    /// already knows.
    /// <para>
    /// <b>Swept rather than hooked, and that is the boundary rather than a
    /// preference.</b> §8 forbids an LTI branch in the results path, and the
    /// event hub only speaks to sockets — so there is nothing to subscribe to
    /// without the core learning that this module exists. §6.4 wanted a
    /// reconciled state anyway: the truth is a row and recovery is a sweep over
    /// rows, not a retry somebody remembered to write.
    /// </para>
    /// <para>
    /// The cost is latency measured in the sweep interval, which a gradebook does
    /// not notice.
    /// </para>
    /// </summary>
    public class GradeSyncService(
        LtiDbContext db, ApplicationDbContext core, TimeProvider clock) : IGradeSyncService
    {
        public async Task<int> RefreshAsync(ResourceLink link, CancellationToken ct)
        {
            var activity = await core.Activities.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == link.ActivityId, ct);
            if (activity is null)
            {
                return 0;
            }

            // Everybody this platform knows here. A person with no link has no
            // `userId` the platform would recognise, so there is nothing to post
            // for them — §6.4's sixth case, and a state rather than a failure.
            var linked = await db.ExternalIdentities.AsNoTracking()
                .Where(i => i.PlatformId == link.PlatformId)
                .ToDictionaryAsync(i => i.UserId, i => i.Subject, ct);

            if (linked.Count == 0)
            {
                return 0;
            }

            var assignments = await core.SeriesProblems.AsNoTracking()
                .Include(sp => sp.Series)
                .Where(sp => sp.ActivityId == link.ActivityId
                    && (link.SeriesId == null || sp.SeriesId == link.SeriesId))
                .ToListAsync(ct);

            var touched = 0;
            foreach (var assignment in assignments)
            {
                touched += await RefreshAssignmentAsync(link, activity, assignment, linked, ct);
            }

            await db.SaveChangesAsync(ct);
            return touched;
        }

        private async Task<int> RefreshAssignmentAsync(
            ResourceLink link,
            Activity activity,
            SeriesProblem assignment,
            IReadOnlyDictionary<string, string> linked,
            CancellationToken ct)
        {
            var maxPoints = Scoring.MaxPoints(assignment);

            var item = await db.LineItems.FirstOrDefaultAsync(
                i => i.ResourceLinkId == link.Id && i.SeriesProblemId == assignment.Id, ct);

            if (item is null)
            {
                item = new LineItem
                {
                    ResourceLinkId = link.Id,
                    SeriesProblemId = assignment.Id,
                    // Empty until the worker creates it at the platform. Kept as
                    // a row from the start so the intended grades can be computed
                    // before the platform has ever been reached — which is what
                    // makes an unreachable platform a delay rather than a loss.
                    PlatformUrl = "",
                    ScoreMaximum = maxPoints,
                };
                db.LineItems.Add(item);
                await db.SaveChangesAsync(ct);
            }
            else if (Math.Abs(item.ScoreMaximum - maxPoints) > 0.0001)
            {
                // **§6.4, case four.** The assignment is worth something else now,
                // so every score already posted is on the wrong scale. Recording
                // the new maximum is what makes the states below stale, and the
                // worker then reposts all of them.
                item.ScoreMaximum = maxPoints;
            }

            // The best attempt per person, which is the one shipped aggregation
            // rule (§6.2). Computed here rather than asked of a ranking renderer:
            // a gradebook column cannot ask one.
            var best = await core.Submissions.AsNoTracking()
                .Where(s => s.SeriesProblemId == assignment.Id && linked.Keys.Contains(s.UserId))
                .Join(core.EvaluationJobs.AsNoTracking(), s => s.Id, j => j.SubmissionId,
                    (s, j) => new { s.UserId, j.Id })
                .Join(core.Results.AsNoTracking(), j => j.Id, r => r.EvaluationJobId,
                    (j, r) => new { j.UserId, r.Score, r.MaxScore, r.Id })
                .Where(x => x.Score != null)
                .ToListAsync(ct);

            var desired = best
                .GroupBy(x => x.UserId)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .ToList();

            var state = ScoreState(activity, assignment);
            var now = clock.GetUtcNow().UtcDateTime;
            var touched = 0;

            foreach (var entry in desired)
            {
                var score = Scoring.Rescale(entry.Score, maxPoints, entry.MaxScore ?? Scoring.RunnerScale)
                    ?? 0;

                var existing = await db.GradeSyncStates.FirstOrDefaultAsync(
                    s => s.LineItemId == item.Id && s.UserId == entry.UserId, ct);

                if (existing is null)
                {
                    db.GradeSyncStates.Add(new GradeSyncState
                    {
                        ResourceLinkId = link.Id,
                        LineItemId = item.Id,
                        UserId = entry.UserId,
                        SourceResultId = entry.Id,
                        DesiredScore = score,
                        State = state,
                        UpdatedAt = now,
                    });
                    touched++;
                    continue;
                }

                var scaleMoved = Math.Abs(item.ScoreMaximum - maxPoints) > 0.0001;
                var wanted = Math.Abs(existing.DesiredScore - score) > 0.0001;

                if (wanted || existing.State != state || scaleMoved)
                {
                    existing.DesiredScore = score;
                    existing.SourceResultId = entry.Id;
                    existing.UpdatedAt = now;

                    // Withheld and deferred are not failures and do not consume
                    // attempts. Moving back to pending resets the backoff,
                    // because what failed before was a different intent.
                    existing.State = state;
                    if (state == GradeSyncStatus.Pending)
                    {
                        existing.Attempts = 0;
                        existing.NextAttemptAt = null;
                        existing.LastError = null;
                    }
                    touched++;
                }
            }

            return touched;
        }

        /// <summary>
        /// When a score may reach the gradebook (§6.3).
        /// <para>
        /// One condition, not two, and stating it that way is what keeps it
        /// correct as visibility rules grow: <b>a gradebook column is a
        /// participant-visible surface, so a score reaches it exactly when the
        /// submitting participant may see it.</b>
        /// </para>
        /// </summary>
        private GradeSyncStatus ScoreState(Activity activity, SeriesProblem assignment)
        {
            if (activity.ScoreVisibility == ScoreVisibility.ManagersOnly)
            {
                // Never. Posting would publish through Moodle exactly what the
                // activity withholds in AlgoJudge.
                return GradeSyncStatus.Withheld;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var series = assignment.Series;

            if (series is not null
                && series.RankingFreezeAt is { } freeze && now >= freeze
                && (series.RankingRevealAt is not { } reveal || now < reveal))
            {
                // **The teacher does not see it either, and that is the cost**
                // (§6.3). A gradebook has no equivalent of reading an unfrozen
                // ranking, so the withheld state cannot be shown to one reader
                // and not another — during a freeze AlgoJudge is the source of
                // truth and Moodle is deliberately behind it.
                return GradeSyncStatus.Deferred;
            }

            return GradeSyncStatus.Pending;
        }
    }
}
