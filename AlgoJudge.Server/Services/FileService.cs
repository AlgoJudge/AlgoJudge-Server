using System.Security.Cryptography;
using AlgoJudge.Server.Api.Contracts;
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

        /// <summary>Which store took them — the value the row will carry forever.</summary>
        public required string StoreId { get; init; }
    }

    /// <summary>
    /// Stored bytes: one way in, one way out, and one rule about who may read.
    /// </summary>
    /// <summary>
    /// Who is uploading, as the file table records it.
    /// <para>
    /// **A parameter rather than something read from the ambient request**,
    /// because that is exactly how the defect arrived: the Runner surface
    /// authenticated a Runner, threw the answer away, and let the commit ask a
    /// session service that answers null for one — so every Runner's uploads
    /// landed in the same anonymous pool and one Runner could attach another's.
    /// A required argument makes the compiler put the question to every writer,
    /// and there are six.
    /// </para>
    /// <para>
    /// <see cref="Session"/> still resolves through <c>ICurrentUserService</c>,
    /// so no controller grows a dependency to say the ordinary thing — but
    /// saying it is now a word at the call site rather than a default nobody
    /// chose.
    /// </para>
    /// </summary>
    public readonly record struct Uploader
    {
        internal Guid? RunnerId { get; private init; }
        internal bool FromSession { get; private init; }

        /// <summary>Whoever is signed in, or nobody if that is the truth.</summary>
        public static Uploader Session => new() { FromSession = true };

        /// <summary>An approved Runner, which carries a token and not a session.</summary>
        public static Uploader Runner(Guid runnerId) => new() { RunnerId = runnerId };

        /// <summary>A seed or a preconfiguration: no principal at all.</summary>
        public static Uploader Nobody => new();
    }

    public interface IFileService
    {
        /// <summary>
        /// Writes bytes and commits the row, for a caller that already holds
        /// everything. Staging and committing in one call.
        /// </summary>
        Task<DbFile> StoreAsync(
            Stream content, string name, string mimeType, string declaredSha256,
            Uploader uploader, CancellationToken ct);

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
            StagedBytes staged, string name, string mimeType, string declaredSha256,
            Uploader uploader, CancellationToken ct);

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
        ISeriesGate gate,
        IBlobStoreRegistry stores,
        // **Resolved late, because the library depends on this service.**
        // `ProblemService` takes `IFileService` to publish a version, so taking
        // `IProblemService` here outright is a cycle the container refuses. The
        // same shape `SubmissionService` uses for the same reason.
        IServiceProvider services
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
            Stream content, string name, string mimeType, string declaredSha256,
            Uploader uploader, CancellationToken ct)
        {
            var staged = await StageAsync(content, ct);
            return await CommitAsync(staged, name, mimeType, declaredSha256, uploader, ct);
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
            StagedBytes staged, string name, string mimeType, string declaredSha256,
            Uploader uploader, CancellationToken ct)
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
                UploadedByUserId = uploader.FromSession ? currentUser.UserId : null,
                UploadedByRunnerId = uploader.RunnerId,
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
                // the operator's colors and typeface before anybody has signed
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

                case FileOwnerKind.Printout:
                    // **At the printout's own activity, never anywhere.** The
                    // problem library asks `HasAnywhereAsync` because a shared
                    // problem is one library entry seen from several activities;
                    // a printout belongs to exactly one, and somebody trusted
                    // with the printer in one room has no business with another's
                    // exam answers.
                    //
                    // `Scope` is deliberately not consulted: unlike the activity
                    // and submission arms, there is no second audience to tell
                    // apart here, and pretending to check it would read as a
                    // guard that exists.
                    return reference.PrintoutId is { } printoutId
                        && await CanReadPrintoutAsync(printoutId, ct);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Whoever works that activity's print queue, and nobody else — not even
        /// the participant who asked, who already had the text.
        /// </summary>
        private async Task<bool> CanReadPrintoutAsync(Guid printoutId, CancellationToken ct)
        {
            var activityId = await context.Printouts
                .AsNoTracking()
                .Where(x => x.Id == printoutId)
                .Select(x => (Guid?)x.ActivityId)
                .FirstOrDefaultAsync(ct);

            return activityId is { } id
                && await permissions.HasAsync(Authorization.Permissions.PrintoutManage, id, ct);
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

        /// <summary>
        /// Whether the caller may read the problem this version belongs to.
        /// <para>
        /// <b>`problem:update` says what somebody may do, never to which
        /// problem.</b> The library has one access list — the owner, whoever it
        /// was shared with, and everybody when it is instance-visible — and the
        /// package endpoint enforces it. Addressed by file id the two arms above
        /// asked only for the verb, so anybody who manages any activity could
        /// fetch any package and any model solution in the installation.
        /// </para>
        /// </summary>
        private async Task<bool> ReadsTheProblemAsync(Guid versionId, CancellationToken ct)
        {
            var problemId = await context.ProblemVersions.AsNoTracking()
                .Where(v => v.Id == versionId)
                .Select(v => (Guid?)v.ProblemId)
                .FirstOrDefaultAsync(ct);

            return problemId is { } id
                && await services.GetRequiredService<IProblemService>().IsReadableAsync(id, ct);
        }

        private async Task<bool> CanReadProblemVersionAsync(FileReference reference, CancellationToken ct)
        {
            if (reference.ProblemVersionId is not { } versionId) return false;

            // Manager scope is where a model solution lives. Never a participant,
            // never a Runner reading it as a participant would.
            //
            // **Anywhere, not at system scope.** The library these files belong
            // to admits whoever manages an activity, so a manager who may open a
            // problem must be able to read its bytes; asking at system scope let
            // them open the screen and not the file.
            if (reference.Scope == FileScope.Manager)
            {
                return await permissions.HasAnywhereAsync(Authorization.Permissions.ProblemUpdate, ct)
                    && await ReadsTheProblemAsync(versionId, ct);
            }

            // Runner scope: the package. Readable by a Runner holding a job for
            // this very version — authorized against the job, not against being
            // a Runner — and by managers.
            if (reference.Scope == FileScope.Runner)
            {
                return await permissions.HasAnywhereAsync(Authorization.Permissions.ProblemUpdate, ct)
                    && await ReadsTheProblemAsync(versionId, ct);
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
                .Select(sp => new { sp.ActivityId, sp.SeriesId, Series = sp.Series!, Activity = sp.Activity! })
                .ToListAsync(ct);

            // One problem hangs in several places and each place is judged on its
            // own; the same place twice is the same answer, so it is asked once.
            holders = holders
                .GroupBy(h => new { h.ActivityId, h.SeriesId })
                .Select(g => g.First())
                .ToList();

            var state = await lockdown.ForReaderAsync(ct);

            foreach (var holder in holders)
            {
                if (!state.Quiet
                    && (state.IsHidden(holder.SeriesId)
                        || state.IsLocked(holder.ActivityId, holder.Series.Importance)))
                {
                    continue;
                }

                // **The round's own gate, which this asked nothing about until
                // 2026-09-09.** `ProblemService` treats `MayReadProblems` as the
                // rule for whether a statement may be sent at all — a round that
                // never opened does not disclose what it holds, a paused one may
                // hide it, an ended one may. Applying the lockdown and not this
                // meant every one of those was readable **by file id**, which is
                // the one address the screens do not control.
                if (!gate.MayReadProblems(holder.Series, holder.Activity)) continue;

                if (await permissions.HasAsync(
                    Authorization.Permissions.ActivityRead, holder.ActivityId, ct)) return true;
            }

            return await permissions.HasAsync(Authorization.Permissions.ProblemReadAll, null, ct);
        }

        private async Task<bool> CanReadSubmissionFileAsync(
            Guid submissionId, string name, FileScope scope, string? userId, CancellationToken ct)
        {
            var submission = await context.Submissions.AsNoTracking()
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Series)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Activity)
                .FirstOrDefaultAsync(s => s.Id == submissionId, ct);
            if (submission?.SeriesProblem is null) return false;

            var activityId = submission.SeriesProblem.ActivityId;

            if (await permissions.HasAsync(Authorization.Permissions.SubmissionSourceReadAll, activityId, ct)) return true;
            if (scope == FileScope.Manager) return false;

            // Refused rather than allowed if either is missing, the way the
            // assignment above is: an unloaded navigation is a question this
            // could not ask, and a read rule that answers "yes" to that is one
            // `Include` away from serving everything.
            var round = submission.SeriesProblem.Series;
            var holder = submission.SeriesProblem.Activity;
            if (round is null || holder is null) return false;

            // **What the lockdown puts out of reach**, asked of a submission's
            // bytes for the first time. `SubmissionService.GetAsync` refuses a
            // displaced or address-restricted round's own submission outright,
            // and `SERIES_LOCKDOWN.md` §8 says a displaced round loses the
            // submissions made in it — but the file id walked past that refusal,
            // so the bytes of the thing the endpoint refused were one request
            // away.
            if (await OutOfReachAsync(round, activityId, ct)) return false;

            // **A round that hides its content hides what was written for it.**
            // The same gate the statement is judged by, one door along: a
            // participant re-reading their own code during a pause called for a
            // leak in the statement is the reading the hiding exists to stop.
            //
            // **Narrower than the lockdown above, on purpose.** A displaced round
            // takes everything; hiding the content takes the submitted bytes and
            // leaves `log` and `details`, which in a course are the feedback.
            // `Source` is the name `SubmitAsync` writes for everything a
            // participant sends, an archive included. The log can quote a source
            // line in a compiler error, and that is accepted rather than
            // unnoticed.
            if (name == AttachmentNames.Source && !gate.MayReadProblems(round, holder)) return false;

            // Under a submission, participant scope means its author — and only
            // if the activity's table admits this name.
            if (submission.UserId != userId) return false;
            return await NameIsPublicAsync(activityId, name, ct);
        }

        /// <summary>
        /// Whether the reader's lockdown puts this round out of reach, hidden by
        /// an address rule or displaced by something more important.
        /// <para>
        /// The same two questions <see cref="CanReadProblemVersionAsync"/> asks
        /// of a statement's holder, in the one place both submission arms can
        /// reach it. Answered per round rather than per activity, because an
        /// activity stays reachable while a round inside it does not.
        /// </para>
        /// </summary>
        private async Task<bool> OutOfReachAsync(Series round, Guid activityId, CancellationToken ct)
        {
            var state = await lockdown.ForReaderAsync(ct);
            return !state.Quiet
                && (state.IsHidden(round.Id) || state.IsLocked(activityId, round.Importance));
        }

        private async Task<bool> CanReadAttemptFileAsync(
            Guid jobId, string name, FileScope scope, string? userId, CancellationToken ct)
        {
            var job = await context.EvaluationJobs.AsNoTracking()
                .Include(j => j.Submission)!.ThenInclude(s => s!.SeriesProblem)!.ThenInclude(sp => sp!.Series)
                .FirstOrDefaultAsync(j => j.Id == jobId, ct);
            var submission = job?.Submission;
            if (submission?.SeriesProblem is null) return false;

            var activityId = submission.SeriesProblem.ActivityId;

            if (await permissions.HasAsync(Authorization.Permissions.ResultLogReadAll, activityId, ct)) return true;
            if (scope == FileScope.Manager) return false;

            // **The lockdown reaches an attempt's documents too.** A displaced
            // round loses the submissions made in it, and a log naming the tests
            // it failed is part of one. The content gate is deliberately not
            // asked here: hiding a round's statements takes the source and
            // leaves the feedback.
            var round = submission.SeriesProblem.Series;
            if (round is null || await OutOfReachAsync(round, activityId, ct)) return false;

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
