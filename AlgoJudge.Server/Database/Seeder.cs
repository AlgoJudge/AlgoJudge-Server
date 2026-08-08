using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Database
{
    /// <summary>
    /// What every installation needs, and what a developer needs on top.
    /// <para>
    /// Split deliberately. The templates and the instance row are not sample
    /// data — an installation without them cannot grant anybody anything — so
    /// they are seeded everywhere. The accounts and the contest are development
    /// scaffolding and are seeded nowhere else.
    /// </para>
    /// </summary>
    public class Seeder(
        ApplicationDbContext context,
        IInstanceService instances,
        UserManager<User> users,
        ILogger<Seeder> logger)
    {
        /// <summary>
        /// The development administrator. A well-known password is only ever
        /// acceptable because this runs in Development alone — the caller passes
        /// `false` everywhere else, and nothing below the first block runs.
        /// </summary>
        public const string DevAdminLogin = "admin";
        public const string DevAdminPassword = "admin-development-only";
        public const string DevParticipantLogin = "student";
        public const string DevParticipantPassword = "student-development-only";

        public async Task EnsureAsync(bool development, CancellationToken ct = default)
        {
            await instances.EnsureAsync(ct);
            await EnsureTemplatesAsync(ct);

            if (!development) return;
            await EnsureDevelopmentDataAsync(ct);
        }

        /// <summary>
        /// The three shipped templates. Marked built-in so deleting one can be
        /// refused; their contents are copied into a grant and never referenced,
        /// so editing one later touches nobody who already used it.
        /// </summary>
        private async Task EnsureTemplatesAsync(CancellationToken ct)
        {
            var shipped = new (string Name, string Description, IReadOnlyList<string> Permissions)[]
            {
                ("participant", "Takes part: solves problems and sees their own results.",
                    Permissions.ParticipantTemplate),
                ("manager", "Runs an activity: problems, submissions, questions, enrolment.",
                    Permissions.ManagerTemplate),
                ("admin", "Administers the installation. Bypasses every check.",
                    Permissions.AdminTemplate),
            };

            foreach (var (name, description, permissions) in shipped)
            {
                var existing = await context.PermissionTemplates.FirstOrDefaultAsync(t => t.Name == name, ct);
                if (existing is not null) continue;

                context.PermissionTemplates.Add(new PermissionTemplate
                {
                    Name = name,
                    Description = description,
                    Permissions = JsonSerializer.Serialize(permissions),
                    IsBuiltIn = true,
                });
            }
            await context.SaveChangesAsync(ct);
        }

        private async Task EnsureDevelopmentDataAsync(CancellationToken ct)
        {
            if (await context.Activities.AnyAsync(ct)) return;

            logger.LogWarning(
                "Seeding development data, including accounts with well-known passwords. "
                + "This runs in Development only.");

            var admin = await EnsureUserAsync(DevAdminLogin, DevAdminPassword, "Ada", "Administrator", ct);
            var student = await EnsureUserAsync(DevParticipantLogin, DevParticipantPassword, "Stefan", "Student", ct);

            // The administrator's grant is one entry, because it bypasses the
            // rest. An administrator with a list of individual permissions is an
            // administrator who can be trimmed.
            context.Grants.Add(new Grant
            {
                UserId = admin.Id,
                ActivityId = null,
                Permissions = JsonSerializer.Serialize(Permissions.AdminTemplate),
                CreatedFromTemplate = "admin",
                IsSystem = true,
            });

            var activity = new Activity
            {
                Slug = "DEV-2026",
                Name = "Development contest",
                Type = "contest@1",
                RankingType = "icpc",
                TimeZone = "Europe/Warsaw",
                ScoreVisibility = ScoreVisibility.Everyone,
                JoinPolicy = JoinPolicy.Open,
                Unlisted = false,
                Languages = ["cpp", "python", "java"],
                MaxUploadBytes = 1024 * 1024,
                MaxAttachments = 1,
            };
            // `source` reaches its author; `log` and `details` do not, because a
            // name with no row is managers-only and these two say so out loud.
            activity.AttachmentRules.Add(new AttachmentRule
            {
                ActivityId = activity.Id, Name = "source", Visibility = AttachmentVisibility.Participant,
            });
            activity.AttachmentRules.Add(new AttachmentRule
            {
                ActivityId = activity.Id, Name = "log", Visibility = AttachmentVisibility.Participant,
            });
            context.Activities.Add(activity);

            context.Grants.Add(new Grant
            {
                UserId = student.Id,
                ActivityId = activity.Id,
                Permissions = JsonSerializer.Serialize(Permissions.ParticipantTemplate),
                CreatedFromTemplate = "participant",
                IsSystem = false,
            });

            // Somebody runs this activity, and it is not a participation. The
            // administrator would reach it through the bypass anyway; the grant
            // is here because an activity nobody manages is not a state worth
            // developing against — and because it is what makes "staff are not
            // counted among the competitors" visible in the seeded data.
            context.Grants.Add(new Grant
            {
                UserId = admin.Id,
                ActivityId = activity.Id,
                Permissions = JsonSerializer.Serialize(Permissions.ManagerTemplate),
                CreatedFromTemplate = "manager",
                IsSystem = true,
            });

            var series = new Series
            {
                ActivityId = activity.Id,
                Slug = "round-1",
                Name = "Round 1",
                Order = 1,
                StartDate = DateTime.UtcNow.AddHours(-1),
                EndDate = DateTime.UtcNow.AddDays(7),
                // Open, and marked announced: the scheduler owns transitions, and
                // seeding one as already open without the marker would make it
                // announce a round that has been running since before the process
                // started.
                IsOpen = true,
                StartAnnouncedAt = DateTime.UtcNow,
            };
            context.Series.Add(series);

            var problem = new Problem
            {
                Slug = "sum",
                Name = "Sum of two numbers",
                Type = "standard-io@1",
                OwnerUserId = admin.Id,
                Visibility = ProblemVisibility.Instance,
            };
            context.Problems.Add(problem);

            var statement = await StoreAsync(
                "content.md",
                "text/markdown",
                "# Sum of two numbers\n\nRead two integers and print their sum.\n",
                admin.Id,
                ct);

            // A placeholder archive: enough for a Runner to be handed something
            // with a checksum, not enough to judge with. A real package arrives
            // through the Client's builder.
            var package = await StoreAsync(
                "package.zip", "application/zip", "not-a-real-package", admin.Id, ct);

            var version = new ProblemVersion
            {
                ProblemId = problem.Id,
                Version = 1,
                CreatedByUserId = admin.Id,
                Note = "Seeded",
                Config = """{"format":"standard-io","version":1,"limits":{"timeMs":1000,"memoryKib":262144}}""",
            };
            context.ProblemVersions.Add(version);

            context.FileReferences.Add(new FileReference
            {
                FileId = statement.Id,
                OwnerKind = FileOwnerKind.ProblemVersion,
                ProblemVersionId = version.Id,
                Scope = FileScope.Participant,
                Name = "content.md",
            });
            context.FileReferences.Add(new FileReference
            {
                FileId = package.Id,
                OwnerKind = FileOwnerKind.ProblemVersion,
                ProblemVersionId = version.Id,
                Scope = FileScope.Runner,
                Name = "package.zip",
            });

            context.SeriesProblems.Add(new SeriesProblem
            {
                SeriesId = series.Id,
                ActivityId = activity.Id,
                ProblemId = problem.Id,
                PinnedProblemVersionId = version.Id,
                Slug = "A",
                Order = 1,
                MaxPoints = 50,
            });

            await context.SaveChangesAsync(ct);
            logger.LogInformation("Development data seeded: activity {Slug}", activity.Slug);

            // And the two the Client's fake also states, so the same screen can
            // be compared against both. `DEV-2026` above stays because the test
            // suite is written against it; these are for looking at.
            await new ParityWorld(context, users, logger).SeedAsync(admin, ct);
        }

        private async Task<Models.File> StoreAsync(
            string name, string mimeType, string content, string userId, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var file = new Models.File
            {
                Name = name,
                MimeType = mimeType,
                Content = bytes,
                SizeBytes = bytes.LongLength,
                Sha256 = IFileService.Checksum(bytes),
                UploadedByUserId = userId,
            };
            context.Files.Add(file);
            await context.SaveChangesAsync(ct);
            return file;
        }

        private async Task<User> EnsureUserAsync(
            string login, string password, string first, string last, CancellationToken ct)
        {
            var existing = await users.FindByNameAsync(login);
            if (existing is not null) return existing;

            var user = new User
            {
                UserName = login,
                Email = $"{login}@example.invalid",
                EmailConfirmed = true,
                FirstName = first,
                LastName = last,
                ApprovedAt = DateTime.UtcNow,
            };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "Seeding failed: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            }
            return user;
        }
    }
}
