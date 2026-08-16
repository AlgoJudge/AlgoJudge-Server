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
    /// <para>
    /// Almost everything here is reported by the Runner itself. The two
    /// exceptions are <see cref="Tags"/>, which an operator sets, and
    /// <see cref="Address"/>, which the Server reads from the connection — a
    /// machine is a bad witness to how it is reached, and a Runner behind a proxy
    /// that reported its own address would record the proxy.
    /// </para>
    /// </summary>
    public class Runner
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>Reported by the Runner. Not unique, and not an identifier.</summary>
        public required string Name { get; set; }

        /// <summary>Which Runner implementation this is, e.g. <c>AlgoJudge-Runner-Judge0</c>.</summary>
        public string? Product { get; set; }

        public string? Version { get; set; }

        /// <summary>Public half of the immutable key pair, as presented at registration.</summary>
        public required string PublicKey { get; set; }

        /// <summary>Stable digest of <see cref="PublicKey"/>; unique, and what an administrator approves.</summary>
        public required string Fingerprint { get; set; }

        public RunnerState State { get; set; } = RunnerState.PendingApproval;

        /// <summary>
        /// The problem types this Runner can evaluate, as discriminators.
        /// <para>
        /// A column because the Server matches jobs against it, and it matches
        /// <b>by equality only</b> — it never parses a discriminator, never
        /// compares versions, never decides that one type is like another. That
        /// is what keeps "adding a problem type is not a Server change" true
        /// while still letting the Server route work.
        /// </para>
        /// </summary>
        public List<string> ProblemTypes { get; set; } = [];

        /// <summary>
        /// Whether this Runner sends submissions outside the installation.
        /// <para>
        /// Reported by the Runner, like almost everything else here, and taken
        /// at its word — a Runner that lied about this would be a Runner an
        /// administrator approved, which is a trust decision made earlier and by
        /// a person.
        /// </para>
        /// <para>
        /// It exists so the Server can pair work with a worker on <b>one
        /// comparison of two booleans</b>: external problems go to external
        /// Runners and local problems do not, without the Server ever reading a
        /// problem type or knowing which service is on the other end. It also
        /// gives an administrator the fact at approval time, which is when it
        /// matters.
        /// </para>
        /// </summary>
        public bool External { get; set; }

        /// <summary>Free labels an operator sets, e.g. <c>gpu</c>, <c>slow</c>.</summary>
        public List<string> Tags { get; set; } = [];

        /// <summary>
        /// Where the Server saw the connection come from. Read from the request,
        /// never reported, and correct only when <c>ForwardedHeaders</c> is
        /// configured — without it every Runner behind a proxy records the proxy.
        /// </summary>
        public string? Address { get; set; }

        /// <summary>
        /// Host facts the panel shows and the Server never interprets: operating
        /// system, processor, cores, memory. Opaque <c>jsonb</c>, null until the
        /// Runner has connected once.
        /// </summary>
        public string? Machine { get; set; }

        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public DateTime? ApprovedAt { get; set; }
        public string? ApprovedByUserId { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevokedReason { get; set; }
        public DateTime? LastSeenAt { get; set; }

        /// <summary>How many jobs this Runner has carried to a verdict.</summary>
        public int CompletedJobs { get; set; }

        public ICollection<EvaluationJob> Jobs { get; set; } = new List<EvaluationJob>();

        /// <summary>
        /// What the Runner uploaded about itself — <c>runner.log</c>,
        /// <c>lscpu.txt</c>. Ordinary files through the ordinary File API; the
        /// panel renders every text attachment it finds, so the Server needs no
        /// vocabulary for them.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();
    }
}
