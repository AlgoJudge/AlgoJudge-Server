using System.Reflection;
using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a copy carries, field by field, for the three entities copying touches.
///
/// <para>
/// <b>This exists because nine fields were dropped silently.</b> Every one of
/// them — <c>ShowGroupMembers</c>, both <c>RunnerTags</c>, <c>Importance</c>,
/// <c>ImportanceScope</c>, <c>AddressRules</c>, <c>RestrictionsEnabled</c>,
/// <c>Spec</c> and <c>Props</c> — arrived after the duplication code was written
/// and nothing brought them into it. A copied contest offered no language and
/// was open to the whole internet where the original was closed to one room.
/// </para>
///
/// <para>
/// The list of dated fields was already guarded by reflection, so a date could
/// not be added and left unshifted. Everything else was a hand-written list
/// nobody re-read. <b>The partition below covers every property</b>, and
/// <c>Every_field_of_a_copy_is_classified</c> fails on one that is in none of
/// the three buckets — so a tenth field cannot be added and forgotten.
/// </para>
///
/// <para>
/// The same classification is written a second time in <c>AlgoJudge-Client</c>,
/// for the exchange bundle, and <b>nothing compares the two</b>. See
/// <c>docs/specs/EXCHANGE_FORMAT.md</c> in the workspace: two local guards that
/// each fail loudly, rather than one claim that something is checked.
/// </para>
/// </summary>
public static class CopiedFields
{
    /// <summary>
    /// Carried unchanged, and compared automatically against the source.
    /// </summary>
    public static readonly Dictionary<Type, string[]> Carried = new()
    {
        [typeof(Activity)] =
        [
            nameof(Activity.Name), nameof(Activity.Type), nameof(Activity.RankingType),
            nameof(Activity.TimeZone), nameof(Activity.HasQuestions),
            nameof(Activity.ScoreVisibility), nameof(Activity.ShowGroupMembers),
            nameof(Activity.JoinPolicy), nameof(Activity.Unlisted),
            nameof(Activity.HideEndedSeriesProblems), nameof(Activity.Props),
            nameof(Activity.MaxUploadBytes), nameof(Activity.MaxAttachments),
            nameof(Activity.MaxSubmissionsPerProblem), nameof(Activity.RunnerTags),
        ],
        [typeof(Series)] =
        [
            nameof(Series.Name),
            // **Pause state, and carried anyway** — argued 2026-08-25 and kept.
            // `SeriesGate` reads it only while `PausedAt` is set, and a copy has
            // none, so carrying it is inert. What decided it is the other
            // direction: if the product ever makes this a setting chosen in
            // advance, a copy that had stopped carrying it would silently drop
            // one — and **this test would not fire**, because it catches a field
            // that is *added*, never one that changes meaning. Free now, and
            // right then.
            nameof(Series.HideProblemsWhilePaused),
            nameof(Series.RevealProblemCount), nameof(Series.Importance),
            nameof(Series.ImportanceScope), nameof(Series.RestrictionsEnabled),
            nameof(Series.RunnerTags),
        ],
        [typeof(SeriesProblem)] =
        [
            nameof(SeriesProblem.Name), nameof(SeriesProblem.Order),
            nameof(SeriesProblem.MaxPoints), nameof(SeriesProblem.Config),
            nameof(SeriesProblem.Spec), nameof(SeriesProblem.Props),
            nameof(SeriesProblem.MaxUploadBytes), nameof(SeriesProblem.MaxAttachments),
            nameof(SeriesProblem.MaxSubmissions), nameof(SeriesProblem.ProblemId),
            nameof(SeriesProblem.PinnedProblemVersionId),
        ],
    };

    /// <summary>
    /// Deliberately not carried. Each is a decision, not an omission.
    /// </summary>
    public static readonly Dictionary<Type, string[]> Reset = new()
    {
        [typeof(Activity)] =
        [
            // A copy is a different activity, and it has done nothing.
            nameof(Activity.Id), nameof(Activity.Slug),
            // Known by everybody who took the original: a new cohort joinable by
            // the previous one is a leak, not a setting.
            nameof(Activity.JoinPassword),
            nameof(Activity.PublishedAt), nameof(Activity.ArchivedAt),
            // Nobody's work, nobody's rights, nobody's teams.
            nameof(Activity.Questions), nameof(Activity.Grants), nameof(Activity.Groups),
        ],
        [typeof(Series)] =
        [
            nameof(Series.Id), nameof(Series.Activity),
            // A copy has never opened, never paused and never announced anything.
            // Carrying an announcement marker would make the scheduler stay
            // silent about a round nobody was ever told about.
            nameof(Series.IsOpen), nameof(Series.PausedAt),
            nameof(Series.StartAnnouncedAt), nameof(Series.EndAnnouncedAt),
            nameof(Series.WindowAnnouncedAt), nameof(Series.UnfrozenAnnouncedAt),
            // The concurrency token of a row that does not exist yet.
            nameof(Series.RowVersion),
        ],
        [typeof(SeriesProblem)] =
        [
            nameof(SeriesProblem.Id),
            // Navigations; the ids beside them are what is carried.
            nameof(SeriesProblem.Series), nameof(SeriesProblem.Activity),
            nameof(SeriesProblem.Problem), nameof(SeriesProblem.PinnedProblemVersion),
            // Nothing that happened travels.
            nameof(SeriesProblem.Submissions), nameof(SeriesProblem.Questions),
        ],
    };

