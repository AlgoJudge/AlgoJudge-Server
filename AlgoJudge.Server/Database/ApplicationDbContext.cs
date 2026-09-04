using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using File = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Database
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : IdentityDbContext<User>(options), IDataProtectionKeyContext
    {
        /// <summary>
        /// The keys that encrypt every session cookie. Not a product table: it
        /// is the framework's, and it is here because this context already
        /// carries the framework's account tables and an installation should
        /// have one database to back up. See <see cref="Authorization.KeyRing"/>.
        /// </summary>
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

        public DbSet<Instance> Instance { get; set; }
        public DbSet<Models.AccessKey> AccessKeys { get; set; }
        public DbSet<MaintenanceState> Maintenance { get; set; }
        public DbSet<File> Files { get; set; }
        public DbSet<FileReference> FileReferences { get; set; }
        public DbSet<StorageMigration> StorageMigrations { get; set; }
        public DbSet<Problem> Problems { get; set; }
        public DbSet<ProblemShare> ProblemShares { get; set; }
        public DbSet<ProblemVersion> ProblemVersions { get; set; }
        public DbSet<SeriesProblem> SeriesProblems { get; set; }
        public DbSet<Series> Series { get; set; }
        public DbSet<SeriesAddressRule> SeriesAddressRules { get; set; }
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
        public DbSet<ActivityGroup> ActivityGroups { get; set; }
        public DbSet<UserSession> UserSessions { get; set; }
        public DbSet<IdentityProvider> IdentityProviders { get; set; }
        public DbSet<IdentityProviderMappingRule> IdentityProviderMappingRules { get; set; }
        public DbSet<UserIdentity> UserIdentities { get; set; }
        public DbSet<FederatedSignInAttempt> FederatedSignInAttempts { get; set; }
        public DbSet<AccountDeletionRequest> AccountDeletionRequests { get; set; }
        public DbSet<AccountMerge> AccountMerges { get; set; }

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
                e.Property(i => i.ExternalFetchHosts).HasColumnType("text[]");
                e.Property(i => i.RowVersion).IsRowVersion();
            });

            builder.Entity<Models.AccessKey>(e =>
            {
                e.ToTable("AccessKeys");
                // The name is the identity: there is one key per name, and
                // setting it again replaces it rather than adding a second.
                e.HasKey(k => k.Name);
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
                e.Property(m => m.RowVersion).IsRowVersion();
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
                e.Property(a => a.Props).HasColumnType("jsonb");
                // The default is the migration's problem, not this one's — but it
                // belongs in the model too, or the next `migrations add` sees a
                // default nothing declared and quietly drops it.
                e.Property(a => a.RunnerTags).HasColumnType("text[]")
                    .HasDefaultValueSql("'{}'::text[]");
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
                e.Property(s => s.RowVersion).IsRowVersion();
                // Nullable on purpose: null inherits the activity's, an empty
                // array overrides it back to the default pool.
                e.Property(s => s.RunnerTags).HasColumnType("text[]");
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
                // Every request a participant makes asks "what is running that
                // outranks or hides things", so the answer is read constantly
                // and is nearly always empty.
                e.HasIndex(s => new { s.IsOpen, s.Importance });
            });

            builder.Entity<SeriesAddressRule>(e =>
            {
                e.ToTable("SeriesAddressRules");
                // **`cidr`, mapped directly.** Npgsql 8 needed a converter here,
                // to its own `NpgsqlCidr`; Npgsql 10 maps
                // `System.Net.IPNetwork` natively and marks that type obsolete.
                // The model was written holding the later type precisely so the
                // upgrade would delete a converter rather than change the
                // entity, the contract and every reader of it — and on
                // 2026-08-29 it did.
                //
                // The column stays `cidr`, so PostgreSQL refuses a malformed
                // range and one with host bits set even if something one day
                // writes past the service that validates first.
                e.Property(r => r.Network).HasColumnType("cidr");
                e.Property(r => r.Note).HasMaxLength(200);
                e.HasOne(r => r.Series)
                    .WithMany(s => s.AddressRules)
                    .HasForeignKey(r => r.SeriesId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(r => r.SeriesId);
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
                e.Property(v => v.Props).HasColumnType("jsonb");
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
                // Three documents, three audiences: `config` decides the
                // verdict, `spec` draws the form, `props` is display. See the
                // entity — the split is what stops them being one field that
                // means three things.
                e.Property(sp => sp.Config).HasColumnType("jsonb");
                e.Property(sp => sp.Spec).HasColumnType("jsonb");
                e.Property(sp => sp.Props).HasColumnType("jsonb");
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
                e.Property(f => f.StorageId).HasMaxLength(32);
                e.Property(f => f.PreviousStorageId).HasMaxLength(32);
                e.HasIndex(f => f.Sha256);
                // "Which files are still in the store we are retiring" — asked by
                // the startup check, by the per-store counts, and by every run of
                // the migration worker.
                e.HasIndex(f => f.StorageId);
                // Filtered, because outside a migration every row is NULL here:
                // an unfiltered index would be the size of the table to answer a
                // question whose answer is almost always "none".
                e.HasIndex(f => f.PreviousCopyDeleteAfter)
                    .HasDatabaseName("IX_Files_PendingCopySweep")
                    .HasFilter("\"PreviousCopyDeleteAfter\" IS NOT NULL");
                e.HasOne(f => f.UploadedBy)
                    .WithMany()
                    .HasForeignKey(f => f.UploadedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);

                // The Runner that produced the bytes, when one did. Its own
                // column beside the user's, because a Runner is a principal with
                // a row and not a session — and because "uploaded by nobody" is
                // what one Runner used to be able to say about another's file.
                //
                // `SetNull` rather than `Restrict`: forgetting a revoked Runner
                // removes the row, and a log it left behind must not hold that
                // up. EF's own convention indexes the foreign key, which is
                // accepted rather than argued away — the alternative is a bare
                // `Guid?` with no referential integrity at all.
                e.HasOne(f => f.UploadedByRunner)
                    .WithMany()
                    .HasForeignKey(f => f.UploadedByRunnerId)
                    .OnDelete(DeleteBehavior.SetNull);

                // The garbage collector asks "what was uploaded before X and is
                // referenced by nothing" on every run.
                e.HasIndex(f => f.CreatedAt);
            });

            builder.Entity<StorageMigration>(e =>
            {
                e.ToTable("StorageMigrations");
                e.Property(m => m.TargetStoreId).HasMaxLength(32);
                e.Property(m => m.Detail).HasMaxLength(512);
                // "Is one running" is asked on every worker tick and by the
                // operator surface, and the answer is almost always no.
                e.HasIndex(m => m.State);
                e.Property(m => m.RowVersion).IsRowVersion();
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
                        "(\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 7 AND \"InstanceId\" IS NOT NULL) OR " +
                        "(\"OwnerKind\" = 8 AND \"InstanceId\" IS NOT NULL)");
                });
            });

            builder.Entity<Submission>(e =>
            {
                e.ToTable("Submissions");
                e.Property(s => s.IpAddress).HasColumnType("inet");
                // **`Restrict`, and it is the opposite choice from the grant's
                // on purpose.** This stamp is the record of what competed; a
                // group that has submissions cannot be deleted without making
                // every one of them say it was sent by nobody. The service
                // refuses the delete with a message, and this is the floor under
                // that refusal.
                e.HasOne(s => s.Group)
                    .WithMany()
                    .HasForeignKey(s => s.GroupId)
                    .OnDelete(DeleteBehavior.Restrict);
                // **`SetNull`, and it is not a preference.** The default for an
                // optional key is `ClientSetNull`, which means EF nulls it when the
                // session is loaded and the database does nothing when it is not —
                // and a delete that reached the database directly would take the
                // submission with it. Sessions are not deleted today; the day
                // somebody writes that sweep, this is what stops it deleting
                // somebody's work.
                e.HasOne<UserSession>()
                    .WithMany()
                    .HasForeignKey(s => s.SessionId)
                    .OnDelete(DeleteBehavior.SetNull);
                e.Property(s => s.Props).HasColumnType("jsonb");
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
                e.Property(t => t.RowVersion).IsRowVersion();
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
                //
                // **Filtered to work that is not finished.** Unfiltered, this
                // index carried every job the installation had ever run, for
                // ever, while the part of it anybody reads is the handful of
                // rows at the queued end — so its size tracked history rather
                // than the queue, and every insert and every reclaim paid to
                // maintain the rest. Correct at ten thousand rows; a real cost
                // at a million.
                //
                // `< 2` rather than `= 0`, because two readers want it and both
                // want live work: the claim seeks Queued, and
                // `MaintenanceDrainer` counts Running on a poll while a drain
                // settles. Filtering to the queue alone would have left that
                // drain scanning the whole table on every look — trading one
                // unbounded cost for another, on the path an operator is
                // actually waiting on.
                e.HasIndex(j => new { j.State, j.CreatedAt })
                    .HasFilter("\"State\" < 2");
                // The claim's fourth filter: is a sibling of this submission
                // already being judged.
                //
                // **Filtered to `Running` alone, so it is the size of the fleet
                // rather than of history.** Every claim runs this subquery, and
                // an unfiltered index on `SubmissionId` would carry a row per
                // attempt ever made to answer a question only about the handful
                // in flight.
                e.HasIndex(j => j.SubmissionId).HasFilter("\"State\" = 1");
                // The reaper's query: leases that have run out.
                e.HasIndex(j => j.LeaseExpiresAt);
                // Reporting a result is idempotent on the lease token, and this
                // is what makes it so — a repeat finds the row rather than
                // creating a second one.
                e.HasIndex(j => j.LeaseToken).IsUnique().HasFilter("\"LeaseToken\" IS NOT NULL");
                e.Property(j => j.RowVersion).IsRowVersion();
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
                e.Property(r => r.Props).HasColumnType("jsonb");
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
                // **`SetNull`, so removing a group leaves its members in the
                // activity.** A grant is somebody's assignment to the contest; the
                // group is one field on it, and deleting a group is not deleting the
                // people who were in it.
                e.HasOne(g => g.Group)
                    .WithMany(x => x.Members)
                    .HasForeignKey(g => g.GroupId)
                    .OnDelete(DeleteBehavior.SetNull);
                e.ToTable("Grants");
                e.Property(g => g.Permissions).HasColumnType("jsonb");
                // One grant per user per activity. A null ActivityId is the
                // system scope, and Postgres treats nulls as distinct in a unique
                // index, so the system scope needs filtered indexes of its own.
                e.HasIndex(g => new { g.UserId, g.ActivityId })
                    .IsUnique()
                    .HasFilter("\"ActivityId\" IS NOT NULL");

                // **Two indexes at system scope, not one.** A user holds exactly
                // one manual contribution and at most one per provider, and the
                // permissions they hold there are the union. A single index over
                // (UserId, SourceProviderId) would not express the first rule:
                // nulls are distinct, so two manual rows would both be allowed
                // and nothing would say which one an administrator was editing.
                e.HasIndex(g => g.UserId)
                    .IsUnique()
                    .HasDatabaseName("IX_Grants_UserId_Manual")
                    .HasFilter("\"ActivityId\" IS NULL AND \"SourceProviderId\" IS NULL");
                e.HasIndex(g => new { g.UserId, g.SourceProviderId })
                    .IsUnique()
                    .HasDatabaseName("IX_Grants_UserId_Provider")
                    .HasFilter("\"ActivityId\" IS NULL AND \"SourceProviderId\" IS NOT NULL");

                // Restrict rather than cascade, matching the provider service:
                // removing a provider that people sign in through is refused
                // while anything of theirs is still here.
                e.HasOne(g => g.SourceProvider)
                    .WithMany()
                    .HasForeignKey(g => g.SourceProviderId)
                    .OnDelete(DeleteBehavior.Restrict);
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

            builder.Entity<ActivityGroup>(e =>
            {
                e.ToTable("ActivityGroups");
                e.Property(g => g.Name).HasMaxLength(128);
                e.Property(g => g.Description).HasMaxLength(256);
                e.HasOne(g => g.Activity)
                    .WithMany(a => a.Groups)
                    .HasForeignKey(g => g.ActivityId)
                    .OnDelete(DeleteBehavior.Cascade);
                // A ranking row's name, so two rows may not carry one.
                e.HasIndex(g => new { g.ActivityId, g.Name }).IsUnique();
            });

            builder.Entity<UserSession>(e =>
            {
                e.ToTable("UserSessions");
                e.Property(s => s.LastRequestPath).HasMaxLength(256);
                e.Property(s => s.UserAgent).HasMaxLength(512);
                // `inet`, and no length: Npgsql maps `IPAddress` to it natively.
                // It was `varchar(64)`, which cannot answer a subnet question.
                e.Property(s => s.IpAddress).HasColumnType("inet");
                e.HasOne(s => s.User)
                    .WithMany(u => u.Sessions)
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(s => new { s.UserId, s.EndedAt });
                // The reaper closes sessions whose expiry has passed.
                e.HasIndex(s => s.ExpiresAt);
            });

            builder.Entity<IdentityProvider>(e =>
            {
                e.ToTable("IdentityProviders");
                e.Property(p => p.Slug).HasMaxLength(32);
                e.Property(p => p.DisplayName).HasMaxLength(128);
                e.Property(p => p.Issuer).HasMaxLength(512);
                e.Property(p => p.ClientId).HasMaxLength(256);
                e.Property(p => p.ClientSecret).HasMaxLength(1024);
                e.Property(p => p.DeletionSecret).HasMaxLength(1024);
                e.Property(p => p.Scopes).HasMaxLength(512);
                e.Property(p => p.AccountUrl).HasMaxLength(512);
                e.Property(p => p.DeletionUrl).HasMaxLength(512);
                e.Property(p => p.ClaimPath).HasMaxLength(128);
                e.Property(p => p.DefaultTemplateName).HasMaxLength(64);
                // The slug appears in a sign-in path and in the redirect URI
                // registered on the provider's side, so it has to be unique and
                // it is expensive to change.
                e.HasIndex(p => p.Slug).IsUnique();
            });

            builder.Entity<IdentityProviderMappingRule>(e =>
            {
                e.ToTable("IdentityProviderMappingRules");
                e.Property(r => r.ClaimValue).HasMaxLength(256);
                e.Property(r => r.TemplateName).HasMaxLength(64);
                // One rule per value per provider. Two would be a question about
                // ordering, and this model deliberately has no answer to it.
                e.HasIndex(r => new { r.ProviderId, r.ClaimValue }).IsUnique();
                e.HasOne(r => r.Provider)
                    .WithMany(p => p.MappingRules)
                    .HasForeignKey(r => r.ProviderId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<UserIdentity>(e =>
            {
                e.ToTable("UserIdentities");
                e.Property(i => i.Subject).HasMaxLength(256);
                // **The federated key.** Issuer plus `sub`, and never the email
                // address: an address is something a person changes at their
                // provider, and a federation keyed on one hands the account to
                // whoever inherits it. The issuer is the provider row, so the
                // pair is unique here.
                e.HasIndex(i => new { i.ProviderId, i.Subject }).IsUnique();
                e.HasIndex(i => i.UserId);
                e.HasOne(i => i.User)
                    .WithMany()
                    .HasForeignKey(i => i.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                // Restrict rather than cascade, to match the service: deleting a
                // provider that people sign in through decides something about
                // their accounts, so it is refused rather than allowed to ripple.
                // The database says so too, in case anything ever writes past the
                // service.
                e.HasOne(i => i.Provider)
                    .WithMany(p => p.Identities)
                    .HasForeignKey(i => i.ProviderId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<AccountDeletionRequest>(e =>
            {
                e.ToTable("AccountDeletionRequests");
                e.Property(r => r.Subject).HasMaxLength(256);
                e.Property(r => r.RequestId).HasMaxLength(128);
                e.Property(r => r.Detail).HasMaxLength(512);
                e.Property(r => r.RowVersion).IsRowVersion();
                // **What makes the back channel idempotent.** A webhook is
                // retried on any hiccup; without this a second delivery opens a
                // second window and the first one having been halted stops
                // meaning anything.
                e.HasIndex(r => new { r.ProviderId, r.RequestId })
                    .IsUnique()
                    .HasFilter("\"RequestId\" IS NOT NULL");
                // The sweeper's only query.
                e.HasIndex(r => new { r.State, r.ExecuteAfter });
                e.HasOne(r => r.Provider)
                    .WithMany()
                    .HasForeignKey(r => r.ProviderId)
                    .OnDelete(DeleteBehavior.SetNull);
                // Restrict, not cascade: a user row is never hard-deleted, and a
                // request that outlived its account is exactly the history this
                // queue exists to keep.
                e.HasOne(r => r.User)
                    .WithMany()
                    .HasForeignKey(r => r.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<AccountMerge>(e =>
            {
                e.ToTable("AccountMerges");
                e.Property(m => m.SourceUserId).HasMaxLength(450);
                e.Property(m => m.TargetUserId).HasMaxLength(450);
                e.Property(m => m.MergedByUserId).HasMaxLength(450);
                e.Property(m => m.UndoneByUserId).HasMaxLength(450);
                e.Property(m => m.Moved).HasColumnType("jsonb");
                e.Property(m => m.RowVersion).IsRowVersion();

                // The sweeper's only query: what is due to be anonymised.
                e.HasIndex(m => new { m.SourceAnonymisedAt, m.AnonymiseAfter });
                // Both screens that show a merge ask by the account they are on.
                e.HasIndex(m => m.TargetUserId);
                e.HasIndex(m => m.SourceUserId);

                // Both restrict, and both can: a user row is never removed in
                // this product, only emptied, so neither key ever stands in the
                // way of anything.
                e.HasOne(m => m.Source)
                    .WithMany()
                    .HasForeignKey(m => m.SourceUserId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne(m => m.Target)
                    .WithMany()
                    .HasForeignKey(m => m.TargetUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<FederatedSignInAttempt>(e =>
            {
                e.ToTable("FederatedSignInAttempts");
                e.Property(a => a.Subject).HasMaxLength(256);
                e.Property(a => a.Detail).HasMaxLength(256);
                e.Property(a => a.Matched).HasColumnType("jsonb");
                // The two questions asked of this table: "what happened to this
                // person" and "what did this provider do to anybody's permissions".
                e.HasIndex(a => new { a.ProviderId, a.At });
                e.HasIndex(a => new { a.UserId, a.At });
                // No foreign key to the user: an attempt is kept even when it
                // matched no account, and the column is null exactly then.
                e.HasOne(a => a.Provider)
                    .WithMany()
                    .HasForeignKey(a => a.ProviderId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
