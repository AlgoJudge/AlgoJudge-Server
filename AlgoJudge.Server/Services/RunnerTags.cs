using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Which Runners a piece of work may be handed to.
    /// <para>
    /// A Runner carries tags and so does the work; they are paired when the two
    /// lists <b>share at least one</b>. Unlike GitLab, whose runner must hold
    /// every tag a job asks for: there the tags are requirements, here they are
    /// pools, because what a Runner is <i>able</i> to do is already answered by
    /// <c>ProblemTypes</c> and <c>External</c>.
    /// </para>
    /// <para>
    /// <b>An empty list means <see cref="Default"/>, on both sides</b>, and that
    /// one line is the whole of the exclusivity. A Runner given a tag leaves the
    /// general pool, because it no longer shares one with untagged work; work
    /// given a tag leaves the general Runners, for the same reason. Neither half
    /// had to be written, and neither can be forgotten.
    /// </para>
    /// <para>
    /// It also makes the migration a no-op: until somebody types a tag, every
    /// Runner and every activity is in <see cref="Default"/> and nothing moves.
    /// </para>
    /// </summary>
    public static class RunnerTags
    {
        /// <summary>
        /// The pool an untagged Runner serves and untagged work belongs to.
        /// Ordinary text, so writing it out means exactly what leaving the field
        /// empty means.
        /// </summary>
        public const string Default = "default";

        public const int MaxTags = 16;
        public const int MaxLength = 64;

        /// <summary>
        /// Trimmed, lowercased, de-duplicated, blanks dropped.
        /// <para>
        /// Applied on both sides so the comparison is plain equality. Without
        /// the lowercasing <c>Lab-A</c> and <c>lab-a</c> are two pools that read
        /// as one, and the failure is a queue that never drains with nothing on
        /// any screen to say why.
        /// </para>
        /// </summary>
        public static List<string> Normalise(IEnumerable<string>? tags) =>
            tags is null
                ? []
                : [.. tags.Select(tag => tag.Trim().ToLowerInvariant())
                          .Where(tag => tag.Length > 0)
                          .Distinct()];

        /// <summary>What is matched against, once the empty case is spelled out.</summary>
        public static string[] Effective(IEnumerable<string>? tags)
        {
            var normalised = Normalise(tags);
            return normalised.Count == 0 ? [Default] : [.. normalised];
        }

        /// <summary>Whether a Runner holding <paramref name="runner"/> may take work tagged <paramref name="work"/>.</summary>
        public static bool Match(IEnumerable<string>? runner, IEnumerable<string>? work) =>
            Effective(runner).Intersect(Effective(work)).Any();

        /// <summary>
        /// The approved Runners each of whose pools reaches <paramref name="work"/>.
        /// <para>
        /// The manager's screen shows this beside the field, because tagging an
        /// activity nothing carries stops its judging silently: the submissions
        /// are accepted, queued, and never claimed. Zero here is the only
        /// warning there can be before somebody notices during a contest.
        /// </para>
        /// </summary>
        public static int CountMatching(IEnumerable<List<string>> runners, IEnumerable<string>? work)
        {
            var pools = Effective(work);
            return runners.Count(tags => Effective(tags).Intersect(pools).Any());
        }

        /// <summary>
        /// The tags of every approved Runner, to count against. One short read
        /// of one small table — there are tens of Runners, not thousands — and
        /// the alternative is a correlated count per round on a page that draws
        /// all of them.
        /// </summary>
        public static async Task<List<List<string>>> ApprovedPoolsAsync(
            ApplicationDbContext context, CancellationToken ct) =>
            await context.Runners.AsNoTracking()
                .Where(r => r.State == RunnerState.Approved)
                .Select(r => r.Tags)
                .ToListAsync(ct);

        /// <summary>
        /// The work's own tags, in SQL: a round's if it has any, otherwise its
        /// activity's, with the empty case spelled out.
        /// <para>
        /// A null on a round inherits the activity's; an activity's is never
        /// null, and an empty one on either side is the default pool.
        /// </para>
        /// <para>
        /// <b>A constant, and it has to be one.</b> Both callers hand it to
        /// <c>SqlQueryRaw</c>, and an analyzer refuses that for anything built at
        /// run time — rightly. Fixing the aliases here (<c>se</c> the round,
        /// <c>a</c> its activity) is what keeps the whole query constant, so
        /// nothing assembled from a value can ever reach the database.
        /// </para>
        /// <para>
        /// <b>Emptiness is asked with <c>cardinality</c>, never against a
        /// literal empty array.</b> These strings reach <c>SqlQueryRaw</c>, which
        /// runs them through <c>string.Format</c> first, so a pair of braces
        /// anywhere in the SQL is read as a malformed placeholder — and the claim
        /// then fails with a parse error that mentions no SQL at all.
        /// <c>COALESCE(…, 0)</c> also answers the trial that has no activity,
        /// where the array is null rather than empty.
        /// </para>
        /// </summary>
        public const string WorkTagsSql =
            "CASE WHEN COALESCE(cardinality(COALESCE(se.\"RunnerTags\", a.\"RunnerTags\")), 0) = 0 " +
            "THEN ARRAY['" + Default + "']::text[] " +
            "ELSE COALESCE(se.\"RunnerTags\", a.\"RunnerTags\") END";

        /// <summary>The same, where there is no round above the work — a trial.</summary>
        public const string TrialTagsSql =
            "CASE WHEN COALESCE(cardinality(a.\"RunnerTags\"), 0) = 0 " +
            "THEN ARRAY['" + Default + "']::text[] " +
            "ELSE a.\"RunnerTags\" END";

        /// <summary>
        /// Refuses a list a form should never have sent. The ceilings exist
        /// because these arrive in an array column that is read on every claim.
        /// </summary>
        public static List<string> Validated(IEnumerable<string>? tags, string field)
        {
            var normalised = Normalise(tags);
            if (normalised.Count > MaxTags)
            {
                throw new ValidationException(
                    $"{field} carries more than {MaxTags} tags", "runner.tags.count");
            }
            if (normalised.Any(tag => tag.Length > MaxLength))
            {
                throw new ValidationException(
                    $"A tag is longer than {MaxLength} characters", "runner.tags.length");
            }
            return normalised;
        }
    }
}
