using System.Security.Cryptography;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using DbFile = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Stored bytes: one way in, one way out, and one rule about who may read.
    /// </summary>
    public interface IFileService
    {
        Task<DbFile> StoreAsync(Stream content, string name, string mimeType, string declaredSha256, CancellationToken ct);
        Task<DbFile?> FindAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Whether the caller may read these bytes, through <b>any</b> reference
        /// pointing at them.
        /// </summary>
        Task<bool> CanReadAsync(Guid fileId, CancellationToken ct);

        /// <summary>
        /// Whether the answer does not depend on who is asking.
        /// <para>
        /// True of an instance document and the logo, and of nothing else — they
        /// are what a signed-out screen renders. It decides one thing: whether a
        /// shared cache may keep a copy.
        /// </para>
        /// </summary>
        Task<bool> IsPublicAsync(Guid fileId, CancellationToken ct);

        /// <summary>Lowercase hexadecimal SHA-256, the one way it is written.</summary>
        static string Checksum(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public class FileService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions
    ) : IFileService
    {
        public async Task<DbFile> StoreAsync(
            Stream content, string name, string mimeType, string declaredSha256, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            // Recomputed, not trusted. A checksum that arrives with the bytes is
            // a claim; what recomputing buys is a truncated upload rejected as
            // corrupt rather than stored as a file whose contents are wrong.
            var actual = IFileService.Checksum(bytes);
            if (!string.Equals(actual, declaredSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ChecksumMismatchException(
                    $"The file did not match its checksum and was not stored: {name}");
            }

            var file = new DbFile
            {
                Name = name,
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
                Content = bytes,
                SizeBytes = bytes.LongLength,
                Sha256 = actual,
                UploadedByUserId = currentUser.UserId,
            };
            context.Files.Add(file);
            await context.SaveChangesAsync(ct);
            return file;
        }

        public Task<DbFile?> FindAsync(Guid id, CancellationToken ct) =>
            context.Files.FirstOrDefaultAsync(f => f.Id == id, ct);

        /// <summary>
        /// The read rule from `FILE_API.md`, in one place.
        /// <para>
        /// Allowed when at least one reference is readable, judged by that
        /// reference's owner and scope. Written as an authorization question
        /// rather than as a filter on a listing, because a filter is the thing a
        /// later endpoint forgets — and the file this guards may be a model
        /// solution.
        /// </para>
        /// </summary>
        public async Task<bool> CanReadAsync(Guid fileId, CancellationToken ct)
        {
            var userId = currentUser.UserId;

            var references = await context.FileReferences
                .AsNoTracking()
                .Where(r => r.FileId == fileId)
                .ToListAsync(ct);

            if (references.Count == 0)
            {
                // A file nothing points at is readable only by whoever uploaded
                // it. This is what makes the two-step publish safe: between the
                // upload and the version being published, the bytes exist and
                // nobody else can see them.
                var file = await context.Files.AsNoTracking()
                    .FirstOrDefaultAsync(f => f.Id == fileId, ct);
                return file is not null && userId is not null && file.UploadedByUserId == userId;
            }

            foreach (var reference in references)
            {
                if (await CanReadThroughAsync(reference, userId, ct)) return true;
            }
            return false;
        }

        public Task<bool> IsPublicAsync(Guid fileId, CancellationToken ct) =>
            context.FileReferences
                .AsNoTracking()
                .AnyAsync(r => r.FileId == fileId
                    && (r.OwnerKind == FileOwnerKind.InstanceDocument
                        || r.OwnerKind == FileOwnerKind.InstanceLogo), ct);

        private async Task<bool> CanReadThroughAsync(FileReference reference, string? userId, CancellationToken ct)
        {
            switch (reference.OwnerKind)
            {
                // An instance document and the logo are readable by anybody,
                // signed in or not — they are what a signed-out screen renders.
                case FileOwnerKind.InstanceDocument:
                case FileOwnerKind.InstanceLogo:
                    return true;

                case FileOwnerKind.ActivityDocument:
                    return reference.ActivityId is { } activityDocumentActivity
                        && await CanReadActivityAsync(activityDocumentActivity, reference.Scope, ct);

                case FileOwnerKind.ProblemVersion:
                    return await CanReadProblemVersionAsync(reference, ct);

                case FileOwnerKind.Submission:
                    return reference.SubmissionId is { } submissionId
                        && await CanReadSubmissionFileAsync(submissionId, reference.Name, reference.Scope, userId, ct);

                case FileOwnerKind.Attempt:
                    return reference.EvaluationJobId is { } jobId
                        && await CanReadAttemptFileAsync(jobId, reference.Name, reference.Scope, userId, ct);

                case FileOwnerKind.Runner:
                    // A Runner's own diagnostics are operator material.
                    return await permissions.HasAsync(Authorization.Permissions.RunnerRead, null, ct);

                default:
                    return false;
            }
        }

        private async Task<bool> CanReadActivityAsync(Guid activityId, FileScope scope, CancellationToken ct)
        {
            if (scope == FileScope.Manager)
            {
                return await permissions.HasAsync(Authorization.Permissions.ActivityUpdate, activityId, ct);
            }
            // A welcome page is what somebody not enrolled reads, so an activity
            // document under participant scope is readable by anyone who may see
            // the activity at all.
            return await permissions.HasAsync(Authorization.Permissions.ActivityRead, activityId, ct)
                || await IsListedAsync(activityId, ct);
        }

        private async Task<bool> IsListedAsync(Guid activityId, CancellationToken ct)
        {
            var activity = await context.Activities.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == activityId, ct);
            return activity is not null && !activity.Unlisted && activity.JoinPolicy != JoinPolicy.Closed;
        }

        private async Task<bool> CanReadProblemVersionAsync(FileReference reference, CancellationToken ct)
        {
            if (reference.ProblemVersionId is not { } versionId) return false;

            // Manager scope is where a model solution lives. Never a participant,
            // never a Runner reading it as a participant would.
            if (reference.Scope == FileScope.Manager)
            {
                return await permissions.HasAsync(Authorization.Permissions.ProblemUpdate, null, ct);
            }

            // Runner scope: the package. Readable by a Runner holding a job for
            // this very version — authorized against the job, not against being
            // a Runner — and by managers.
            if (reference.Scope == FileScope.Runner)
            {
                return await permissions.HasAsync(Authorization.Permissions.ProblemUpdate, null, ct);
            }

            // Participant scope: readable from any assignment of this version in
            // an activity the caller may read.
            var activityIds = await context.SeriesProblems.AsNoTracking()
                .Where(sp => sp.PinnedProblemVersionId == versionId
                    || context.ProblemVersions.Any(v => v.Id == versionId && v.ProblemId == sp.ProblemId))
                .Select(sp => sp.ActivityId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var activityId in activityIds)
            {
                if (await permissions.HasAsync(Authorization.Permissions.ActivityRead, activityId, ct)) return true;
            }

            return await permissions.HasAsync(Authorization.Permissions.ProblemReadAll, null, ct);
        }

        private async Task<bool> CanReadSubmissionFileAsync(
            Guid submissionId, string name, FileScope scope, string? userId, CancellationToken ct)
        {
            var submission = await context.Submissions.AsNoTracking()
                .Include(s => s.SeriesProblem)
                .FirstOrDefaultAsync(s => s.Id == submissionId, ct);
            if (submission?.SeriesProblem is null) return false;

            var activityId = submission.SeriesProblem.ActivityId;

            if (await permissions.HasAsync(Authorization.Permissions.SubmissionSourceReadAll, activityId, ct)) return true;
            if (scope == FileScope.Manager) return false;

            // Under a submission, participant scope means its author — and only
            // if the activity's table admits this name.
            if (submission.UserId != userId) return false;
            return await NameIsPublicAsync(activityId, name, ct);
        }

        private async Task<bool> CanReadAttemptFileAsync(
            Guid jobId, string name, FileScope scope, string? userId, CancellationToken ct)
        {
            var job = await context.EvaluationJobs.AsNoTracking()
                .Include(j => j.Submission)!.ThenInclude(s => s!.SeriesProblem)
                .FirstOrDefaultAsync(j => j.Id == jobId, ct);
            var submission = job?.Submission;
            if (submission?.SeriesProblem is null) return false;

            var activityId = submission.SeriesProblem.ActivityId;

            if (await permissions.HasAsync(Authorization.Permissions.ResultLogReadAll, activityId, ct)) return true;
            if (scope == FileScope.Manager) return false;
            if (submission.UserId != userId) return false;
            return await NameIsPublicAsync(activityId, name, ct);
        }

        /// <summary>
        /// Whether the activity lets a participant read this attachment name.
        /// <b>A name with no row is managers-only</b> — a Runner that starts
        /// attaching something new must not publish it by arriving.
        /// </summary>
        private async Task<bool> NameIsPublicAsync(Guid activityId, string name, CancellationToken ct)
        {
            var rule = await context.AttachmentRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ActivityId == activityId && r.Name == name, ct);
            return rule is not null && rule.Visibility == AttachmentVisibility.Participant;
        }
    }
}
