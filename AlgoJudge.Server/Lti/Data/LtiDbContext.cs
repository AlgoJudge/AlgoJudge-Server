using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// This module's tables, in the same database and a separate context.
    /// <para>
    /// <b>Separate because §8 of <c>LMS_INTEGRATION.md</c> says the module has to
    /// be deletable in one commit touching nothing else</b>, and that is checked
    /// rather than asserted. Adding these entities to
    /// <c>ApplicationDbContext</c> would put LTI into the core's model snapshot,
    /// its migration history and its designer files — deleting the module would
    /// then be a migration against everybody's database rather than a directory
    /// removal.
    /// </para>
    /// <para>
    /// Its own migrations history table, for the same reason: two contexts
    /// sharing <c>__EFMigrationsHistory</c> each see the other's rows as
    /// migrations they do not have, and both refuse to start.
    /// </para>
    /// <para>
    /// <b>The cost, stated rather than discovered:</b> there is no single
    /// transaction across the boundary. A launch writes an
    /// <see cref="ExternalIdentity"/> here and a grant through
    /// <c>IGrantService</c> there, and a crash between them leaves the link
    /// without the grant. That is survivable because the next launch makes the
    /// same grant — <c>IGrantService</c> is idempotent on
    /// <c>(userId, activityId)</c> — and because the module's whole grade design
    /// is already a reconciled state rather than a sequence of events.
    /// </para>
    /// </summary>
    public class LtiDbContext(DbContextOptions<LtiDbContext> options) : DbContext(options)
    {
        public DbSet<Platform> Platforms => Set<Platform>();
        public DbSet<ToolKey> ToolKeys => Set<ToolKey>();
        public DbSet<ResourceLink> ResourceLinks => Set<ResourceLink>();
        public DbSet<LineItem> LineItems => Set<LineItem>();
        public DbSet<GradeSyncState> GradeSyncStates => Set<GradeSyncState>();
        public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
        public DbSet<LaunchState> LaunchStates => Set<LaunchState>();
        public DbSet<LtiSettings> Settings => Set<LtiSettings>();
        public DbSet<LaunchTicket> LaunchTickets => Set<LaunchTicket>();
        public DbSet<RegistrationInvitation> RegistrationInvitations => Set<RegistrationInvitation>();
        public DbSet<DeepLinkSession> DeepLinkSessions => Set<DeepLinkSession>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Prefixed, all of them. These tables sit in the same schema as the
            // core's, and a bare `Platforms` beside the product's own tables
            // reads like one of them — which matters on the day somebody has to
            // work out what to drop.
            builder.Entity<Platform>().ToTable("LtiPlatforms");
            builder.Entity<ToolKey>().ToTable("LtiToolKeys");
            builder.Entity<ResourceLink>().ToTable("LtiResourceLinks");
            builder.Entity<LineItem>().ToTable("LtiLineItems");
            builder.Entity<GradeSyncState>().ToTable("LtiGradeSyncStates");
            builder.Entity<ExternalIdentity>().ToTable("LtiExternalIdentities");
            builder.Entity<LaunchState>().ToTable("LtiLaunchStates");
            builder.Entity<LtiSettings>().ToTable("LtiSettings");
            builder.Entity<LaunchTicket>().ToTable("LtiLaunchTickets");
            builder.Entity<RegistrationInvitation>().ToTable("LtiRegistrationInvitations");
            builder.Entity<DeepLinkSession>().ToTable("LtiDeepLinkSessions");

            // The code is what the whole endpoint is gated on, so it is looked up
            // by it on every attempt — and two rows carrying one code would make
            // "spent" ambiguous.
            builder.Entity<RegistrationInvitation>().HasIndex(i => i.Code).IsUnique();

            // Same reasoning for a choosing in progress: the code is the whole
            // handle on it, and it is spent exactly once.
            builder.Entity<DeepLinkSession>().HasIndex(s => s.Code).IsUnique();

            builder.Entity<Platform>(platform =>
            {
                // One registration per deployment of one client at one issuer.
                // All three are needed: a platform may register the tool twice,
                // and the deployment is the only thing that separates them.
                platform.HasIndex(p => new { p.Issuer, p.ClientId, p.DeploymentId }).IsUnique();
                platform.HasIndex(p => p.ProviderId).IsUnique();
                platform.HasMany(p => p.ResourceLinks)
                    .WithOne(l => l.Platform!)
                    .HasForeignKey(l => l.PlatformId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<ToolKey>().HasIndex(k => k.Kid).IsUnique();

            builder.Entity<ResourceLink>(link =>
            {
                link.HasIndex(l => new { l.PlatformId, l.PlatformResourceLinkId }).IsUnique();

                // **Deliberately not unique on ActivityId.** Decided 2026-08-13:
                // one activity may be placed in more than one course, because a
                // shared problem set across two groups is real. What stops it
                // happening silently is `SharingAcknowledged`, not a constraint —
                // a constraint here would refuse the case rather than surface it.
                link.HasIndex(l => l.ActivityId);

                link.HasMany(l => l.LineItems)
                    .WithOne(i => i.ResourceLink!)
                    .HasForeignKey(i => i.ResourceLinkId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<LineItem>()
                .HasIndex(i => new { i.ResourceLinkId, i.SeriesProblemId }).IsUnique();

            builder.Entity<GradeSyncState>(state =>
            {
                // One intended grade per person per column. The whole design
                // rests on this: the row *is* the intent, so a second row for the
                // same pair would be two intents and no way to tell which the
                // gradebook reflects.
                state.HasIndex(s => new { s.LineItemId, s.UserId }).IsUnique();

                // What the worker sweeps on.
                state.HasIndex(s => new { s.State, s.NextAttemptAt });

                state.HasOne(s => s.LineItem!)
                    .WithMany()
                    .HasForeignKey(s => s.LineItemId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<ExternalIdentity>(identity =>
            {
                identity.HasIndex(i => new { i.PlatformId, i.Subject }).IsUnique();

                // A person has one link per platform. Two would mean one account
                // reachable as two people from the same Moodle, which is the
                // thing this table exists to prevent.
                identity.HasIndex(i => new { i.PlatformId, i.UserId }).IsUnique();

                identity.HasOne(i => i.Platform!)
                    .WithMany()
                    .HasForeignKey(i => i.PlatformId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<LaunchTicket>(ticket =>
            {
                ticket.HasIndex(t => t.Ticket).IsUnique();
                ticket.HasIndex(t => t.ExpiresAt);
            });

            builder.Entity<LaunchState>(state =>
            {
                state.HasIndex(s => s.State).IsUnique();
                // Swept by expiry, so the sweep is an index scan rather than a
                // table scan on a table that turns over once per launch.
                state.HasIndex(s => s.ExpiresAt);
            });
        }
    }
}
