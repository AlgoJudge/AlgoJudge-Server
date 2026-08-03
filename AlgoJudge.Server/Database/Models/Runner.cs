using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A registered evaluation worker. A Runner self-registers its key and an
    /// administrator approves it; nothing is evaluated before approval.
    /// <para>
    /// The key is generated once at the Runner's first start and is immutable, so
    /// there is no rotation. A leaked key is revoked, the Runner's configuration
    /// is rolled, and it registers again as a new identity.
    /// </para>
    /// </summary>
    public class Runner
    {
        public Guid Id { get; set; } = Uuid.New();

        public required string Name { get; set; }

        /// <summary>Public half of the immutable key pair, as presented at registration.</summary>
        public required string PublicKey { get; set; }

        /// <summary>Stable digest of <see cref="PublicKey"/>; unique, and what an administrator approves.</summary>
        public required string Fingerprint { get; set; }

        public RunnerState State { get; set; } = RunnerState.PendingApproval;

        /// <summary>
        /// What this Runner can evaluate — problem types, tags, sandbox and host
        /// facts. Stored as <c>jsonb</c> and opaque: the Server matches jobs to
        /// Runners by comparison, not by understanding.
        /// </summary>
        public string Capabilities { get; set; } = "{}";

        public string? Version { get; set; }

        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public DateTime? ApprovedAt { get; set; }
        public string? ApprovedByUserId { get; set; }
        public DateTime? LastSeenAt { get; set; }

        public ICollection<EvaluationJob> Jobs { get; set; } = new List<EvaluationJob>();
    }
}
