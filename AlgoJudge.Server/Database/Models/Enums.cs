namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// Who a stored file is for.
    /// <para>
    /// The scope lives on <see cref="FileReference"/> rather than on the bytes,
    /// because the same bytes may be referenced twice — a figure carried forward
    /// into a new problem version is one file and two references. Putting the
    /// scope on the file would force a copy to change who may read it.
    /// </para>
    /// </summary>
    public enum FileScope
    {
        Participant = 0,
        Manager = 1,
        Runner = 2,
    }

    /// <summary>
    /// What a <see cref="FileReference"/> hangs off.
    /// <para>
    /// A discriminator beside typed foreign keys, not instead of them: exactly
    /// one owner column is set and a check constraint holds it to agreeing with
    /// this value. A bare <c>(ownerKind, ownerId)</c> pair cannot be a foreign
    /// key, and this is the table where a dangling row means handing out a model
    /// solution.
    /// </para>
    /// </summary>
    public enum FileOwnerKind
    {
        ProblemVersion = 0,
        ActivityDocument = 1,
        InstanceDocument = 2,
        InstanceLogo = 3,
        Runner = 4,
        Submission = 5,
        /// <summary>One evaluation attempt: its log, its per-test document.</summary>
        Attempt = 6,
    }

    public enum EvaluationJobState
    {
        Queued = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4,
    }

    /// <summary>
    /// A Runner self-registers and an administrator approves it. Its key is
    /// immutable, so a compromised Runner is revoked rather than rotated.
    /// </summary>
    public enum RunnerState
    {
        PendingApproval = 0,
        Approved = 1,
        Revoked = 2,
    }

    /// <summary>
    /// Who can see a problem in the library. Private is the default: a manager's
    /// drafts are not everyone's business.
    /// </summary>
    public enum ProblemVisibility
    {
        Private = 0,
        /// <summary>Visible to the users listed on the problem.</summary>
        Shared = 1,
        /// <summary>Visible to everyone in the installation.</summary>
        Instance = 2,
    }

    public enum QuestionKind
    {
        Question = 0,
        Announcement = 1,
    }

    /// <summary>
    /// Who may see scores — and, by the same answer, who may see the ranking.
    /// One setting rather than two: a board switched on where nobody may see a
    /// score shows nothing.
    /// </summary>
    public enum ScoreVisibility
    {
        Everyone = 0,
        /// <summary>The reader's own results, and no placing.</summary>
        ParticipantOnly = 1,
        ManagersOnly = 2,
    }

    /// <summary>
    /// Who reads one named attachment on a submission.
    /// <para>
    /// <see cref="Participant"/> means whoever may read the submission — its
    /// author and the managers — not every participant of the activity.
    /// A name with no rule is <see cref="ManagersOnly"/>; see
    /// <see cref="Activity.AttachmentVisibility"/>.
    /// </para>
    /// </summary>
    public enum AttachmentVisibility
    {
        ManagersOnly = 0,
        Participant = 1,
    }

    /// <summary>
    /// How somebody gets into an activity without a manager doing it for them.
    /// A manager may always enrol by hand — that is what a grant is — so these
    /// are the three answers to <b>self</b>-enrolment and nothing else.
    /// </summary>
    public enum JoinPolicy
    {
        /// <summary>No self-enrolment. Only a manager may enrol someone.</summary>
        Closed = 0,
        /// <summary>Self-enrolment on giving the activity's join password.</summary>
        Password = 1,
        /// <summary>Self-enrolment, no password.</summary>
        Open = 2,
    }

    /// <summary>
    /// Which document an owner publishes. The kinds an instance may publish and
    /// the kinds an activity may publish overlap deliberately — they are the
    /// same mechanism with two owners.
    /// </summary>
    public enum DocumentKind
    {
        /// <summary>What somebody not enrolled, or not signed in, reads.</summary>
        Welcome = 0,
        /// <summary>The landing page for somebody who is in.</summary>
        Home = 1,
        /// <summary>Regulations; what an enrolment form asks acceptance of.</summary>
        Rules = 2,
        Terms = 3,
        Privacy = 4,
        Cookies = 5,
        Accessibility = 6,
    }
}
