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
    /// <remarks>
    /// It reads bytes through <see cref="IBlobStoreRegistry"/> rather than
    /// through <c>IFileService</c>, and that is not a shortcut. This answer is
    /// public, so there is no authorization question to ask — and the file
    /// service carries the permission engine and the lockdown filter behind it,
    /// which would drag both into every caller that only wanted the singleton
    /// row. The seeder is one such caller, and the production-seed test is what
    /// said so.
    /// </remarks>
    public class InstanceService(
        ApplicationDbContext context,
        TimeProvider clock,
        Storage.IBlobStoreRegistry stores,
        ILogger<InstanceService> logger
    ) : IInstanceService
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
                Theme = await ThemeAsync(references, ct),
            };
        }

        /// <summary>
        /// The theme, read from the file it was published from.
        /// <para>
        /// <b>The file is read on every call rather than kept in a column.</b> One
        /// authoritative copy is worth a small blob read: a column beside the file
        /// would be a second answer to one question, and the day they disagreed
        /// nothing would say which was in force. A theme is a few hundred bytes.
        /// </para>
        /// <para>
        /// <b>A theme that cannot be read is no theme, not a broken page.</b> The
        /// bytes were validated when they were published, so this cannot normally
        /// fail — and if it does, an installation showing the default colours is a
        /// great deal better than one whose every screen answers 500. It is logged
        /// as an error, because it is one.
        /// </para>
        /// </summary>
        private async Task<InstanceThemeDto?> ThemeAsync(
            List<FileReference> references, CancellationToken ct)
        {
            var published = references
                .Where(r => r.OwnerKind == FileOwnerKind.InstanceTheme)
                .OrderByDescending(r => r.ValidFrom ?? DateTime.MinValue)
                .FirstOrDefault();

            if (published?.File is null) return null;

            var faces = references
                .Where(r => r.OwnerKind == FileOwnerKind.InstanceFont && r.File is not null)
                .ToDictionary(r => r.Name, StringComparer.Ordinal);

            try
            {
                // A store this installation is not configured for is the same
                // answer as no theme, and is logged below: the file exists and
                // this Server cannot reach it, which is not a reason to stop
                // answering "what is this installation called".
                var store = stores.Find(published.File.StorageId)
                    ?? throw new InvalidOperationException("the store this file names is not configured");

                await using var content = await store.OpenReadAsync(
                    new Storage.BlobKey(published.File.Id, published.File.Sha256), ct);
                using var reader = new StreamReader(content);
                var text = await reader.ReadToEndAsync(ct);

                var theme = ThemeDocument.Parse(text, faces.Keys.ToHashSet(StringComparer.Ordinal));

                return new InstanceThemeDto
                {
                    Light = Colours(theme.Light),
                    Dark = Colours(theme.Dark),
                    FontFamily = theme.FontFamily,
                    FontFamilyHeadings = theme.FontFamilyHeadings,
                    Fonts = (theme.Fonts ?? [])
                        .Select(face => new InstanceFontDto
                        {
                            Name = face.File!,
                            Family = face.Family!,
                            Weight = face.Weight ?? 400,
                            Style = face.Style ?? "normal",
                            Url = $"/api/v1/files/{Wire.Id(faces[face.File!].FileId)}",
                            Sha256 = faces[face.File!].File?.Sha256 ?? "",
                            SizeBytes = faces[face.File!].File?.SizeBytes ?? 0,
                        })
                        .ToList(),
                    FileId = Wire.Id(published.FileId),
                    Sha256 = published.File.Sha256,
                };
            }
            catch (Exception error)
            {
                logger.LogError(
                    error,
                    "The published theme (file {FileId}) could not be read. Serving the default.",
                    published.FileId);
                return null;
            }
        }

        private static ThemeColoursDto? Colours(ThemeColours? colours) => colours is null ? null : new()
        {
            Primary = colours.Primary,
            Secondary = colours.Secondary,
            Accent = colours.Accent,
            Link = colours.Link,
            Body = colours.Body,
            Surface = colours.Surface,
            Text = colours.Text,
            Dimmed = colours.Dimmed,
            Border = colours.Border,
            NavBackground = colours.NavBackground,
            NavText = colours.NavText,
            NavActiveBackground = colours.NavActiveBackground,
            NavActiveText = colours.NavActiveText,
            HeaderBackground = colours.HeaderBackground,
            HeaderText = colours.HeaderText,
        };

        private static InstanceLogoDto Logo(FileReference reference) => new()
        {
            Url = $"/api/v1/files/{Wire.Id(reference.FileId)}",
            MimeType = reference.File?.MimeType ?? "image/svg+xml",
            SizeBytes = reference.File?.SizeBytes ?? 0,
            Sha256 = reference.File?.Sha256 ?? "",
        };
    }
}
