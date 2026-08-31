using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti
{
    /// <summary>
    /// Everything this module needs from the application, in two calls.
    /// <para>
    /// <b>Two lines outside <c>Lti/</c>, and that is the whole point.</b> §8 of
    /// <c>LMS_INTEGRATION.md</c> makes deletability the test of whether the
    /// boundary held — the module has to come out in one commit touching nothing
    /// else — so its registrations, its migrations and its endpoints are all
    /// reached from here rather than spread through <c>Program.cs</c>.
    /// </para>
    /// <para>
    /// Called fully qualified at both sites rather than as extension methods,
    /// which would need a <c>using</c> and make it three lines. Two is not a
    /// slogan here: it is what the deletability test greps for.
    /// </para>
    /// <para>
    /// The invariant it protects is not about location, it is about dependency:
    /// nothing outside this directory names LTI. No <c>ltiUserId</c> on
    /// <c>User</c>, no LTI branch in the results path, no LTI column on
    /// <c>Activity</c>. The module reads the rest of the Server through the same
    /// interfaces any other caller uses.
    /// </para>
    /// </summary>
    public static class LtiModule
    {
        public static IServiceCollection AddLti(
            this IServiceCollection services, IConfiguration configuration)
        {
            // The same database as everything else. A separate context, not a
            // separate database: the module is optional, not remote.
            var connectionString = configuration.GetConnectionString("DbConnectionString");
            services.AddDbContext<LtiDbContext>(options => options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Lti")));

            services.AddScoped<Services.IToolKeyService, Services.ToolKeyService>();
            services.AddScoped<Services.IPlatformService, Services.PlatformService>();
            services.AddScoped<Services.ILaunchService, Services.LaunchService>();
            services.AddScoped<Services.IResourceLinkService, Services.ResourceLinkService>();
            services.AddScoped<Services.IIdentityResolver, Services.IdentityResolver>();
            services.AddScoped<Services.ILtiEnrolmentService, Services.EnrolmentService>();
            services.AddScoped<Services.IGradeSyncService, Services.GradeSyncService>();
            services.AddScoped<Services.IAgsClient, Services.AgsClient>();
            services.AddScoped<Services.IGradeVerifier, Services.GradeVerifier>();
            services.AddScoped<Services.IPlacementService, Services.PlacementService>();
            services.AddScoped<Services.INrpsClient, Services.NrpsClient>();
            services.AddScoped<Services.IRosterService, Services.RosterService>();
            services.AddScoped<Services.IDynamicRegistrationService, Services.DynamicRegistrationService>();
            services.AddScoped<Services.IDeepLinkService, Services.DeepLinkService>();
            services.AddScoped<Controllers.ILaunchTickets, Controllers.LaunchTickets>();
            services.AddSingleton<Services.IPlatformTokens, Services.PlatformTokens>();
            services.AddHostedService<Workers.GradeSyncWorker>();

            services.AddHttpClient(nameof(Services.PlatformTokens),
                http => http.Timeout = TimeSpan.FromSeconds(15));
            services.AddHttpClient(nameof(Services.AgsClient),
                http => http.Timeout = TimeSpan.FromSeconds(30));

            // Longer than the others: a roster is one request per page and a
            // large course is several, against a university's Moodle rather than
            // ours. Nobody is waiting in a redirect chain for this one.
            services.AddHttpClient(nameof(Services.NrpsClient),
                http => http.Timeout = TimeSpan.FromSeconds(60));

            // Short: a person is watching a spinner in an iframe on somebody
            // else's site while this runs.
            //
            // **The one client here whose address is chosen by a stranger.** Every
            // other call in this module goes to a platform an administrator wrote
            // out; this one goes wherever the `openid_configuration` query
            // parameter says. So it is the one that carries the connect guard —
            // widened to the operator's own network, because a paired Moodle is
            // routinely on it, and still closed to cloud metadata and to this
            // Server's own loopback.
            services.AddHttpClient(nameof(Services.DynamicRegistrationService),
                    http => http.Timeout = TimeSpan.FromSeconds(20))
                .ConfigurePrimaryHttpMessageHandler(
                    () => Utils.GuardedHttp.Handler(Utils.PublicAddress.IsPublicOrPrivateNetwork));

            // **A singleton, because the cache is the point.** A per-request
            // instance would fetch every platform's key set on every launch.
            services.AddSingleton<Services.IPlatformKeys, Services.PlatformKeys>();

            // Named, with a timeout that is short on purpose: this call sits in
            // the middle of a redirect chain somebody is waiting through, and a
            // platform that is not answering should fail the launch quickly with
            // a reason rather than hang until a default expires.
            services.AddHttpClient(nameof(Services.PlatformKeys),
                http => http.Timeout = TimeSpan.FromSeconds(10));

            return services;
        }

        /// <summary>
        /// Brings the module's schema up to date and maps its endpoints.
        /// <para>
        /// The migration lives here rather than in <c>Program.cs</c> for the same
        /// reason as everything else in this file, and it follows the
        /// application's own rule rather than inventing one:
        /// <c>Database/Schema.cs</c> decides, this passes it the same switch.
        /// </para>
        /// <para>
        /// <b>It has to read the switch too.</b> Two contexts share this
        /// database and each has its own history table, so an installation that
        /// migrated only the application's schema would still be refused here —
        /// by the module, over a table nobody had mentioned.
        /// </para>
        /// </summary>
        public static WebApplication MapLti(this WebApplication app)
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();

                Database.Schema.Ensure(
                    db.Database,
                    app.Environment.IsDevelopment()
                        || app.Configuration.GetValue<bool>(
                            Database.Schema.MigrateOnStartSetting),
                    "The LTI module's schema",
                    app.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("AlgoJudge.Schema"));
            }

            return app;
        }
    }
}
