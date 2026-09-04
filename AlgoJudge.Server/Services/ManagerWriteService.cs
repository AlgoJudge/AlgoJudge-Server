using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// The manager's writes to an activity, its rounds and its assignments.
    /// <para>
    /// Held together rather than split by entity, because almost every one of
    /// them is a rule about <b>when a change is refused</b> — an archived
    /// activity, a round that has submissions, an assignment somebody has
    /// already answered — and those rules read better beside one another than
    /// scattered across three services.
    /// </para>
    /// </summary>
    public interface IManagerWriteService
    {
        Task<ManagedActivityDto> UpdateActivityAsync(string idOrSlug, ActivityInputDto input, CancellationToken ct);
        Task<ManagedActivityDto> SetActivityArchivedAsync(string idOrSlug, bool archived, CancellationToken ct);
        Task DeleteActivityAsync(string idOrSlug, CancellationToken ct);
        Task<IReadOnlyList<ManagedSeriesDto>> ReorderSeriesAsync(string idOrSlug, IReadOnlyList<string> ordered, CancellationToken ct);

        Task<ManagedSeriesDto> UpdateSeriesAsync(Guid seriesId, SeriesInputDto input, CancellationToken ct);
        Task DeleteSeriesAsync(Guid seriesId, CancellationToken ct);
        Task<ManagedSeriesDto> ShiftSeriesAsync(Guid seriesId, int minutes, CancellationToken ct);
        Task<ManagedSeriesDto> PauseSeriesAsync(Guid seriesId, bool hideProblems, CancellationToken ct);
        Task<ManagedSeriesDto> ResumeSeriesAsync(Guid seriesId, bool extendEnd, CancellationToken ct);
        Task<ManagedSeriesDto> ReorderProblemsAsync(Guid seriesId, IReadOnlyList<string> ordered, CancellationToken ct);
        Task<ManagedSeriesDto> UpdateAssignmentAsync(Guid assignmentId, SeriesProblemInputDto input, CancellationToken ct);
        Task<ManagedSeriesDto> DetachAsync(Guid assignmentId, CancellationToken ct);
    }

    public class ManagerWriteService(
        ApplicationDbContext context,
        IPermissionService permissions,
        IActivityService activities,
        ISeriesService series,
        IEventHub events,
        IEventAudience audience,
        IQueueSignal queue,
        TimeProvider clock
    ) : IManagerWriteService
    {
        /// <summary>
        /// Tells whoever runs this activity that it changed, and its members
        /// that what they see of it did.
        /// <para>
        /// Both, because they are different facts to different readers: a
        /// manager's row carries the ceilings and the join password, and a
        /// participant's carries their own standing. Sending one and calling it
        /// done leaves one of the two screens stale, which is how this surface
        /// behaved until 2026-08-08 — every write here was silent, and the
        /// screens listening for these had never received one.
        /// </para>
        /// <para>
        /// Announced <b>after</b> the save, so a screen that refetches on the
        /// event reads what has already been committed.
        /// </para>
        /// </summary>
        private async Task AnnounceActivityAsync(Guid activityId, CancellationToken ct)
        {
            var staff = await audience.InActivityAsync(activityId, Permissions.ActivityUpdate, ct);
            if (staff.Count == 0) return;

            await events.SendToUsersAsync(staff, EventTypes.ActivityChanged, new
            {
                activity = await activities.GetManagedAsync(Wire.Id(activityId), ct),
            }, ct);
        }

        /// <summary>
        /// The dates moving, to the activity's members.
        /// <para>
        /// Its own event because it is the one change to an activity that can be
        /// described **without knowing who is reading**: a countdown reads the
        /// two instants and nothing else. `activityUpdated` carries a whole
        /// `Activity`, which is a per-reader projection — it holds that reader's
        /// membership and their own final score — so it cannot be computed once
        /// and sent to everybody, and this does not pretend otherwise.
        /// </para>
        /// </summary>
        private async Task AnnounceTimesAsync(Activity activity, CancellationToken ct)
        {
            var members = await audience.InActivityAsync(activity.Id, Permissions.ActivityRead, ct);
            if (members.Count == 0) return;

            await events.SendToUsersAsync(members, EventTypes.ActivityTimesChanged, new
            {
                activityId = Wire.Id(activity.Id),
                startDate = Wire.At(activity.StartDate),
                endDate = Wire.At(activity.EndDate),
            }, ct);
        }

        /// <summary>
        /// The same for a round. The manager's event carries the whole series,
        /// assignments included, because they are edited together; a deletion
        /// carries the id instead, because there is nothing left to send.
        /// </summary>
        private async Task AnnounceSeriesAsync(Guid activityId, Series? round, CancellationToken ct)
        {
            var staff = await audience.InActivityAsync(activityId, Permissions.ActivityUpdate, ct);
            if (staff.Count == 0) return;

            object payload = round is null
                ? new { activityId = Wire.Id(activityId) }
                : new { activityId = Wire.Id(activityId), series = await OneAsync(round, ct) };

            await events.SendToUsersAsync(staff, EventTypes.ManagerSeriesChanged, payload, ct);
        }

        /// <summary>
        /// A round that is gone, named by the id it had. Its own overload because
        /// there is no `Series` left to project.
        /// </summary>
        private async Task AnnounceSeriesDeletedAsync(
            Guid activityId, Guid seriesId, CancellationToken ct)
        {
            var staff = await audience.InActivityAsync(activityId, Permissions.ActivityUpdate, ct);
            if (staff.Count == 0) return;

            await events.SendToUsersAsync(staff, EventTypes.ManagerSeriesChanged, new
            {
                activityId = Wire.Id(activityId),
                deletedId = Wire.Id(seriesId),
            }, ct);
        }
        public async Task<ManagedActivityDto> UpdateActivityAsync(
            string idOrSlug, ActivityInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);
            AssertOpen(activity);

            // The slug is immutable once set. It is in every link somebody has
            // shared, and renaming it would break them silently.
            if (input.Slug is { } slug && slug.Trim() != activity.Slug)
            {
                throw new ConflictException("An activity's slug cannot be changed", "activity.slug.immutable");
            }

            activity.Name = input.Name?.Trim() is { Length: > 0 } name ? name : activity.Name;
            activity.Type = input.Type ?? activity.Type;
            activity.RankingType = input.RankingType ?? activity.RankingType;
            activity.TimeZone = input.TimeZone ?? activity.TimeZone;
            activity.StartDate = ActivityService.ParseInstant(input.StartDate);
            activity.EndDate = ActivityService.ParseInstant(input.EndDate);
            if (input.Modules is { } modules) activity.HasQuestions = modules.Questions;
            if (input.ScoreVisibility is { } visibility) activity.ScoreVisibility = ParseScoreVisibility(visibility);
            if (input.HideEndedSeriesProblems is { } hide) activity.HideEndedSeriesProblems = hide;
            if (input.ShowGroupMembers is { } roster) activity.ShowGroupMembers = roster;
            if (input.Props is not null) activity.Props = Opaque.Store(input.Props, "props");
            SubmissionLimits.Check(
                input.MaxUploadBytes, input.MaxAttachments, input.MaxSubmissionsPerProblem, "activity");
            if (input.MaxUploadBytes is { } upload) activity.MaxUploadBytes = upload;
            if (input.MaxAttachments is { } attachments) activity.MaxAttachments = attachments;
            activity.MaxSubmissionsPerProblem = input.MaxSubmissionsPerProblem;
            if (input.RunnerTags is { } runnerTags)
            {
                activity.RunnerTags = RunnerTags.Validated(runnerTags, "The activity's Runner tags");
            }

            if (input.JoinPolicy is { } policy)
            {
                activity.JoinPolicy = ParseJoinPolicy(policy);
                // Kept only under `password`, so switching to open and back does
                // not quietly restore a code somebody had already shared.
                activity.JoinPassword = activity.JoinPolicy == JoinPolicy.Password ? input.JoinPassword : null;
            }
            else if (activity.JoinPolicy == JoinPolicy.Password && input.JoinPassword is not null)
            {
                activity.JoinPassword = input.JoinPassword;
            }

            // Under `closed` this is what the policy already means, so it is
            // forced rather than left to disagree with it.
            activity.Unlisted = activity.JoinPolicy == JoinPolicy.Closed || (input.Unlisted ?? activity.Unlisted);

            if (input.AttachmentVisibility is { } rules)
            {
                var existing = await context.AttachmentRules
                    .Where(r => r.ActivityId == activity.Id)
                    .ToListAsync(ct);
                context.AttachmentRules.RemoveRange(existing);

                foreach (var rule in rules)
                {
                    context.AttachmentRules.Add(new AttachmentRule
                    {
                        ActivityId = activity.Id,
                        Name = rule.Name,
                        Visibility = rule.Visibility == "participant"
                            ? AttachmentVisibility.Participant
                            : AttachmentVisibility.ManagersOnly,
                    });
                }
            }

            await context.SaveChangesAsync(ct);
            // **The other half of the tag comparison.** A claim reads the
            // activity's pool at claim time rather than stamping it on the job,
            // so moving an activity to a pool redirects work that is already
            // queued — to Runners that are holding claims open and will not look
            // again unless told. Sent whenever the field was written, because
            // "the same tags again" is not worth a comparison for one nudge.
            if (input.RunnerTags is not null) queue.Wake();
            // The dates moving is its own event: a participant's countdown reads
            // them, and a round list has to be rebuilt when they shift.
            await AnnounceActivityAsync(activity.Id, ct);
            await AnnounceTimesAsync(activity, ct);
            return await activities.GetManagedAsync(idOrSlug, ct);
        }

        public async Task<ManagedActivityDto> SetActivityArchivedAsync(
            string idOrSlug, bool archived, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityArchive, activity.Id, ct);

            // The ordinary way an activity ends: still readable, accepting
            // nothing new.
            activity.ArchivedAt = archived ? clock.GetUtcNow().UtcDateTime : null;
            await context.SaveChangesAsync(ct);
            await AnnounceActivityAsync(activity.Id, ct);
            return await activities.GetManagedAsync(idOrSlug, ct);
        }

        /// <summary>
        /// Deleting is refused while the activity holds anything somebody sent.
        /// Archiving is what ending one looks like; deleting destroys
        /// submissions people may still want to look back at.
        /// </summary>
        public async Task DeleteActivityAsync(string idOrSlug, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityDelete, activity.Id, ct);

            var submissions = await context.Submissions
                .CountAsync(s => s.SeriesProblem!.ActivityId == activity.Id, ct);
            if (submissions > 0)
            {
                throw new ConflictException(
                    "This activity holds submissions. Archive it instead of deleting it.",
                    "activity.hasSubmissions");
            }

            // Read before the row goes: afterwards there are no grants left to
            // resolve an audience from.
            var staff = await audience.InActivityAsync(activity.Id, Permissions.ActivityUpdate, ct);
            var members = await audience.InActivityAsync(activity.Id, Permissions.ActivityRead, ct);
            var id = Wire.Id(activity.Id);

            context.Activities.Remove(activity);
            await context.SaveChangesAsync(ct);

            if (staff.Count > 0)
            {
                await events.SendToUsersAsync(
                    staff, EventTypes.ActivityChanged, new { deletedId = id }, ct);
            }
            if (members.Count > 0)
            {
                await events.SendToUsersAsync(
                    members, EventTypes.ActivityDeleted, new { activityId = id }, ct);
            }
        }

        public async Task<IReadOnlyList<ManagedSeriesDto>> ReorderSeriesAsync(
            string idOrSlug, IReadOnlyList<string> ordered, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);
            AssertOpen(activity);

            var rounds = await context.Series.Where(s => s.ActivityId == activity.Id).ToListAsync(ct);
            Reorder(rounds, ordered, s => s.Id, (s, order) => s.Order = order);

            await context.SaveChangesAsync(ct);
            await AnnounceActivityAsync(activity.Id, ct);
            return await series.ListManagedAsync(idOrSlug, ct);
        }

        /// <summary>
        /// Applies a given order and leaves anything the caller did not mention
        /// after it, in the order it already had.
        /// <para>
        /// A screen that reorders three of ten rows must not silently renumber
        /// the other seven — and a caller that names an id twice, or one that is
        /// not there, gets a refusal rather than a shuffle.
        /// </para>
        /// </summary>
        private static void Reorder<T>(
            List<T> items, IReadOnlyList<string> ordered, Func<T, Guid> id, Action<T, int> setOrder)
        {
            var wanted = new List<Guid>();
            foreach (var raw in ordered)
            {
                if (!Guid.TryParse(raw, out var parsed))
                {
                    throw new ValidationException($"Not an id: {raw}", "order.malformed");
                }
                if (!wanted.Contains(parsed)) wanted.Add(parsed);
            }

            var known = items.Select(id).ToHashSet();
            var stranger = wanted.FirstOrDefault(w => !known.Contains(w));
            if (stranger != Guid.Empty)
            {
                throw new ValidationException($"Not in this collection: {stranger}", "order.foreign");
            }

            var position = 1;
            foreach (var target in wanted)
            {
                setOrder(items.First(item => id(item) == target), position++);
            }
            foreach (var rest in items.Where(item => !wanted.Contains(id(item))))
            {
                setOrder(rest, position++);
            }
        }

        public async Task<ManagedSeriesDto> UpdateSeriesAsync(
            Guid seriesId, SeriesInputDto input, CancellationToken ct)
        {
            var round = await Round(seriesId, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, round.ActivityId, ct);

            if (input.Slug is { } slug && slug.Trim() != round.Slug)
            {
                var taken = await context.Series.AnyAsync(
                    s => s.ActivityId == round.ActivityId && s.Slug == slug.Trim() && s.Id != seriesId, ct);
                if (taken)
                {
                    throw new ConflictException(
                        "A series with that slug already exists in this activity", "series.slug.taken");
                }
                round.Slug = slug.Trim();
            }

            round.Name = input.Name?.Trim() is { Length: > 0 } name ? name : round.Name;
            round.StartDate = ActivityService.ParseInstant(input.StartDate);
            round.EndDate = ActivityService.ParseInstant(input.EndDate);
            if (input.RevealProblemCount is { } reveal) round.RevealProblemCount = reveal;
            round.RankingFreezeAt = ActivityService.ParseInstant(input.RankingFreezeAt);
            round.RankingRevealAt = ActivityService.ParseInstant(input.RankingRevealAt);
            round.RankingVisibleFrom = ActivityService.ParseInstant(input.RankingVisibleFrom);
            round.RankingVisibleTo = ActivityService.ParseInstant(input.RankingVisibleTo);
            SeriesService.ApplyRestrictions(context, round, input);

            Reconcile(round);
            await context.SaveChangesAsync(ct);
            // A round's pool overrides its activity's, so the same holds here.
            if (input.RunnerTags is not null) queue.Wake();
            await AnnounceSeriesAsync(round.ActivityId, round, ct);
            return await OneAsync(round, ct);
        }

        /// <summary>
        /// Keeps the stored flag in step with the dates it was just given.
        /// <para>
        /// Openness is stored (2026-08-08), so every write that moves a date has
        /// to settle it in the same transaction — otherwise a round shifted into
        /// the future stays open until the scheduler's next pass, and one shifted
        /// into the past stays shut with no marker to reopen it.
        /// </para>
        /// </summary>
        private void Reconcile(Series round)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            if (round.PausedAt is not null)
            {
                // A paused round is shut by the pause, whatever the clock says.
                round.IsOpen = false;
                return;
            }

            var started = round.StartDate is null || round.StartDate <= now;
            var ended = round.EndDate is not null && round.EndDate <= now;

            round.IsOpen = started && !ended;

            // The announcement markers follow the state, so a round moved back
            // into the future is announced again when it arrives.
            if (!started) round.StartAnnouncedAt = null;
            if (!ended) round.EndAnnouncedAt = null;
        }

        public async Task DeleteSeriesAsync(Guid seriesId, CancellationToken ct)
        {
            var round = await Round(seriesId, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, round.ActivityId, ct);

            var submissions = await context.Submissions
                .CountAsync(s => s.SeriesProblem!.SeriesId == seriesId, ct);
            if (submissions > 0)
            {
                throw new ConflictException(
                    "This series holds submissions and cannot be deleted", "series.hasSubmissions");
            }

            var activityId = round.ActivityId;
            context.Series.Remove(round);
            await context.SaveChangesAsync(ct);
            await AnnounceSeriesDeletedAsync(activityId, seriesId, ct);
        }

        /// <summary>
        /// Moves a round by a delta.
        /// <para>
        /// A delta and not two dates: two managers reacting to the same delayed
        /// round would each read the old start, add ten minutes, and write the
        /// same new time — losing one of the two shifts. The freeze and the
        /// window move with it, or a round delayed by an hour would freeze at
        /// the old wall clock.
        /// </para>
        /// </summary>
        public async Task<ManagedSeriesDto> ShiftSeriesAsync(Guid seriesId, int minutes, CancellationToken ct)
        {
            var round = await Round(seriesId, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, round.ActivityId, ct);

            var delta = TimeSpan.FromMinutes(minutes);
            round.StartDate = round.StartDate?.Add(delta);
            round.EndDate = round.EndDate?.Add(delta);
            round.RankingFreezeAt = round.RankingFreezeAt?.Add(delta);
            round.RankingRevealAt = round.RankingRevealAt?.Add(delta);
            round.RankingVisibleFrom = round.RankingVisibleFrom?.Add(delta);
            round.RankingVisibleTo = round.RankingVisibleTo?.Add(delta);

            Reconcile(round);
            await context.SaveChangesAsync(ct);
            await AnnounceSeriesAsync(round.ActivityId, round, ct);
            return await OneAsync(round, ct);
        }

        public async Task<ManagedSeriesDto> PauseSeriesAsync(
            Guid seriesId, bool hideProblems, CancellationToken ct)
        {
            var round = await Round(seriesId, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, round.ActivityId, ct);

            if (round.PausedAt is not null)
            {
                throw new ConflictException("That series is already paused", "series.alreadyPaused");
            }

            round.PausedAt = clock.GetUtcNow().UtcDateTime;
            round.HideProblemsWhilePaused = hideProblems;
            // A pause takes no submission, so it is shut — and whether the
            // statements go with it is the manager's answer at this moment.
            round.IsOpen = false;

            await context.SaveChangesAsync(ct);
            await AnnounceSeriesAsync(round.ActivityId, round, ct);
            return await OneAsync(round, ct);
        }

        public async Task<ManagedSeriesDto> ResumeSeriesAsync(
            Guid seriesId, bool extendEnd, CancellationToken ct)
        {
            var round = await Round(seriesId, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, round.ActivityId, ct);

            if (round.PausedAt is not { } pausedAt)
            {
                throw new ConflictException("That series is not paused", "series.notPaused");
            }

            var now = clock.GetUtcNow().UtcDateTime;

            if (extendEnd)
            {
                // Gives the interruption back. Everything downstream of the end
                // moves with it, for the same reason a shift moves them.
                var lost = now - pausedAt;
                round.EndDate = round.EndDate?.Add(lost);
                round.RankingFreezeAt = round.RankingFreezeAt?.Add(lost);
                round.RankingRevealAt = round.RankingRevealAt?.Add(lost);
                round.RankingVisibleTo = round.RankingVisibleTo?.Add(lost);
            }

            round.PausedAt = null;
            round.HideProblemsWhilePaused = false;
            Reconcile(round);

            await context.SaveChangesAsync(ct);
            await AnnounceSeriesAsync(round.ActivityId, round, ct);
            return await OneAsync(round, ct);
        }

        public async Task<ManagedSeriesDto> ReorderProblemsAsync(
            Guid seriesId, IReadOnlyList<string> ordered, CancellationToken ct)
        {
            var round = await Round(seriesId, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, round.ActivityId, ct);

            var assignments = await context.SeriesProblems.Where(sp => sp.SeriesId == seriesId).ToListAsync(ct);
            Reorder(assignments, ordered, sp => sp.Id, (sp, order) => sp.Order = order);

            await context.SaveChangesAsync(ct);
            await AnnounceSeriesAsync(round.ActivityId, round, ct);
            return await OneAsync(round, ct);
        }

        public async Task<ManagedSeriesDto> UpdateAssignmentAsync(
            Guid assignmentId, SeriesProblemInputDto input, CancellationToken ct)
        {
            var assignment = await context.SeriesProblems
                .Include(sp => sp.Series)
                .FirstOrDefaultAsync(sp => sp.Id == assignmentId, ct)
                ?? throw new NotFoundException("Assignment");

            await permissions.RequireAsync(Permissions.ProblemAttach, assignment.ActivityId, ct);

            if (input.Slug is { } raw && raw.Trim() is { Length: > 0 } slug && slug != assignment.Slug)
            {
                var taken = await context.SeriesProblems.AnyAsync(
                    sp => sp.ActivityId == assignment.ActivityId && sp.Slug == slug && sp.Id != assignmentId, ct);
                if (taken)
                {
                    throw new ConflictException(
                        "That problem slug is already used in this activity", "assignment.slug.taken");
                }
                assignment.Slug = slug;
            }

            assignment.Name = input.Name;
            CheckMaxPoints(input.MaxPoints);
            SubmissionLimits.Check(
                input.MaxUploadBytes, input.MaxAttachments, input.MaxSubmissions, "assignment");
            assignment.MaxPoints = input.MaxPoints;
            assignment.MaxUploadBytes = input.MaxUploadBytes;
            assignment.MaxAttachments = input.MaxAttachments;
            assignment.MaxSubmissions = input.MaxSubmissions;
            assignment.Config = Opaque.Store(input.Config, "config");
            assignment.Spec = Opaque.Store(input.Spec, "spec");
            assignment.Props = Opaque.Store(input.Props, "props");

            if (input.PinnedProblemVersionId is { } rawPin)
            {
                assignment.PinnedProblemVersionId = Guid.TryParse(rawPin, out var pin) ? pin : null;
            }

            await context.SaveChangesAsync(ct);
            return await OneAsync(assignment.Series!, ct);
        }

        /// <summary>
        /// Detaching is refused once anything has been submitted against the
        /// assignment: the submissions point at it, and a standing computed from
        /// them would develop a hole.
        /// </summary>
        public async Task<ManagedSeriesDto> DetachAsync(Guid assignmentId, CancellationToken ct)
        {
            var assignment = await context.SeriesProblems
                .Include(sp => sp.Series)
                .FirstOrDefaultAsync(sp => sp.Id == assignmentId, ct)
                ?? throw new NotFoundException("Assignment");

            await permissions.RequireAsync(Permissions.ProblemAttach, assignment.ActivityId, ct);

            var submissions = await context.Submissions.CountAsync(s => s.SeriesProblemId == assignmentId, ct);
            if (submissions > 0)
            {
                throw new ConflictException(
                    "Something has already been submitted here. The assignment cannot be removed.",
                    "assignment.hasSubmissions");
            }

            var round = assignment.Series!;
            context.SeriesProblems.Remove(assignment);
            await context.SaveChangesAsync(ct);
            await AnnounceSeriesAsync(round.ActivityId, round, ct);
            return await OneAsync(round, ct);
        }

        private async Task<Series> Round(Guid seriesId, CancellationToken ct) =>
            // The rules come with it: an update replaces the whole list, and a
            // collection that was never loaded is one `Clear()` cannot empty.
            await context.Series
                .Include(s => s.AddressRules)
                .FirstOrDefaultAsync(s => s.Id == seriesId, ct)
                ?? throw new NotFoundException("Series");

        private async Task<ManagedSeriesDto> OneAsync(Series round, CancellationToken ct)
        {
            var all = await series.ListManagedAsync(Wire.Id(round.ActivityId), ct);
            return all.First(s => s.Id == Wire.Id(round.Id));
        }

        private static void AssertOpen(Activity activity)
        {
            if (activity.ArchivedAt is not null)
            {
                throw new ConflictException("An archived activity accepts no changes", "activity.archived");
            }
        }

        private static JoinPolicy ParseJoinPolicy(string? value) => value switch
        {
            "open" => JoinPolicy.Open,
            "password" => JoinPolicy.Password,
            _ => JoinPolicy.Closed,
        };

        private static ScoreVisibility ParseScoreVisibility(string? value) => value switch
        {
            "participantOnly" => ScoreVisibility.ParticipantOnly,
            "managersOnly" => ScoreVisibility.ManagersOnly,
            _ => ScoreVisibility.Everyone,
        };
    
        /// <summary>
        /// A point value, or nothing. <b>Never zero and never negative.</b>
        /// <para>
        /// Zero was accepted and is not a problem worth nothing — it is a
        /// problem whose every number is <c>0 / 0</c>, which a board reads as
        /// full marks because zero out of zero is the whole of it. A problem
        /// nobody should score is a problem nobody should attach.
        /// </para>
        /// <para>
        /// Checked on both write paths rather than on one: an assignment is
        /// created by attaching and changed by editing, and a rule enforced on
        /// the first alone is a rule the second removes.
        /// </para>
        /// </summary>
        private static void CheckMaxPoints(int? maxPoints)
        {
            if (maxPoints is { } value && value <= 0)
            {
                throw new ValidationException(
                    $"A problem is worth {value} points here, which is not a value anything can be scored against",
                    "assignment.maxPoints.invalid");
            }
        }

}
}
