using System.Security.Cryptography;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Storage;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using DbFile = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Bytes that are in a store and in no row.
    /// <para>
    /// The state between §7's step 5 and step 6, made explicit because a
    /// multipart upload really is in it for a while: the file has gone past, and
    /// the field saying what it should have hashed to has not arrived yet.
    /// </para>
    /// </summary>
    public record StagedBytes
    {
        /// <summary>Where they are, addressed by what they turned out to be.</summary>
        public required BlobKey Key { get; init; }

        public required long SizeBytes { get; init; }

        /// <summary>Which store took them — the value the row will carry for ever.</summary>
        public required string StoreId { get; init; }
    }

    /// <summary>
    /// Stored bytes: one way in, one way out, and one rule about who may read.
    /// </summary>
    public interface IFileService
    {
        /// <summary>
        /// Writes bytes and commits the row, for a caller that already holds
        /// everything. Staging and committing in one call.
        /// </summary>
        Task<DbFile> StoreAsync(Stream content, string name, string mimeType, string declaredSha256, CancellationToken ct);

        /// <summary>
        /// Puts the bytes down and answers what they turned out to be.
        /// <para>
        /// <b>Nothing points at them yet.</b> Splitting this from the commit is
        /// what lets an upload be read in one pass while the fields that describe
        /// it are still arriving: a multipart body may carry <c>sha256</c> after
        /// the file, and the Client's own form does exactly that. The alternative
        /// is buffering the file until the last field shows up, which is the one
        /// thing this design exists to avoid.
        /// </para>
        /// <para>
        /// A staged blob that is never committed is an orphan, and orphans are
        /// already somebody's job: the collector takes them after twenty-four
        /// hours whether or not <see cref="DiscardAsync"/> was reached.
        /// </para>
        /// </summary>
        Task<StagedBytes> StageAsync(Stream content, CancellationToken ct);

        /// <summary>
        /// Turns staged bytes into a file, or refuses them.
        /// <para>
        /// This is where the declared checksum is finally answered: a mismatch
        /// discards the blob and throws <c>422</c>, so nothing is stored — no
        /// row, and nothing left for the collector to find either.
        /// </para>
        /// </summary>
        Task<DbFile> CommitAsync(
            StagedBytes staged, string name, string mimeType, string declaredSha256, CancellationToken ct);

        /// <summary>Throws staged bytes away. Idempotent, and safe to call twice.</summary>
        Task DiscardAsync(StagedBytes staged, CancellationToken ct);

        Task<DbFile?> FindAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// The bytes, as a stream nobody has to hold.
        /// <para>
        /// <b>Seekable, and that is load-bearing</b>: ASP.NET Core answers a
        /// <c>Range</c> request with <c>206</c> only from a seekable stream, and
        /// silently serves the whole file otherwise.
        /// </para>
        /// <para>
        /// Answers <c>503</c> — never <c>404</c> — when the row names a store this
        /// installation is not configured for. The file exists; this Server
        /// cannot reach it. Saying "not found" would be a claim about somebody
        /// else's data that happens to be false.
        /// </para>
        /// </summary>
        Task<Stream> OpenAsync(DbFile file, CancellationToken ct);

        /// <summary>
        /// Removes bytes nothing points at. **Refuses anything referenced.**
        /// <para>
        /// Narrow on purpose, and this is the only place the product deletes a
        /// stored file at all. It exists for D-12: a trial's package does not
        /// survive the trial, because a trial is a question rather than content
        /// and there is nothing to come back to.
        /// </para>
        /// <para>
        /// A file with a <see cref="FileReference"/> is somebody's attachment,
        /// statement or package, and deleting it would leave a row pointing at
        /// nothing. The refusal is silent — <c>false</c> rather than an
        /// exception — because the caller is a cleanup path and a trial whose
        /// bytes outlive it is untidy, not broken.
        /// </para>
        /// </summary>
        Task<bool> DeleteUnreferencedAsync(Guid fileId, CancellationToken ct);

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
        IPermissionService permissions,
        ISeriesLockdown lockdown,
        IBlobStoreRegistry stores
    ) : IFileService
    {
        /// <summary>
        /// Writes the bytes, then the row — in that order, and the order is the
        /// point.
        /// <para>
        /// A crash anywhere before the row commits leaves bytes nobody points at,
        /// which the collector removes twenty-four hours later. The other order
        /// would leave a row promising bytes that are not there, and nothing in
        /// the product could tell that from corruption.
        /// </para>
        /// </summary>
        public async Task<DbFile> StoreAsync(
            Stream content, string name, string mimeType, string declaredSha256, CancellationToken ct)
        {
            var staged = await StageAsync(content, ct);
            return await CommitAsync(staged, name, mimeType, declaredSha256, ct);
        }

        public async Task<StagedBytes> StageAsync(Stream content, CancellationToken ct)
        {
            var store = stores.Default;
            var fileId = Uuid.New();

            // Hashed while it is written, in one pass, by the store — so the
            // bytes are never held anywhere in order to be checked afterwards.
            var written = await store.WriteAsync(fileId, content, ct);

            return new StagedBytes
            {
                Key = new BlobKey(fileId, written.Sha256),
                SizeBytes = written.SizeBytes,
                StoreId = store.Id,
            };
        }

        public async Task<DbFile> CommitAsync(
            StagedBytes staged, string name, string mimeType, string declaredSha256, CancellationToken ct)
        {
            var declared = Normalized(declaredSha256, name);

            // Recomputed, not trusted. A checksum that arrives with the bytes is
            // a claim; what recomputing buys is a truncated upload rejected as
            // corrupt rather than stored as a file whose contents are wrong.
            if (!string.Equals(staged.Key.Sha256, declared, StringComparison.Ordinal))
            {
                // Nothing else knows this key, so removing it here is the whole
                // of "stores nothing": no row was written, and no reference could
                // have been taken to one that does not exist.
                await DiscardAsync(staged, ct);
                throw new ChecksumMismatchException(
                    $"The file did not match its checksum and was not stored: {name}");
            }

            var file = new DbFile
            {
                Id = staged.Key.FileId,
                Name = name,
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
                SizeBytes = staged.SizeBytes,
                Sha256 = staged.Key.Sha256,
                StorageId = staged.StoreId,
                UploadedByUserId = currentUser.UserId,
            };
            context.Files.Add(file);
            await context.SaveChangesAsync(ct);
            return file;
        }

        public async Task DiscardAsync(StagedBytes staged, CancellationToken ct)
        {
            if (stores.Find(staged.StoreId) is { } store) await store.DeleteAsync(staged.Key, ct);
        }

        /// <summary>
        /// A declared checksum, or a refusal.
        /// <para>
        /// Checked for <b>shape</b> before it is used, because it is about to
        /// decide where the blob is written: a short or non-hexadecimal value
        /// would either throw somewhere less legible or, worse, land every such
        /// upload in one directory. A malformed checksum cannot match what the
        /// bytes hash to, so the answer is the same 422 either way — it just
        /// arrives before the bytes are read rather than after.
        /// </para>
        /// </summary>
        private static string Normalized(string? declared, string name)
        {
            var value = declared?.Trim().ToLowerInvariant() ?? "";

            if (value.Length != 64 || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new ChecksumMismatchException(
                    $"The file did not arrive with a usable checksum and was not stored: {name}");
            }

            return value;
        }

        public Task<DbFile?> FindAsync(Guid id, CancellationToken ct) =>
            context.Files.FirstOrDefaultAsync(f => f.Id == id, ct);

        public Task<Stream> OpenAsync(DbFile file, CancellationToken ct)
        {
            var store = StoreFor(file);
            var key = new BlobKey(file.Id, file.Sha256);

            // The length is what the Server counted while storing the bytes, so
            // a range can be served without asking the backend how big the blob
            // is — one fewer round trip, and one fewer thing to disagree.
            return Task.FromResult<Stream>(new BlobStream(
                (offset, length, token) => store.OpenReadAsync(key, offset, length, token),
                file.SizeBytes));
        }

        /// <summary>
        /// The store a row names, or a refusal that does not name it.
        /// <para>
        /// The message says nothing about which store, what kind, or where — a
        /// public error that named a bucket would be the one place this product
        /// discloses its own infrastructure (A65c).
        /// </para>
        /// </summary>
        private IBlobStore StoreFor(DbFile file) =>
            stores.Find(file.StorageId)
            ?? throw new StorageUnavailableException();

        public async Task<bool> DeleteUnreferencedAsync(Guid fileId, CancellationToken ct)
        {
            if (await context.FileReferences.AnyAsync(r => r.FileId == fileId, ct)) return false;

            var file = await context.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct);
            if (file is null) return false;

            // The blob first. A row removed while its bytes stay is a leak
            // nothing will ever find again — the row was the only thing that knew
            // where they were.
            if (stores.Find(file.StorageId) is { } store)
            {
                await store.DeleteAsync(new BlobKey(file.Id, file.Sha256), ct);
            }

            context.Files.Remove(file);
            await context.SaveChangesAsync(ct);
            return true;
        }

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
                        || r.OwnerKind == FileOwnerKind.InstanceLogo
                        || r.OwnerKind == FileOwnerKind.InstanceTheme
                        || r.OwnerKind == FileOwnerKind.InstanceFont), ct);

        private async Task<bool> CanReadThroughAsync(FileReference reference, string? userId, CancellationToken ct)
        {
            switch (reference.OwnerKind)
            {
                // An instance document and the logo are readable by anybody,
                // signed in or not — they are what a signed-out screen renders.
                // So are the theme and its faces: the sign-in screen is drawn in
                // the operator's colours and typeface before anybody has signed
                // in, and the theme file holds nothing that is not already on
                // `/instance` for the same readers.
                case FileOwnerKind.InstanceDocument:
                case FileOwnerKind.InstanceLogo:
                case FileOwnerKind.InstanceTheme:
                case FileOwnerKind.InstanceFont:
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
            //
            // **A lockdown does not reach here, deliberately.** These documents
            // carry no problem content, and they are what a locked card renders
            // itself from — withholding them would leave a participant looking
            // at a blank refusal instead of at an activity that says why.
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

            // Participant scope: readable from **any assignment of this version
            // the caller can currently reach**.
            //
            // **This is where a lockdown is walked past if it is not applied.**
            // One problem is often attached in several places, and the check is
            // "any holder" — so without the two tests below, the statement of a
            // locked examination is served through whichever other course the
            // same problem also hangs in. Nothing else in the read path leaks
            // this way, because nothing else is addressed by file id.
            //
            // The narrowing is per **round**, not per activity: an activity may
            // be reachable while one round inside it is displaced or restricted
            // to an address, and that round's statement is not. Each holder is
            // judged against its own activity's floor, so a round displaced
            // inside one course does not follow the problem into another.
            var holders = await context.SeriesProblems.AsNoTracking()
                .Where(sp => sp.PinnedProblemVersionId == versionId
                    || context.ProblemVersions.Any(v => v.Id == versionId && v.ProblemId == sp.ProblemId))
                .Select(sp => new { sp.ActivityId, sp.SeriesId, sp.Series!.Importance })
                .Distinct()
                .ToListAsync(ct);

            var state = await lockdown.ForReaderAsync(ct);

            foreach (var holder in holders)
            {
                if (!state.Quiet
                    && (state.IsHidden(holder.SeriesId)
                        || state.IsLocked(holder.ActivityId, holder.Importance)))
                {
                    continue;
                }
                if (await permissions.HasAsync(
                    Authorization.Permissions.ActivityRead, holder.ActivityId, ct)) return true;
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
