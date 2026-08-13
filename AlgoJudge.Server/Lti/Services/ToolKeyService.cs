using System.Security.Cryptography;
using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>The tool's own key: generated here, and never given out.</summary>
    public interface IToolKeyService
    {
        /// <summary>
        /// The key new signatures are made with, generating one on first use.
        /// </summary>
        Task<ToolKey> CurrentAsync(CancellationToken cancellationToken);

        /// <summary>
        /// The public key set, as a platform fetches it. Carries the current key
        /// and every retired one still inside its overlap.
        /// </summary>
        Task<object> KeySetAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Signing credentials for the current key, for a client assertion.
        /// </summary>
        Task<SigningCredentials> CredentialsAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// <para>
    /// <b>The private key is generated in the Server and never leaves it</b> —
    /// §9 of <c>LMS_INTEGRATION.md</c>, approved by the project owner. This class
    /// is the only thing that reads <see cref="ToolKey.PrivatePem"/>, and nothing
    /// it returns contains it: the key set is public halves, and the credentials
    /// are an in-memory <see cref="RsaSecurityKey"/> handed straight to the token
    /// handler.
    /// </para>
    /// <para>
    /// It replaces the thesis's arrangement, where a key pair was made elsewhere
    /// and pasted into configuration — which is how a private key ends up in a
    /// chat message, a wiki and a backup nobody remembers.
    /// </para>
    /// </summary>
    public class ToolKeyService(LtiDbContext db) : IToolKeyService
    {
        /// <summary>
        /// RSA-2048 is the specification's floor and what every platform
        /// accepts. Generated with the framework's own RSA rather than with
        /// BouncyCastle: .NET 8 exports and imports PEM natively, so this needs
        /// no dependency at all, and the Ed25519 package already here is for the
        /// Runner handshake, which is a different problem.
        /// </summary>
        private const int KeySizeBits = 2048;

        public async Task<ToolKey> CurrentAsync(CancellationToken cancellationToken)
        {
            var existing = await db.ToolKeys
                .Where(k => k.RetiredAt == null)
                .OrderByDescending(k => k.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                return existing;
            }

            using var rsa = RSA.Create(KeySizeBits);
            var key = new ToolKey
            {
                // Random rather than derived from the key. A kid derived from the
                // material tells a reader something about it, and there is
                // nothing about a private key worth telling.
                Kid = Guid.NewGuid().ToString("N"),
                PublicPem = rsa.ExportSubjectPublicKeyInfoPem(),
                PrivatePem = rsa.ExportPkcs8PrivateKeyPem(),
            };

            db.ToolKeys.Add(key);
            await db.SaveChangesAsync(cancellationToken);
            return key;
        }

        public async Task<object> KeySetAsync(CancellationToken cancellationToken)
        {
            // Generating on a read is deliberate: a platform fetching the set
            // before anything has been signed should find a key rather than an
            // empty set, which it would cache.
            await CurrentAsync(cancellationToken);

            // **Retired keys stay published.** Rotation without an overlap is an
            // outage in both directions — a platform holding the old set rejects
            // everything signed with the new key, and one that refetched rejects
            // everything still in flight from the old.
            var keys = await db.ToolKeys
                .OrderByDescending(k => k.CreatedAt)
                .ToListAsync(cancellationToken);

            return new
            {
                keys = keys.Select(key =>
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(key.PublicPem);
                    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                        new RsaSecurityKey(rsa.ExportParameters(false)));

                    return new
                    {
                        kty = jwk.Kty,
                        use = "sig",
                        alg = SecurityAlgorithms.RsaSha256,
                        kid = key.Kid,
                        n = jwk.N,
                        e = jwk.E,
                    };
                }).ToArray(),
            };
        }

        public async Task<SigningCredentials> CredentialsAsync(CancellationToken cancellationToken)
        {
            var key = await CurrentAsync(cancellationToken);

            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PrivatePem);

            // Not disposed here on purpose: `RsaSecurityKey` keeps using the
            // instance while the handler signs, and disposing it first throws
            // inside the token handler with a message about a disposed object
            // that names nothing to do with LTI.
            return new SigningCredentials(
                new RsaSecurityKey(rsa) { KeyId = key.Kid },
                SecurityAlgorithms.RsaSha256);
        }
    }
}
