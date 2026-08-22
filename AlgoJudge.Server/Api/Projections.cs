using System.Text.Json;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Api
{
    /// <summary>
    /// Entity to wire, in one place.
    /// <para>
    /// Written out rather than mapped by convention. A convention-based mapper
    /// copies whatever it finds, which on the day somebody adds
    /// <c>Activity.JoinPassword</c> quietly copies it into the participant's
    /// model too. Every field here was typed on purpose, and the ones that are
    /// absent are absent on purpose.
    /// </para>
    /// </summary>
    public static class Projections
    {
        private static readonly JsonSerializerOptions Relaxed = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// An opaque document on its way out. Absent stays absent — never `{}`,
        /// which is a second way of saying the same nothing.
        /// </summary>
        public static object? Opaque(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(json, Relaxed);
            }
            catch (JsonException)
            {
                // Stored without being read, so it can only have been written by
                // something that did not check. Dropping it beats failing the
                // request that happens to load it.
                return null;
            }
        }

        // ── enums, as the Client spells them ──────────────────────────────────

        public static string Wire(ScoreVisibility v) => v switch
        {
            ScoreVisibility.Everyone => "everyone",
            ScoreVisibility.ParticipantOnly => "participantOnly",
            _ => "managersOnly",
        };

        public static string Wire(JoinPolicy p) => p switch
        {
            JoinPolicy.Open => "open",
            JoinPolicy.Password => "password",
            _ => "closed",
        };

        public static string Wire(AttachmentVisibility v) =>
            v == AttachmentVisibility.Participant ? "participant" : "managersOnly";

        public static string Wire(FileScope s) => s switch
        {
            FileScope.Manager => "manager",
            FileScope.Runner => "runner",
            _ => "participant",
        };

        public static string Wire(ProblemVisibility v) => v switch
        {
            ProblemVisibility.Shared => "shared",
            ProblemVisibility.Instance => "instance",
            _ => "private",
        };

        public static string Wire(EvaluationJobState s) => s switch
        {
            EvaluationJobState.Running => "running",
            EvaluationJobState.Completed => "completed",
            EvaluationJobState.Failed => "failed",
            EvaluationJobState.Cancelled => "cancelled",
            _ => "queued",
        };

        public static string Wire(DocumentKind k) => k switch
        {
            DocumentKind.Welcome => "welcome",
            DocumentKind.Home => "home",
            DocumentKind.Rules => "rules",
            DocumentKind.Terms => "terms",
            DocumentKind.Privacy => "privacy",
            DocumentKind.Cookies => "cookies",
            _ => "accessibility",
        };

        public static string Wire(RunnerState s) => s switch
        {
            RunnerState.Approved => "approved",
            RunnerState.Revoked => "revoked",
            _ => "pendingApproval",
        };

        // ── people ────────────────────────────────────────────────────────────

        /// <summary>
        /// How a person is written wherever one is shown: first and last name
        /// when they were given, the <b>login</b> otherwise.
        /// <para>
        /// Computed here so every screen says the same thing about the same
        /// person. An installation that wants an anonymous scoreboard issues
        /// opaque logins and leaves the name fields empty — the choice sits with
        /// whoever creates accounts, not with the model.
        /// </para>
        /// </summary>
        public static string DisplayName(User user)
        {
            if (user.Anonymized) return user.UserName ?? "—";
            var full = $"{user.FirstName} {user.LastName}".Trim();
            return full.Length > 0 ? full : user.UserName ?? "—";
        }

        public static SessionDto Session(User user) => new()
        {
            UserId = user.Id,
            Username = user.UserName ?? "",
            FirstName = user.FirstName,
            LastName = user.LastName,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            IsLocal = IsLocal(user),
        };

        /// <summary>
        /// Whether this account is <b>this installation's</b>, and therefore
        /// whether the person may change their own name, login, address and
        /// password here.
        /// <para>
        /// <b>One rule: it is local when it has a local credential.</b> An account
        /// provisioned by a provider carries no password, so its profile belongs
        /// to whoever owns the identity — editing it here would either fail at
        /// the next sign-in or quietly disagree with the directory. An account
        /// that has both a password and a link stays local: somebody deliberately
        /// gave it a credential of its own.
        /// </para>
        /// <para>
        /// Defined once, here, because the Client greys its inputs on this and
        /// the Server refuses on it — and two definitions of "local" would be two
        /// answers to whose the account is.
        /// </para>
        /// </summary>
        public static bool IsLocal(User user) => user.PasswordHash is not null;

        // ── files ─────────────────────────────────────────────────────────────

        public static UploadedFileDto Uploaded(Database.Models.File file) => new()
        {
            Id = Contracts.Wire.Id(file.Id),
            Name = file.Name,
            MimeType = file.MimeType,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256,
            CreatedAt = Contracts.Wire.At(file.CreatedAt),
        };

        public static StatementRefDto Statement(FileReference reference) => new()
        {
            Name = reference.Name,
            Language = reference.Language,
            FileId = Contracts.Wire.Id(reference.FileId),
            Sha256 = reference.File?.Sha256 ?? "",
            SizeBytes = reference.File?.SizeBytes ?? 0,
        };

        public static SubmissionFileDto SubmissionFile(FileReference reference) => new()
        {
            Name = reference.Name,
            FileName = reference.File?.Name ?? reference.Name,
            Language = reference.Language,
            FileId = Contracts.Wire.Id(reference.FileId),
            Sha256 = reference.File?.Sha256 ?? "",
            SizeBytes = reference.File?.SizeBytes ?? 0,
        };

        public static AttachmentDto Attachment(FileReference reference, string url) => new()
        {
            Name = reference.Name,
            MimeType = reference.File?.MimeType ?? "application/octet-stream",
            SizeBytes = reference.File?.SizeBytes ?? 0,
            Url = url,
            Sha256 = reference.File?.Sha256 ?? "",
        };

        // ── activity ──────────────────────────────────────────────────────────

        public static ActivityDocumentRefDto ActivityDocument(FileReference reference) => new()
        {
            Kind = reference.Name,
            Language = reference.Language,
            ValidFrom = Contracts.Wire.At(reference.ValidFrom),
            FileId = Contracts.Wire.Id(reference.FileId),
            Sha256 = reference.File?.Sha256 ?? "",
            SizeBytes = reference.File?.SizeBytes ?? 0,
        };

        /// <summary>
        /// Where the activity sits on the clock. Derived, because it is a
        /// statement about now and storing it would mean a row that goes stale
        /// on its own.
        /// </summary>
        public static string ActivityState(Activity activity, DateTime now)
        {
            if (activity.EndDate is { } end && end <= now) return "finished";
            if (activity.StartDate is { } start && start > now) return "upcoming";
            return "ongoing";
        }

        public static ActivityDto Activity(
            Activity activity,
            string membership,
            DateTime now,
            IEnumerable<FileReference> documents) => new()
        {
            Id = Contracts.Wire.Id(activity.Id),
            Slug = activity.Slug,
            Name = activity.Name,
            Type = activity.Type,
            RankingType = activity.RankingType,
            TimeZone = activity.TimeZone,
            State = ActivityState(activity, now),
            Membership = membership,
            JoinPolicy = Wire(activity.JoinPolicy),
            ScoreVisibility = Wire(activity.ScoreVisibility),
            StartDate = Contracts.Wire.At(activity.StartDate),
            EndDate = Contracts.Wire.At(activity.EndDate),
            HideEndedSeriesProblems = activity.HideEndedSeriesProblems,
            Documents = documents.Select(ActivityDocument).ToList(),
            Modules = new ActivityModulesDto { Questions = activity.HasQuestions },
            // Deliberately absent from the participant's model: the join
            // password, the attachment table, every ceiling, and the counts.
            Props = Opaque(activity.Props),
        };

        public static ManagedActivityDto ManagedActivity(
            Activity activity,
            IEnumerable<FileReference> documents,
            int seriesCount,
            int problemCount,
            int participantCount) => new()
            {
                Id = Contracts.Wire.Id(activity.Id),
                Slug = activity.Slug,
                Name = activity.Name,
                Type = activity.Type,
                RankingType = activity.RankingType,
                TimeZone = activity.TimeZone,
                StartDate = Contracts.Wire.At(activity.StartDate),
                EndDate = Contracts.Wire.At(activity.EndDate),
                Modules = new ActivityModulesDto { Questions = activity.HasQuestions },
                Documents = documents.Select(ActivityDocument).ToList(),
                ScoreVisibility = Wire(activity.ScoreVisibility),
                AttachmentVisibility = activity.AttachmentRules
                    .OrderBy(r => r.Name, StringComparer.Ordinal)
                    .Select(r => new AttachmentRuleDto { Name = r.Name, Visibility = Wire(r.Visibility) })
                    .ToList(),
                Props = Opaque(activity.Props),
                JoinPolicy = Wire(activity.JoinPolicy),
                Unlisted = activity.Unlisted,
                JoinPassword = activity.JoinPolicy == Database.Models.JoinPolicy.Password ? activity.JoinPassword : null,
                HideEndedSeriesProblems = activity.HideEndedSeriesProblems,
                MaxUploadBytes = activity.MaxUploadBytes,
                MaxAttachments = activity.MaxAttachments,
                MaxSubmissionsPerProblem = activity.MaxSubmissionsPerProblem,
                ArchivedAt = Contracts.Wire.At(activity.ArchivedAt),
                SeriesCount = seriesCount,
                ProblemCount = problemCount,
                ParticipantCount = participantCount,
            };

        // ── series ────────────────────────────────────────────────────────────

        public static ManagedSeriesDto ManagedSeries(
            Series series,
            IEnumerable<ManagedSeriesProblemDto> problems) => new()
            {
                Id = Contracts.Wire.Id(series.Id),
                ActivityId = Contracts.Wire.Id(series.ActivityId),
                Slug = series.Slug,
                Name = series.Name,
                Order = series.Order,
                StartDate = Contracts.Wire.At(series.StartDate),
                EndDate = Contracts.Wire.At(series.EndDate),
                IsOpen = series.IsOpen,
                PausedAt = Contracts.Wire.At(series.PausedAt),
                HideProblemsWhilePaused = series.HideProblemsWhilePaused,
                RevealProblemCount = series.RevealProblemCount,
                RankingFreezeAt = Contracts.Wire.At(series.RankingFreezeAt),
                RankingRevealAt = Contracts.Wire.At(series.RankingRevealAt),
                RankingVisibleFrom = Contracts.Wire.At(series.RankingVisibleFrom),
                RankingVisibleTo = Contracts.Wire.At(series.RankingVisibleTo),
                Problems = problems.ToList(),
            };

        // ── problems ──────────────────────────────────────────────────────────

        public static ManagedProblemDto ManagedProblem(
            Problem problem, int currentVersion, int versionCount, int attachedCount, string ownerName) => new()
            {
                Id = Contracts.Wire.Id(problem.Id),
                Slug = problem.Slug,
                Name = problem.Name,
                Type = problem.Type,
                External = problem.External,
                OwnerUserId = problem.OwnerUserId,
                OwnerName = ownerName,
                Visibility = Wire(problem.Visibility),
                SharedWith = problem.SharedWith.Select(s => s.UserId).ToList(),
                ArchivedAt = Contracts.Wire.At(problem.ArchivedAt),
                CurrentVersion = currentVersion,
                VersionCount = versionCount,
                CreatedAt = Contracts.Wire.At(problem.CreatedAt),
                AttachedCount = attachedCount,
            };

        public static ManagedProblemVersionDto ManagedVersion(
            ProblemVersion version, string? createdByName, Func<FileReference, string?> url) => new()
            {
                Id = Contracts.Wire.Id(version.Id),
                Version = version.Version,
                CreatedAt = Contracts.Wire.At(version.CreatedAt),
                CreatedByName = createdByName,
                Note = version.Note,
                HasPackage = version.Files.Any(f => f.Name == PackageNames.Archive),
                Files = version.Files
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => new ProblemFileDto
                    {
                        Name = f.Name,
                        Scope = Wire(f.Scope),
                        MimeType = f.File?.MimeType ?? "application/octet-stream",
                        SizeBytes = f.File?.SizeBytes ?? 0,
                        Sha256 = f.File?.Sha256 ?? "",
                        Url = url(f),
                    })
                    .ToList(),
            };
    }

    /// <summary>
    /// The names a problem version keeps by convention. Well-known to the Client
    /// and to the Runner; the Server only stores them.
    /// </summary>
    public static class PackageNames
    {
        public const string Archive = "package.zip";
        public const string Samples = "examples.zip";
        public const string Statement = "content.md";
        public const string StatementPrefix = "content";

        /// <summary>
        /// Whether a name is a statement. A statement is never an attachment —
        /// it is excluded wherever attachments are listed, offered or refused,
        /// by this one predicate rather than by each screen remembering.
        /// </summary>
        public static bool IsStatement(string name) =>
            name.StartsWith(StatementPrefix + ".", StringComparison.Ordinal)
            || name.StartsWith(StatementPrefix + "-", StringComparison.Ordinal);

        public static bool IsPackage(string name) =>
            name == Archive || name == Samples;

        /// <summary>`content.md`, or `content-en.md` for a translation.</summary>
        public static string StatementName(string? language) =>
            string.IsNullOrWhiteSpace(language) ? Statement : $"{StatementPrefix}-{language}.md";
    }
}
