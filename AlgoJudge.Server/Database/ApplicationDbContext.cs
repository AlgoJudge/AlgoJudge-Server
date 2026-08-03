using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using File = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Database
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<User>(options)
    {
        public DbSet<File> Files { get; set; }
        public DbSet<Problem> Problems { get; set; }
        public DbSet<ProblemVersion> ProblemVersions { get; set; }
        public DbSet<SeriesProblem> SeriesProblems { get; set; }
        public DbSet<Series> Series { get; set; }
        public DbSet<Activity> Activities { get; set; }
        public DbSet<Submission> Submissions { get; set; }
        public DbSet<EvaluationJob> EvaluationJobs { get; set; }
        public DbSet<Result> Results { get; set; }
        public DbSet<Runner> Runners { get; set; }
        public DbSet<Question> Questions { get; set; }
        public DbSet<QuestionRead> QuestionReads { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Activity>(e =>
            {
                e.ToTable("Activities");
                // Slugs are compared case-insensitively, so uniqueness is enforced
                // on the lowered value rather than on the stored one.
                e.HasIndex(a => a.Slug).IsUnique();
                e.Property(a => a.Slug).HasMaxLength(32);
                e.HasOne(a => a.RulesFile)
                    .WithMany()
                    .HasForeignKey(a => a.RulesFileId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<Series>(e =>
            {
                e.ToTable("Series");
                e.HasIndex(s => new { s.ActivityId, s.Slug }).IsUnique();
                e.Property(s => s.Slug).HasMaxLength(32);
                e.HasOne(s => s.Activity)
                    .WithMany(a => a.Series)
                    .HasForeignKey(s => s.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Problem>(e =>
            {
                e.ToTable("Problems");
                e.HasIndex(p => p.Slug).IsUnique();
                e.Property(p => p.Slug).HasMaxLength(32);
            });

            builder.Entity<ProblemVersion>(e =>
            {
                e.ToTable("ProblemVersions");
                e.HasIndex(v => new { v.ProblemId, v.Version }).IsUnique();
                e.HasOne(v => v.Problem)
                    .WithMany(p => p.Versions)
                    .HasForeignKey(v => v.ProblemId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(v => v.CreatedBy)
                    .WithMany()
                    .HasForeignKey(v => v.CreatedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<SeriesProblem>(e =>
            {
                e.ToTable("SeriesProblems");
                e.Property(sp => sp.Slug).HasMaxLength(32);
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
                // The slug is unique across the whole activity, not within the
                // series. That spans two tables, so it cannot be a plain unique
                // index here and is enforced in the service layer instead.
                e.HasIndex(sp => new { sp.SeriesId, sp.Slug }).IsUnique();
            });

            builder.Entity<File>(e =>
            {
                e.ToTable("Files");
                e.Property(f => f.Sha256).HasMaxLength(64);
                e.HasIndex(f => f.Sha256);
                e.HasOne(f => f.ProblemVersion)
                    .WithMany(v => v.Files)
                    .HasForeignKey(f => f.ProblemVersionId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(f => f.Submission)
                    .WithMany(s => s.Files)
                    .HasForeignKey(f => f.SubmissionId)
                    .OnDelete(DeleteBehavior.Cascade);
                // A file belongs to one owner. Without this the column pair
                // silently allows a file that belongs to both or to neither.
                e.ToTable(t => t.HasCheckConstraint(
                    "CK_Files_SingleOwner",
                    "num_nonnulls(\"ProblemVersionId\", \"SubmissionId\") <= 1"));
            });

            builder.Entity<Submission>(e =>
            {
                e.ToTable("Submissions");
                e.HasIndex(s => new { s.SeriesProblemId, s.UserId });
                e.HasOne(s => s.User)
                    .WithMany()
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne(s => s.SeriesProblem)
                    .WithMany(sp => sp.Submissions)
                    .HasForeignKey(s => s.SeriesProblemId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<EvaluationJob>(e =>
            {
                e.ToTable("EvaluationJobs");
                e.HasIndex(j => new { j.SubmissionId, j.Attempt }).IsUnique();
                // The claim query orders queued jobs by age; without this index it
                // is a sequential scan under exactly the contention it must not be.
                e.HasIndex(j => new { j.State, j.CreatedAt });
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
                e.Property(r => r.Detail).HasColumnType("jsonb");
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
                e.Property(r => r.Capabilities).HasColumnType("jsonb");
                e.HasIndex(r => r.Fingerprint).IsUnique();
            });

            builder.Entity<Question>(e =>
            {
                e.ToTable("Questions");
                e.HasIndex(q => new { q.ActivityId, q.CreatedAt });
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
        }
    }
}
