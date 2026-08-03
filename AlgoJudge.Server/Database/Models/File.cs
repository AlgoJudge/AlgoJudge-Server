using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A stored file. The Server handles scoped bytes and never looks inside
    /// them: participant material, manager material such as a model solution,
    /// and the archive a Runner evaluates against are all rows here, told apart
    /// by <see cref="Scope"/>.
    /// <para>
    /// <see cref="Scope"/> is the most security-relevant column in the schema —
    /// manager scope is where model solutions live — so it must be checked when
    /// access is granted, not merely applied as a filter that some later
    /// endpoint forgets.
    /// </para>
    /// </summary>
    public class File
    {
        public Guid Id { get; set; } = Uuid.New();

        public required string Name { get; set; }
        public required string MimeType { get; set; }
        public required byte[] Content { get; set; }

        public long SizeBytes { get; set; }

        /// <summary>Lowercase hexadecimal SHA-256 of <see cref="Content"/>.</summary>
        public required string Sha256 { get; set; }

        public FileScope Scope { get; set; } = FileScope.Participant;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Owner. Exactly one of these is set: a file belongs either to a problem
        /// version or to a submission, and a rules document belongs to an
        /// activity through <see cref="Activity.RulesFileId"/>.
        /// </summary>
        public Guid? ProblemVersionId { get; set; }
        public ProblemVersion? ProblemVersion { get; set; }

        public Guid? SubmissionId { get; set; }
        public Submission? Submission { get; set; }
    }
}
