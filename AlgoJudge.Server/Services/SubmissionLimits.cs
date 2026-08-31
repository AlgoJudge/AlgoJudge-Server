using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// The three limits the Server itself enforces on a submission, checked at
    /// the point a manager sets them.
    /// <para>
    /// <b>They are explicit columns and not part of the opaque configuration</b>
    /// — decided 2026-08-04, on the grounds that the Server cannot police what
    /// it cannot read. <c>SubmissionService</c> applies all three before a
    /// submission is committed. What was missing is the other half: nothing
    /// checked the value being written, so a number the product cannot honour
    /// was accepted into the panel, projected to the manager who typed it, and
    /// then contradicted at the moment somebody tried to submit.
    /// </para>
    /// <para>
    /// The same shape as the <c>MaxAttachments</c> amendment of 2026-08-22 —
    /// stored, projected, editable, unpoliced — one layer up.
    /// </para>
    /// <para>
    /// Checked on <b>every</b> path that writes them, which is four: creating an
    /// activity, editing one, attaching a problem, and editing an assignment.
    /// <c>CheckMaxPoints</c> states the reason and had to be duplicated to
    /// obey it — "created by attaching and changed by editing, and a rule
    /// enforced on the first alone is a rule the second removes". This is one
    /// place instead, so a fifth path has one call to add rather than a copy.
    /// </para>
    /// </summary>
    public static class SubmissionLimits
    {
        /// <summary>
        /// Refuses the values a manager cannot mean.
        /// <para>
        /// <paramref name="subject"/> is <c>activity</c> or <c>assignment</c>,
        /// and names the half of the error code that says which form was wrong.
        /// A null is always allowed where the column is nullable: it means
        /// "inherit" on an assignment and "unlimited" for a submission count,
        /// and neither is a value to police.
        /// </para>
        /// </summary>
        public static void Check(
            long? maxUploadBytes,
            int? maxAttachments,
            int? maxSubmissions,
            string subject)
        {
            // **Bounded above by the outer wall, which is not this file's
            // opinion.** `UploadLimits.Submission` is what the endpoint refuses
            // at, before any of this runs, and its own comment says an activity
            // "may set something smaller still". Larger is a number the panel
            // would show and the product would not honour: the request dies at
            // the framework with a message about the request body, and the
            // manager's own ceiling is never consulted.
            if (maxUploadBytes is { } upload)
            {
                if (upload <= 0)
                {
                    throw new ValidationException(
                        $"An upload ceiling of {upload} bytes accepts nothing at all",
                        $"{subject}.maxUploadBytes.invalid");
                }
                if (upload > UploadLimits.Submission)
                {
                    throw new ValidationException(
                        $"An upload ceiling of {upload} bytes is above the {UploadLimits.Submission} "
                            + "this Server accepts, so nothing would ever reach it",
                        $"{subject}.maxUploadBytes.tooLarge");
                }
            }

            // **Zero is a setting and not a mistake here**, unlike the two
            // beside it: `SubmissionService` reads it and refuses with "This
            // problem accepts no attachments", which is a problem answered
            // without a file. Negative is the only thing to refuse.
            if (maxAttachments is { } attachments && attachments < 0)
            {
                throw new ValidationException(
                    $"A submission cannot carry {attachments} files",
                    $"{subject}.maxAttachments.invalid");
            }

            // **Zero closes the problem silently.** The count is compared as
            // `used >= limit`, so a zero refuses the first attempt and every
            // one after it, with a message about having no submissions left
            // rather than about the problem being shut. A manager closing a
            // problem has the series window and the archive for it; null is
            // how they say "as many as they like".
            if (maxSubmissions is { } submissions && submissions <= 0)
            {
                throw new ValidationException(
                    $"A limit of {submissions} submissions lets nobody submit; leave it empty for no limit",
                    $"{subject}.maxSubmissions.invalid");
            }
        }
    }
}
