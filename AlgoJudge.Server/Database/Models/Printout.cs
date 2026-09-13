using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A page of source somebody asked to have on paper, and the queue row an
    /// operator works from.
    /// <para>
    /// <b>The source is a file, not a column here.</b> It arrives as bytes with a
    /// checksum, like everything else a participant sends, and it is owned
    /// through a <see cref="FileReference"/> with
    /// <see cref="FileOwnerKind.Printout"/>. That is what makes disposal
    /// possible: resolving the request removes the reference, and
    /// <c>FileService.DeleteUnreferencedAsync</c> takes the bytes with it.
    /// A text column would copy every printed solution into a second table,
    /// unchecksummed, and into every backup for ever.
    /// </para>
    /// <para>
    /// <see cref="ActivityId"/> is always present, so authorisation never has to
    /// walk a chain upwards to find out where a row belongs — the same reason
    /// <see cref="Question"/> carries one.
    /// </para>
    /// </summary>
    public class Printout
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>
        /// Where the text came from, when it came from a submission's source
        /// view. Provenance for the sheet and nothing more: the bytes are the
        /// printout's own, because the source view is tabbed and a submission may
        /// be an archive, so "the submission" does not name one page of text.
        /// </summary>
        public Guid? SubmissionId { get; set; }
        public Submission? Submission { get; set; }

        public required string RequestedByUserId { get; set; }
        public User? RequestedBy { get; set; }

        /// <summary>
        /// The group as it was when the request was made.
        /// <para>
        /// Stamped, never resolved at print time, for the reason
        /// <see cref="Submission.GroupId"/> gives: a manager may move somebody
        /// between groups, and the panel promises that doing so moves nothing
        /// already sent. A sheet that named the group somebody is in *now* would
        /// contradict the paper printed an hour ago.
        /// </para>
        /// </summary>
        public Guid? GroupId { get; set; }
        public ActivityGroup? Group { get; set; }

        /// <summary>What the requester called it, when they said.</summary>
        public string? Title { get; set; }

        public required string FileName { get; set; }

        /// <summary>
        /// Over the bytes, recomputed by the Server before storing. Printed in
        /// the sheet's footer, so a page on a desk can be matched to a row.
        /// </summary>
        public required string Sha256 { get; set; }

        public long SizeBytes { get; set; }

        public PrintoutState State { get; set; } = PrintoutState.Requested;

        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Who took it to a printer, and when. Cleared when they hand it back.
        /// </summary>
        public string? ClaimedByUserId { get; set; }
        public User? ClaimedBy { get; set; }
        public DateTime? ClaimedAt { get; set; }

        public DateTime? ResolvedAt { get; set; }
        public string? ResolvedByUserId { get; set; }
        public User? ResolvedBy { get; set; }

        /// <summary>
        /// When the source stopped being available, which is the same moment the
        /// request was resolved. Kept as its own column rather than inferred from
        /// <see cref="State"/>, because the row outlives the bytes and has to be
        /// able to say so: the endpoint answers 200 with no source, not 404.
        /// </summary>
        public DateTime? SourceDisposedAt { get; set; }

        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();
    }
}
