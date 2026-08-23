using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// Several people competing as one.
    /// <para>
    /// A group belongs to <b>one activity</b> and is what competes inside it: it
    /// submits, it spends the submission allowance, it holds a row in the ranking,
    /// and its grade goes to every member. Somebody in a group does not appear in
    /// the ranking themselves — the group does, and that is the whole point of
    /// the entity.
    /// </para>
    /// <para>
    /// <b>Membership is on <see cref="Grant"/>, not here.</b> A grant is a
    /// person's assignment to an activity, and the schema already holds one grant
    /// per user per activity — so a nullable <c>GroupId</c> there <i>is</i> the
    /// rule "at most one group", with no constraint of its own to write or to
    /// forget.
    /// </para>
    /// <para>
    /// <b>A group of one is legitimate.</b> It is how a manager gives one person
    /// a name and a description in the ranking, and nothing here treats it as a
    /// special case.
    /// </para>
    /// </summary>
    public class ActivityGroup
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>What the ranking calls it.</summary>
        public required string Name { get; set; }

        /// <summary>
        /// A short line beside the name, shown in the ranking. A class, a school,
        /// a year — whatever the activity's organiser wants a reader to know
        /// about a row.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Kept out of results and out of the ranking.
        /// <para>
        /// The same rule <see cref="Grant.IsSystem"/> applies to a person, one
        /// level up: a jury member in the ranking beside the students is a bug,
        /// and so is a test group. <b>It still submits and still spends its
        /// allowance</b> — what it does not do is appear, which is what makes it
        /// useful for checking an activity from the inside while it runs.
        /// </para>
        /// </summary>
        public bool IsSystem { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Who is in it. One grant per person per activity.</summary>
        public ICollection<Grant> Members { get; set; } = new List<Grant>();
    }
}
