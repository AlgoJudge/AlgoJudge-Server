using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// Stored bytes, and nothing about who they are for.
    /// <para>
    /// Bytes are <b>immutable</b>: there is no replace, and a corrected file is a
    /// new upload with a new id. That is what lets a pinned problem version mean
    /// something, and what licenses a permanent cache header.
    /// </para>
    /// <para>
    /// Ownership and scope live on <see cref="FileReference"/>, not here, because
    /// the same bytes may be referenced twice — carrying a figure forward into a
    /// new problem version is a second reference, not a second upload. A scope
    /// column here would force a copy just to change who may read it.
    /// </para>
    /// </summary>
    public class File
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>The name as uploaded, e.g. <c>main.cpp</c>. For a person to read.</summary>
        public required string Name { get; set; }

        public required string MimeType { get; set; }

        /// <summary>
        /// How many bytes there are, counted by the Server as it stored them.
        /// <para>
        /// Never asked of the backend at read time, and never taken from a
        /// caller: it is what makes a <c>Range</c> request answerable without a
        /// round trip, so it has to be the number this Server itself saw.
        /// </para>
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Which configured store holds these bytes.
        /// <para>
        /// <b>Names a store, not a kind of backend.</b> A deployment may run
        /// several of the same kind — two buckets, two volumes — and the day one
        /// of them is retired, "which files are still in there" has to be a
        /// question the database can answer. A column saying <c>s3</c> could not.
        /// </para>
        /// <para>
        /// The id is opaque and <b>permanent</b>: once a row names it, it may not
        /// leave the configuration and may never be reused for another physical
        /// location. Startup checks the first half of that; nothing but care
        /// checks the second.
        /// </para>
        /// </summary>
        public string StorageId { get; set; } = InitialStorageId;

        /// <summary>
        /// What every row was on the day storage became a choice. Existing rows
        /// are backfilled to it, and it is what a new row gets until the writer
        /// learns to ask the configuration — one step later, by design.
        /// </summary>
        public const string InitialStorageId = "pg";

        /// <summary>
        /// The store still holding a stale copy, while a migration is in flight.
        /// <c>NULL</c> at every other time.
        /// </summary>
        public string? PreviousStorageId { get; set; }

        /// <summary>
        /// When the stale copy may go.
        /// <para>
        /// A grace period rather than a delete in the same transaction, because a
        /// reader that resolved the row a moment ago is still holding the old
        /// <see cref="StorageId"/> — and the copy it is reading has to outlive
        /// that. The source copy goes after this passes, never before the target
        /// is committed.
        /// </para>
        /// </summary>
        public DateTime? PreviousCopyDeleteAfter { get; set; }

        /// <summary>
        /// Lowercase hexadecimal SHA-256 of the stored bytes.
        /// <para>
        /// <b>Half of the blob's address.</b> Since the bytes left this table it
        /// is not only a check but a coordinate: the store derives the path from
        /// it, so a row whose checksum was edited would point at nothing.
        /// </para>
        /// <para>
        /// The Client computes it, the Server <b>recomputes</b> it and refuses to
        /// store a mismatch (<c>422</c>, <c>checksum_mismatch</c>), and the Runner
        /// verifies it before evaluating. A checksum that arrives with the bytes
        /// is a claim, not evidence; what it buys is a truncated upload rejected
        /// as corrupt rather than judged as a wrong answer.
        /// </para>
        /// <para>The field is <c>sha256</c>, never <c>hash</c>.</para>
        /// </summary>
        public required string Sha256 { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Who uploaded it. A file that no reference points at is readable by
        /// this user alone — which is what makes the two-step publish safe:
        /// between the upload and the version being published, the bytes exist
        /// and nobody else can see them.
        /// </summary>
        public string? UploadedByUserId { get; set; }
        public User? UploadedBy { get; set; }

        public ICollection<FileReference> References { get; set; } = new List<FileReference>();
    }

    /// <summary>
    /// What a file is <b>for</b>: which owner holds it, under what name, and who
    /// may therefore read it.
    /// <para>
    /// <see cref="Scope"/> is the most security-relevant column in the schema —
    /// manager scope is where model solutions live — and it is checked when
    /// access is granted, never applied as a filter some later endpoint forgets.
    /// <c>GET /files/{id}</c> is allowed when <b>at least one reference to it is
    /// readable by the caller</b>.
    /// </para>
    /// <para>
    /// The owner is a discriminator beside typed foreign keys, not instead of
    /// them: exactly one owner column is set, and a check constraint holds it to
    /// agreeing with <see cref="OwnerKind"/>. A bare <c>(kind, id)</c> pair
    /// cannot be a foreign key, and this is the one table where a dangling row
    /// means handing somebody a model solution. The cost is stated plainly: a new
    /// owner kind is a migration, not a new enum value.
    /// </para>
    /// </summary>
    public class FileReference
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid FileId { get; set; }
        public File? File { get; set; }

        public FileOwnerKind OwnerKind { get; set; }

        /// <summary>
        /// Who may read the file through this reference — judged against the
        /// owner, so the same word means different audiences for different
        /// owners. Under a submission, <c>Participant</c> means <b>its author</b>:
        /// the word that publishes a statement to a whole activity publishes a
        /// compiler log to exactly one reader.
        /// </summary>
        public FileScope Scope { get; set; } = FileScope.Participant;

        /// <summary>
        /// The name <b>within the owner</b> — <c>content.md</c>, <c>source</c>,
        /// <c>log</c>, <c>details</c>, <c>package.zip</c>. This is what the
        /// activity's <see cref="AttachmentRule"/> table keys on, never the
        /// uploaded file name.
        /// </summary>
        public required string Name { get; set; }

        /// <summary>BCP-47 subtag, where an owner keeps one document per language. Null is the default one.</summary>
        public string? Language { get; set; }

        /// <summary>
        /// When this revision came into force, on an instance or activity
        /// document. The reader is served the newest revision whose date has
        /// passed, which also lets an operator publish terms ahead of the day
        /// they take effect.
        /// </summary>
        public DateTime? ValidFrom { get; set; }

        /// <summary>
        /// When a newer revision replaced this one.
        /// <para>
        /// Marked, never deleted. Deleting it would leave the file it names with
        /// no reference at all, and an unreferenced file goes in twenty-four
        /// hours — which would make the thirty-day and forever rules unreachable.
        /// </para>
        /// </summary>
        public DateTime? SupersededAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Exactly one of these is set, and it agrees with OwnerKind.
        // CK_FileReferences_SingleOwner holds both halves of that sentence.

        public Guid? ProblemVersionId { get; set; }
        public ProblemVersion? ProblemVersion { get; set; }

        public Guid? ActivityId { get; set; }
        public Activity? Activity { get; set; }

        public Guid? SubmissionId { get; set; }
        public Submission? Submission { get; set; }

        public Guid? EvaluationJobId { get; set; }
        public EvaluationJob? EvaluationJob { get; set; }

        public Guid? RunnerId { get; set; }
        public Runner? Runner { get; set; }

        /// <summary>Set for an instance document or the instance logo — the singleton row.</summary>
        public Guid? InstanceId { get; set; }
        public Instance? Instance { get; set; }
    }
}
