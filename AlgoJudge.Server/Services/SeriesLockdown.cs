using System.Net;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// What a running series puts out of reach, for the reader of this request.
    /// </summary>
    /// <param name="Floor">
    /// The highest rank running that admits this reader. Anything below it is
    /// locked; zero means nothing is.
    /// </param>
    /// <param name="Hidden">
    /// Series carrying address rules this reader does not match. Absent from
    /// every list, and refused by name with the reason and nothing else.
    /// </param>
    /// <param name="Exempt">Staff of the series that set the floor.</param>
    public sealed record LockdownState(
        int Floor,
        Guid? BySeriesId,
        string? BySeriesName,
        IReadOnlySet<Guid> Hidden,
        bool Exempt)
    {
        public static readonly LockdownState Open =
            new(0, null, null, new HashSet<Guid>(), false);

        /// <summary>Whether anything is out of reach at all. The common answer is no.</summary>
        public bool Quiet => Floor == 0 && Hidden.Count == 0;

        /// <summary>A series this reader may not reach from here.</summary>
        public bool IsHidden(Guid seriesId) => Hidden.Contains(seriesId);

        /// <summary>A series something more important has displaced.</summary>
        public bool IsLocked(int importance) => !Exempt && importance < Floor;
    }

    public interface ISeriesLockdown
    {
        /// <summary>What is out of reach for the current reader, right now.</summary>
        Task<LockdownState> ForReaderAsync(CancellationToken ct);

        /// <summary>
        /// Whether an activity is locked: it is running nothing that survives
        /// the floor.
        /// </summary>
        Task<bool> IsActivityLockedAsync(Guid activityId, LockdownState state, CancellationToken ct);

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

                if (series.Importance > (top?.Importance ?? 0)) top = series;
            }

            if (top is null) return new LockdownState(0, null, null, hidden, false);

            return new LockdownState(
                top.Importance,
                top.Id,
                top.Name,
                hidden,
                // Staff of the series doing the displacing. Whoever runs the
                // examination would otherwise lose the panel they run it from.
                Exempt: granted.GetValueOrDefault(top.ActivityId));
        }

        public async Task<bool> IsActivityLockedAsync(
            Guid activityId, LockdownState state, CancellationToken ct)
        {
            if (state.Floor == 0 || state.Exempt) return false;

            // Not locked while it runs something at the floor. `>=` reads as the
            // rule rather than as an equality that happens to hold: the floor is
            // a maximum, so nothing can exceed it.
            var survives = await context.Series.AsNoTracking()
                .AnyAsync(s => s.ActivityId == activityId
                    && s.IsOpen && s.PausedAt == null
                    && s.Importance >= state.Floor
                    && !state.Hidden.Contains(s.Id), ct);

            return !survives;
        }

        public async Task RequireReachableAsync(Guid activityId, CancellationToken ct)
        {
            var state = await ForReaderAsync(ct);
            if (state.Quiet) return;
            if (!await IsActivityLockedAsync(activityId, state, ct)) return;

            throw new ForbiddenActionException(
                $"Locked while \"{state.BySeriesName}\" is running", LockdownCodes.Displaced);
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
