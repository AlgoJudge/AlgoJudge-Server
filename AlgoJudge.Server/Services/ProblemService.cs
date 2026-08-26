using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Realtime;
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

        Task<ManagedProblemDto> GetAsync(Guid id, CancellationToken ct);
        Task<ManagedProblemDto> UpdateAsync(Guid id, ProblemInputDto input, CancellationToken ct);
        Task DeleteAsync(Guid id, CancellationToken ct);
        Task<ManagedProblemDto> SetArchivedAsync(Guid id, bool archived, CancellationToken ct);
        Task<ManagedProblemDto> DuplicateAsync(Guid id, CancellationToken ct);
        Task<ManagedProblemDto> SetVisibilityAsync(Guid id, string visibility, IReadOnlyList<string>? sharedWith, CancellationToken ct);
        Task<IReadOnlyList<ManagedProblemVersionDto>> ListVersionsAsync(Guid problemId, CancellationToken ct);
        Task<IReadOnlyList<StatementRefDto>> ContentAsync(Guid problemId, Guid versionId, CancellationToken ct);
        Task<(Stream Bytes, string Name)?> PackageAsync(Guid problemId, Guid versionId, CancellationToken ct);
    }

    public class ProblemService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        ISeriesGate gate,
        ISeriesLockdown lockdown,
        IEventHub events,
        IEventAudience audience,
        IFileService files,
        IConfiguration configuration,
        TimeProvider clock
    ) : IProblemService
    {
        /// <summary>
        /// Slug prefixes an importer owns, from the installation's configuration.
        /// <para>
        /// <b>Configuration, never a literal here.</b> Writing
        /// <c>slug.StartsWith("Imported-")</c> in this file would put the name of one
        /// archive into the Server, which is the one thing this product's
        /// architecture forbids — adding a problem type must not be a Server
        /// change, and a Server that knows a type's name by heart has already
        /// stopped being true to that. This reads a list and compares strings; it
        /// never parses one, and it cannot tell you what any entry means.
        /// </para>
        /// <para>
        /// Empty by default, so an installation that imports nothing reserves
        /// nothing.
        /// </para>
        /// </summary>
        private string[] ReservedSlugPrefixes =>
            configuration.GetSection("Problems:ReservedSlugPrefixes").Get<string[]>() ?? [];

        /// <summary>
        /// Refuses a slug in a namespace an importer owns, unless the caller is
        /// allowed to import.
        /// <para>
        /// The point is not secrecy — it is that two problems called
        /// <c>Imported-100</c>, one imported and one typed in by hand, is a collision
        /// nobody can untangle afterwards. So the namespace belongs to whoever
        /// holds <see cref="Permissions.ProblemImportExternal"/>, and everybody
        /// else is told plainly which prefix they hit.
        /// </para>
        /// </summary>
        private async Task RefuseReservedPrefixAsync(string slug, CancellationToken ct)
        {
            var claimed = ReservedSlugPrefixes.FirstOrDefault(prefix =>
                prefix.Length > 0 && slug.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (claimed is null) return;
            if (await permissions.HasAsync(Permissions.ProblemImportExternal, null, ct)) return;

            throw new ValidationException(
                $"The slug prefix \"{claimed}\" is reserved for imported problems",
                "problem.slug.reserved");
        }


        /// <summary>
        /// Tells whoever may read the library that it changed.
        /// <para>
        /// The audience is everybody holding <c>problem:read:all</c> <b>anywhere</b>:
        /// the library is not an activity's, so there is no activity to narrow
        /// to. After the save, so a screen refetching on the event reads what has
        /// already been committed.
        /// </para>
        /// <para>
        /// Silent until 2026-08-08 — `ManagerProblemsPage` listened for this and
        /// no write ever sent one.
        /// </para>
        /// </summary>
        private async Task AnnounceProblemAsync(
            ManagedProblemDto? problem, string? deletedId, CancellationToken ct)
        {
            var readers = await audience.AnywhereAsync(Permissions.ProblemReadAll, ct);
            if (readers.Count == 0) return;

            await events.SendToUsersAsync(readers, EventTypes.ProblemChanged,
                deletedId is null ? new { problem } : new { deletedId }, ct);
        }
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
            await RefuseReservedPrefixAsync(slug, ct);
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
                // The only place this is ever written. There is no endpoint that
                // changes it afterwards, which is the whole of how "permanent"
                // is enforced — a rule stated in a comment and checked nowhere
                // is a rule until somebody adds a setter.
                External = input.External,
            };
            context.Problems.Add(problem);
            await context.SaveChangesAsync(ct);
            var announced = await ManagedAsync(problem, ct);
            await AnnounceProblemAsync(announced, null, ct);
            return announced;
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
                // An absent document carries the previous version's forward; a
                // present one is checked before it replaces it. A problem's
                // identity does not change because somebody fixed a typo in its
                // statement.
                Props = input.Props is null
                    ? previous?.Props
                    : Opaque.Store(input.Props, "props"),
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

            // Statements. The name follows from the language and from what the
            // bytes are, so nobody types it and nobody can mistype it.
            if (input.Statements is { } statements)
            {
                foreach (var statement in statements)
                {
                    var fileId = Guid.Parse(statement.FileId);
                    // The media type this Server recorded when the bytes arrived,
                    // never a caller's word for it. Absent means no such file,
                    // and `AttachAsync` is what says so.
                    var mimeType = await context.Files
                        .Where(f => f.Id == fileId)
                        .Select(f => f.MimeType)
                        .FirstOrDefaultAsync(ct);

                    await AttachAsync(
                        fileId,
                        PackageNames.StatementName(
                            statement.Language, PackageNames.StatementExtension(mimeType)),
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

        public async Task<ManagedProblemDto> GetAsync(Guid id, CancellationToken ct)
        {
            var problem = await LoadAsync(id, ct);
            await RequireReadableAsync(problem, ct);
            return await ManagedAsync(problem, ct);
        }

        private async Task<Problem> LoadAsync(Guid id, CancellationToken ct) =>
            await context.Problems
                .Include(p => p.SharedWith)
                .FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new NotFoundException("Problem");

        /// <summary>
        /// The library's one access list: the owner, whoever it is shared with,
        /// and anybody at all when it is instance-visible — or somebody holding
        /// <c>problem:read:all</c>.
        /// </summary>
        private async Task RequireReadableAsync(Problem problem, CancellationToken ct)
        {
            if (await permissions.HasAsync(Permissions.ProblemReadAll, null, ct)) return;

            var user = await currentUser.RequireAsync(ct);
            var readable = problem.OwnerUserId == user.Id
                || problem.Visibility == ProblemVisibility.Instance
                || (problem.Visibility == ProblemVisibility.Shared
                    && problem.SharedWith.Any(s => s.UserId == user.Id));

            // Not 403: a problem somebody may not see must not be confirmed to
            // exist by the shape of the refusal.
            if (!readable) throw new NotFoundException("Problem");
        }

        public async Task<ManagedProblemDto> UpdateAsync(Guid id, ProblemInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemUpdate, null, ct);
            var problem = await LoadAsync(id, ct);
            await RequireReadableAsync(problem, ct);

            if (input.Slug is { } raw && raw.Trim() is { Length: > 0 } slug && slug != problem.Slug)
            {
                // Renaming into the namespace is the same act as creating in it,
                // and a rule that only guards creation is one somebody walks
                // around on their second try.
                await RefuseReservedPrefixAsync(slug, ct);
                if (await context.Problems.AnyAsync(p => p.Slug.ToLower() == slug.ToLower() && p.Id != id, ct))
                {
                    throw new ConflictException("A problem with that slug already exists", "problem.slug.taken");
                }
                problem.Slug = slug;
            }

            if (input.Name?.Trim() is { Length: > 0 } name) problem.Name = name;
            // The type is not editable: every renderer and every stored package
            // was chosen for it, and changing it would leave both pointing at a
            // problem they cannot read.

            await context.SaveChangesAsync(ct);
            var announced = await ManagedAsync(problem, ct);
            await AnnounceProblemAsync(announced, null, ct);
            return announced;
        }

        /// <summary>
        /// Refused while the problem is attached anywhere — a rule the database
        /// enforces on its own through <c>DeleteBehavior.Restrict</c>, and one
        /// this method answers with a code the Client understands.
        /// </summary>
        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemDelete, null, ct);
            var problem = await LoadAsync(id, ct);
            await RequireReadableAsync(problem, ct);

            var attached = await context.SeriesProblems.CountAsync(sp => sp.ProblemId == id, ct);
            if (attached > 0)
            {
                throw new ConflictException(
                    "This problem is attached to an activity. Archive it instead of deleting it.",
                    "problem.attached");
            }

            var removed = Wire.Id(problem.Id);
            context.Problems.Remove(problem);
            await context.SaveChangesAsync(ct);
            await AnnounceProblemAsync(null, removed, ct);
        }

        /// <summary>
        /// Archiving retires a problem: gone from the attach picker and taking no
        /// new versions, while every assignment already using it keeps working.
        /// </summary>
        public async Task<ManagedProblemDto> SetArchivedAsync(Guid id, bool archived, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemArchive, null, ct);
            var problem = await LoadAsync(id, ct);
            await RequireReadableAsync(problem, ct);

            problem.ArchivedAt = archived ? clock.GetUtcNow().UtcDateTime : null;
            await context.SaveChangesAsync(ct);
            var announced = await ManagedAsync(problem, ct);
            await AnnounceProblemAsync(announced, null, ct);
            return announced;
        }

        /// <summary>
        /// Copies <b>only the newest version</b>, as version 1 of a new problem.
        /// <para>
        /// Duplicating the history would copy notes about changes that never
        /// happened to this problem. What a person wants from "duplicate" is a
        /// starting point, not somebody else's past.
        /// </para>
        /// </summary>
        public async Task<ManagedProblemDto> DuplicateAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemCreate, null, ct);
            var source = await LoadAsync(id, ct);
            await RequireReadableAsync(source, ct);
            var user = await currentUser.RequireAsync(ct);

            var slug = await FreeSlugAsync(source.Slug, ct);

            var copy = new Problem
            {
                Slug = slug,
                Name = source.Name + " (copy)",
                Type = source.Type,
                OwnerUserId = user.Id,
                // Carried, because a copy of a problem judged elsewhere is also
                // judged elsewhere. Dropping it here would quietly produce a
                // problem the Server believes is local and no Runner can take.
                External = source.External,
                // Private, whatever the original was: a copy is a draft, and
                // inheriting an instance-wide visibility would publish it by
                // accident.
                Visibility = ProblemVisibility.Private,
            };
            context.Problems.Add(copy);

            var newest = await context.ProblemVersions
                .Include(v => v.Files)
                .Where(v => v.ProblemId == id)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(ct);

            if (newest is not null)
            {
                var version = new ProblemVersion
                {
                    ProblemId = copy.Id,
                    Version = 1,
                    CreatedByUserId = user.Id,
                    Note = $"Copied from {source.Slug} v{newest.Version}",
                    Props = newest.Props,
                };
                context.ProblemVersions.Add(version);

                // References, not copies: the bytes are immutable and shared, so
                // duplicating a problem costs nothing in storage.
                foreach (var file in newest.Files)
                {
                    context.FileReferences.Add(new FileReference
                    {
                        FileId = file.FileId,
                        OwnerKind = FileOwnerKind.ProblemVersion,
                        ProblemVersionId = version.Id,
                        Scope = file.Scope,
                        Name = file.Name,
                        Language = file.Language,
                    });
                }
            }

            await context.SaveChangesAsync(ct);
            return await ManagedAsync(copy, ct);
        }

        private async Task<string> FreeSlugAsync(string basis, CancellationToken ct)
        {
            for (var suffix = 2; suffix < 100; suffix++)
            {
                var candidate = $"{basis}-{suffix}";
                if (candidate.Length > 32) candidate = candidate[^32..];
                if (!await context.Problems.AnyAsync(p => p.Slug == candidate, ct)) return candidate;
            }
            throw new ConflictException("Could not find a free slug for the copy", "problem.slug.exhausted");
        }

        public async Task<ManagedProblemDto> SetVisibilityAsync(
            Guid id, string visibility, IReadOnlyList<string>? sharedWith, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemShare, null, ct);
            var problem = await LoadAsync(id, ct);
            await RequireReadableAsync(problem, ct);

            problem.Visibility = visibility switch
            {
                "shared" => ProblemVisibility.Shared,
                "instance" => ProblemVisibility.Instance,
                _ => ProblemVisibility.Private,
            };

            var existing = await context.ProblemShares.Where(s => s.ProblemId == id).ToListAsync(ct);
            context.ProblemShares.RemoveRange(existing);

            // The list is meaningful only under `shared`. Keeping it otherwise
            // would leave a stale answer to "who else may see this" that comes
            // back the moment somebody flips the setting.
            if (problem.Visibility == ProblemVisibility.Shared && sharedWith is not null)
            {
                foreach (var userId in sharedWith.Distinct())
                {
                    if (!await context.Users.AnyAsync(u => u.Id == userId, ct)) continue;
                    context.ProblemShares.Add(new ProblemShare { ProblemId = id, UserId = userId });
                }
            }

            await context.SaveChangesAsync(ct);
            var shared = await ManagedAsync(await LoadAsync(id, ct), ct);
            await AnnounceProblemAsync(shared, null, ct);
            return shared;
        }

        public async Task<IReadOnlyList<ManagedProblemVersionDto>> ListVersionsAsync(
            Guid problemId, CancellationToken ct)
        {
            var problem = await LoadAsync(problemId, ct);
            await RequireReadableAsync(problem, ct);

            var versions = await context.ProblemVersions
                .AsNoTracking()
                .Include(v => v.Files).ThenInclude(f => f.File)
                .Include(v => v.CreatedBy)
                .Where(v => v.ProblemId == problemId)
                .OrderByDescending(v => v.Version)
                .ToListAsync(ct);

            return versions.Select(v => Projections.ManagedVersion(
                v,
                v.CreatedBy is null ? null : Projections.DisplayName(v.CreatedBy),
                file => $"/api/v1/files/{Wire.Id(file.FileId)}")).ToList();
        }

        public async Task<IReadOnlyList<StatementRefDto>> ContentAsync(
            Guid problemId, Guid versionId, CancellationToken ct)
        {
            var problem = await LoadAsync(problemId, ct);
            await RequireReadableAsync(problem, ct);

            var statements = await context.FileReferences
                .AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.ProblemVersionId == versionId)
                .ToListAsync(ct);

            return statements
                .Where(r => PackageNames.IsStatement(r.Name))
                .Select(Projections.Statement)
                .ToList();
        }

        /// <summary>
        /// The Runner archive. Null when the version has none — a version nobody
        /// has finished preparing is not a failure.
        /// </summary>
        public async Task<(Stream Bytes, string Name)?> PackageAsync(
            Guid problemId, Guid versionId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProblemUpdate, null, ct);
            var problem = await LoadAsync(problemId, ct);
            await RequireReadableAsync(problem, ct);

            var reference = await context.FileReferences
                .AsNoTracking()
                .Include(r => r.File)
                .FirstOrDefaultAsync(
                    r => r.ProblemVersionId == versionId && r.Name == PackageNames.Archive, ct);

            if (reference?.File is null) return null;

            // A package is the largest thing this product stores, so this is the
            // one caller that most obviously must not receive an array.
            return (await files.OpenAsync(reference.File, ct), reference.File.Name);
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
            await lockdown.RequireReachableAsync(activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            var assignment = await context.SeriesProblems
                .Include(sp => sp.Series)
                .Include(sp => sp.Problem)
                .FirstOrDefaultAsync(sp => sp.ActivityId == activity.Id && sp.Slug == problemSlug, ct)
                ?? throw new NotFoundException("Problem");

            // Its own series, checked by name: an activity may be reachable while
            // one round inside it is not. Hidden answers the reason and nothing
            // about the round; displaced names what displaced it.
            var state = await lockdown.ForReaderAsync(ct);
            if (state.IsHidden(assignment.SeriesId))
            {
                throw new ForbiddenActionException(
                    "This round is only available from a permitted address", LockdownCodes.Address);
            }
            if (state.IsLocked(activity.Id, assignment.Series!.Importance))
            {
                throw new ForbiddenActionException(
                    $"Locked while \"{state.DisplacerFor(activity.Id)?.SeriesName}\" is running",
                    LockdownCodes.Displaced);
            }

            if (!gate.MayReadProblems(assignment.Series!, activity)) throw new NotFoundException("Problem");

            var version = await ResolveVersionAsync(assignment, ct)
                ?? throw new NotFoundException("Problem");

            var files = await context.FileReferences.AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.ProblemVersionId == version.Id && r.Scope == FileScope.Participant)
                .ToListAsync(ct);

            // **The contestant's, not the person's**, and it feeds both the best
            // score shown and the allowance left — so somebody in a group reads
            // the group's standing and the group's remaining attempts, which is
            // what they actually have. `Services/Contestant` owns the rule.
            var group = await Contestant.GroupAsync(context, activity.Id, user.Id, ct);
            var mine = await Contestant
                .Sent(context.Submissions.AsNoTracking()
                    .Where(s => s.SeriesProblemId == assignment.Id), user.Id, group)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            var (scale, outOf) = Scoring.BestOf(mine);
            var maxPoints = Scoring.Scale(assignment, outOf);
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
                // The three documents, each to the reader it is for. The Server
                // reads none of them: it checked the envelope and the ceiling
                // when a manager wrote them, and that is the whole of its
                // involvement.
                Config = Projections.Opaque(assignment.Config),
                Spec = Projections.Opaque(assignment.Spec),
                Props = Projections.Opaque(assignment.Props),
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
