using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlgoJudge.Server.Database
{
    /// <summary>
    /// Brings one context's schema up to date, or refuses to start.
    ///
    /// <para>
    /// <b>The default is still to refuse.</b> Applying a schema change to a
    /// production database is a decision an operator makes, and until 2026-08-30
    /// that was the whole of the policy: outside Development a pending migration
    /// threw. The gap it left is that <i>nothing shipped could apply one</i> —
    /// <c>aj-admin</c> has no migrate command, the image carries no SDK, and
    /// running it as Development to get past the guard would seed the demo world
    /// and replace the administrator's password with a well-known one. A fresh
    /// installation therefore had every migration pending and never started.
    /// </para>
    ///
    /// <para>
    /// <c>Database:MigrateOnStart</c> is that decision, written down where an
    /// operator can make it: off by default, and set once in the deployment's
    /// own configuration. It does not weaken the rule — it is the rule, with a
    /// way to say yes.
    /// </para>
    /// </summary>
    public static class Schema
    {
        /// <summary><c>AJ_Database__MigrateOnStart</c>. Off unless an operator says otherwise.</summary>
        public const string MigrateOnStartSetting = "Database:MigrateOnStart";

        /// <summary>
        /// The advisory lock every migrating instance takes first.
        ///
        /// <para>
        /// <b>One number for the whole database, not one per context.</b> The two
        /// contexts share a database and are migrated one after the other by the
        /// same starting process, so a lock per context would let a second
        /// instance start migrating the application schema while the first was
        /// still on the LTI one.
        /// </para>
        ///
        /// <para>
        /// The value itself carries no meaning beyond being ours. PostgreSQL's
        /// advisory locks are one flat namespace shared with anything else using
        /// the same database, which is why it is a large arbitrary constant
        /// rather than 1.
        /// </para>
        /// </summary>
        private const long MigrationLock = 0x1A60_11D6_0000_0001;

        /// <summary>
        /// Migrates when told to, and then refuses to carry on if anything is
        /// still pending.
        /// </summary>
        /// <param name="database">The context to bring up to date.</param>
        /// <param name="migrate">
        /// Whether this process may apply migrations. Development always may;
        /// anything else may only when the operator has set the switch.
        /// </param>
        /// <param name="what">
        /// What to call this schema when something goes wrong, in a sentence an
        /// operator reads at three in the morning.
        /// </param>
        /// <param name="logger">Where the applied migrations are named.</param>
        public static void Ensure(
            DatabaseFacade database, bool migrate, string what, ILogger logger)
        {
            if (migrate)
            {
                Migrate(database, what, logger);
            }

            var pending = database.GetPendingMigrations().ToList();
            if (pending.Count == 0)
            {
                return;
            }

            // Names the switch, because this is the message a fresh installation
            // meets and "pending migrations" alone leaves an operator with
            // nothing to do about it.
            throw new InvalidOperationException(
                $"{what} has {pending.Count} pending migration(s), starting with "
                + $"{pending[0]}, and this Server is not configured to apply them. "
                + "Set AJ_Database__MigrateOnStart=true to have the Server bring the "
                + "schema up to date at start, or apply them yourself with "
                + "`dotnet ef database update` before starting it. Take a backup first.");
        }

        /// <summary>
        /// Applies what is pending, holding a lock for as long as it takes.
        ///
        /// <para>
        /// <b>Why a lock at all.</b> Several Server instances against one
        /// database is a supported arrangement, and they start together after an
        /// update. EF Core 10 has no lock of its own — measured 2026-08-30 by
        /// taking this one out: two instances migrating an empty database
        /// together kill one of them with <c>23505 duplicate key value violates
        /// unique constraint "PK___EFMigrationsHistory"</c>. Not the
        /// <c>42P07 relation already exists</c> one might expect, because each
        /// migration runs in a transaction and the collision therefore lands on
        /// the history row rather than on the table. With the lock the second
        /// waits, and by the time it looks there is nothing pending.
        /// </para>
        ///
        /// <para>
        /// <b>The connection is opened by hand and that is load-bearing.</b> An
        /// advisory lock belongs to a session, and EF opens and closes a
        /// connection around each call it makes — so taking the lock through
        /// <c>ExecuteSql</c> and then migrating would release it before the first
        /// table was created. Holding the connection open makes every call below
        /// one session.
        /// </para>
        /// </summary>
        private static void Migrate(DatabaseFacade database, string what, ILogger logger)
        {
            database.OpenConnection();
            try
            {
                database.ExecuteSql($"SELECT pg_advisory_lock({MigrationLock})");

                // Nested, so that a lock this never took is never released. The
                // outer `finally` closes the connection whatever happened; the
                // inner one runs only once the lock is actually held.
                try
                {
                    // Read again, now that nothing else can be writing it. The
                    // instance that waited here finds the work already done,
                    // which is the whole point of waiting.
                    var pending = database.GetPendingMigrations().ToList();
                    if (pending.Count == 0)
                    {
                        return;
                    }

                    // Said before rather than after: if this is the run that
                    // hangs or dies half way, the log has to name what it was
                    // doing.
                    logger.LogInformation(
                        "Applying {Count} migration(s) to {What}: {Migrations}",
                        pending.Count, what, string.Join(", ", pending));

                    database.Migrate();

                    logger.LogInformation("{What} is up to date.", what);
                }
                finally
                {
                    // **Belt and braces, and measured as such on 2026-08-30.**
                    // Taking this line out — and the `CloseConnection` below with
                    // it — still leaves no advisory lock held, because disposing
                    // the context returns the connection to the pool and Npgsql
                    // resets it. So this is not what makes the lock go away.
                    //
                    // It stays because `Ensure` takes somebody else's
                    // `DatabaseFacade` and should leave it as it found it: the
                    // release then belongs to the code that acquired it rather
                    // than to whenever the caller happens to dispose a context.
                    // There is no test on it, deliberately — the one written for
                    // it passed with the line deleted, and a green test that
                    // cannot fail is worse than none.
                    database.ExecuteSql($"SELECT pg_advisory_unlock({MigrationLock})");
                }
            }
            finally
            {
                database.CloseConnection();
            }
        }
    }
}
