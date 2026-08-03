namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// Who a stored file is for. The Server enforces this and never looks inside
    /// the bytes: participant material, manager material such as a model
    /// solution, and the archive a Runner needs to evaluate.
    /// </summary>
    public enum FileScope
    {
        Participant = 0,
        Manager = 1,
        Runner = 2,
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

    public enum QuestionKind
    {
        Question = 0,
        Announcement = 1,
    }

    public enum ScoreVisibility
    {
        Everyone = 0,
        ParticipantOnly = 1,
        ManagersOnly = 2,
    }

    public enum LogVisibility
    {
        ManagersOnly = 0,
        Participant = 1,
    }

    public enum JoinPolicy
    {
        /// <summary>Only a manager may enrol someone.</summary>
        Closed = 0,
        /// <summary>Enrolment by invitation or join code.</summary>
        Invitation = 1,
        /// <summary>Listed to everyone and open to join.</summary>
        Open = 2,
    }
}
