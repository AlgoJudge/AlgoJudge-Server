using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using File = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Database
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<User>(options)
    {
        public DbSet<Instance> Instance { get; set; }
        public DbSet<MaintenanceState> Maintenance { get; set; }
        public DbSet<File> Files { get; set; }
        public DbSet<FileReference> FileReferences { get; set; }
        public DbSet<Problem> Problems { get; set; }
        public DbSet<ProblemShare> ProblemShares { get; set; }
        public DbSet<ProblemVersion> ProblemVersions { get; set; }
        public DbSet<SeriesProblem> SeriesProblems { get; set; }
        public DbSet<Series> Series { get; set; }
        public DbSet<Activity> Activities { get; set; }
        public DbSet<AttachmentRule> AttachmentRules { get; set; }
        public DbSet<Submission> Submissions { get; set; }
        public DbSet<EvaluationJob> EvaluationJobs { get; set; }
        public DbSet<Trial> Trials { get; set; }
        public DbSet<Result> Results { get; set; }
        public DbSet<Runner> Runners { get; set; }
        public DbSet<Question> Questions { get; set; }
        public DbSet<QuestionRead> QuestionReads { get; set; }
        public DbSet<PermissionTemplate> PermissionTemplates { get; set; }
        public DbSet<Grant> Grants { get; set; }
        public DbSet<UserSession> UserSessions { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Every instant in this schema is UTC. Postgres `timestamptz` is the
            // only type that records that fact, and Npgsql maps a DateTime to
            // `timestamp` without it — which stores the number and forgets the
            // zone, so a server whose clock moves an hour silently rewrites
            // history. Applied once, here, rather than per property.
            foreach (var entity in builder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    {
                        property.SetColumnType("timestamptz");
                    }
                }
            }

            builder.Entity<Instance>(e =>
            {
                e.ToTable("Instance");
                // One row, for ever. A check constraint rather than a convention:
                // a second row is not a state this product has an answer for, and
                // a seed or a migration is exactly where one would appear.
                e.ToTable(t => t.HasCheckConstraint(
                    "CK_Instance_Singleton",
                    $"\"Id\" = '{Models.Instance.SingletonId}'"));
            });

            builder.Entity<MaintenanceState>(e =>
            {
                e.ToTable("Maintenance");
                // One row, for the same reason and by the same means as
                // `Instance` above: a second row is not a state this product has
                // an answer for, and "how far are we withdrawn" cannot have two
                // answers at once.
                e.ToTable(t => t.HasCheckConstraint(
                    "CK_Maintenance_Singleton",
                    $"\"Id\" = '{Models.MaintenanceState.SingletonId}'"));
                e.Property(m => m.Version).IsRowVersion();
            });

            builder.Entity<Activity>(e =>
            {
                e.ToTable("Activities");
                // Slugs are compared case-insensitively, so uniqueness is enforced
                // on the lowered value rather than on the stored one.
                e.HasIndex(a => a.Slug).IsUnique();
                e.Property(a => a.Slug).HasMaxLength(32);
                e.Property(a => a.Name).HasMaxLength(200);
                e.Property(a => a.Type).HasMaxLength(64);
                e.Property(a => a.RankingType).HasMaxLength(64);
                e.Property(a => a.TimeZone).HasMaxLength(64);
                // A real array column, not a document: the Server refuses a
                // language that is not in it, and `text[]` is a value Postgres
                // can constrain and index if that is ever needed.
                e.Property(a => a.Languages).HasColumnType("text[]");
                e.Property(a => a.Props).HasColumnType("jsonb");
                // Listing filters on it on every arrival at the activity list.
                e.HasIndex(a => new { a.Unlisted, a.ArchivedAt });
            });

            builder.Entity<AttachmentRule>(e =>
            {
                e.ToTable("AttachmentRules");
                // The name is the key within its activity, so the database
                // refuses two answers for `log` rather than trusting a service to.
                e.HasKey(r => new { r.ActivityId, r.Name });
                e.Property(r => r.Name).HasMaxLength(64);
                e.HasOne(r => r.Activity)
                    .WithMany(a => a.AttachmentRules)
                    .HasForeignKey(r => r.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Series>(e =>
            {
                e.ToTable("Series");
                e.HasIndex(s => new { s.ActivityId, s.Slug }).IsUnique();
                e.Property(s => s.Slug).HasMaxLength(32);
                e.Property(s => s.Name).HasMaxLength(200);
                e.Property(s => s.Version).IsRowVersion();
                e.HasOne(s => s.Activity)
                    .WithMany(a => a.Series)
                    .HasForeignKey(s => s.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
                // The scheduler's scan: everything whose start or end has passed
                // and has not been announced. Without these it is a sequential
                // scan every fifteen seconds, for ever.
                e.HasIndex(s => new { s.StartDate, s.StartAnnouncedAt });
                e.HasIndex(s => new { s.EndDate, s.EndAnnouncedAt });
                e.HasIndex(s => new { s.RankingVisibleFrom, s.WindowAnnouncedAt });
                e.HasIndex(s => new { s.RankingRevealAt, s.UnfrozenAnnouncedAt });
            });

            builder.Entity<Problem>(e =>
            {
                e.ToTable("Problems");
                e.HasIndex(p => p.Slug).IsUnique();
                e.Property(p => p.Slug).HasMaxLength(32);
                e.Property(p => p.Name).HasMaxLength(200);
                e.Property(p => p.Type).HasMaxLength(64);
                // The library list is filtered by owner and visibility on every
                // load, which is the one query this table has to be fast at.
                e.HasIndex(p => new { p.OwnerUserId, p.Visibility });
                e.HasOne(p => p.Owner)
                    .WithMany()
                    .HasForeignKey(p => p.OwnerUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<ProblemShare>(e =>
            {
                e.ToTable("ProblemShares");
                e.HasKey(s => new { s.ProblemId, s.UserId });
                e.HasOne(s => s.Problem)
                    .WithMany(p => p.SharedWith)
                    .HasForeignKey(s => s.ProblemId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(s => s.User)
                    .WithMany()
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                // "Which shared problems may I see" is a lookup by user.
                e.HasIndex(s => s.UserId);
            });

            builder.Entity<ProblemVersion>(e =>
            {
                e.ToTable("ProblemVersions");
                e.HasIndex(v => new { v.ProblemId, v.Version }).IsUnique();
                e.HasOne(v => v.Problem)
                    .WithMany(p => p.Versions)
                    .HasForeignKey(v => v.ProblemId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.Property(v => v.Config).HasColumnType("jsonb");
                e.HasOne(v => v.CreatedBy)
                    .WithMany()
                    .HasForeignKey(v => v.CreatedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<SeriesProblem>(e =>
            {
                e.ToTable("SeriesProblems");
                e.Property(sp => sp.Slug).HasMaxLength(32);
                e.Property(sp => sp.Name).HasMaxLength(200);
                e.Property(sp => sp.Config).HasColumnType("jsonb");
                e.HasOne(sp => sp.Series)
                    .WithMany(s => s.SeriesProblems)
                    .HasForeignKey(sp => sp.SeriesId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(sp => sp.Problem)
                    .WithMany(p => p.SeriesProblems)
                    .HasForeignKey(sp => sp.ProblemId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne(sp => sp.PinnedProblemVersion)
                    .WithMany()
                    .HasForeignKey(sp => sp.PinnedProblemVersionId)
                    .OnDelete(DeleteBehavior.Restrict);
                // The assignment slug is unique across the whole ACTIVITY, not
                // within the series. That spans two tables, so `ActivityId` is
                // denormalised onto this row and the database enforces the rule
                // itself — a service-layer check is one two concurrent requests
                // can both pass.
                e.HasOne(sp => sp.Activity)
                    .WithMany()
                    .HasForeignKey(sp => sp.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(sp => new { sp.ActivityId, sp.Slug }).IsUnique();
            });

            builder.Entity<File>(e =>
            {
                e.ToTable("Files");
                e.Property(f => f.Sha256).HasMaxLength(64);
                e.Property(f => f.Name).HasMaxLength(255);
                e.Property(f => f.MimeType).HasMaxLength(128);
                e.HasIndex(f => f.Sha256);
                e.HasOne(f => f.UploadedBy)
                    .WithMany()
                    .HasForeignKey(f => f.UploadedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);
                // The garbage collector asks "what was uploaded before X and is
                // referenced by nothing" on every run.
                e.HasIndex(f => f.CreatedAt);
            });

            builder.Entity<FileReference>(e =>
            {
                e.ToTable("FileReferences");
                e.Property(r => r.Name).HasMaxLength(128);
                e.Property(r => r.Language).HasMaxLength(16);
                e.HasOne(r => r.File)
                    .WithMany(f => f.References)
                    // Restrict, not Cascade: bytes are never deleted out from
                    // under a reference. The collector removes the reference
                    // first, and only then may the file become collectable.
                    .HasForeignKey(r => r.FileId)
                    .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(r => r.ProblemVersion)
                    .WithMany(v => v.Files)
                    .HasForeignKey(r => r.ProblemVersionId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(r => r.Activity)
                    .WithMany()
                    .HasForeignKey(r => r.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(r => r.Submission)
                    .WithMany(s => s.Files)
                    .HasForeignKey(r => r.SubmissionId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(r => r.EvaluationJob)
                    .WithMany(j => j.Files)
                    .HasForeignKey(r => r.EvaluationJobId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(r => r.Runner)
                    .WithMany(x => x.Files)
                    .HasForeignKey(r => r.RunnerId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(r => r.Instance)
                    .WithMany(i => i.Files)
                    .HasForeignKey(r => r.InstanceId)
                    .OnDelete(DeleteBehavior.Cascade);

                // "May this caller read these bytes" walks from the file to its
                // references, and the collector walks the other way.
                e.HasIndex(r => r.FileId);
                e.HasIndex(r => new { r.OwnerKind, r.SupersededAt });

                // Exactly one owner, and it agrees with the discriminator. Two
                // separate failures, both of which produce a row that authorises
                // against nothing: no owner at all, or an owner the read rule
                // will not look at because the kind says elsewhere.
                e.ToTable(t =>
                {
                    t.HasCheckConstraint(
                        "CK_FileReferences_SingleOwner",
                        "num_nonnulls(\"ProblemVersionId\", \"ActivityId\", \"SubmissionId\", " +
                        "\"EvaluationJobId\", \"RunnerId\", \"InstanceId\") = 1");
                    t.HasCheckConstraint(
                        "CK_FileReferences_OwnerKindMatches",
                        "(\"OwnerKind\" = 0 AND \"ProblemVersionId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 1 AND \"ActivityId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 2 AND \"InstanceId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 3 AND \"InstanceId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 4 AND \"RunnerId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 5 AND \"SubmissionId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL)");
                });
            });

            builder.Entity<Submission>(e =>
            {
                e.ToTable("Submissions");
                // Every participant screen asks "my submissions for this
                // assignment, newest first", and the submission-count ceiling is
                // the same query.
                e.HasIndex(s => new { s.SeriesProblemId, s.UserId, s.CreatedDate });
                e.HasOne(s => s.User)
                    .WithMany()
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne(s => s.SeriesProblem)
                    .WithMany(sp => sp.Submissions)
                    .HasForeignKey(s => s.SeriesProblemId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<Trial>(e =>
            {
                e.ToTable("Trials");
                // The claim query, and the reaper's, on their own table: a
                // trial queue that is busy must not slow the queue that decides
                // somebody's mark.
                e.HasIndex(t => new { t.State, t.CreatedAt });
                e.HasIndex(t => t.LeaseExpiresAt);
                // Reporting a trial is idempotent for the same reason a result
                // is, and by the same mechanism.
                e.HasIndex(t => t.LeaseToken).IsUnique().HasFilter("\"LeaseToken\" IS NOT NULL");
                e.Property(t => t.Version).IsRowVersion();
                e.HasOne(t => t.Activity)
                    .WithMany()
                    .HasForeignKey(t => t.ActivityId)
                    // A deleted activity takes its trials with it: they are
                    // scoped to it and mean nothing outside it.
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(t => t.Runner)
                    .WithMany()
                    .HasForeignKey(t => t.RunnerId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<EvaluationJob>(e =>
            {
                e.ToTable("EvaluationJobs");
                e.HasIndex(j => new { j.SubmissionId, j.Attempt }).IsUnique();
                // The claim query orders queued jobs by age; without this index it
                // is a sequential scan under exactly the contention it must not be.
                e.HasIndex(j => new { j.State, j.CreatedAt });
                // The reaper's query: leases that have run out.
                e.HasIndex(j => j.LeaseExpiresAt);
                // Reporting a result is idempotent on the lease token, and this
                // is what makes it so — a repeat finds the row rather than
                // creating a second one.
                e.HasIndex(j => j.LeaseToken).IsUnique().HasFilter("\"LeaseToken\" IS NOT NULL");
                e.Property(j => j.Version).IsRowVersion();
                e.HasOne(j => j.Submission)
                    .WithMany(s => s.Jobs)
                    .HasForeignKey(j => j.SubmissionId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(j => j.ProblemVersion)
                    .WithMany()
                    .HasForeignKey(j => j.ProblemVersionId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne(j => j.Runner)
                    .WithMany(r => r.Jobs)
                    .HasForeignKey(j => j.RunnerId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<Result>(e =>
            {
                e.ToTable("Results");
                e.Property(r => r.Extra).HasColumnType("jsonb");
                e.Property(r => r.Verdict).HasMaxLength(64);
                // One result per completed job. Retries and rejudges add jobs,
                // not results, which is what keeps "which one counts" answerable.
                e.HasIndex(r => r.EvaluationJobId).IsUnique();
                e.HasOne(r => r.EvaluationJob)
                    .WithOne(j => j.Result)
                    .HasForeignKey<Result>(r => r.EvaluationJobId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Runner>(e =>
            {
                e.ToTable("Runners");
                e.Property(r => r.Name).HasMaxLength(128);
                e.Property(r => r.Product).HasMaxLength(128);
                e.Property(r => r.Version).HasMaxLength(64);
                e.Property(r => r.Fingerprint).HasMaxLength(128);
                e.Property(r => r.Address).HasMaxLength(64);
                // Compared by equality when a job is matched to a Runner, and
                // never parsed — which is what keeps "adding a problem type is
                // not a Server change" true while still letting it route work.
                e.Property(r => r.ProblemTypes).HasColumnType("text[]");
                e.Property(r => r.Tags).HasColumnType("text[]");
                e.Property(r => r.Machine).HasColumnType("jsonb");
                e.HasIndex(r => r.Fingerprint).IsUnique();
                e.HasIndex(r => r.State);
            });

            builder.Entity<Question>(e =>
            {
                e.ToTable("Questions");
                e.HasIndex(q => new { q.ActivityId, q.CreatedAt });
                e.Property(q => q.Topic).HasMaxLength(200);
                e.HasOne(q => q.Activity)
                    .WithMany(a => a.Questions)
                    .HasForeignKey(q => q.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(q => q.Series)
                    .WithMany()
                    .HasForeignKey(q => q.SeriesId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(q => q.SeriesProblem)
                    .WithMany(sp => sp.Questions)
                    .HasForeignKey(q => q.SeriesProblemId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(q => q.Author)
                    .WithMany()
                    .HasForeignKey(q => q.AuthorUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<QuestionRead>(e =>
            {
                e.ToTable("QuestionReads");
                e.HasKey(r => new { r.QuestionId, r.UserId });
                e.HasOne(r => r.Question)
                    .WithMany(q => q.Reads)
                    .HasForeignKey(r => r.QuestionId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<PermissionTemplate>(e =>
            {
                e.ToTable("PermissionTemplates");
                e.Property(t => t.Name).HasMaxLength(64);
                e.Property(t => t.Permissions).HasColumnType("jsonb");
                e.HasIndex(t => t.Name).IsUnique();
            });

            builder.Entity<Grant>(e =>
            {
                e.ToTable("Grants");
                e.Property(g => g.Permissions).HasColumnType("jsonb");
                // One grant per user per scope. A null ActivityId is the system
                // scope, and Postgres treats nulls as distinct in a unique index,
                // so the system grant needs its own filtered index to be unique.
                e.HasIndex(g => new { g.UserId, g.ActivityId })
                    .IsUnique()
                    .HasFilter("\"ActivityId\" IS NOT NULL");
                e.HasIndex(g => g.UserId)
                    .IsUnique()
                    .HasFilter("\"ActivityId\" IS NULL");
                // "Who is in this activity" is a query over this table, because
                // there is no membership table beside it. `IsSystem` is in the
                // index because the participant count excludes staff.
                e.HasIndex(g => new { g.ActivityId, g.State, g.IsSystem });
                e.HasOne(g => g.User)
                    .WithMany()
                    .HasForeignKey(g => g.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne(g => g.Activity)
                    .WithMany(a => a.Grants)
                    .HasForeignKey(g => g.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<UserSession>(e =>
            {
                e.ToTable("UserSessions");
                e.Property(s => s.LastRequestPath).HasMaxLength(256);
                e.Property(s => s.UserAgent).HasMaxLength(512);
                e.Property(s => s.IpAddress).HasMaxLength(64);
                e.HasOne(s => s.User)
                    .WithMany(u => u.Sessions)
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(s => new { s.UserId, s.EndedAt });
                // The reaper closes sessions whose expiry has passed.
                e.HasIndex(s => s.ExpiresAt);
            });
        }
    }
}
