namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// Where one blob lives, said in the two ways a store needs to hear it.
    /// <para>
    /// <b>Both identifiers, always.</b> The layout comes from the checksum and
    /// the leaf name from the id (§5.1), so a store cannot be asked to find a
    /// blob from only one of them. Passing the pair everywhere is what stops a
    /// caller inventing a path.
    /// </para>
    /// <para>
    /// The checksum here is the one the <b>caller declared</b>, not one the
    /// Server has verified — on a write there is nothing to verify yet, because
    /// the bytes have not been read. That is safe because the two are reconciled
    /// before anything is committed: <see cref="IBlobStore.WriteAsync"/> returns
    /// what the bytes actually hashed to, and a caller that finds a mismatch
    /// deletes this key and stores no row. A blob nobody has a row for is
    /// unreachable, and the collector takes it.
    /// </para>
    /// </summary>
    public readonly record struct BlobKey(Guid FileId, string Sha256)
    {
        /// <summary>
        /// The path every store uses: three levels of fan-out from the checksum,
        /// then the file id.
        /// <para>
        /// <b>The leaf is the id, not the checksum</b>, so identical bytes
        /// uploaded twice are two blobs. That gives up deduplication on purpose:
        /// with the checksum as the leaf, deleting one file would have to prove
        /// no other file shares its bytes, and reference counting below the
        /// <c>File</c> row is exactly the bookkeeping this product does not want
        /// to own. Collecting a file deletes one blob, and the question never
        /// arises.
        /// </para>
        /// <para>
        /// Deliberately the layout the Runner's cache uses, so one description
        /// covers both (FILE_INTEGRITY.md).
        /// </para>
        /// </summary>
        public string Path
        {
            get
            {
                // Not defensive dressing: a short or absent checksum would
                // produce a path like `//<id>`, which lands every such blob in
                // one directory on filesystem and one prefix on S3 — the exact
                // hot spot the fan-out exists to avoid. Better to fail here than
                // to find out at a hundred thousand files.
                if (Sha256 is not { Length: >= 6 })
                {
                    throw new InvalidOperationException(
                        "A blob key needs its checksum to derive a path");
                }
                return $"{Sha256[..2]}/{Sha256[2..4]}/{Sha256[4..6]}/{FileId:D}";
            }
        }
    }

    /// <summary>What the bytes turned out to be, once they had all gone past.</summary>
    public record BlobWriteResult
    {
        /// <summary>Lowercase hexadecimal SHA-256, computed over what was written.</summary>
        public required string Sha256 { get; init; }

        public required long SizeBytes { get; init; }
    }

    /// <summary>
    /// How the bytes should reach the caller.
    /// <para>
    /// One case today, and that is the point: this is the seam a later offload —
    /// a presigned URL, an <c>X-Accel-Redirect</c> — would grow a second case in.
    /// Without it, that change becomes an operation on the endpoint, the
    /// authorization check and the cache headers at once (§10.0).
    /// </para>
    /// </summary>
    public enum BlobDeliveryKind
    {
        /// <summary>Read it here and write it to the response. The only one in the base version.</summary>
        StreamFromServer = 0,
    }

    public record BlobDelivery
    {
        public required BlobDeliveryKind Kind { get; init; }
    }

    /// <summary>
    /// Whether a store is there and whether it works, which are two questions.
    /// <para>
    /// <b><see cref="Detail"/> never reaches a public response.</b> It names
    /// whatever went wrong, and what goes wrong in a store is a bucket, a host or
    /// a path — the things A65c says no public answer, error code or header may
    /// carry. It exists for the administrator surface and for the log.
    /// </para>
    /// </summary>
    public record StoreHealth
    {
        public required string StoreId { get; init; }

        /// <summary>Whether the store answered at all.</summary>
        public required bool Reachable { get; init; }

        /// <summary>Whether a write, a read and a checksum comparison all succeeded.</summary>
        public required bool SmokeTestPassed { get; init; }

        public string? Detail { get; init; }

        public bool Ok => Reachable && SmokeTestPassed;
    }

    /// <summary>
    /// One configured place where bytes may live.
    /// <para>
    /// <b>Nothing above this may know which backend it is talking to.</b> No
    /// controller, no service and no test outside the implementations may branch
    /// on the kind of store configured (§2, invariant 5) — that is what keeps a
    /// backend added later from leaking its assumptions upward, and what makes
    /// the conformance suite worth running.
    /// </para>
    /// <para>
    /// Implementations MUST NOT depend on EF Core (§4). Two independent reasons:
    /// EF cannot stream a <c>byte[]</c>, and its change tracking would take a
    /// snapshot of every file it touched — so a store built on it would break
    /// the one invariant this whole document exists for.
    /// </para>
    /// </summary>
    public interface IBlobStore
    {
        /// <summary>
        /// The configured id, not the kind. A deployment may run several stores
        /// of one kind, and <c>File.StorageId</c> names one of them for ever.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Writes a stream, computing SHA-256 along the way, and answers with
        /// what it actually got.
        /// <para>
        /// The write goes to a temporary name and is moved to
        /// <see cref="BlobKey.Path"/> only once it is whole, so a partially
        /// written blob is never observable under its final key (§5.2). A caller
        /// whose declared checksum does not match the returned one deletes this
        /// key and commits no row.
        /// </para>
        /// </summary>
        Task<BlobWriteResult> WriteAsync(BlobKey key, Stream content, CancellationToken ct);

        /// <summary>The whole blob, from the beginning.</summary>
        Task<Stream> OpenReadAsync(BlobKey key, CancellationToken ct);

        /// <summary>
        /// Part of the blob — <paramref name="offset"/> bytes in, at most
        /// <paramref name="length"/> bytes, or to the end when it is null.
        /// <para>
        /// <b>Added beyond §4, because §13.1 cannot be met without it.</b>
        /// ASP.NET Core answers <c>206</c> only from a stream whose
        /// <c>CanSeek</c> is true, and neither <c>NpgsqlDataReader.GetStream()</c>
        /// nor an S3 <c>GetObject</c> body is seekable. Both backends can serve a
        /// range natively — <c>substring(… from … for …)</c> and the <c>Range</c>
        /// header — so what was missing was the interface, not the capability.
        /// <see cref="BlobStream"/> turns this into the seekable stream MVC wants.
        /// </para>
        /// </summary>
        Task<Stream> OpenReadAsync(BlobKey key, long offset, long? length, CancellationToken ct);

        Task<bool> ExistsAsync(BlobKey key, CancellationToken ct);

        /// <summary>
        /// Removes the blob. <b>Idempotent</b>: a blob that is already gone is a
        /// success, because the collector may run twice over the same file and
        /// the second run must not be an error.
        /// </summary>
        Task DeleteAsync(BlobKey key, CancellationToken ct);

        Task<BlobDelivery> PrepareDeliveryAsync(BlobKey key, CancellationToken ct);

        /// <summary>
        /// Write, read back, compare, clean up. Never throws — an unreachable
        /// store is an answer, not an exception, because the caller is a health
        /// endpoint that has to report on the others too.
        /// </summary>
        Task<StoreHealth> CheckHealthAsync(CancellationToken ct);
    }

    /// <summary>
    /// Every configured store, and which one takes new writes.
    /// <para>
    /// The indirection exists because <c>File.StorageId</c> is a permanent
    /// property of a row while <c>Storage__Default</c> is a property of the
    /// configuration: a read follows its own row, for ever, and only a write
    /// asks what the default is. Conflating them is how a deployment that
    /// changed its default would stop being able to read its own history.
    /// </para>
    /// </summary>
    public interface IBlobStoreRegistry
    {
        /// <summary>The store that accepts new writes — <c>Storage__Default</c>.</summary>
        IBlobStore Default { get; }

        /// <summary>
        /// The store a row names, or null when the configuration no longer has
        /// one by that id. Null is a real answer and becomes a <c>503</c>: the
        /// file exists and this installation cannot currently reach it, which is
        /// not the same thing as a <c>404</c>.
        /// </summary>
        IBlobStore? Find(string storageId);

        IReadOnlyList<IBlobStore> All { get; }
    }
}
