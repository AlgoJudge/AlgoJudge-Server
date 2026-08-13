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

            return services;
        }

        /// <summary>
        /// Brings the module's schema up to date and maps its endpoints.
        /// <para>
        /// The migration lives here rather than in <c>Program.cs</c> for the same
        /// reason as everything else in this file. It follows the application's
        /// own rule, which is not "migrate on start": the core migrates only in
        /// development and otherwise <b>refuses to start with a migration
        /// pending</b>, because applying schema changes automatically to a
        /// production database is a decision an operator makes.
        /// </para>
        /// </summary>
        public static WebApplication MapLti(this WebApplication app)
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();

                if (app.Environment.IsDevelopment())
                {
                    db.Database.Migrate();
                }
                else if (db.Database.GetPendingMigrations().Any())
                {
                    throw new InvalidOperationException(
                        "The LTI module has pending migrations");
                }
            }

            return app;
        }
    }
}
