using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Database
{
    /// <summary>
    /// The two activities the Client's fake also states, seeded so the same
    /// screen can be compared against both.
    /// <para>
    /// Ported from <c>AlgoJudge-Client/src/api/fake/fixtures/world.ts</c> on
    /// 2026-08-08 — slugs, names, round order, assignment letters, dates and
    /// every attempt. Before this the two worlds shared nothing at all: one
    /// activity against thirteen, <b>no submissions against fifty-eight
    /// attempts</b>, not one slug in common, English against Polish. A screen
    /// fed by each could not be told apart from a screen that was broken.
    /// </para>
    /// <para>
    /// <b>Deliberately not ported</b>, because they exercise Client-side
    /// fallbacks that need no Server: the forty-five-second countdown, the
    /// unsupported activity and problem types, the six pagination fillers, and
    /// the archived activity. <c>DEV-2026</c> stays beside these as the fixture
    /// the test suite is written against.
    /// </para>
    /// <para>
    /// <b>One known difference.</b> The fake's contest is fought by five
    /// <i>teams</i>, each sending what one of its members typed. The Server has
    /// no team: a contestant is a grant, and its name is the account's. Decided
    /// 2026-08-08 to seed a team as an ordinary account named after the team,
    /// which reproduces the board exactly and gives up knowing which member sent
    /// a given submission.
    /// </para>
    /// </summary>
    public class ParityWorld(
        ApplicationDbContext context,
        UserManager<User> users,
        Storage.IBlobStoreRegistry stores,
        ILogger logger)
    {
        /// <summary>The Runner's own scale, as the fake's seed reports on it.</summary>
        private const int RunnerScale = 100;

        private const string Password = "parity-development-only";

        /// <summary>
        /// A round's shape. Mirrors `SeedSeries`, minus what only the Client
        /// needs.
        /// </summary>
        private sealed record Round(
            string Slug,
            string Name,
            int Order,
            TimeSpan? Start,
            TimeSpan? End,
            bool RevealProblemCount,
            (string Slug, string Problem, string? Name, int? MaxPoints, int? PinnedVersion)[] Assignments,
            (string Who, string Problem, int AtMinutes, string Language, string State,
                string? Verdict, int? Score, string? Log, bool Rejudged)[] Attempts,
            TimeSpan? FreezeAt = null,
            TimeSpan? RevealAt = null,
            TimeSpan? WindowFrom = null);

        /// <summary>An account, and what it is called on a board.</summary>
        private sealed record Who(string Login, string Name);

        // ── the library ──────────────────────────────────────────────────────
        // Six problems, shared by both activities exactly as the fake shares
        // them: an assignment points at a library entry, it does not own one.

        private static readonly (string Slug, string Name, int Versions, bool Package, string Statement)[] Library =
        [
            ("spojnosc-grafu", "Spójność grafu", 3, true,
                "# Spójność grafu\n\nDany jest graf nieskierowany. Sprawdź, czy jest spójny.\n"),
            ("najkrotsza-sciezka", "Najkrótsza ścieżka", 2, true,
                "# Najkrótsza ścieżka\n\nZnajdź najkrótszą ścieżkę między dwoma wierzchołkami.\n"),
            // No package: nothing can be judged, and the screen has to say so
            // before the round opens rather than after.
            ("sortowanie-topologiczne", "Sortowanie topologiczne", 1, false,
                "# Sortowanie topologiczne\n\nUporządkuj wierzchołki grafu skierowanego.\n"),
            ("petle-i-sumy", "Pętle i sumy", 1, true,
                "# Pętle i sumy\n\nWczytaj n i wypisz sumę liczb od 1 do n.\n"),
            ("tablice", "Tablice", 2, true,
                "# Tablice\n\nWczytaj tablicę i wypisz ją w odwrotnej kolejności.\n"),
        ];

        // ── the contest ──────────────────────────────────────────────────────

        private static readonly Who[] ContestTeams =
        [
            new("team1", "Politechnika Poznańska 1"),
            new("team2", "Uniwersytet Warszawski 2"),
            new("team4", "AGH 1"),
            new("team5", "Uniwersytet Jagielloński 1"),
            // The one a developer signs in as to be "me" in the contest.
            new("team7", "Politechnika Poznańska 3"),
        ];

        private static readonly Round[] ContestRounds =
        [
            new("runda-0", "Runda 0 — rozgrzewkowa", 0,
                TimeSpan.FromDays(-1), TimeSpan.FromDays(-1) + TimeSpan.FromHours(3), true,
                [("R", "petle-i-sumy", "Rozgrzewka", null, null),
                 ("S", "tablice", "Sumy prefiksowe", null, null)],
                [("team2", "R", 12, "cpp", "completed", "Accepted", 100, null, false),
                 ("team2", "S", 20, "cpp", "completed", "Wrong answer", 40, null, false),
                 ("team2", "S", 42, "cpp", "completed", "Accepted", 100, null, false),
                 ("team7", "R", 18, "cpp", "completed", "Accepted", 100, null, false),
                 ("team7", "S", 30, "cpp", "completed", "Wrong answer", 60, null, false),
                 ("team7", "S", 58, "cpp", "completed", "Accepted", 100, null, false),
                 ("team1", "R", 20, "java", "completed", "Time limit exceeded", 0, null, false),
                 ("team1", "R", 31, "java", "completed", "Accepted", 100, null, false),
                 ("team1", "S", 44, "java", "completed", "Wrong answer", 20, null, false),
                 ("team1", "S", 66, "java", "completed", "Wrong answer", 20, null, false)]),

            // The round being fought, frozen for its last half hour — the ICPC
            // convention, and the only round in the seed whose window is open
            // while it is frozen.
            new("runda-1", "Runda 1", 1,
                TimeSpan.FromHours(-2), TimeSpan.FromHours(1), true,
                // `A` is pinned to version 2 although the library has moved to 3:
                // a contest must not have its statement change underneath it.
                [("A", "spojnosc-grafu", "Zadanie A — spójność", null, 2),
                 ("B", "najkrotsza-sciezka", null, null, null),
                 ("C", "sortowanie-topologiczne", null, 50, null),
                 ("D", "sortowanie-topologiczne", "Kolorowanie grafu", null, null)],
                [("team7", "C", 43, "java", "completed", "Runtime error", 0, null, false),
                 ("team7", "A", 57, "cpp", "failed", "Compilation error", null,
                     "main.cpp:7:5: error: 'cout' was not declared in this scope", false),
                 ("team7", "C", 72, "python", "completed", "Time limit exceeded", 0, null, false),
                 ("team7", "B", 85, "cpp", "completed", "Wrong answer", 40, null, false),
                 ("team7", "A", 98, "cpp", "completed", "Accepted", 100, null, false),
                 ("team7", "B", 111, "cpp", "running", null, null, null, false),
                 ("team7", "A", 116, "cpp", "queued", null, null, null, false),
                 ("team1", "A", 12, "cpp", "completed", "Accepted", 100, null, false),
                 ("team1", "B", 33, "cpp", "completed", "Wrong answer", 30, null, false),
                 ("team1", "B", 54, "cpp", "completed", "Accepted", 100, null, false),
                 ("team1", "C", 88, "cpp", "completed", "Accepted", 100, null, false),
                 // Judged twice: the first attempt is the rejudged history.
                 ("team2", "A", 9, "python", "completed", "Accepted", 100, null, true),
                 ("team2", "B", 61, "python", "completed", "Accepted", 100, null, false),
                 ("team2", "C", 25, "python", "completed", "Wrong answer", 10, null, false),
                 ("team2", "C", 40, "python", "completed", "Wrong answer", 10, null, false),
                 ("team2", "C", 55, "python", "completed", "Wrong answer", 30, null, false),
                 ("team2", "C", 71, "python", "completed", "Accepted", 100, null, false),
                 ("team2", "D", 95, "python", "failed", null, null,
                     "runner: package checksum mismatch, evaluation abandoned", false),
                 ("team4", "A", 21, "cpp", "completed", "Wrong answer", 50, null, false),
                 ("team4", "A", 41, "cpp", "completed", "Accepted", 100, null, false),
                 ("team4", "B", 117, "cpp", "queued", null, null, null, false),
                 ("team5", "A", 45, "java", "completed", "Accepted", 100, null, false),
                 ("team5", "B", 63, "java", "completed", "Wrong answer", 20, null, false),
                 ("team5", "B", 90, "java", "running", null, null, null, false)],
                FreezeAt: TimeSpan.FromMinutes(-30),
                RevealAt: TimeSpan.FromHours(1)),

            // Closed, count shown, board held back until it ends: one activity,
            // three windows, which is what the per-round window is for.
            new("runda-2", "Runda 2", 2,
                TimeSpan.FromHours(2), TimeSpan.FromHours(5), true,
                [("E", "spojnosc-grafu", "Maksymalny przepływ", null, null),
                 ("F", "najkrotsza-sciezka", "Drzewo przedziałowe", null, null),
                 ("G", "sortowanie-topologiczne", "Mosty w grafie", null, null)],
                [],
                WindowFrom: TimeSpan.FromHours(5)),

            // Closed with the count withheld too, so the screen has that case.
            new("runda-3", "Runda 3", 3,
                TimeSpan.FromDays(2), TimeSpan.FromDays(2) + TimeSpan.FromHours(3), false,
                [("H", "spojnosc-grafu", "Cykl Eulera", null, null),
                 ("I", "tablice", "Najdłuższy podciąg", null, null)],
                []),
        ];

        // ── the course ───────────────────────────────────────────────────────

        private static readonly Who[] CourseStudents =
        [
            new("nowak", "Anna Nowak"),
            new("wisniewski", "Tomasz Wiśniewski"),
            // The reader on the course side. `world.ts` uses one account for
            // both activities; a team account cannot also be a person, so the
            // course keeps the person and the contest keeps the team.
            new("amy", "Amy Horsefighter"),
        ];

        private static readonly Round[] CourseRounds =
        [
            new("zajecia-1", "Zajęcia 1 — podstawy", 1,
                TimeSpan.FromDays(-21), TimeSpan.FromDays(-14), true,
                [("petle", "petle-i-sumy", null, null, null),
                 ("tablice", "tablice", null, 200, null)],
                [("amy", "petle", 1200, "python", "completed", "Wrong answer", 60, null, false),
                 ("amy", "petle", 1500, "python", "completed", "Accepted", 100, null, false),
                 ("amy", "tablice", 1800, "python", "completed", "Wrong answer", 20, null, false),
                 ("amy", "tablice", 1900, "python", "completed", "Wrong answer", 50, null, false),
                 ("amy", "tablice", 2000, "python", "completed", "Wrong answer", 70, null, false),
                 ("amy", "tablice", 2100, "python", "completed", "Partially accepted", 80, null, false),
                 ("nowak", "petle", 1500, "python", "completed", "Accepted", 100, null, false),
                 ("nowak", "tablice", 2400, "python", "completed", "Accepted", 100, null, false),
                 ("wisniewski", "petle", 2600, "cpp", "completed", "Accepted", 100, null, false),
                 ("wisniewski", "tablice", 2900, "cpp", "completed", "Compilation error", 0,
                     "solution.cpp:7:5: error: 'cout' was not declared in this scope", false),
                 ("wisniewski", "tablice", 3100, "cpp", "completed", "Partially accepted", 50, null, false)]),

            new("zajecia-2", "Zajęcia 2 — rekurencja", 2,
                TimeSpan.FromDays(-7), TimeSpan.FromDays(3), true,
                [("rekurencja", "petle-i-sumy", "Rekurencja — rozgrzewka", 50, null),
                 ("sortowanie", "tablice", "Sortowanie", null, null)],
                [("amy", "rekurencja", 8640, "python", "completed", "Partially accepted", 50, null, false),
                 ("nowak", "rekurencja", 7200, "python", "completed", "Accepted", 100, null, false),
                 ("nowak", "sortowanie", 7800, "python", "completed", "Partially accepted", 80, null, false),
                 ("wisniewski", "rekurencja", 9000, "python", "cancelled", null, null, null, false)]),

            new("zajecia-3", "Zajęcia 3 — struktury danych", 3,
                TimeSpan.FromDays(5), TimeSpan.FromDays(12), true,
                [("kopiec", "spojnosc-grafu", "Kopiec binarny", null, null),
                 ("drzewo", "najkrotsza-sciezka", "Drzewo BST", null, null)],
                []),
        ];

        /// <summary>
        /// What the footer is built from. The Client derives its legal links
        /// from the references the instance publishes, so an installation that
        /// publishes none has no footer — which is correct, and indistinguishable
        /// from a footer that is broken.
        /// </summary>
        private static readonly (string Kind, string Title, string Body)[] LegalDocuments =
        [
            ("terms", "Regulamin",
                "# Regulamin\n\nKorzystanie z instalacji oznacza akceptację niniejszego regulaminu.\n"),
            ("privacy", "Polityka prywatności",
                "# Polityka prywatności\n\nPrzetwarzamy wyłącznie dane niezbędne do prowadzenia zajęć i zawodów.\n"),
            ("cookies", "Ciasteczka",
                "# Ciasteczka\n\nUżywamy jednego ciasteczka sesyjnego. Nie śledzimy nikogo.\n"),
            ("accessibility", "Deklaracja dostępności",
                "# Deklaracja dostępności\n\nStaramy się spełniać WCAG 2.1 na poziomie AA.\n"),
        ];

        public async Task SeedAsync(User admin, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            await InstanceAsync(admin, now, ct);
            var problems = await LibraryAsync(admin, ct);

            await ActivityAsync(admin, now, problems, ct,
                slug: "AMMPZ-2019",
                name: "Akademickie Mistrzostwa Polski w Programowaniu Zespołowym 2019",
                type: "contest@1", rankingType: "icpc",
                start: TimeSpan.FromDays(-1) - TimeSpan.FromHours(1), end: TimeSpan.FromDays(3),
                scoreVisibility: ScoreVisibility.Everyone,
                joinPolicy: JoinPolicy.Closed,
                languages: ["cpp", "python", "java"],
                maxUploadBytes: 8L * 1024 * 1024, maxAttachments: 1, maxSubmissions: 20,
                // A contest keeps the compiler output internal — it says a good
                // deal about a solution — while showing the per-test table.
                logVisible: false,
                people: ContestTeams, rounds: ContestRounds);

            await ActivityAsync(admin, now, problems, ct,
                slug: "PROG-1-LA",
                name: "Programowanie 1 — grupa LA",
                type: "course@1", rankingType: "points",
                start: TimeSpan.FromDays(-30), end: TimeSpan.FromDays(60),
                // One row, and no place: a student sees their own standing.
                scoreVisibility: ScoreVisibility.ParticipantOnly,
                joinPolicy: JoinPolicy.Password,
                languages: ["python"],
                maxUploadBytes: 4L * 1024 * 1024, maxAttachments: 3, maxSubmissions: null,
                // A course shows the log: it is where a student learns what they
                // got wrong.
                logVisible: true,
                people: CourseStudents, rounds: CourseRounds,
                joinPassword: "PROG1-LA");

            await context.SaveChangesAsync(ct);
            logger.LogInformation("Parity world seeded: AMMPZ-2019 and PROG-1-LA");
        }

        /// <summary>
        /// The installation's own name and the four documents its footer links.
        /// <para>
        /// Published as references with a <c>ValidFrom</c> in the past, which is
        /// how a real publication reaches a reader — the same path the manager
        /// screen writes and <c>InstanceService</c> reads, rather than a shortcut
        /// that would work here and nowhere else.
        /// </para>
        /// </summary>
        private async Task InstanceAsync(User admin, DateTime now, CancellationToken ct)
        {
            var instance = await context.Instance.FirstAsync(ct);
            instance.Name ??= "AlgoJudge — instalacja deweloperska";

            foreach (var (kind, _, body) in LegalDocuments)
            {
                var file = await StoreAsync($"instance/{kind}.md", "text/markdown", body, admin.Id, ct);
                context.FileReferences.Add(new FileReference
                {
                    FileId = file.Id,
                    OwnerKind = FileOwnerKind.InstanceDocument,
                    InstanceId = instance.Id,
                    Scope = FileScope.Participant,
                    Name = kind,
                    ValidFrom = now.AddDays(-1),
                });
            }

            await context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// The shared library. Every version carries a statement, and every one
        /// but the package-less problem carries a package too.
        /// </summary>
        private async Task<Dictionary<string, (Problem Problem, Dictionary<int, ProblemVersion> Versions)>>
            LibraryAsync(User admin, CancellationToken ct)
        {
            var built = new Dictionary<string, (Problem, Dictionary<int, ProblemVersion>)>();

            foreach (var (slug, name, versions, hasPackage, statement) in Library)
            {
                var problem = new Problem
                {
                    Slug = slug,
                    Name = name,
                    Type = "standard-io@1",
                    OwnerUserId = admin.Id,
                    Visibility = ProblemVisibility.Instance,
                };
                context.Problems.Add(problem);

                var made = new Dictionary<int, ProblemVersion>();
                for (var number = 1; number <= versions; number++)
                {
                    var version = new ProblemVersion
                    {
                        ProblemId = problem.Id,
                        Version = number,
                        CreatedByUserId = admin.Id,
                        Note = number == 1 ? "Pierwsza wersja" : $"Wersja {number}",
                        // The shape `docs/specs/PACKAGE_FORMAT.md` states, decided
                        // 2026-08-08 over the fixture's `kind`/`memoryBytes` form.
                        Config = """{"format":"standard-io","version":1,"limits":{"timeMs":1000,"memoryBytes":268435456}}""",
                    };
                    context.ProblemVersions.Add(version);
                    made[number] = version;

                    var text = await StoreAsync($"{slug}-v{number}/content.md", "text/markdown", statement, admin.Id, ct);
                    Reference(text, version, FileScope.Participant, "content.md");

                    if (hasPackage)
                    {
                        var package = await StoreAsync(
                            $"{slug}-v{number}/package.zip", "application/zip",
                            $"not-a-real-package:{slug}:{number}", admin.Id, ct);
                        Reference(package, version, FileScope.Runner, "package.zip");
                    }
                }

                built[slug] = (problem, made);
            }

            await context.SaveChangesAsync(ct);
            return built;
        }

        private async Task ActivityAsync(
            User admin, DateTime now,
            Dictionary<string, (Problem Problem, Dictionary<int, ProblemVersion> Versions)> library,
            CancellationToken ct,
            string slug, string name, string type, string rankingType,
            TimeSpan start, TimeSpan end,
            ScoreVisibility scoreVisibility, JoinPolicy joinPolicy,
            List<string> languages, long maxUploadBytes, int maxAttachments, int? maxSubmissions,
            bool logVisible, Who[] people, Round[] rounds, string? joinPassword = null)
        {
            var activity = new Activity
            {
                Slug = slug,
                Name = name,
                Type = type,
                RankingType = rankingType,
                TimeZone = "Europe/Warsaw",
                StartDate = now + start,
                EndDate = now + end,
                ScoreVisibility = scoreVisibility,
                JoinPolicy = joinPolicy,
                JoinPassword = joinPassword,
                Unlisted = true,
                HideEndedSeriesProblems = false,
                Languages = languages,
                MaxUploadBytes = maxUploadBytes,
                MaxAttachments = maxAttachments,
                MaxSubmissionsPerProblem = maxSubmissions,
            };

            // An unlisted name is managers-only, so only the ones that differ
            // from that need a row.
            activity.AttachmentRules.Add(new AttachmentRule
            {
                ActivityId = activity.Id, Name = "source", Visibility = AttachmentVisibility.Participant,
            });
            activity.AttachmentRules.Add(new AttachmentRule
            {
                ActivityId = activity.Id, Name = "details", Visibility = AttachmentVisibility.Participant,
            });
            if (logVisible)
            {
                activity.AttachmentRules.Add(new AttachmentRule
                {
                    ActivityId = activity.Id, Name = "log", Visibility = AttachmentVisibility.Participant,
                });
            }
            context.Activities.Add(activity);

            context.Grants.Add(new Grant
            {
                UserId = admin.Id,
                ActivityId = activity.Id,
                Permissions = JsonSerializer.Serialize(Permissions.ManagerTemplate),
                CreatedFromTemplate = "manager",
                IsSystem = true,
            });

            var accounts = new Dictionary<string, User>();
            foreach (var who in people)
            {
                var account = await EnsureAsync(who, ct);
                accounts[who.Login] = account;
                context.Grants.Add(new Grant
                {
                    UserId = account.Id,
                    ActivityId = activity.Id,
                    Permissions = JsonSerializer.Serialize(Permissions.ParticipantTemplate),
                    CreatedFromTemplate = "participant",
                    IsSystem = false,
                });
            }

            foreach (var round in rounds)
            {
                var opens = round.Start is { } s ? now + s : (DateTime?)null;
                var closes = round.End is { } e ? now + e : (DateTime?)null;
                var open = (opens is null || opens <= now) && (closes is null || closes > now);

                var series = new Series
                {
                    ActivityId = activity.Id,
                    Slug = round.Slug,
                    Name = round.Name,
                    Order = round.Order,
                    StartDate = opens,
                    EndDate = closes,
                    RevealProblemCount = round.RevealProblemCount,
                    RankingFreezeAt = round.FreezeAt is { } f ? now + f : null,
                    RankingRevealAt = round.RevealAt is { } r ? now + r : null,
                    RankingVisibleFrom = round.WindowFrom is { } w ? now + w : null,
                    IsOpen = open,
                    // Marked announced where the state is already past, so the
                    // scheduler does not announce a round that has been running
                    // since before the process started.
                    StartAnnouncedAt = opens is null || opens <= now ? now : null,
                    EndAnnouncedAt = closes is not null && closes <= now ? now : null,
                };
                context.Series.Add(series);

                var assignments = new Dictionary<string, SeriesProblem>();
                var order = 1;
                foreach (var (letter, problemSlug, assignmentName, maxPoints, pinned) in round.Assignments)
                {
                    var (problem, versions) = library[problemSlug];
                    var assignment = new SeriesProblem
                    {
                        SeriesId = series.Id,
                        ActivityId = activity.Id,
                        ProblemId = problem.Id,
                        // Attaching pins the version that is current at the time,
                        // unless the seed names an older one.
                        PinnedProblemVersionId = versions[pinned ?? versions.Count].Id,
                        Slug = letter,
                        Name = assignmentName,
                        Order = order++,
                        MaxPoints = maxPoints,
                    };
                    context.SeriesProblems.Add(assignment);
                    assignments[letter] = assignment;
                }

                foreach (var attempt in round.Attempts)
                {
                    await AttemptAsync(series, assignments, accounts, attempt, ct);
                }
            }
        }

        /// <summary>
        /// One attempt: a submission, the job that carried it, its result where
        /// there is one, and what the Runner attached.
        /// </summary>
        private async Task AttemptAsync(
            Series series,
            Dictionary<string, SeriesProblem> assignments,
            Dictionary<string, User> accounts,
            (string Who, string Problem, int AtMinutes, string Language, string State,
                string? Verdict, int? Score, string? Log, bool Rejudged) attempt,
            CancellationToken ct)
        {
            var assignment = assignments[attempt.Problem];
            var author = accounts[attempt.Who];
            var at = (series.StartDate ?? DateTime.UtcNow).AddMinutes(attempt.AtMinutes);

            var submission = new Submission
            {
                CreatedDate = at,
                UserId = author.Id,
                SeriesProblemId = assignment.Id,
                Language = attempt.Language,
            };
            context.Submissions.Add(submission);

            var source = await StoreAsync(
                $"{series.Slug}/{attempt.Who}/{attempt.Problem}/{attempt.AtMinutes}/source",
                "text/plain",
                $"// {attempt.Language}\n// {assignment.Slug}\n",
                author.Id, ct);
            context.FileReferences.Add(new FileReference
            {
                FileId = source.Id,
                OwnerKind = FileOwnerKind.Submission,
                SubmissionId = submission.Id,
                Scope = FileScope.Participant,
                Name = "source",
                CreatedAt = at,
            });

            // A rejudge is a second job on one submission, never a second
            // submission: the attempt count is what somebody sent, not what was
            // run.
            var deliveries = attempt.Rejudged ? 2 : 1;
            for (var number = 1; number <= deliveries; number++)
            {
                var last = number == deliveries;
                var state = last ? attempt.State : "completed";

                var job = new EvaluationJob
                {
                    SubmissionId = submission.Id,
                    Attempt = number,
                    ProblemVersionId = assignment.PinnedProblemVersionId!.Value,
                    CreatedAt = at,
                    State = state switch
                    {
                        "queued" => EvaluationJobState.Queued,
                        "running" => EvaluationJobState.Running,
                        "failed" => EvaluationJobState.Failed,
                        "cancelled" => EvaluationJobState.Cancelled,
                        _ => EvaluationJobState.Completed,
                    },
                    ClaimedAt = state is "queued" ? null : at.AddSeconds(2),
                    FinishedAt = state is "queued" or "running" ? null : at.AddSeconds(20),
                    FailureReason = state == "failed" ? attempt.Log ?? attempt.Verdict : null,
                };
                context.EvaluationJobs.Add(job);

                // A failed job produced no result — that is what failing is —
                // and a queued or running one has not produced one yet.
                if (job.State != EvaluationJobState.Completed) continue;

                context.Results.Add(new Result
                {
                    EvaluationJobId = job.Id,
                    ProblemVersionId = job.ProblemVersionId,
                    CreatedDate = at.AddSeconds(20),
                    // The Runner reports on the package's own scale; what the
                    // assignment counts for is applied where a number is shown.
                    Score = last ? attempt.Score : 0,
                    MaxScore = RunnerScale,
                    Verdict = last ? attempt.Verdict : "Wrong answer",
                    RunnerVersion = "0.0.1",
                });

                if (last && attempt.Log is { } log)
                {
                    var stored = await StoreAsync(
                        $"{series.Slug}/{attempt.Who}/{attempt.Problem}/{attempt.AtMinutes}/log",
                        "text/plain", log, author.Id, ct);
                    context.FileReferences.Add(new FileReference
                    {
                        FileId = stored.Id,
                        OwnerKind = FileOwnerKind.Attempt,
                        EvaluationJobId = job.Id,
                        Scope = FileScope.Runner,
                        Name = "log",
                        CreatedAt = at.AddSeconds(20),
                    });
                }
            }
        }

        private void Reference(Models.File file, ProblemVersion version, FileScope scope, string name) =>
            context.FileReferences.Add(new FileReference
            {
                FileId = file.Id,
                OwnerKind = FileOwnerKind.ProblemVersion,
                ProblemVersionId = version.Id,
                Scope = scope,
                Name = name,
            });

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
            var key = new Storage.BlobKey(Utils.Uuid.New(), sha256);
            var written = await store.WriteAsync(key.FileId, new MemoryStream(bytes), ct);

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

        private async Task<User> EnsureAsync(Who who, CancellationToken ct)
        {
            var existing = await users.FindByNameAsync(who.Login);
            if (existing is not null) return existing;

            // `Projections.DisplayName` joins the two with a space, so splitting
            // on the last one puts the name back together exactly — for a person
            // and for "Politechnika Poznańska 1" alike. The halves mean nothing
            // for a team account; only their concatenation is ever shown.
            var space = who.Name.LastIndexOf(' ');
            var user = new User
            {
                UserName = who.Login,
                Email = $"{who.Login}@example.invalid",
                EmailConfirmed = true,
                FirstName = space > 0 ? who.Name[..space] : who.Name,
                LastName = space > 0 ? who.Name[(space + 1)..] : null,
                ApprovedAt = DateTime.UtcNow,
            };

            var created = await users.CreateAsync(user, Password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Seeding {who.Login} failed: "
                    + string.Join("; ", created.Errors.Select(e => e.Description)));
            }
            _ = ct;
            return user;
        }
    }
}
