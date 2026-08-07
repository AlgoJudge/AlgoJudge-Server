using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IProblemService
    {
        Task<PageDto<ManagedProblemDto>> ListAsync(PageQuery paging, string? search, bool mineOnly, bool includeArchived, CancellationToken ct);
        Task<ManagedProblemDto> CreateAsync(ProblemInputDto input, CancellationToken ct);
        Task<ManagedProblemVersionDto> PublishVersionAsync(Guid problemId, ProblemVersionInputDto input, CancellationToken ct);
        Task<ProblemDetailDto> GetForParticipantAsync(string activityIdOrSlug, string problemSlug, CancellationToken ct);
    }

    public class ProblemService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        ISeriesGate gate
    ) : IProblemService
    {
        public async Task<PageDto<ManagedProblemDto>> ListAsync(
            PageQuery paging, string? search, bool mineOnly, bool includeArchived, CancellationToken ct)
        {
            var user = await currentUser.RequireAsync(ct);
            var seesEverything = await permissions.HasAsync(Permissions.ProblemReadAll, null, ct);
            if (!seesEverything) await permissions.RequireAsync(Permissions.ProblemReadOwn, null, ct);

            var query = context.Problems
                .Include(p => p.SharedWith)
                .AsQueryable();

            // The library's one access list, and the only one in the product:
            // a problem is private by default, and `shared` names who else.
            if (!seesEverything)
            {
                query = query.Where(p =>
                    p.OwnerUserId == user.Id
                    || p.Visibility == ProblemVisibility.Instance
                    || (p.Visibility == ProblemVisibility.Shared
                        && p.SharedWith.Any(s => s.UserId == user.Id)));
            }

            if (mineOnly) query = query.Where(p => p.OwnerUserId == user.Id);
            if (!includeArchived) query = query.Where(p => p.ArchivedAt == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(p => p.Name.ToLower().Contains(needle) || p.Slug.ToLower().Contains(needle));
            }

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(p => p.CreatedAt)
                .ThenBy(p => p.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            var items = new List<ManagedProblemDto>(page.Count);
            foreach (var problem in page) items.Add(await ManagedAsync(problem, ct));

            return new PageDto<ManagedProblemDto>
            {
                Items = items, Total = total, Page = paging.Page, PageSize = paging.PageSize,
            };
        }

        private async Task<ManagedProblemDto> ManagedAsync(Problem problem, CancellationToken ct)
        {
            var versions = await context.ProblemVersions
                .Where(v => v.ProblemId == problem.Id)
                .Select(v => v.Version)
                .ToListAsync(ct);
            var attached = await context.SeriesProblems.CountAsync(sp => sp.ProblemId == problem.Id, ct);
            var owner = await context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == problem.OwnerUserId, ct);

            return Projections.ManagedProblem(
                problem,
                versions.Count == 0 ? 0 : versions.Max(),
                versions.Count,
                attached,
                owner is null ? problem.OwnerUserId : Projections.DisplayName(owner));
        }

        public async Task<ManagedProblemDto> CreateAsync(ProblemInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemCreate, null, ct);
            var user = await currentUser.RequireAsync(ct);

            var slug = input.Slug?.Trim() ?? "";
            if (slug.Length == 0) throw new ValidationException("A slug is required", "slug.required");
            if (await context.Problems.AnyAsync(p => p.Slug.ToLower() == slug.ToLower(), ct))
            {
                throw new ConflictException("A problem with that slug already exists", "problem.slug.taken");
            }

            var problem = new Problem
            {
                Slug = slug,
                Name = input.Name?.Trim() is { Length: > 0 } name ? name : slug,
                Type = input.Type ?? "standard-io@1",
                OwnerUserId = user.Id,
                Visibility = ProblemVisibility.Private,
            };
            context.Problems.Add(problem);
            await context.SaveChangesAsync(ct);
            return await ManagedAsync(problem, ct);
        }

        /// <summary>
        /// Publishes a version, whole, in one request.
        /// <para>
        /// Versions are <b>append-only</b>: an existing one takes no new file and
        /// no new package, so a correction is a new version rather than an edit.
        /// Everything travels as a reference — the bytes are already stored, and
        /// carrying a figure forward is a second reference, not a second upload.
        /// </para>
        /// </summary>
        public async Task<ManagedProblemVersionDto> PublishVersionAsync(
            Guid problemId, ProblemVersionInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemUpdate, null, ct);
            var user = await currentUser.RequireAsync(ct);

            var problem = await context.Problems.FirstOrDefaultAsync(p => p.Id == problemId, ct)
                ?? throw new NotFoundException("Problem");
            if (problem.ArchivedAt is not null)
            {
                throw new ConflictException("An archived problem takes no new versions", "problem.archived");
            }

            var previous = await context.ProblemVersions
                .Include(v => v.Files).ThenInclude(f => f.File)
                .Where(v => v.ProblemId == problemId)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(ct);

            if (input.Statements is { Count: 0 })
            {
                throw new ValidationException(
                    "A version with no statement is a problem nobody can read", "version.statements.empty");
            }

            var version = new ProblemVersion
            {
                ProblemId = problemId,
                Version = (previous?.Version ?? 0) + 1,
                CreatedByUserId = user.Id,
                Note = input.Note,
                Config = input.Config is null
                    ? previous?.Config
                    : JsonSerializer.Serialize(input.Config),
            };
            context.ProblemVersions.Add(version);

            var removed = (input.RemovedFiles ?? []).ToHashSet(StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            async Task AttachAsync(Guid fileId, string name, FileScope scope, string? language)
            {
                if (!claimed.Add(name))
                {
                    throw new ConflictException($"This version already has a file called {name}", "version.file.duplicate");
                }
                if (!await context.Files.AnyAsync(f => f.Id == fileId, ct))
                {
                    throw new ValidationException($"No such file: {fileId}", "file.missing");
                }
                context.FileReferences.Add(new FileReference
                {
                    FileId = fileId,
                    OwnerKind = FileOwnerKind.ProblemVersion,
                    ProblemVersionId = version.Id,
                    Scope = scope,
                    Name = name,
                    Language = language,
                });
            }

            // Statements. The name follows from the language, so nobody types it
            // and nobody can mistype it.
            if (input.Statements is { } statements)
            {
                foreach (var statement in statements)
                {
                    await AttachAsync(
                        Guid.Parse(statement.FileId),
                        PackageNames.StatementName(statement.Language),
                        FileScope.Participant,
                        statement.Language);
                }
            }

            // Anything the previous version held and this one did not replace or
            // remove is carried forward — as a reference to the same bytes.
            foreach (var carried in previous?.Files ?? [])
            {
                if (removed.Contains(carried.Name) || claimed.Contains(carried.Name)) continue;
                if (input.Package is not null && PackageNames.IsPackage(carried.Name)) continue;
                await AttachAsync(carried.FileId, carried.Name, carried.Scope, carried.Language);
            }

            foreach (var file in input.Files ?? [])
            {
                if (PackageNames.IsStatement(file.Name))
                {
                    throw new ValidationException(
                        "content.* is the statement; publish it as a statement", "version.file.isStatement");
                }
                if (PackageNames.IsPackage(file.Name))
                {
                    throw new ValidationException("The package is published as a package", "version.file.isPackage");
                }
                await AttachAsync(Guid.Parse(file.FileId), file.Name, ParseScope(file.Scope), null);
            }

            if (input.Package is { } package)
            {
                // Runner scope: the tests. Never a participant, whatever else the
                // activity's attachment table says — that table is about
                // submissions, and this is the problem.
                await AttachAsync(Guid.Parse(package.FileId), PackageNames.Archive, FileScope.Runner, null);
                if (package.SamplesFileId is { } samples)
                {
                    // The examples, as the participant receives them. Handing over
                    // the package itself would disclose the whole problem.
                    await AttachAsync(Guid.Parse(samples), PackageNames.Samples, FileScope.Participant, null);
                }
            }

            await context.SaveChangesAsync(ct);

            var stored = await context.ProblemVersions
                .Include(v => v.Files).ThenInclude(f => f.File)
                .FirstAsync(v => v.Id == version.Id, ct);

            return Projections.ManagedVersion(stored, Projections.DisplayName(user), _ => null);
        }

        private static FileScope ParseScope(string? value) => value switch
        {
            "manager" => FileScope.Manager,
            "runner" => FileScope.Runner,
            _ => FileScope.Participant,
        };

        /// <summary>
        /// A problem as a participant sees it, through one assignment.
        /// <para>
        /// The series is asked here and not only where the list is drawn: the
        /// address of a problem is guessable and gets shared, so a round that has
        /// not opened answers <b>404</b> rather than confirming what it holds.
        /// </para>
        /// </summary>
        public async Task<ProblemDetailDto> GetForParticipantAsync(
            string activityIdOrSlug, string problemSlug, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityRead, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            var assignment = await context.SeriesProblems
                .Include(sp => sp.Series)
                .Include(sp => sp.Problem)
                .FirstOrDefaultAsync(sp => sp.ActivityId == activity.Id && sp.Slug == problemSlug, ct)
                ?? throw new NotFoundException("Problem");

            if (!gate.MayReadProblems(assignment.Series!, activity)) throw new NotFoundException("Problem");

            var version = await ResolveVersionAsync(assignment, ct)
                ?? throw new NotFoundException("Problem");

            var files = await context.FileReferences.AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.ProblemVersionId == version.Id && r.Scope == FileScope.Participant)
                .ToListAsync(ct);

            var mine = await context.Submissions.AsNoTracking()
                .Where(s => s.SeriesProblemId == assignment.Id && s.UserId == user.Id)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            var scale = Scoring.Best(mine);
            var maxPoints = Scoring.MaxPoints(assignment);
            var ceiling = assignment.MaxSubmissions ?? activity.MaxSubmissionsPerProblem;

            return new ProblemDetailDto
            {
                Id = Wire.Id(assignment.Id),
                Slug = assignment.Slug,
                Name = assignment.Name ?? assignment.Problem!.Name,
                Type = assignment.Problem!.Type,
                SeriesId = Wire.Id(assignment.SeriesId),
                Statements = files
                    .Where(f => PackageNames.IsStatement(f.Name))
                    .Select(Projections.Statement).ToList(),
                Attachments = files
                    .Where(f => !PackageNames.IsStatement(f.Name))
                    .Select(f => Projections.Attachment(f, $"/api/v1/files/{Wire.Id(f.FileId)}"))
                    .ToList(),
                Status = Scoring.Status(mine, scale),
                BestScore = Scoring.Rescale(scale, maxPoints),
                MaxScore = mine.Count == 0 ? null : maxPoints,
                Attempts = mine.Count,
                Languages = activity.Languages,
                MaxUploadBytes = assignment.MaxUploadBytes ?? activity.MaxUploadBytes,
                SubmitFields =
                [
                    new SubmitFieldDto { Kind = "code", Name = "code", Label = "Source" },
                    new SubmitFieldDto { Kind = "file", Name = "file", Label = "File" },
                ],
                SubmissionsLeft = ceiling is null ? null : Math.Max(0, ceiling.Value - mine.Count),
            };
        }

        /// <summary>
        /// Which content version an assignment evaluates against.
        /// <para>
        /// The pin when there is one — and since 2026-08-08 attaching sets it, so
        /// there usually is. Null still means "the current version", which a
        /// manager may choose and which is only safe while nobody is editing the
        /// statement underneath a running round.
        /// </para>
        /// </summary>
        internal async Task<ProblemVersion?> ResolveVersionAsync(SeriesProblem assignment, CancellationToken ct)
        {
            if (assignment.PinnedProblemVersionId is { } pinned)
            {
                return await context.ProblemVersions.FirstOrDefaultAsync(v => v.Id == pinned, ct);
            }
            return await context.ProblemVersions
                .Where(v => v.ProblemId == assignment.ProblemId)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(ct);
        }
    }
}
