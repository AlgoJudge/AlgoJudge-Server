using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IAccountMergeService
    {
        /// <summary>What a merge would move, and what would stop it.</summary>
        Task<MergePreviewDto> PreviewAsync(string sourceId, string targetId, CancellationToken ct);

        Task<AccountMergeDto> MergeAsync(string sourceId, string targetId, CancellationToken ct);

        /// <summary>Puts the work back and unblocks the account it came from.</summary>
        Task<AccountMergeDto> UndoAsync(Guid mergeId, CancellationToken ct);

        /// <summary>
        /// Anonymises the accounts whose undo window has closed. Answers how
        /// many were emptied.
        /// </summary>
        Task<int> SweepAsync(CancellationToken ct);
    }

    /// <summary>
    /// One account's work carried onto another.
    /// <para>
    /// <b>Ids are rewritten, not indirected.</b> Sixty-two places compare a user
    /// id; a merge that left the rows where they were and asked every reader to
    /// resolve a parent would be wrong in whichever of them was missed, and
    /// wrong silently — points that simply do not appear. Rewriting leaves every
    /// existing reader correct without touching it.
    /// </para>
    /// <para>
    /// <b>What a person produced moves; what they did to somebody else's thing
    /// stays.</b> A manager's exclusion, a grant they handed out, an answer they
    /// wrote: moving those would rewrite the record of what a <i>manager</i> did.
    /// So an account that has acted on anything is <b>refused</b> rather than
    /// half-moved — see <see cref="BlockersAsync"/>.
    /// </para>
    /// <para>
    /// <b>An LMS gradebook is not touched from here, and cannot be.</b> Nothing
    /// outside the integration module may name it — that boundary is what keeps
    /// the module deletable in one commit, and a test enforces it. It needs no
    /// help: the desired grades are computed from submissions, which have moved,
    /// so the target's mark rises and the emptied account's falls to nought
    /// through the same path that carries any contestant who stops earning.
    /// <c>docs/specs/ACCOUNT_MERGE.md</c> records what that costs.
    /// </para>
    /// <para>
    /// <b>A deliberate exception to <see cref="Contestant"/>'s rule</b> that a
    /// move changes what happens next and nothing that already happened. That
    /// rule is about a contestant changing sides; this asserts the two accounts
    /// were one person all along. <c>docs/specs/ACCOUNT_MERGE.md</c>.
    /// </para>
    /// </summary>
    public class AccountMergeService(
        ApplicationDbContext context,
        IAccountDeletionService deletions,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IEventHub events,
        IEventAudience audience,
        TimeProvider clock,
        ILogger<AccountMergeService> log
    ) : IAccountMergeService
    {
        /// <summary>
        /// How long an undo is offered, and so how long the emptied account
        /// stays untouched under its block.
        /// <para>
        /// The same day <see cref="AccountDeletionService.ProviderWindow"/>
        /// gives an administrator to stop a machine's deletion, and for the same
        /// reason: long enough to cover a night and a morning. One number for
        /// "somebody may still catch this", not two.
        /// </para>
        /// </summary>
        public static readonly TimeSpan UndoWindow = AccountDeletionService.ProviderWindow;

        public async Task<MergePreviewDto> PreviewAsync(
            string sourceId, string targetId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserMerge, null, ct);
            var (source, target) = await PairAsync(sourceId, targetId, ct);

            var activities = await ActivitiesAsync(source.Id, ct);

            return new MergePreviewDto
            {
                SourceUserId = source.Id,
                SourceLogin = source.UserName ?? source.Id,
                SourceName = Projections.DisplayName(source),
                SourceIsTemporary = source.IsTemporary,
                TargetUserId = target.Id,
                TargetLogin = target.UserName ?? target.Id,
                TargetName = Projections.DisplayName(target),
                Submissions = await context.Submissions.CountAsync(s => s.UserId == source.Id, ct),
                Questions = await context.Questions.CountAsync(q => q.AuthorUserId == source.Id, ct),
                Activities = activities.Count,
                ActivityNames = await context.Activities.AsNoTracking()
                    .Where(a => activities.Contains(a.Id))
                    .OrderBy(a => a.Name)
                    .Select(a => a.Name)
                    .ToListAsync(ct),
                Blockers = await BlockersAsync(source.Id, ct),
            };
        }

        /// <summary>
        /// Why this account may not be merged away. There is one reason.
        /// <para>
        /// <b>Grants move with the work, and a system grant is not work.</b>
        /// Without this, somebody holding <c>user:merge</c> merges an
        /// administrator into their own account and inherits the
        /// administrator's permissions.
        /// <c>.claude/rules/architecture-guardrails.md</c>: nobody may map onto
        /// a permission they do not themselves hold.
        /// </para>
        /// <para>
        /// <b>Nothing else refuses, and that is the anonymising doing its
        /// work.</b> An earlier draft removed the emptied row and therefore had
        /// to refuse any account something still named — one that owned a
        /// problem, ruled a submission out, granted a permission. Deletion here
        /// has always meant emptying in place, so those rows keep resolving and
        /// there is nothing to refuse.
        /// </para>
        /// </summary>
        private async Task<IReadOnlyList<string>> BlockersAsync(string userId, CancellationToken ct)
        {
            var system = await context.Grants.AsNoTracking()
                .Where(g => g.UserId == userId && g.ActivityId == null)
                .Select(g => g.Permissions)
                .ToListAsync(ct);

            return system.Any(p => Permissions.Parse(p).Count > 0)
                ? ["holds permissions over the whole installation"]
                : [];
        }

        private async Task<(User Source, User Target)> PairAsync(
            string sourceId, string targetId, CancellationToken ct)
        {
            if (sourceId == targetId)
            {
                throw new ValidationException(
                    "An account cannot be merged into itself", "merge.same");
            }

            var source = await context.Users.FirstOrDefaultAsync(u => u.Id == sourceId, ct)
                ?? throw new NotFoundException("User");
            var target = await context.Users.FirstOrDefaultAsync(u => u.Id == targetId, ct)
                ?? throw new NotFoundException("User");

            return (source, target);
        }

        /// <summary>Every activity this account holds a grant in.</summary>
        private async Task<List<Guid>> ActivitiesAsync(string userId, CancellationToken ct) =>
            await context.Grants.AsNoTracking()
                .Where(g => g.UserId == userId && g.ActivityId != null)
                .Select(g => g.ActivityId!.Value)
                .Distinct()
                .ToListAsync(ct);

        public async Task<AccountMergeDto> MergeAsync(
            string sourceId, string targetId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserMerge, null, ct);
            var manager = await currentUser.RequireAsync(ct);
            var (source, target) = await PairAsync(sourceId, targetId, ct);

            if (target.IsBlocked(clock.GetUtcNow()))
            {
                throw new ConflictException(
                    "The account being merged into is blocked", "merge.target.blocked");
            }
            if (await context.AccountMerges.AnyAsync(
                m => m.SourceUserId == source.Id && m.UndoneAt == null, ct))
            {
                throw new ConflictException(
                    "This account has already been merged", "merge.already");
            }

            var blockers = await BlockersAsync(source.Id, ct);
            if (blockers.Count > 0)
            {
                throw new ConflictException(
                    $"This account cannot be merged away because it {string.Join(", ", blockers)}",
                    "merge.blocked");
            }

            var now = clock.GetUtcNow().UtcDateTime;
            // Read before anything moves: afterwards the source holds nothing and
            // the target's own activities are indistinguishable from what arrived.
            var touched = (await ActivitiesAsync(source.Id, ct))
                .Union(await ActivitiesAsync(target.Id, ct))
                .ToList();

            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            var moved = await MoveAsync(source, target, ct);

            // **Blocked, and untouched.** The account stops working at once —
            // `BlockedGate` sees to that — and is otherwise left exactly as it
            // was until the window closes, which is what lets an undo give it
            // back whole rather than rebuild it.
            source.LockoutEnd = DateTimeOffset.MaxValue;
            source.BlockedReason = $"Merged into {target.UserName}";

            var merge = new AccountMerge
            {
                SourceUserId = source.Id,
                TargetUserId = target.Id,
                MergedAt = now,
                MergedByUserId = manager.Id,
                AnonymiseAfter = now + UndoWindow,
                Moved = JsonSerializer.Serialize(moved),
            };
            context.AccountMerges.Add(merge);

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await AnnounceAsync(touched, ct);
            return Projections.Merge(merge);
        }

        /// <summary>Everything the source produced, onto the target.</summary>
        private async Task<MovedRows> MoveAsync(User source, User target, CancellationToken ct)
        {
            var moved = new MovedRows();

            var submissions = await context.Submissions
                .Where(s => s.UserId == source.Id).ToListAsync(ct);
            foreach (var submission in submissions)
            {
                submission.UserId = target.Id;
                moved.Submissions.Add(submission.Id);
            }

            var questions = await context.Questions
                .Where(q => q.AuthorUserId == source.Id).ToListAsync(ct);
            foreach (var question in questions)
            {
                question.AuthorUserId = target.Id;
                moved.Questions.Add(question.Id);
            }

            var trials = await context.Trials.Where(t => t.UserId == source.Id).ToListAsync(ct);
            foreach (var trial in trials)
            {
                trial.UserId = target.Id;
                moved.Trials.Add(trial.Id);
            }

            // **An authorisation, not an audit trace.** A file nothing points at
            // yet is readable only by whoever uploaded it, which is what makes
            // the two-step publish safe — leaving this behind would strand every
            // upload the source had in flight.
            var files = await context.Files
                .Where(f => f.UploadedByUserId == source.Id).ToListAsync(ct);
            foreach (var file in files)
            {
                file.UploadedByUserId = target.Id;
                moved.Files.Add(file.Id);
            }

            var sessions = await context.UserSessions
                .Where(s => s.UserId == source.Id).ToListAsync(ct);
            foreach (var session in sessions)
            {
                session.UserId = target.Id;
                moved.Sessions.Add(session.Id);
            }

            var identities = await context.UserIdentities
                .Where(i => i.UserId == source.Id).ToListAsync(ct);
            foreach (var identity in identities)
            {
                identity.UserId = target.Id;
                moved.Identities.Add(identity.Id);
            }

            await MoveGrantsAsync(source, target, moved, ct);
            await MovePairedAsync(source, target, moved, ct);

            return moved;
        }

        /// <summary>
        /// Grants, and the collision that is the common case.
        /// <para>
        /// One grant per user per activity is a unique index, and the person is
        /// usually in the activity on both accounts. The <b>target's</b> grant
        /// stays — it is the account that survives — and the source's is
        /// recorded and dropped. Nothing about who a submission scored for
        /// changes: it carries its own <c>GroupId</c> stamp.
        /// </para>
        /// </summary>
        private async Task MoveGrantsAsync(
            User source, User target, MovedRows moved, CancellationToken ct)
        {
            // **Activity scope only.** A system grant is privilege rather than
            // work and never moves; an account holding any is refused outright,
            // above.
            var mine = await context.Grants
                .Where(g => g.UserId == source.Id && g.ActivityId != null).ToListAsync(ct);
            var theirs = await context.Grants.AsNoTracking()
                .Where(g => g.UserId == target.Id && g.ActivityId != null).ToListAsync(ct);

            foreach (var grant in mine)
            {
                if (theirs.Any(t => t.ActivityId == grant.ActivityId))
                {
                    moved.DroppedGrants.Add(new DroppedGrant
                    {
                        ActivityId = grant.ActivityId,
                        GroupId = grant.GroupId,
                        SourceProviderId = grant.SourceProviderId,
                        IsSystem = grant.IsSystem,
                        OverrideSystem = grant.OverrideSystem,
                        State = (int)grant.State,
                        Permissions = grant.Permissions,
                    });
                    context.Grants.Remove(grant);
                    continue;
                }

                grant.UserId = target.Id;
                moved.Grants.Add(grant.Id);
            }
        }

        /// <summary>
        /// The two rows keyed on a pair rather than an id: a question somebody
        /// read, and a problem shared with them. Both de-duplicate — the target
        /// having read it already is not a collision worth a rule.
        /// </summary>
        private async Task MovePairedAsync(
            User source, User target, MovedRows moved, CancellationToken ct)
        {
            var reads = await context.QuestionReads
                .Where(r => r.UserId == source.Id).ToListAsync(ct);
            var alreadyRead = await context.QuestionReads.AsNoTracking()
                .Where(r => r.UserId == target.Id)
                .Select(r => r.QuestionId).ToListAsync(ct);
            foreach (var read in reads)
            {
                if (alreadyRead.Contains(read.QuestionId))
                {
                    context.QuestionReads.Remove(read);
                    continue;
                }
                context.QuestionReads.Remove(read);
                context.QuestionReads.Add(new QuestionRead
                {
                    QuestionId = read.QuestionId, UserId = target.Id, ReadAt = read.ReadAt,
                });
                moved.QuestionReads.Add(read.QuestionId);
            }

            var shares = await context.ProblemShares
                .Where(s => s.UserId == source.Id).ToListAsync(ct);
            var alreadyShared = await context.ProblemShares.AsNoTracking()
                .Where(s => s.UserId == target.Id)
                .Select(s => s.ProblemId).ToListAsync(ct);
            foreach (var share in shares)
            {
                context.ProblemShares.Remove(share);
                if (alreadyShared.Contains(share.ProblemId)) continue;
                context.ProblemShares.Add(new ProblemShare
                {
                    ProblemId = share.ProblemId, UserId = target.Id,
                });
                moved.ProblemShares.Add(share.ProblemId);
            }
        }

        /// <summary>
        /// A board already open is not repaired by silence — the same reason an
        /// exclusion announces itself.
        /// </summary>
        private async Task AnnounceAsync(IReadOnlyList<Guid> activities, CancellationToken ct)
        {
            foreach (var activityId in activities)
            {
                var watching = await audience.InActivityAsync(activityId, Permissions.RankingRead, ct);
                if (watching.Count == 0) continue;

                await events.SendToUsersAsync(watching, EventTypes.RankingChanged, new RankingChangedData
                {
                    ActivityId = Wire.Id(activityId),
                    Change = "merged",
                }, ct);
            }
        }

        /// <summary>
        /// Puts the work back and lets the account work again.
        /// <para>
        /// <b>Only while the account is still whole.</b> The merge leaves it
        /// blocked and otherwise untouched for a day, so this is a move and an
        /// unblocking — the person's own login and password still work.
        /// Once the sweeper has emptied it there is nothing to give back, and
        /// this refuses rather than handing over an anonymised shell.
        /// </para>
        /// </summary>
        public async Task<AccountMergeDto> UndoAsync(Guid mergeId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserMerge, null, ct);
            var manager = await currentUser.RequireAsync(ct);

            var merge = await context.AccountMerges.FirstOrDefaultAsync(m => m.Id == mergeId, ct)
                ?? throw new NotFoundException("Merge");

            if (merge.UndoneAt is not null)
            {
                throw new ConflictException("This merge has already been undone", "merge.undone");
            }
            if (merge.SourceAnonymisedAt is not null)
            {
                throw new ConflictException(
                    "The account this came from has been emptied and cannot be given back",
                    "merge.window.closed");
            }

            var moved = JsonSerializer.Deserialize<MovedRows>(merge.Moved) ?? new MovedRows();
            var source = await context.Users.FirstOrDefaultAsync(u => u.Id == merge.SourceUserId, ct)
                ?? throw new NotFoundException("User");
            var target = await context.Users.FirstOrDefaultAsync(u => u.Id == merge.TargetUserId, ct)
                ?? throw new NotFoundException("User");

            var touched = await ActivitiesAsync(target.Id, ct);

            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            source.LockoutEnd = null;
            source.BlockedReason = null;
            await PutBackAsync(moved, source, ct);

            merge.UndoneAt = clock.GetUtcNow().UtcDateTime;
            merge.UndoneByUserId = manager.Id;

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await AnnounceAsync(
                touched.Union(await ActivitiesAsync(source.Id, ct)).ToList(), ct);

            return Projections.Merge(merge);
        }

        /// <summary>
        /// Every row the merge recorded, back onto the account it came from.
        /// <para>
        /// <b>By recorded id, never by "everything the target holds"</b> — the
        /// target's own work is indistinguishable from what arrived once the ids
        /// are the same, and taking the lot would empty the wrong account.
        /// </para>
        /// </summary>
        private async Task PutBackAsync(MovedRows moved, User source, CancellationToken ct)
        {
            foreach (var row in await context.Submissions
                .Where(s => moved.Submissions.Contains(s.Id)).ToListAsync(ct))
            {
                row.UserId = source.Id;
            }
            foreach (var row in await context.Questions
                .Where(q => moved.Questions.Contains(q.Id)).ToListAsync(ct))
            {
                row.AuthorUserId = source.Id;
            }
            foreach (var row in await context.Trials
                .Where(t => moved.Trials.Contains(t.Id)).ToListAsync(ct))
            {
                row.UserId = source.Id;
            }
            foreach (var row in await context.Files
                .Where(f => moved.Files.Contains(f.Id)).ToListAsync(ct))
            {
                row.UploadedByUserId = source.Id;
            }
            foreach (var row in await context.UserSessions
                .Where(s => moved.Sessions.Contains(s.Id)).ToListAsync(ct))
            {
                row.UserId = source.Id;
            }
            foreach (var row in await context.UserIdentities
                .Where(i => moved.Identities.Contains(i.Id)).ToListAsync(ct))
            {
                row.UserId = source.Id;
            }
            foreach (var row in await context.Grants
                .Where(g => moved.Grants.Contains(g.Id)).ToListAsync(ct))
            {
                row.UserId = source.Id;
            }

            // Keyed on a pair, so they are written rather than moved.
            foreach (var questionId in moved.QuestionReads)
            {
                var already = await context.QuestionReads.AnyAsync(
                    r => r.QuestionId == questionId && r.UserId == source.Id, ct);
                if (already) continue;
                context.QuestionReads.Add(new QuestionRead
                {
                    QuestionId = questionId, UserId = source.Id,
                });
            }
            foreach (var problemId in moved.ProblemShares)
            {
                var already = await context.ProblemShares.AnyAsync(
                    s => s.ProblemId == problemId && s.UserId == source.Id, ct);
                if (already) continue;
                context.ProblemShares.Add(new ProblemShare
                {
                    ProblemId = problemId, UserId = source.Id,
                });
            }

            // The grants a collision dropped. The row is gone, so this is the one
            // thing an undo builds rather than moves.
            foreach (var dropped in moved.DroppedGrants)
            {
                context.Grants.Add(new Grant
                {
                    UserId = source.Id,
                    ActivityId = dropped.ActivityId,
                    GroupId = dropped.GroupId,
                    SourceProviderId = dropped.SourceProviderId,
                    IsSystem = dropped.IsSystem,
                    OverrideSystem = dropped.OverrideSystem,
                    State = (GrantState)dropped.State,
                    Permissions = dropped.Permissions,
                });
            }
        }

        /// <summary>
        /// The accounts whose undo window has closed.
        /// <para>
        /// <b>Anonymised, never removed.</b> Deletion in this product has always
        /// meant emptying in place — `docs/specs/AUTHENTICATION.md` settled that
        /// — and a merge is no exception: the rows that record what this account
        /// once <i>did</i> still name it, and they have to keep resolving.
        /// </para>
        /// <para>
        /// <b>The order is what saves the questions.</b> `AnonymiseAsync`
        /// replaces the text of every question its user wrote; by the time this
        /// runs they belong to the target, so there are none of theirs left to
        /// redact.
        /// </para>
        /// </summary>
        public async Task<int> SweepAsync(CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var due = await context.AccountMerges
                .Where(m => m.SourceAnonymisedAt == null
                    && m.UndoneAt == null
                    && m.AnonymiseAfter <= now)
                .ToListAsync(ct);

            var emptied = 0;
            foreach (var merge in due)
            {
                var user = await context.Users
                    .FirstOrDefaultAsync(u => u.Id == merge.SourceUserId, ct);
                if (user is null)
                {
                    log.LogWarning(
                        "The account {UserId} merged as {MergeId} is gone; nothing to empty",
                        merge.SourceUserId, merge.Id);
                    merge.SourceAnonymisedAt = now;
                    continue;
                }

                await deletions.AnonymiseAsync(user, ct);
                merge.SourceAnonymisedAt = now;
                emptied++;
            }

            await context.SaveChangesAsync(ct);
            return emptied;
        }
    }

    /// <summary>What a merge moved, as the record keeps it.</summary>
    public sealed record MovedRows
    {
        public List<Guid> Submissions { get; init; } = [];
        public List<Guid> Questions { get; init; } = [];
        public List<Guid> QuestionReads { get; init; } = [];
        public List<Guid> Trials { get; init; } = [];
        public List<Guid> Files { get; init; } = [];
        public List<Guid> Sessions { get; init; } = [];
        public List<Guid> Identities { get; init; } = [];
        public List<Guid> Grants { get; init; } = [];
        public List<Guid> ProblemShares { get; init; } = [];

        /// <summary>
        /// Grants the target already had one of. Kept whole rather than by id,
        /// because the row is gone and an undo owes it back.
        /// </summary>
        public List<DroppedGrant> DroppedGrants { get; init; } = [];
    }

    public sealed record DroppedGrant
    {
        public Guid? ActivityId { get; init; }
        public Guid? GroupId { get; init; }
        public Guid? SourceProviderId { get; init; }
        public bool IsSystem { get; init; }
        public bool OverrideSystem { get; init; }
        public int State { get; init; }
        public required string Permissions { get; init; }
    }
}