    /// <summary>
    /// Carried, and checked by a test of its own — a date that moves, a slug the
    /// caller chooses, a collection copied row by row.
    /// </summary>
    public static readonly Dictionary<Type, string[]> Checked = new()
    {
        [typeof(Activity)] =
        [
            nameof(Activity.StartDate), nameof(Activity.EndDate),
            nameof(Activity.Series), nameof(Activity.AttachmentRules),
        ],
        [typeof(Series)] =
        [
            nameof(Series.ActivityId), nameof(Series.Slug), nameof(Series.Order),
            nameof(Series.StartDate), nameof(Series.EndDate),
            nameof(Series.RankingFreezeAt), nameof(Series.RankingRevealAt),
            nameof(Series.RankingVisibleFrom), nameof(Series.RankingVisibleTo),
            nameof(Series.AddressRules), nameof(Series.SeriesProblems),
        ],
        [typeof(SeriesProblem)] =
        [
            nameof(SeriesProblem.SeriesId), nameof(SeriesProblem.ActivityId),
            nameof(SeriesProblem.Slug),
        ],
    };

    public static IEnumerable<PropertyInfo> PropertiesOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0);

    /// <summary>The three entities, so a test can walk them without listing them.</summary>
    public static readonly Type[] Entities = [typeof(Activity), typeof(Series), typeof(SeriesProblem)];

    /// <summary>
    /// Every carried field of <paramref name="copy"/> equals the source's.
    ///
    /// <para>
    /// <b>And the source's value must differ from what a new entity already
    /// holds</b>, which is the half that makes this bite. A copy that drops a
    /// field leaves the property initializer's value behind, so comparing
    /// against a source that never moved off it passes whether the field
    /// travelled or not. <c>RestrictionsEnabled</c> defaults to <c>true</c> and
    /// <c>Unlisted</c> to <c>false</c>: neither "set it to true" nor "set it to
    /// a non-default" is the rule — <b>differ from a fresh one</b> is.
    /// </para>
    /// </summary>
    public static void AssertCarried(object source, object copy)
    {
        var type = source.GetType();
        var fresh = Fresh(type);

        foreach (var name in Carried[type])
        {
            var property = type.GetProperty(name)!;
            var was = property.GetValue(source);
            var now = property.GetValue(copy);

            Assert.False(Same(was, property.GetValue(fresh)),
                $"{type.Name}.{name} still holds what a new one holds, so this test "
                + "cannot tell a carried field from a dropped one. Set it in the fixture.");

            Assert.True(Same(was, now), $"{type.Name}.{name} did not travel: {Show(was)} became {Show(now)}");
        }
    }

    /// <summary>
    /// An entity as constructed, to read the property initializers off.
    ///
    /// <para>
    /// The <c>required</c> members get sentinels no fixture would produce,
    /// because they have <b>no initializer</b> for the comparison above to be
    /// about — a required field cannot be left at a default, so the compiler is
    /// what guards those and the sentinel keeps them from failing the check
    /// vacuously.
    /// </para>
    /// </summary>
    private static object Fresh(Type type)
    {
        const string none = " none";
        return type switch
        {
            _ when type == typeof(Activity) => new Activity
            {
                Slug = none, Name = none, Type = none, RankingType = none, TimeZone = none,
            },
            _ when type == typeof(Series) => new Series { Slug = none, Name = none },
            _ when type == typeof(SeriesProblem) => new SeriesProblem { Slug = none },
            _ => throw new ArgumentException($"No fresh {type.Name} to compare against", nameof(type)),
        };
    }

    /// <summary>Equality that sees through a list — <c>RunnerTags</c> is one.</summary>
    private static bool Same(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (IEnumerable<string> a, IEnumerable<string> b) => a.SequenceEqual(b),
        _ => left.Equals(right),
    };

    private static string Show(object? value) => value switch
    {
        null => "null",
        IEnumerable<string> list => "[" + string.Join(", ", list) + "]",
        _ => value.ToString() ?? "",
    };
}
