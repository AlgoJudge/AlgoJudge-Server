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
        /// RSA-2048, which is <b>this project's choice and not a requirement of
        /// the specification</b>.
        /// <para>
        /// 1EdTech Security Framework v1.0 makes RS256 a default at
        /// <c>SHOULD</c> strength and explicitly negotiable at registration —
        /// "the algorithm sent by the Tool in the
        /// <c>id_token_signed_response_alg</c> parameter" — and mandates no RSA
        /// key size at all. §9 of <c>LMS_INTEGRATION.md</c> stated both as the
        /// specification's until it was corrected on 2026-08-13.
        /// </para>
        /// <para>
        /// The reason to pick them anyway is the platform. Measured 2026-08-13
        /// against Moodle 4.5.13, 5.2.2 and
        /// 5.3dev: <c>openid-configuration.php</c> advertises exactly one value
        /// for both <c>id_token_signing_alg_values_supported</c> and
        /// <c>token_endpoint_auth_signing_alg_values_supported</c> — `RS256` —
        /// and <c>locallib.php</c> hardcodes it when decoding. The second of
        /// those is the one that binds us: it governs the client assertion this
        /// key signs for AGS.
        /// </para>
        /// <para>
        /// An EC key is not unthinkable — <c>jwks_helper.php</c> parses
        /// ES256/384/512 — but nothing advertises them, so it would be a bet on
        /// an undocumented path.
        /// </para>
        /// <para>
        /// <b>Worth replacing when a platform offers better, and cheap to.</b>
        /// The algorithm is negotiated at registration rather than fixed by the
        /// protocol, and this key is already rotatable with an overlapping pair
        /// in the JWKS — so adopting ES256 the day a platform advertises it is a
        /// key rotation, not a migration. The one thing missing would be a
        /// column here naming the algorithm, which is implied while there is
        /// only one. §9 of <c>LMS_INTEGRATION.md</c> records the trigger:
        /// a new value in <c>token_endpoint_auth_signing_alg_values_supported</c>,
        /// which <c>AlgoJudge-Moodle/scripts/probe.sh</c> answers in a line.
        /// </para>
        /// <para>
        /// Generated with the framework's own RSA rather than with BouncyCastle:
        /// .NET 8 exports and imports PEM natively, so this needs no dependency
        /// at all, and the Ed25519 package already here is for the Runner
        /// handshake, which is a different problem.
        /// </para>
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
