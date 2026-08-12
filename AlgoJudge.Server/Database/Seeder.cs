using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
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
        Storage.IBlobStoreRegistry stores,
        ILogger<Seeder> logger)
    {
        /// <summary>
        /// The administrator every installation gets, and the one login this
        /// product reserves. See <see cref="Authorization.ReservedLoginValidator"/>.
        /// </summary>
        public const string AdminLogin = "admin";

        /// <summary>
        /// <b>Twenty characters nobody is ever told.</b>
        /// <para>
        /// Not a default password: a default is a password an attacker also
        /// knows, and a well-known administrator login with a well-known
        /// password is the most reliable way an installation is taken over. The
        /// alternative to a default is a password nobody knows and a documented
        /// way to set one — <c>POST /admin/password</c>, from the machine
        /// itself, with the operator's token.
        /// </para>
        /// </summary>
        private const int AdminPasswordLength = 20;

        /// <summary>
        /// The development administrator's password. Well known, and only ever
        /// acceptable because it is applied in Development alone — the caller
        /// passes <c>false</c> everywhere else.
        /// </summary>
        public const string DevAdminLogin = AdminLogin;
        public const string DevAdminPassword = "admin-development-only";
        public const string DevParticipantLogin = "student";
        public const string DevParticipantPassword = "student-development-only";

        public async Task EnsureAsync(bool development, CancellationToken ct = default)
        {
            await instances.EnsureAsync(ct);
            await EnsureTemplatesAsync(ct);
            // Beside the templates and the instance row, and for the same
            // reason: an installation without one cannot be operated at all.
            // This used to live in the development block, which left a
            // production database with nobody to sign in as and no way to make
            // anybody.
            await EnsureAdministratorAsync(ct);

            if (!development) return;
            await EnsureDevelopmentAdminPasswordAsync(ct);
            await EnsureDevelopmentDataAsync(ct);
        }

        /// <summary>
        /// The administrator account, created once and never touched again.
        /// <para>
        /// <b>No name and no address.</b> It is not a person; it is the account
        /// an operator uses to make people. Inventing an identity for it would
        /// put a name on a board and an address in a mailbox that belong to
        /// nobody.
        /// </para>
        /// <para>
        /// One grant, not a list: the administrator template bypasses the rest,
        /// and an administrator holding individual permissions is an
        /// administrator who can be trimmed.
        /// </para>
        /// </summary>
        private async Task EnsureAdministratorAsync(CancellationToken ct)
        {
            if (await users.FindByNameAsync(AdminLogin) is not null) return;

            var admin = new User
            {
                UserName = AdminLogin,
                Email = null,
                EmailConfirmed = false,
                FirstName = null,
                LastName = null,
                ApprovedAt = DateTime.UtcNow,
            };

            var created = await users.CreateAsync(admin, Passwords.Generate(AdminPasswordLength));
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "The administrator could not be created: "
                    + string.Join("; ", created.Errors.Select(e => e.Description)));
            }

            context.Grants.Add(new Grant
            {
                UserId = admin.Id,
                ActivityId = null,
                Permissions = JsonSerializer.Serialize(Permissions.AdminTemplate),
                CreatedFromTemplate = "admin",
                IsSystem = true,
            });
            await context.SaveChangesAsync(ct);

            // Said loudly, because an operator who does not read this has an
            // installation they cannot get into. **The password is not in the
            // message** — it is not in any message, which is the point of it.
            logger.LogWarning(
                "Created the {Login} account with a random password that has not been recorded anywhere. "
                + "Set one with POST {Path} from inside the container before signing in; "
                + "it needs AJ_Admin__Token to be configured.",
                AdminLogin, "/api/v1/admin/password");
        }

        /// <summary>
        /// Puts the well-known password on it, in Development only.
        /// <para>
        /// <b>Outside the "has this already been seeded" check on purpose.</b>
        /// The account above is created once; this has to run on every start,
        /// because a developer whose database predates the random password would
        /// otherwise have an <c>admin</c> they cannot sign in as and no obvious
        /// reason why.
        /// </para>
        /// </summary>
        private async Task EnsureDevelopmentAdminPasswordAsync(CancellationToken ct)
        {
            var admin = await users.FindByNameAsync(AdminLogin);
            if (admin is null) return;
            if (await users.CheckPasswordAsync(admin, DevAdminPassword)) return;

            var token = await users.GeneratePasswordResetTokenAsync(admin);
            var reset = await users.ResetPasswordAsync(admin, token, DevAdminPassword);
            if (!reset.Succeeded)
            {
                throw new InvalidOperationException(
                    "The development administrator's password could not be set: "
                    + string.Join("; ", reset.Errors.Select(e => e.Description)));
            }

            // Also clears whatever a failed sign-in left behind, so a developer
            // does not start the day locked out of a well-known account.
            admin.AccessFailedCount = 0;
            admin.LockoutEnd = null;
            await context.SaveChangesAsync(ct);

            logger.LogWarning(
                "Development: {Login} now has the well-known password. This runs in Development only.",
                AdminLogin);
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

            // The administrator and its grant are seeded above, everywhere, and
            // its password has already been set to the well-known one. It owns
            // the seeded material below because somebody has to.
            var admin = await users.FindByNameAsync(AdminLogin)
                ?? throw new InvalidOperationException(
                    $"{AdminLogin} is seeded before this runs, and was not");

            // All this block adds is somebody to compete against it.
            var student = await EnsureUserAsync(DevParticipantLogin, DevParticipantPassword, "Stefan", "Student", ct);

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
                Config = """{"format":"standard-io","version":1,"limits":{"timeMs":1000,"memoryBytes":268435456}}""",
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
            await new ParityWorld(context, users, stores, logger).SeedAsync(admin, ct);
        }

        private async Task<Models.File> StoreAsync(
            string name, string mimeType, string content, string userId, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var sha256 = IFileService.Checksum(bytes);

            // Through the store like every other write, rather than straight into
            // a column. A seed that wrote bytes its own way would be a seed that
            // works on exactly one backend, and the development stack is where a
            // broken store is meant to be noticed first.
            var store = stores.Default;
            var key = new Storage.BlobKey(Uuid.New(), sha256);
            var written = await store.WriteAsync(key, new MemoryStream(bytes), ct);

            var file = new Models.File
            {
                Id = key.FileId,
                Name = name,
                MimeType = mimeType,
                SizeBytes = written.SizeBytes,
                Sha256 = written.Sha256,
                StorageId = store.Id,
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
