namespace AlgoJudge.Server.Tests;

/// <summary>
/// A test that needs an endpoint which will turn bucket-default server-side
/// encryption on and go on serving.
/// <para>
/// <b>SeaweedFS accepts the configuration and then breaks the bucket.</b>
/// Measured on 4.43 and 4.45 (2026-09-07) and again on 4.47 (2026-09-20),
/// identically:
/// <c>PutBucketEncryption</c> is accepted, <c>GetBucketEncryption</c> returns
/// the rule, and every write to that bucket afterwards fails with "we
/// encountered an internal error". A write to the same bucket before the call
/// succeeds. So the store does not refuse what it cannot do — it agrees and
/// then stops working, which is worse and is why this skips rather than fails
/// there.
/// </para>
/// <para>
/// RustFS 1.0.0 accepts it, keeps serving, and reports <c>AES256</c> for
/// the object — which is what the test asserts, and why the default endpoint
/// runs it.
/// </para>
/// <code>
/// ALGOJUDGE_S3=seaweedfs dotnet test --filter S3BlobStoreTests
/// </code>
/// </summary>
public sealed class EncryptionCapableFactAttribute : FactAttribute
{
    public EncryptionCapableFactAttribute()
    {
        var implementation =
            Environment.GetEnvironmentVariable(S3BlobStoreTests.ImplementationVariable);

        if (string.Equals(implementation, "seaweedfs", StringComparison.OrdinalIgnoreCase))
        {
            Skip =
                "SeaweedFS accepts bucket-default encryption and then fails every write to that "
                + "bucket — measured on 4.43, 4.45 and 4.47 alike. The endpoint has to be one "
                + "that keeps serving after PutBucketEncryption.";
        }
    }
}
