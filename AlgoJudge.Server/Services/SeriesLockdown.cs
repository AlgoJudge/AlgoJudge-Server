using System.Net;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>The round that set a floor, and the floor it set.</summary>
    public sealed record Displacer(int Floor, Guid SeriesId, string SeriesName);

    /// <summary>
    /// What a running series puts out of reach, for the reader of this request.
    /// </summary>
    /// <param name="Global">
    /// The highest <see cref="SeriesImportanceScope.Installation"/> rank running
    /// that admits this reader. It applies to every activity they are in.
    /// </param>
    /// <param name="Local">
    /// The same, per activity, from that activity's own
    /// <see cref="SeriesImportanceScope.Activity"/> rounds.
    /// </param>
    /// <param name="Hidden">
    /// Series carrying address rules this reader does not match. Absent from
    /// every list, and refused by name with the reason and nothing else.
    /// </param>
    public sealed record LockdownState(
        Displacer? Global,
        IReadOnlyDictionary<Guid, Displacer> Local,
        IReadOnlySet<Guid> Hidden)
    {
        public static readonly LockdownState Open =
            new(null, new Dictionary<Guid, Displacer>(), new HashSet<Guid>());

        /// <summary>Whether anything is out of reach at all. The common answer is no.</summary>
        public bool Quiet => Global is null && Local.Count == 0 && Hidden.Count == 0;

        /// <summary>A series this reader may not reach from here.</summary>
        public bool IsHidden(Guid seriesId) => Hidden.Contains(seriesId);

        /// <summary>
        /// What displaces the lower ranks of this activity, or null.
        /// <para>
        /// The higher of the two floors, the global one winning a tie: it is the
        /// one whose reach a reader is least likely to guess, so it is the one
        /// worth naming. Exemption is settled before a displacer is recorded, so
        /// whatever is here applies.
        /// </para>
        /// </summary>
        public Displacer? DisplacerFor(Guid activityId)
        {
            var local = Local.GetValueOrDefault(activityId);
            if (Global is null) return local;
            if (local is null) return Global;
            return local.Floor > Global.Floor ? local : Global;
        }

        /// <summary>The rank this activity's rounds must reach. Zero locks nothing.</summary>
        public int FloorFor(Guid activityId) => DisplacerFor(activityId)?.Floor ?? 0;

        /// <summary>A series something more important has displaced.</summary>
        public bool IsLocked(Guid activityId, int importance) => importance < FloorFor(activityId);
    }

    public interface ISeriesLockdown
    {
        /// <summary>What is out of reach for the current reader, right now.</summary>
        Task<LockdownState> ForReaderAsync(CancellationToken ct);

        /// <summary>
        /// Whether an activity is locked: it is running nothing that survives
        /// the floor.
        /// <para>
        /// <b>An activity-scoped round can never lock its own activity</b>, and
        /// that falls out rather than being a special case: the round that set
        /// the floor is running here, at the floor.
        /// </para>
        /// </summary>
        Task<bool> IsActivityLockedAsync(Guid activityId, LockdownState state, CancellationToken ct);

        /// <summary>
        /// The rounds of one activity this reader cannot reach — hidden, or
        /// displaced by something above them.
        /// <para>
        /// <b>What the round-granular paths ask.</b> An activity-scoped floor
        /// never locks the activity, so the board, the submission list and the
        /// questions cannot be answered with "all of it or none of it" any more.
        /// </para>
        /// </summary>
        Task<IReadOnlySet<Guid>> UnreachableRoundsAsync(
            Guid activityId, LockdownState state, CancellationToken ct);

        /// <summary>
        /// Refuses everything under a locked activity.
        /// <para>
        /// <b>Called by every participant path that serves its contents</b> —
        /// problems, submissions, questions, the board. The activity's own shell
        /// is the one thing that still answers, because it is what carries the
        /// reason.
        /// </para>
        /// </summary>
        Task RequireReachableAsync(Guid activityId, CancellationToken ct);
    }

    /// <summary>The codes a refusal carries, so a screen can tell them apart.</summary>
    public static class LockdownCodes
    {
        /// <summary>Something more important is running.</summary>
        public const string Displaced = "series.displaced";

        /// <summary>Restricted to addresses this request did not come from.</summary>
        public const string Address = "series.address";
    }

    /// <summary>
    /// One rule, in one place, because the activity list, the file service and
    /// the submit path all ask it and three copies would disagree.
    /// <para>
    /// <b>Visibility, applied after authorization — never a permission.</b> The
    /// model has no subtraction in it: grants answer <i>who may</i>, this answers
    /// <i>what is reachable from here while that runs</i>.
    /// <c>docs/specs/SERIES_LOCKDOWN.md</c>.
    /// </para>
    /// <para>
    /// <b>Per request, not per sign-in.</b> A rule decided at sign-in is one you
    /// defeat by signing in at home and walking to the room. The address is on
    /// every request already.
    /// </para>
    /// <para>
    /// <b>It follows the grant, not the room.</b> Only a series this reader takes
    /// part in can displace anything, so a student sitting in the same laboratory
    /// and not writing the examination loses nothing.
    /// </para>
    /// </summary>
    public class SeriesLockdown(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IRequestOrigin origin
    ) : ISeriesLockdown
    {
        public async Task<LockdownState> ForReaderAsync(CancellationToken ct)
        {
            // The installation-wide switch, first and cheapest: nothing else is
            // read while it is off.
            var enabled = await context.Instance.AsNoTracking()
                .Select(i => i.SeriesRestrictionsEnabled)
                .FirstOrDefaultAsync(ct);
            if (!enabled) return LockdownState.Open;

            var reader = await currentUser.GetAsync(ct);
            if (reader is null) return LockdownState.Open;

            // Everything that could restrict anything, anywhere. A handful at
            // any moment: open, not paused, its own switch on, and carrying
            // either a rank or a rule.
            var restricting = await context.Series.AsNoTracking()
                .Where(s => s.IsOpen && s.PausedAt == null && s.RestrictionsEnabled
                    && (s.Importance > 0 || s.AddressRules.Any()))
                .Include(s => s.AddressRules)
                .ToListAsync(ct);

            if (restricting.Count == 0) return LockdownState.Open;

            var activityIds = restricting.Select(s => s.ActivityId).ToHashSet();
            var grants = await context.Grants.AsNoTracking()
                .Where(g => g.UserId == reader.Id
                    && g.State == GrantState.Active
                    && g.ActivityId != null
                    && activityIds.Contains(g.ActivityId.Value))
                .Select(g => new { ActivityId = g.ActivityId!.Value, g.IsSystem })
                .ToListAsync(ct);

            var granted = grants.ToDictionary(g => g.ActivityId, g => g.IsSystem);
            var address = origin.Address;

            var hidden = new HashSet<Guid>();
            Series? top = null;
            var local = new Dictionary<Guid, Series>();

            foreach (var series in restricting)
            {
                // Not theirs to be displaced by, and not theirs to be hidden
                // from either — they cannot see it in the first place.
                if (!granted.ContainsKey(series.ActivityId)) continue;

                if (series.AddressRules.Count > 0 && !Admits(series, address))
                {
                    hidden.Add(series.Id);
                    continue;
                }

                if (series.Importance == 0) continue;

                if (series.ImportanceScope == SeriesImportanceScope.Installation)
                {
                    if (series.Importance > (top?.Importance ?? 0)) top = series;
                }
                else if (series.Importance > (local.GetValueOrDefault(series.ActivityId)?.Importance ?? 0))
                {
                    local[series.ActivityId] = series;
                }
            }

            // **Staff are exempt from what their own activity's round does**, and
            // it is settled here so that nothing downstream has to ask again.
            // Whoever runs the examination would otherwise lose the panel they
            // run it from.
            if (top is not null && granted.GetValueOrDefault(top.ActivityId)) top = null;

            return new LockdownState(
                top is null ? null : Of(top),
                local
                    .Where(entry => !granted.GetValueOrDefault(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => Of(entry.Value)),
                hidden);

            static Displacer Of(Series series) => new(series.Importance, series.Id, series.Name);
        }

        public async Task<bool> IsActivityLockedAsync(
            Guid activityId, LockdownState state, CancellationToken ct)
        {
            var floor = state.FloorFor(activityId);
            if (floor == 0) return false;

            // Not locked while it runs something at the floor. `>=` reads as the
            // rule rather than as an equality that happens to hold: the floor is
            // a maximum, so nothing can exceed it — and it is what makes an
            // activity-scoped round unable to lock the activity it runs in.
            var survives = await context.Series.AsNoTracking()
                .AnyAsync(s => s.ActivityId == activityId
                    && s.IsOpen && s.PausedAt == null
                    && s.Importance >= floor
                    && !state.Hidden.Contains(s.Id), ct);

            return !survives;
        }

        public async Task<IReadOnlySet<Guid>> UnreachableRoundsAsync(
            Guid activityId, LockdownState state, CancellationToken ct)
        {
            if (state.Quiet) return new HashSet<Guid>();

            var floor = state.FloorFor(activityId);
            var rounds = await context.Series.AsNoTracking()
                .Where(s => s.ActivityId == activityId)
                .Select(s => new { s.Id, s.Importance })
                .ToListAsync(ct);

            return rounds
                .Where(s => state.IsHidden(s.Id) || s.Importance < floor)
                .Select(s => s.Id)
                .ToHashSet();
        }

        public async Task RequireReachableAsync(Guid activityId, CancellationToken ct)
        {
            var state = await ForReaderAsync(ct);
            if (state.Quiet) return;
            if (!await IsActivityLockedAsync(activityId, state, ct)) return;

            throw new ForbiddenActionException(
                $"Locked while \"{state.DisplacerFor(activityId)?.SeriesName}\" is running",
                LockdownCodes.Displaced);
        }

        /// <summary>
        /// Whether one of this series' ranges holds the address.
        /// <para>
        /// <b>An address the Server does not know is a failed match, not an
        /// error.</b> A missing header admits nobody — which is the loud half —
        /// while locking nobody out of anything else, which is the half that
        /// keeps a proxy hiccup from stopping every course at once. Nothing is
        /// gained by stripping it: the examination is what is lost.
        /// </para>
        /// <para>
        /// Compared in .NET against the address <see cref="RequestOrigin"/> has
        /// already un-mapped. A stored range that will not parse matches
        /// nothing — <c>cidr</c> should have refused it on the way in, and a
        /// range nobody can read must not admit everybody.
        /// </para>
        /// </summary>
        private static bool Admits(Series series, IPAddress? address)
        {
            if (address is null) return false;

            foreach (var rule in series.AddressRules)
            {
                if (rule.Network.Contains(address)) return true;
            }
            return false;
        }
    }
}
