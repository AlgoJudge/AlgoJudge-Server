using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using DbFile = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Services
{
    public interface IPrintoutService
    {
        // The participant's half.
        Task<PageDto<PrintoutDto>> ListMineAsync(
            string activityIdOrSlug, PageQuery paging, CancellationToken ct);
        Task<PrintoutDto> RequestAsync(
            string activityIdOrSlug, StagedBytes staged, string fileName, string declaredSha256,
            string? title, Guid? submissionId, CancellationToken ct);

        // The operator's half.
        Task<PageDto<ManagedPrintoutDto>> ListAsync(
            PageQuery paging, Guid? activityId, string? state, CancellationToken ct);
        Task<IReadOnlyList<PrintoutActivityDto>> ActivitiesAsync(CancellationToken ct);
        Task<PrintoutSheetDto> SheetAsync(Guid printoutId, CancellationToken ct);
        /// <summary>Take it to a printer, so nobody else prints the same page.</summary>
        Task<ManagedPrintoutDto> ClaimAsync(Guid printoutId, CancellationToken ct);
        /// <summary>Hand it back without printing it.</summary>
        Task<ManagedPrintoutDto> ReleaseAsync(Guid printoutId, CancellationToken ct);
        Task<ManagedPrintoutDto> ResolveAsync(
            Guid printoutId, ResolvePrintoutInputDto input, CancellationToken ct);

        /// <summary>
        /// Dispose of everything still outstanding for one person, used when an
        /// account is deleted or merged away. Anonymising a name leaves the
        /// source sitting in the queue; this is the other half.
        /// </summary>
        Task<int> DisposeOfEveryOutstandingAsync(string userId, CancellationToken ct);
    }

    /// <summary>
    /// Print requests: a participant asks for a page of source on paper, and
    /// somebody at a printer works the queue.
    /// <para>
    /// <b>Both halves live here on purpose.</b> The point of this feature is a
    /// surface that can be handed to one person who holds nothing else, and
    /// burying the operator's half in the service that also rejudges submissions
    /// and revokes Runners would make that harder to read than it is to build.
    /// </para>
    /// </summary>
    public class PrintoutService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        IFileService files,
        IEventHub events,
        IEventAudience audience,
        TimeProvider clock
    ) : IPrintoutService
    {
        /// <summary>
        /// How many requests one person may have waiting in one activity.
        /// <para>
        /// A number rather than a setting, with the reason beside it: a queue is
        /// worked by a person, and somebody who has asked three times and had
        /// none of them come back wants the printer looked at, not a fourth
        /// place in the line. A tunable ceiling is a later decision with real
        /// numbers behind it.
        /// </para>
        /// </summary>
        private const int PendingPerPerson = 3;

        // ── The participant's half ────────────────────────────────────────────

        public async Task<PageDto<PrintoutDto>> ListMineAsync(
            string activityIdOrSlug, PageQuery paging, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await activities.RequireVisibleAsync(activity, ct);
            await permissions.RequireAsync(Permissions.PrintoutRequest, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);
            await Membership.RequireAsync(context, permissions, activity.Id, user.Id, ct);

            // **Own, and only own.** The key admits every enrolled participant,
            // so without this the list would hand each of them everybody else's
            // file names and titles. "Own" is the whole read rule here; there is
            // no staff variant of this endpoint, because staff read the queue.
            var query = context.Printouts
                .AsNoTracking()
                .Where(x => x.ActivityId == activity.Id && x.RequestedByUserId == user.Id);

            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderByDescending(x => x.RequestedAt)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            return new PageDto<PrintoutDto>
            {
                Items = rows.Select(Project).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        public async Task<PrintoutDto> RequestAsync(
            string activityIdOrSlug, StagedBytes staged, string fileName, string declaredSha256,
            string? title, Guid? submissionId, CancellationToken ct)
        {
            // The guard order `QuestionService.AskAsync` uses, for the reasons
            // written there — chiefly that a permission held at system scope
            // reaches every activity and being in one is a different question.
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await activities.RequireVisibleAsync(activity, ct);
            await permissions.RequireAsync(Permissions.PrintoutRequest, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);
            await Membership.RequireAsync(context, permissions, activity.Id, user.Id, ct);

            if (activity.ArchivedAt is not null)
            {
                throw new ConflictException(
                    "An archived activity accepts no print requests", "activity.archived");
            }
            if (!activity.HasPrintouts)
            {
                throw new ForbiddenActionException(
                    "This activity does not take print requests", "printouts.disabled");
            }

            var name = fileName.Trim();
            if (name.Length == 0)
            {
                throw new ValidationException("A file name is required", "printout.fileName.required");
            }

            // **Provenance is checked, not believed.** The id names a submission
            // the caller must own, in this activity. Without both halves a
            // participant could attach somebody else's exam answer to their own
            // request and collect it at the printer. `Submission` carries no
            // `ActivityId`, so the activity is reached through its assignment.
            if (submissionId is { } id)
            {
                var submission = await context.Submissions
                    .AsNoTracking()
                    .Include(s => s.SeriesProblem)
                    .FirstOrDefaultAsync(s => s.Id == id, ct)
                    ?? throw new NotFoundException("Submission");

                if (submission.SeriesProblem!.ActivityId != activity.Id || submission.UserId != user.Id)
                {
                    throw new ForbiddenActionException(
                        "A print request may only name your own submission",
                        "printout.submission.notYours");
                }
            }

            var pending = await context.Printouts.CountAsync(
                x => x.ActivityId == activity.Id
                    && x.RequestedByUserId == user.Id
                    && x.State == PrintoutState.Requested, ct);
            if (pending >= PendingPerPerson)
            {
                throw new ConflictException(
                    "You already have print requests waiting", "printout.tooMany");
            }

            // Read from the grant, never from the request, and stamped rather
            // than resolved later — the rule `Submission.GroupId` records.
            var group = await Contestant.GroupAsync(context, activity.Id, user.Id, ct);

            // **`CommitAsync` is the point of no return**, so every refusal is
            // above this line. It recomputes the checksum and throws 422 on a
            // mismatch, which is the only place the declared value is answered.
            var file = await files.CommitAsync(
                staged, name, "text/plain", declaredSha256, Uploader.Session, ct);

            var printout = new Printout
            {
                ActivityId = activity.Id,
                SubmissionId = submissionId,
                RequestedByUserId = user.Id,
                GroupId = group,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                FileName = name,
                Sha256 = file.Sha256,
                SizeBytes = file.SizeBytes,
                RequestedAt = clock.GetUtcNow().UtcDateTime,
            };
            context.Printouts.Add(printout);

            context.FileReferences.Add(new FileReference
            {
                FileId = file.Id,
                OwnerKind = FileOwnerKind.Printout,
                PrintoutId = printout.Id,
                // **A label, not a guard.** The printout arm of
                // `CanReadThroughAsync` asks the permission and never reads this
                // field, unlike the submission and activity arms where it is
                // load-bearing. It is set so a row reads honestly to somebody
                // looking at the table.
                Scope = FileScope.Manager,
                Name = AttachmentNames.Source,
            });

            await context.SaveChangesAsync(ct);

            await AnnounceAsync(printout, ct);
            return Project(printout);
        }

        // ── The operator's half ───────────────────────────────────────────────

        public async Task<PageDto<ManagedPrintoutDto>> ListAsync(
            PageQuery paging, Guid? activityId, string? state, CancellationToken ct)
        {
            // **Narrowed, never required at no scope.** The printer operator's
            // grant is on an activity by construction — that is the whole point
            // of the delegation — so asking `RequireAsync(key, null)` here would
            // answer 403 to exactly the people this screen is for.
            var reader = await currentUser.RequireAsync(ct);
            var allowed = await permissions.ListScopeAsync(Permissions.PrintoutManage, activityId, ct);

            var query = context.Printouts
                .AsNoTracking()
                .Include(x => x.Activity)
                .Include(x => x.RequestedBy)
                .Include(x => x.ResolvedBy)
                .Include(x => x.ClaimedBy)
                .Include(x => x.Group)
                .AsQueryable();

            if (activityId is { } scoped)
            {
                query = query.Where(x => x.ActivityId == scoped);
            }
            else if (allowed is not null)
            {
                var ids = allowed.ToHashSet();
                query = query.Where(x => ids.Contains(x.ActivityId));
            }

            if (ParseState(state) is { } wanted) query = query.Where(x => x.State == wanted);

            var total = await query.CountAsync(ct);
            var rows = await query
                // Oldest first: a queue is worked from the front, and whoever has
                // waited longest is the one to serve.
                .OrderBy(x => x.RequestedAt)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            return new PageDto<ManagedPrintoutDto>
            {
                Items = rows.Select(row => ProjectManaged(row, reader.Id)).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        public async Task<IReadOnlyList<PrintoutActivityDto>> ActivitiesAsync(CancellationToken ct)
        {
            // **Its own endpoint, and not the panel's activity summary.** That
            // one asks `activity:update`, so an operator holding nothing but
            // `printout:manage` would be handed an empty filter with no error to
            // notice — a screen that looks like it is working and is not.
            await permissions.RequireAnywhereAsync(Permissions.PrintoutManage, ct);
            var allowed = await permissions.ActivitiesWithAsync(Permissions.PrintoutManage, ct);

            var query = context.Activities.AsNoTracking().AsQueryable();
            if (allowed is not null)
            {
                var ids = allowed.ToHashSet();
                query = query.Where(a => ids.Contains(a.Id));
            }

            return await query
                .OrderBy(a => a.Name)
                .Select(a => new PrintoutActivityDto
                {
                    Id = a.Id.ToString(),
                    Name = a.Name,
                    Slug = a.Slug,
                })
                .ToListAsync(ct);
        }

        public async Task<PrintoutSheetDto> SheetAsync(Guid printoutId, CancellationToken ct)
        {
            var printout = await LoadForOperatorAsync(printoutId, tracked: false, ct);
            var operatorId = (await currentUser.RequireAsync(ct)).Id;

            string? source = null;
            if (printout.SourceDisposedAt is null && await SourceFileAsync(printout.Id, ct) is { } file)
            {
                await using var stream = await files.OpenAsync(file, ct);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                source = await reader.ReadToEndAsync(ct);
            }

            var assignment = printout.Submission?.SeriesProblem;

            return new PrintoutSheetDto
            {
                Printout = ProjectManaged(printout, operatorId),
                TimeZone = printout.Activity!.TimeZone,
                ProblemSlug = assignment?.Slug,
                ProblemName = assignment?.Problem?.Name,
                Source = source,
            };
        }

        /// <summary>
        /// Take a waiting row, or take one over.
        /// <para>
        /// **Taking over is allowed and is not a race.** Two people at one
        /// printer see each other; what the state prevents is each of them
        /// opening a sheet believing they are alone. Somebody who walked away
        /// with a row is a person the other can ask, and refusing the take-over
        /// would leave the page stuck until an administrator edited a row.
        /// </para>
        /// </summary>
        public async Task<ManagedPrintoutDto> ClaimAsync(Guid printoutId, CancellationToken ct)
        {
            var printout = await LoadForOperatorAsync(printoutId, tracked: true, ct);
            var user = await currentUser.RequireAsync(ct);

            if (printout.State is PrintoutState.Printed or PrintoutState.Discarded)
            {
                throw new ConflictException("This request is already resolved", "printout.resolved");
            }

            printout.State = PrintoutState.Printing;
            printout.ClaimedByUserId = user.Id;
            printout.ClaimedAt = clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(ct);

            await AnnounceAsync(printout, ct);
            return ProjectManaged(printout, user.Id);
        }

        public async Task<ManagedPrintoutDto> ReleaseAsync(Guid printoutId, CancellationToken ct)
        {
            var printout = await LoadForOperatorAsync(printoutId, tracked: true, ct);
            var user = await currentUser.RequireAsync(ct);

            if (printout.State != PrintoutState.Printing)
            {
                throw new ConflictException("Nobody is printing this", "printout.notClaimed");
            }

            printout.State = PrintoutState.Requested;
            printout.ClaimedByUserId = null;
            printout.ClaimedAt = null;
            await context.SaveChangesAsync(ct);

            await AnnounceAsync(printout, ct);
            return ProjectManaged(printout, user.Id);
        }

        public async Task<ManagedPrintoutDto> ResolveAsync(
            Guid printoutId, ResolvePrintoutInputDto input, CancellationToken ct)
        {
            var printout = await LoadForOperatorAsync(printoutId, tracked: true, ct);
            var user = await currentUser.RequireAsync(ct);

            var outcome = (input.Outcome ?? "").Trim().ToLowerInvariant() switch
            {
                "printed" => PrintoutState.Printed,
                "discarded" => PrintoutState.Discarded,
                _ => throw new ValidationException(
                    "Say whether it printed or was discarded", "printout.outcome.invalid"),
            };

            if (printout.State is PrintoutState.Printed or PrintoutState.Discarded)
            {
                throw new ConflictException("This request is already resolved", "printout.resolved");
            }

            await DisposeAsync(printout, outcome, user.Id, ct);
            return ProjectManaged(printout, user.Id);
        }

        public async Task<int> DisposeOfEveryOutstandingAsync(string userId, CancellationToken ct)
        {
            var outstanding = await context.Printouts
                .Where(x => x.RequestedByUserId == userId && x.State == PrintoutState.Requested)
                .ToListAsync(ct);

            foreach (var printout in outstanding)
            {
                await DisposeAsync(printout, PrintoutState.Discarded, resolvedBy: null, ct);
            }

            return outstanding.Count;
        }

        // ── Disposal, which is the whole design ───────────────────────────────

        /// <summary>
        /// Resolve one request and take its source with it.
        /// <para>
        /// <b>Removed, not superseded.</b> <c>FileCollector</c> acts on
        /// <c>SupersededAt</c> only for <see cref="FileOwnerKind.Runner"/>, so
        /// superseding a printout's reference would protect the bytes for ever —
        /// the exact opposite of what this is for, and the worst possible failure
        /// here, because it would look like a disposal.
        /// </para>
        /// <para>
        /// Row first, bytes second, the ordering <c>TrialService</c> uses: a
        /// failure after the save leaves an orphan, and the collector is the
        /// backstop. Its real latency is a day or two — the cutoff is measured
        /// from upload rather than from orphaning — so it is a backstop and not
        /// the plan.
        /// </para>
        /// </summary>
        private async Task DisposeAsync(
            Printout printout, PrintoutState outcome, string? resolvedBy, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var reference = await context.FileReferences
                .FirstOrDefaultAsync(r => r.PrintoutId == printout.Id, ct);

            printout.State = outcome;
            printout.ResolvedAt = now;
            printout.ResolvedByUserId = resolvedBy;
            printout.SourceDisposedAt = now;

            if (reference is not null) context.FileReferences.Remove(reference);
            await context.SaveChangesAsync(ct);

            // Deletes only when nothing else holds the bytes, which is what makes
            // it safe to call unconditionally.
            if (reference is not null) await files.DeleteUnreferencedAsync(reference.FileId, ct);

            await AnnounceAsync(printout, ct);
        }

        /// <summary>
        /// Tell whoever works this activity's queue that it moved.
        /// <para>
        /// Only them. A participant learns their page printed when the paper
        /// arrives, and the audience for this is the two people at one printer
        /// who would otherwise each print it.
        /// </para>
        /// </summary>
        private async Task AnnounceAsync(Printout printout, CancellationToken ct)
        {
            var readers = await audience.InActivityAsync(
                printout.ActivityId, Permissions.PrintoutManage, ct);
            if (readers.Count == 0) return;

            await events.SendToUsersAsync(
                readers, EventTypes.PrintoutChanged, new { printoutId = printout.Id.ToString() }, ct);
        }

        // ── Loading and projecting ────────────────────────────────────────────

        /// <summary>
        /// The row, and the permission asked at <b>its own</b> activity before
        /// anything is projected. Nothing in this codebase authorises by
        /// attribute, so a method that forgets this line is authenticated-only.
        /// </summary>
        private async Task<Printout> LoadForOperatorAsync(
            Guid printoutId, bool tracked, CancellationToken ct)
        {
            var query = context.Printouts
                .Include(x => x.Activity)
                .Include(x => x.RequestedBy)
                .Include(x => x.ResolvedBy)
                .Include(x => x.ClaimedBy)
                .Include(x => x.Group)
                .Include(x => x.Submission!).ThenInclude(s => s.SeriesProblem!).ThenInclude(sp => sp.Problem)
                .AsQueryable();
            if (!tracked) query = query.AsNoTracking();

            var printout = await query.FirstOrDefaultAsync(x => x.Id == printoutId, ct)
                ?? throw new NotFoundException("Printout");

            await permissions.RequireAsync(Permissions.PrintoutManage, printout.ActivityId, ct);
            return printout;
        }

        private async Task<DbFile?> SourceFileAsync(Guid printoutId, CancellationToken ct)
        {
            var fileId = await context.FileReferences
                .AsNoTracking()
                .Where(r => r.PrintoutId == printoutId)
                .Select(r => (Guid?)r.FileId)
                .FirstOrDefaultAsync(ct);

            return fileId is { } id ? await files.FindAsync(id, ct) : null;
        }

        private static PrintoutState? ParseState(string? state) =>
            (state ?? "").Trim().ToLowerInvariant() switch
            {
                "requested" => PrintoutState.Requested,
                "printed" => PrintoutState.Printed,
                "discarded" => PrintoutState.Discarded,
                _ => null,
            };

        private static string StateName(PrintoutState state) => state switch
        {
            PrintoutState.Printing => "printing",
            PrintoutState.Printed => "printed",
            PrintoutState.Discarded => "discarded",
            _ => "requested",
        };

        private static PrintoutDto Project(Printout x) => new()
        {
            Id = x.Id.ToString(),
            Title = x.Title,
            FileName = x.FileName,
            SizeBytes = x.SizeBytes,
            State = StateName(x.State),
            RequestedAt = Wire.At(x.RequestedAt)!,
            ResolvedAt = Wire.At(x.ResolvedAt),
        };

        private static ManagedPrintoutDto ProjectManaged(Printout x, string? readerId) => new()
        {
            Id = x.Id.ToString(),
            ActivityId = x.ActivityId.ToString(),
            ActivityName = x.Activity?.Name ?? "",
            ActivitySlug = x.Activity?.Slug ?? "",
            RequestedByName = x.RequestedBy is null
                ? x.RequestedByUserId
                : Projections.DisplayName(x.RequestedBy),
            GroupName = x.Group?.Name,
            Title = x.Title,
            FileName = x.FileName,
            SizeBytes = x.SizeBytes,
            Sha256 = x.Sha256,
            State = StateName(x.State),
            RequestedAt = Wire.At(x.RequestedAt)!,
            ClaimedByName = x.ClaimedBy is null ? null : Projections.DisplayName(x.ClaimedBy),
            ClaimedAt = Wire.At(x.ClaimedAt),
            ClaimedByMe = x.ClaimedByUserId is { } held && held == readerId,
            ResolvedAt = Wire.At(x.ResolvedAt),
            ResolvedByName = x.ResolvedBy is null ? null : Projections.DisplayName(x.ResolvedBy),
            SourceDisposedAt = Wire.At(x.SourceDisposedAt),
        };
    }
}
