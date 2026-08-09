using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// How far the Server has withdrawn from service. One row, for ever.
    /// <para>
    /// **In the database rather than in configuration**, so a Server that
    /// restarts in the middle of a backup comes back withdrawn rather than
    /// quietly opening to the world. An environment variable would be read once
    /// at start and could not be changed without a restart, which is the one
    /// thing maintenance mode exists to avoid.
    /// </para>
    /// <para>
    /// **Its own table rather than a column on <see cref="Instance"/>**, though
    /// that is the singleton this copies. `Instance` is branding and is written
    /// from the manager panel by whoever holds `instance:update`; this is
    /// written only from the machine itself. Sharing the row would sooner or
    /// later mean sharing the write path, and a manager who can rename the
    /// installation must not be able to take it off the air.
    /// </para>
    /// </summary>
    public class MaintenanceState
    {
        /// <summary>The one and only row. Fixed, so the constraint can name it.</summary>
        public static readonly Guid SingletonId = new("00000000-0000-7000-8000-000000000002");

        public Guid Id { get; set; } = SingletonId;

        /// <summary>
        /// `0` open, `1` draining, `2` closed. See <see cref="MaintenanceLevel"/>.
        /// <para>
        /// An integer rather than a boolean because the middle state is the
        /// point: a Runner that is halfway through marking somebody's work needs
        /// somewhere to put the answer, and a single flag would either keep the
        /// door open for everybody or slam it on that Runner.
        /// </para>
        /// </summary>
        public MaintenanceLevel Level { get; set; } = MaintenanceLevel.Open;

        /// <summary>
        /// When the withdrawal was asked for — **not** when the level last
        /// changed. It is what the forced transition to `Closed` is measured
        /// against, so it must survive `Draining` becoming `Closed`.
        /// </summary>
        public DateTime? RequestedAt { get; set; }

        /// <summary>
        /// Free text from whoever threw the switch: "nightly backup", a change
        /// number, a name. Shown to nobody by default and stored so that an
        /// operator finding the Server closed can tell who closed it and why.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>Optimistic concurrency, as elsewhere: PostgreSQL's `xmin`.</summary>
        public uint Version { get; set; }
    }

    /// <summary>
    /// How far the withdrawal has gone. **Ordered**, and compared with `>=`
    /// throughout — a level added between two of these would change what every
    /// comparison means, so a new one goes on the end or not at all.
    /// </summary>
    public enum MaintenanceLevel
    {
        /// <summary>Business as usual.</summary>
        Open = 0,

        /// <summary>
        /// No new work, but work in flight may finish.
        /// <para>
        /// Participants are refused, submissions are refused, and both claim
        /// queues answer as though they were empty. A Runner that already holds
        /// a job keeps its lease, may renew it, and — the point of this level —
        /// **may still report what it computed**. Otherwise every evaluation
        /// running when the switch was thrown would be thrown away.
        /// </para>
        /// </summary>
        Draining = 1,

        /// <summary>
        /// Nothing but `/health`.
        /// <para>
        /// Reached when the last job finishes, or forced after a deadline. This
        /// is the state in which a database may be backed up or the process
        /// stopped: nothing is writing.
        /// </para>
        /// </summary>
        Closed = 2,
    }
}
