using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IInstanceService
    {
        Task<InstanceInfoDto> GetAsync(CancellationToken ct);
        Task<Instance> EnsureAsync(CancellationToken ct);
    }

    /// <summary>
    /// What the installation admits to a screen nobody has signed in to.
    /// <para>
    /// Read on every arrival, so it carries <b>references</b> to the documents
    /// rather than the documents: a privacy policy is tens of kilobytes per
    /// language, and the reader needs one of them, once.
    /// </para>
    /// </summary>
    public class InstanceService(ApplicationDbContext context, TimeProvider clock) : IInstanceService
    {
        public async Task<Instance> EnsureAsync(CancellationToken ct)
        {
            var instance = await context.Instance.FirstOrDefaultAsync(ct);
            if (instance is not null) return instance;

            // The row is created on first use rather than by the migration, so a
            // database restored from a dump that predates it still works. The
            // check constraint keeps there being only ever one.
            instance = new Instance { Id = Instance.SingletonId };
            context.Instance.Add(instance);
            await context.SaveChangesAsync(ct);
            return instance;
        }

        public async Task<InstanceInfoDto> GetAsync(CancellationToken ct)
        {
            var instance = await EnsureAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;

            var references = await context.FileReferences
                .AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.InstanceId == instance.Id && r.SupersededAt == null)
                .ToListAsync(ct);

            // The reader is served the newest revision whose date has passed,
            // which is what lets an operator publish terms ahead of the day they
            // take effect.
            var documents = references
                .Where(r => r.OwnerKind == FileOwnerKind.InstanceDocument)
                .Where(r => r.ValidFrom is null || r.ValidFrom <= now)
                .GroupBy(r => (r.Name, r.Language))
                .Select(g => g.OrderByDescending(r => r.ValidFrom ?? DateTime.MinValue).First())
                .Select(r => new InstanceDocumentRefDto
                {
                    Kind = r.Name,
                    Language = r.Language,
                    ValidFrom = Wire.At(r.ValidFrom),
                    // Nothing ships with the software yet, so nothing here is a
                    // template. The field stays because the screen has to be able
                    // to say "this names the wrong controller" the day one does.
                    IsTemplate = false,
                    FileId = Wire.Id(r.FileId),
                    Sha256 = r.File?.Sha256 ?? "",
                    SizeBytes = r.File?.SizeBytes ?? 0,
                })
                .ToList();

            var logos = references.Where(r => r.OwnerKind == FileOwnerKind.InstanceLogo).ToList();
            var defaultLogo = logos.FirstOrDefault(r => r.Language is null);

            // Only what a sign-in button needs. The projection selects two
            // columns rather than loading the row and picking from it, so a
            // field added to the entity later cannot arrive here by accident —
            // and this answer is served to anybody who can reach the login page.
            var providers = await context.IdentityProviders
                .AsNoTracking()
                .Where(p => p.Enabled)
                .OrderBy(p => p.DisplayName)
                .Select(p => new PublicProviderDto { Slug = p.Slug, DisplayName = p.DisplayName })
                .ToListAsync(ct);

            return new InstanceInfoDto
            {
                Providers = providers,
                AccountDeletionEnabled = instance.AccountDeletionEnabled,
                Name = instance.Name,
                LocalRegistrationEnabled = instance.LocalRegistrationEnabled,
                RequireEmail = instance.RequireEmail,
                RequireConfirmedEmail = instance.RequireConfirmedEmail,
                ExternalJudgingEnabled = instance.ExternalJudgingEnabled,
                SeriesRestrictionsEnabled = instance.SeriesRestrictionsEnabled,
                Documents = documents,
                Logo = defaultLogo is null ? null : Logo(defaultLogo),
                LogoTranslations = logos
                    .Where(r => r.Language is not null)
                    .Select(r => new LocalisedLogoDto { Language = r.Language!, Logo = Logo(r) })
                    .ToList() is { Count: > 0 } translations ? translations : null,
                ShowLogo = instance.ShowLogo,
                ShowLocalSignIn = instance.ShowLocalSignIn,
            };
        }

        private static InstanceLogoDto Logo(FileReference reference) => new()
        {
            Url = $"/api/v1/files/{Wire.Id(reference.FileId)}",
            MimeType = reference.File?.MimeType ?? "image/svg+xml",
            SizeBytes = reference.File?.SizeBytes ?? 0,
            Sha256 = reference.File?.Sha256 ?? "",
        };
    }
}
