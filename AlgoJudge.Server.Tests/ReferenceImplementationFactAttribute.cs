namespace AlgoJudge.Server.Tests;

/// <summary>
/// A test that runs only against the S3 implementation a release is checked
/// against.
/// <para>
/// <b>Conditional, and reported either way.</b> §13.3 is a check to be run
/// against SeaweedFS before a release, not on every commit — and one of its
/// items cannot be run against the development endpoint at all, because RustFS
/// does not lay object bytes down in a form a grep of its data directory can
/// read. Marked <c>Skip</c> at discovery when the implementation is anything
/// else, so a run says "skipped, and here is why" rather than passing quietly.
/// </para>
/// <code>
/// ALGOJUDGE_S3=seaweedfs dotnet test --filter S3BlobStoreTests
/// </code>
/// </summary>
public class ReferenceImplementationFactAttribute : FactAttribute
{
    public ReferenceImplementationFactAttribute()
    {
        var implementation =
            Environment.GetEnvironmentVariable(S3BlobStoreTests.ImplementationVariable);

        if (!string.Equals(implementation, "seaweedfs", StringComparison.OrdinalIgnoreCase))
        {
            Skip =
                $"Runs against the reference implementation. Set {S3BlobStoreTests.ImplementationVariable}"
                + "=seaweedfs — the development endpoint stores object bytes in a form this check "
                + "cannot read, so a result there would mean nothing.";
        }
    }
}

/// <summary>
/// A test that also needs an endpoint which will turn server-side encryption on.
/// <para>
/// <b>Neither implementation this repository can start will.</b> Measured on
/// 2026-08-13: RustFS accepts <c>PutBucketEncryption</c> but stores objects in a
/// form no grep of its data directory can read, encrypted or not; SeaweedFS 4.41
/// stores them readably — which is what makes the check possible at all — and
/// answers <c>PutBucketEncryption</c> with an internal error.
/// </para>
/// <para>
/// So §13.3's last item is <b>not vacuous and not verified</b>: the method is
/// proven to work by the control beside it, and what is missing is an endpoint
/// that will enable the thing being checked. Set
/// <c>ALGOJUDGE_S3_SSE=1</c> against one that does, and it runs.
/// </para>
/// </summary>
public sealed class EncryptionCapableFactAttribute : ReferenceImplementationFactAttribute
{
    public EncryptionCapableFactAttribute()
    {
        if (Skip is not null) return;

        if (Environment.GetEnvironmentVariable("ALGOJUDGE_S3_SSE") != "1")
        {
            Skip =
                "Needs an endpoint that enables bucket-default server-side encryption. "
                + "Measured 2026-08-13: SeaweedFS 4.41 answers PutBucketEncryption with an "
                + "internal error, and RustFS stores objects unreadably to a grep either way. "
                + "Set ALGOJUDGE_S3_SSE=1 against one that supports it.";
        }
    }
}
