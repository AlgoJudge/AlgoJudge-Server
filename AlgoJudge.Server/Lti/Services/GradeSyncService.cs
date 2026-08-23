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
        /// <summary>
        /// What a gradebook column is worth where the assignment states no point
        /// value of its own.
        /// <para>
        /// A percentage column, and deliberately: LTI requires a
        /// <c>scoreMaximum</c> on the line item, it is fixed when the column is
        /// created, and a platform cannot be told "whatever the package said".
        /// </para>
        /// </summary>
        public const double PercentageColumn = 100d;

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
            // **A gradebook column is a fixed scale, by definition**, and this
            // is the one place in the product that genuinely needs a number
            // where the assignment states none: LTI's `scoreMaximum` is
            // required, and it is set once when the line item is created rather
            // than per submission.
            //
            // So a hundred here, and a percentage column is what that means.
            // Everywhere else `?? 100` was a lie about the problem's own
            // scoring; here it is a decision about a column somebody else owns.
            var maxPoints = assignment.MaxPoints ?? PercentageColumn;

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

            // Who competes as whom, so the rest of this can work on contestants.
            // **Read at sync time**, which is the only thing the data supports: a
            // submission remembers the group it was sent as, a gradebook row does
            // not — so moving somebody moves the grade they will next be given.
            // That follows from moves being allowed, and it is worth knowing
            // before somebody discovers it in a mark.
            var groups = await core.Grants.AsNoTracking()
                .Where(g => g.ActivityId == activity.Id && g.GroupId != null)
                .Select(g => new { g.UserId, GroupId = g.GroupId!.Value })
                .ToListAsync(ct);

            var membersOf = groups
                .GroupBy(g => g.GroupId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.UserId).ToList());

            // The best attempt per contestant, which is the one shipped
            // aggregation rule (§6.2). Computed here rather than asked of a
            // ranking renderer: a gradebook column cannot ask one.
            //
            // **Every member's work is read, not only a linked member's.** The
            // submission that earns the grade may have been sent by somebody the
            // platform never linked; the grade still belongs to the group.
            var sent = await core.Submissions.AsNoTracking()
                .Where(s => s.SeriesProblemId == assignment.Id)
                .Join(core.EvaluationJobs.AsNoTracking(), s => s.Id, j => j.SubmissionId,
                    (s, j) => new { s.UserId, s.GroupId, j.Id })
                .Join(core.Results.AsNoTracking(), j => j.Id, r => r.EvaluationJobId,
                    (j, r) => new { j.UserId, j.GroupId, r.Score, r.MaxScore, r.Id })
                .Where(x => x.Score != null)
                .ToListAsync(ct);

            // A submission counts for the group it was **sent as**; one sent by
            // somebody competing alone counts for them, and only if the platform
            // knows who they are.
            //
            // The two are told apart by a field rather than by the shape of an
            // id: `User.Id` is a UUID in a string column, so "does it look like a
            // Guid" would say yes to both.
            var best = sent
                .Select(x => new
                {
                    Key = x.GroupId is { } group ? group.ToString() : x.UserId,
                    x.GroupId,
                    x.UserId,
                    x.Score,
                    x.MaxScore,
                    x.Id,
                })
                .Where(x => x.GroupId is not null || linked.ContainsKey(x.UserId))
                .ToList();

            // **Best by fraction, not by raw score.** Ordering on `Score` alone
            // compares numbers marked out of different maxima — a package
            // republished with more tests, or a type marking out of one — so
            // 70 out of 100 beat 1 out of 1, and the gradebook was sent the
            // worse of somebody's two attempts. Every other reader in the
            // product had already been fixed to compare fractions; this one
            // was missed.
            var perContestant = best
                .GroupBy(x => x.Key)
                .Select(g => g
                    .OrderByDescending(x => Scoring.Fraction(x.Score, x.MaxScore) ?? -1)
                    .First())
                .ToList();

            // **Fanned out to every linked member**, so a group of three leaves
            // three gradebook rows carrying one score. A member the platform
            // never linked is skipped rather than failing the sweep — they have
            // no row to write to.
            var desired = perContestant
                .SelectMany(entry => entry.GroupId is { } group
                    ? membersOf.GetValueOrDefault(group, [])
                        .Where(linked.ContainsKey)
                        .Select(member => new { UserId = member, entry.Score, entry.MaxScore, entry.Id })
                    : [new { entry.UserId, entry.Score, entry.MaxScore, entry.Id }])
                .ToList();

            var state = ScoreState(activity, assignment);
            var now = clock.GetUtcNow().UtcDateTime;
            var touched = 0;

            foreach (var entry in desired)
            {
                var score = Scoring.Rescale(Scoring.Fraction(entry.Score, entry.MaxScore), maxPoints)
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

                // **The policy state and the sync state are different things**,
                // and conflating them cost an hour: comparing them directly moved
                // every synchronised row back to pending on every sweep, so every
                // grade in the installation was reposted every minute for ever.
                // Caught by a test that expected a sweep to leave a teacher's
                // edit alone.
                var scoreChanged = Math.Abs(existing.DesiredScore - score) > 0.0001;

                if (state != GradeSyncStatus.Pending)
                {
                    // Withheld or deferred. The desired score is still tracked, so
                    // that when a freeze lifts the right number is already known.
                    if (scoreChanged)
                    {
                        existing.DesiredScore = score;
                        existing.SourceResultId = entry.Id;
                    }
                    if (existing.State != state || scoreChanged)
                    {
                        existing.State = state;
                        existing.UpdatedAt = now;
                        touched++;
                    }
                    continue;
                }

                // Postable. Something has to have changed for it to be sent
                // again: a different number, or a freeze that has lifted.
                var released = existing.State is GradeSyncStatus.Deferred or GradeSyncStatus.Withheld;
                if (!scoreChanged && !released)
                {
                    continue;
                }

                existing.DesiredScore = score;
                existing.SourceResultId = entry.Id;
                existing.UpdatedAt = now;
                existing.State = GradeSyncStatus.Pending;
                // The backoff resets because what failed before was a different
                // intent, and a new one deserves its own attempts.
                existing.Attempts = 0;
                existing.NextAttemptAt = null;
                existing.LastError = null;
                touched++;
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
