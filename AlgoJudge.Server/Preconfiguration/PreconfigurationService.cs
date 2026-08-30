using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Preconfiguration
{
    /// <summary>One thing the files would change, or have changed.</summary>
    public sealed record PreconfigurationChange(string Target, string Current, string Proposed);

    public sealed record PreconfigurationPlan(
        string? Directory,
        bool Applied,
        IReadOnlyList<PreconfigurationChange> Changes,
        IReadOnlyList<string> Warnings);

    public interface IPreconfiguration
    {
        /// <summary>Whether this installation names a directory at all.</summary>
        bool Configured { get; }

        /// <summary>What applying would do. Writes nothing.</summary>
        Task<PreconfigurationPlan> PlanAsync(CancellationToken ct);

        /// <summary>The same, and does it.</summary>
        Task<PreconfigurationPlan> ApplyAsync(CancellationToken ct);
    }

    /// <summary>
    /// An installation configured from files on disk.
    /// <para>
    /// <b>Applied at the first start, and after that only when asked.</b> A fresh
    /// database has nothing an administrator chose, so there is nothing to
    /// overwrite; an installation that has been running has, and a file re-read
    /// on every boot would undo it silently. `Program` owns the first half,
    /// <c>aj-admin config apply</c> the second.
    /// </para>
    /// <para>
    /// <b>It adds; it never withdraws.</b> A setting the file does not mention is
    /// left alone rather than reset, and a document the directory does not carry
    /// stays published. Withdrawing is a panel act, done by somebody who meant
    /// it — the same reading <c>InstanceSettingsInputDto</c> already gives an
    /// absent field, for the same reason.
    /// </para>
    /// <para>
    /// <b>Nothing records what was applied.</b> The comparison is the checksum of
    /// each file against the checksum of what is published, which is the bundle's
    /// own "is this already here" test. So there is no migration, and no stored
    /// digest that can drift away from what the database actually holds.
    /// </para>
    /// </summary>
    public sealed class PreconfigurationService(
        IConfiguration configuration,
        ApplicationDbContext context,
        IInstanceService instances,
        IDocumentService documents,
        IFileService files,
        IEventHub events,
        ILogger<PreconfigurationService> logger
    ) : IPreconfiguration
    {
        private string? Path =>
            configuration[PreconfigurationFile.PathSetting] is { Length: > 0 } named
                ? named.Trim()
                : null;

        public bool Configured => Path is not null;

        public Task<PreconfigurationPlan> PlanAsync(CancellationToken ct) => RunAsync(false, ct);

        public Task<PreconfigurationPlan> ApplyAsync(CancellationToken ct) => RunAsync(true, ct);

        /// <summary>
        /// One walk for both, deliberately: a dry run that took a different path
        /// from the act would be a dry run nobody could trust.
        /// </summary>
        private async Task<PreconfigurationPlan> RunAsync(bool apply, CancellationToken ct)
        {
            var directory = Path ?? throw new ValidationException(
                $"This installation is not configured from disk: {PreconfigurationFile.PathSetting} "
                + "names no directory. Set AJ_Preconfiguration__Path to a mounted directory holding "
                + $"{PreconfigurationFile.FileName}.",
                "preconfiguration.path");

            // Everything is validated here, before a single write. A directory
            // that is going to be refused is refused whole.
            var source = PreconfigurationFile.Read(directory);
            var instance = await instances.EnsureAsync(ct);

            var changes = new List<PreconfigurationChange>();
            Settings(source.Instance, instance, changes, apply);

            // The panel writes this row too. An apply that lost that race
            // writes nothing and says so, rather than putting the file's answer
            // over a manager's — and running it again is free.
            if (apply && changes.Count > 0) await Concurrency.SaveAsync(context, ct);

            await FilesAsync(source, instance, changes, apply, ct);

            if (apply && changes.Count > 0)
            {
                logger.LogInformation(
                    "Pre-configuration applied from {Directory}: {Count} change(s).",
                    directory, changes.Count);
                await AnnounceAsync(ct);
            }

            return new PreconfigurationPlan(directory, apply, changes, source.Warnings);
        }

        /// <summary>
        /// The settings, one comparison each. A field the file leaves out is
        /// never read here, which is the whole of "absent means leave alone".
        /// </summary>
        private static void Settings(
            InstanceSection stated, Instance instance, List<PreconfigurationChange> changes, bool apply)
        {
            Compare("instance.name", instance.Name ?? "", stated.Name, value => instance.Name = value);

            Flag("instance.localRegistrationEnabled", instance.LocalRegistrationEnabled,
                stated.LocalRegistrationEnabled, value => instance.LocalRegistrationEnabled = value);
            Flag("instance.requireEmail", instance.RequireEmail,
                stated.RequireEmail, value => instance.RequireEmail = value);
            Flag("instance.requireConfirmedEmail", instance.RequireConfirmedEmail,
                stated.RequireConfirmedEmail, value => instance.RequireConfirmedEmail = value);
            Flag("instance.showLogo", instance.ShowLogo,
                stated.ShowLogo, value => instance.ShowLogo = value);
            Flag("instance.showLocalSignIn", instance.ShowLocalSignIn,
                stated.ShowLocalSignIn, value => instance.ShowLocalSignIn = value);
            Flag("instance.accountDeletionEnabled", instance.AccountDeletionEnabled,
                stated.AccountDeletionEnabled, value => instance.AccountDeletionEnabled = value);
            Flag("instance.externalJudgingEnabled", instance.ExternalJudgingEnabled,
                stated.ExternalJudgingEnabled, value => instance.ExternalJudgingEnabled = value);
            Flag("instance.seriesRestrictionsEnabled", instance.SeriesRestrictionsEnabled,
                stated.SeriesRestrictionsEnabled, value => instance.SeriesRestrictionsEnabled = value);

            if (stated.ExternalFetchHosts is { } hosts)
            {
                var wanted = hosts
                    .Select(host => host.Trim().ToLowerInvariant())
                    .Where(host => host.Length > 0)
                    .Distinct()
                    .ToList();

                Compare(
                    "instance.externalFetchHosts",
                    string.Join(", ", instance.ExternalFetchHosts),
                    string.Join(", ", wanted),
                    _ => instance.ExternalFetchHosts = wanted);
            }

            void Flag(string target, bool current, bool? stated, Action<bool> set)
            {
                if (stated is not { } value) return;
                Compare(target, current ? "true" : "false", value ? "true" : "false", _ => set(value));
            }

            void Compare(string target, string current, string? proposed, Action<string> set)
            {
                if (proposed is null || string.Equals(current, proposed, StringComparison.Ordinal)) return;

                changes.Add(new PreconfigurationChange(target, current, proposed));
                if (apply) set(proposed);
            }
        }

        /// <summary>
        /// The pages and the mark, compared by <b>checksum</b>.
        /// <para>
        /// This is what makes re-applying safe. Publishing <i>adds</i> a
        /// revision, so a walk that republished whatever it found would grow the
        /// privacy policy's history by one entry per run — destroying the history
        /// that the versioning exists to keep.
        /// </para>
        /// </summary>
        private async Task FilesAsync(
            PreconfigurationSource source,
            Instance instance,
            List<PreconfigurationChange> changes,
            bool apply,
            CancellationToken ct)
        {
            if (source.Pages.Count == 0 && source.Logos.Count == 0
                && source.Fonts.Count == 0 && source.Theme is null) return;

            var published = await context.FileReferences
                .AsNoTracking()
                .Include(reference => reference.File)
                .Where(reference => reference.InstanceId == instance.Id && reference.SupersededAt == null)
                .ToListAsync(ct);

            // The newest revision per (kind, language) is what a reader is being
            // served, so it is what a file on disk is compared against.
            string? Current(FileOwnerKind owner, string name, string? language) => published
                .Where(reference => reference.OwnerKind == owner
                    && reference.Name == name
                    && reference.Language == language)
                .OrderByDescending(reference => reference.ValidFrom ?? DateTime.MinValue)
                .FirstOrDefault()?.File?.Sha256;

            var pending = new List<(FileOwnerKind Owner, PreconfigurationFileBytes File)>();

            foreach (var page in source.Pages)
            {
                var target = page.Language is null
                    ? $"document.{page.Kind}"
                    : $"document.{page.Kind}.{page.Language}";

                if (Differs(FileOwnerKind.InstanceDocument, page, target))
                {
                    pending.Add((FileOwnerKind.InstanceDocument, page));
                }
            }

            foreach (var logo in source.Logos)
            {
                var target = logo.Language is null ? "logo" : $"logo.{logo.Language}";

                if (Differs(FileOwnerKind.InstanceLogo, logo, target))
                {
                    pending.Add((FileOwnerKind.InstanceLogo, logo));
                }
            }

            // **Faces before the theme.** A theme is read by resolving each face
            // it names against what is stored, so a theme published ahead of its
            // own fonts would be unreadable until the next apply — and an
            // unreadable theme is no theme, which is the whole installation
            // silently back on the default.
            foreach (var font in source.Fonts)
            {
                if (Differs(FileOwnerKind.InstanceFont, font, $"font.{font.Kind}"))
                {
                    pending.Add((FileOwnerKind.InstanceFont, font));
                }
            }

            if (source.Theme is { } theme && Differs(FileOwnerKind.InstanceTheme, theme, "theme"))
            {
                pending.Add((FileOwnerKind.InstanceTheme, theme));
            }

            if (!apply || pending.Count == 0) return;

            // Grouped by owner and reference name so a kind with two changed
            // translations is published once: `PublishAsync` supersedes the
            // languages it is given and leaves the rest standing, which is
            // exactly the behaviour an unchanged translation needs.
            foreach (var group in pending.GroupBy(entry => (entry.Owner, entry.File.Kind)))
            {
                var statements = new List<NewStatementDto>();

                foreach (var (_, file) in group)
                {
                    var stored = await files.StoreAsync(
                        new MemoryStream(file.Bytes), file.FileName, file.MimeType, file.Sha256, ct);

                    statements.Add(new NewStatementDto
                    {
                        FileId = Wire.Id(stored.Id),
                        Language = file.Language,
                    });
                }

                await documents.PublishAsync(
                    group.Key.Owner, instance.Id, group.Key.Kind,
                    new PublishDocumentInputDto { Statements = statements }, ct);
            }

            // Answers whether this file has to be published, and records why.
            bool Differs(FileOwnerKind owner, PreconfigurationFileBytes file, string target)
            {
                var current = Current(owner, file.Kind, file.Language);
                if (string.Equals(current, file.Sha256, StringComparison.Ordinal)) return false;

                changes.Add(new PreconfigurationChange(
                    target, current is null ? "not published" : Short(current), Short(file.Sha256)));
                return true;
            }
        }

        /// <summary>Enough of a checksum to tell two revisions apart in a report.</summary>
        private static string Short(string sha256) => sha256[..12];

        /// <summary>
        /// The same announcement the panel makes, so a Client that is already
        /// open follows an apply instead of showing yesterday's welcome page
        /// until somebody reloads.
        /// </summary>
        private async Task AnnounceAsync(CancellationToken ct)
        {
            var info = await instances.GetAsync(ct);
            var everybody = await context.Users
                .AsNoTracking()
                .Where(user => !user.Anonymized)
                .Select(user => user.Id)
                .ToListAsync(ct);

            await events.SendToUsersAsync(
                everybody, EventTypes.InstanceChanged, new { instance = info }, ct);
        }
    }
}
