using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IDocumentService
    {
        Task PublishAsync(
            FileOwnerKind ownerKind, Guid ownerId, string kind,
            PublishDocumentInputDto input, CancellationToken ct);
        Task UnpublishAsync(FileOwnerKind ownerKind, Guid ownerId, string kind, CancellationToken ct);
        Task<IReadOnlyList<FileReference>> HistoryAsync(
            FileOwnerKind ownerKind, Guid ownerId, string kind, CancellationToken ct);
    }

    /// <summary>
    /// Publishing a document, for an instance and for an activity alike.
    /// <para>
    /// One mechanism with two owners, not two mechanisms. A document is a
    /// <see cref="FileReference"/> whose <c>Name</c> is the kind and whose
    /// <c>Language</c> is the translation — the same shape a problem statement
    /// uses, because it is the same thing: text somebody wrote, stored as a file
    /// and pointed at.
    /// </para>
    /// <para>
    /// Publishing <b>adds</b> a revision and supersedes the last; it replaces
    /// none. "Which policy was in force on the third of August" is a question
    /// that gets asked about a privacy policy for real, and by somebody who is
    /// owed an answer.
    /// </para>
    /// </summary>
    public class DocumentService(
        ApplicationDbContext context,
        TimeProvider clock
    ) : IDocumentService
    {
        public async Task PublishAsync(
            FileOwnerKind ownerKind, Guid ownerId, string kind,
            PublishDocumentInputDto input, CancellationToken ct)
        {
            if (input.Statements is not { Count: > 0 })
            {
                throw new ValidationException(
                    "A document with no text is a document nobody can read", "document.empty");
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var validFrom = ActivityService.ParseInstant(input.ValidFrom) ?? now;

            // Supersede what this kind currently publishes, in the languages
            // being replaced. Marked, never deleted — deleting would leave the
            // file it names unreferenced, and an unreferenced file goes in
            // twenty-four hours, which would make the history unreachable.
            var languages = input.Statements.Select(s => s.Language).ToList();

            var current = await Owned(ownerKind, ownerId)
                .Where(r => r.Name == kind && r.SupersededAt == null)
                .ToListAsync(ct);

            foreach (var reference in current)
            {
                if (languages.Contains(reference.Language)) reference.SupersededAt = now;
            }

            foreach (var statement in input.Statements)
            {
                if (!Guid.TryParse(statement.FileId, out var fileId)
                    || !await context.Files.AnyAsync(f => f.Id == fileId, ct))
                {
                    throw new ValidationException("That file is not stored", "file.missing");
                }

                var reference = new FileReference
                {
                    FileId = fileId,
                    OwnerKind = ownerKind,
                    Scope = FileScope.Participant,
                    Name = kind,
                    Language = statement.Language,
                    ValidFrom = validFrom,
                };
                Assign(reference, ownerKind, ownerId);
                context.FileReferences.Add(reference);
            }

            await context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Withdrawing removes the references and nothing else — the revisions
        /// stay readable under their dates. Which documents exist is read from
        /// what has a current reference, and from nowhere else.
        /// </summary>
        public async Task UnpublishAsync(
            FileOwnerKind ownerKind, Guid ownerId, string kind, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var current = await Owned(ownerKind, ownerId)
                .Where(r => r.Name == kind && r.SupersededAt == null)
                .ToListAsync(ct);

            foreach (var reference in current) reference.SupersededAt = now;
            await context.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<FileReference>> HistoryAsync(
            FileOwnerKind ownerKind, Guid ownerId, string kind, CancellationToken ct) =>
            await Owned(ownerKind, ownerId)
                .AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.Name == kind)
                .OrderByDescending(r => r.ValidFrom ?? r.CreatedAt)
                .ToListAsync(ct);

        private IQueryable<FileReference> Owned(FileOwnerKind ownerKind, Guid ownerId) =>
            ownerKind switch
            {
                FileOwnerKind.ActivityDocument => context.FileReferences
                    .Where(r => r.OwnerKind == ownerKind && r.ActivityId == ownerId),
                FileOwnerKind.InstanceDocument or FileOwnerKind.InstanceLogo => context.FileReferences
                    .Where(r => r.OwnerKind == ownerKind && r.InstanceId == ownerId),
                _ => throw new InvalidOperationException($"{ownerKind} does not publish documents"),
            };

        private static void Assign(FileReference reference, FileOwnerKind ownerKind, Guid ownerId)
        {
            switch (ownerKind)
            {
                case FileOwnerKind.ActivityDocument:
                    reference.ActivityId = ownerId;
                    break;
                case FileOwnerKind.InstanceDocument:
                case FileOwnerKind.InstanceLogo:
                    reference.InstanceId = ownerId;
                    break;
                default:
                    throw new InvalidOperationException($"{ownerKind} does not publish documents");
            }
        }
    }
}
