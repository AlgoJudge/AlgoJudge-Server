using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    public enum StorageMigrationState
    {
        /// <summary>Asked for, and waiting for its window or for the queue to empty.</summary>
        Requested = 0,

        /// <summary>Moving files right now.</summary>
        Running = 1,

        /// <summary>Every file is on the target.</summary>
        Finished = 2,

        /// <summary>
        /// Stopped before it started, and <see cref="StorageMigration.Detail"/>
        /// says why — almost always a target that failed its smoke test (A84).
        /// </summary>
        Refused = 3,

        /// <summary>An operator called it off. Whatever moved stays moved.</summary>
        Cancelled = 4,
    }

    /// <summary>
    /// One run of moving files from wherever they are to the store that takes new
    /// writes.
    /// <para>
    /// <b>A row rather than a flag, for two reasons.</b> §11 requires the
    /// operator surface to report the progress of a running migration, which
    /// needs somewhere to count; and a migration has to survive a restart —
    /// starting one is a deliberate act (A83) and it would be a strange one if a
    /// deployment in the middle of it silently forgot.
    /// </para>
    /// <para>
    /// <b>It holds no per-file state.</b> Which files have moved is on the files:
    /// <c>File.StorageId</c> says where each one is and <c>PreviousStorageId</c>
    /// says where its stale copy still is. That is what makes a crash
    /// uninteresting (A85) — there is nothing here to reconcile, because this row
    /// never knew.
    /// </para>
    /// </summary>
    public class StorageMigration
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// Where everything is going. Recorded rather than read from
        /// configuration at each step, so that changing <c>Storage__Default</c>
        /// halfway through does not turn one migration into two.
        /// </summary>
        public required string TargetStoreId { get; set; }

        public StorageMigrationState State { get; set; } = StorageMigrationState.Requested;

        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public int FilesMoved { get; set; }
        public long BytesMoved { get; set; }

        /// <summary>
        /// What happened, when it is worth a sentence: why it was refused, or
        /// what it is waiting for. Read by an operator, never by a caller from
        /// the network.
        /// </summary>
        public string? Detail { get; set; }

        /// <summary>
        /// Optimistic concurrency. The advisory lock in <c>StorageMigrator</c>
        /// keeps two instances from <b>moving</b> at once; it does not stand
        /// between the worker and an operator cancelling. Without this a cancel
        /// is overwritten by the next state the worker writes, and a migration
        /// somebody was told had stopped carries on.
        /// </summary>
        public uint RowVersion { get; set; }
    }
}
