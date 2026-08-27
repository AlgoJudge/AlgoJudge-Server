using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The keys that encrypt every session cookie, for somebody standing on the
    /// machine.
    /// <para>
    /// <b>It exists because the alternative was raw SQL.</b> Until this endpoint
    /// the documented way to get an encrypted key onto an installation that
    /// already had a plaintext one was to delete rows from
    /// <c>DataProtectionKeys</c> by hand — which <c>aj-admin</c> exists to
    /// remove, and which destroys the record instead of revoking it.
    /// </para>
    /// <para>
    /// Guarded like the rest of <c>/admin</c>: the loopback interface
    /// <b>and</b> the configured token, checked by <see cref="AdminSurface"/> for
    /// the whole group. Not a permission, for the reason the maintenance switch
    /// is not one: revoking every key signs out every session on the
    /// installation, and that must not be something a stolen administrator
    /// session can do.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("admin/keyring")]
    [AllowAnonymous]
    public class AdminKeyRingController(IKeyRingOperations keyring) : ControllerBase
    {
        /// <summary>
        /// What is in force, what is stored, and what needs doing — including
        /// the validation nothing else performs: whether every stored key can
        /// still be read with the certificates configured today.
        /// </summary>
        [HttpGet]
        [ProducesResponseType<KeyRingReportDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public KeyRingReportDto Get() => Report(keyring.Report());

        /// <summary>
        /// A new key, active now, and <b>nobody is signed out</b>.
        /// <para>
        /// The answer to a certificate that was configured after the fact:
        /// Data Protection encrypts a key when it writes one, so an installation
        /// that turned encryption on keeps a plaintext key until the ring next
        /// rotates on its own — ninety days. This writes one now, and every
        /// existing session keeps working because the old key stays readable.
        /// </para>
        /// </summary>
        [HttpPost("rotate")]
        [ProducesResponseType<KeyRingKeyDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<KeyRingKeyDto> Rotate() => StatusCode(
            StatusCodes.Status201Created, Key(keyring.Rotate()));

        /// <summary>
        /// Every key revoked. <b>Everybody is signed out</b>, and a key that
        /// leaked in a database dump can no longer mint a cookie this Server
        /// accepts.
        /// <para>
        /// <c>confirm</c> is required and must be the word <c>revoke</c>. Not a
        /// boolean: a flag that defaults to false is still a flag somebody sets
        /// while reading the other half of a sentence, and this one ends every
        /// session on the installation.
        /// </para>
        /// </summary>
        [HttpPost("revoke")]
        [ProducesResponseType<KeyRingRevokeDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public KeyRingRevokeDto Revoke(
            [FromQuery] string? confirm, [FromQuery] string? reason)
        {
            if (!string.Equals(confirm, "revoke", StringComparison.Ordinal))
            {
                throw new ValidationException(
                    "Revoking signs out every session on this installation. Repeat the request "
                    + "with confirm=revoke if that is what you meant.",
                    "keyring.confirm");
            }

            var why = string.IsNullOrWhiteSpace(reason) ? "revoked by the operator" : reason.Trim();
            return new KeyRingRevokeDto { Revoked = keyring.RevokeAll(why), Reason = why };
        }

        private static KeyRingReportDto Report(KeyRingState state) => new()
        {
            Kind = state.Kind,
            ApplicationName = state.ApplicationName,
            Certificates = state.Certificates.Select(certificate => new KeyRingCertificateDto
            {
                Subject = certificate.Subject,
                Thumbprint = certificate.Thumbprint,
                NotAfter = Wire.At(certificate.NotAfter.UtcDateTime),
                Encrypts = certificate.Encrypts,
            }).ToList(),
            Keys = state.Keys.Select(Key).ToList(),
            Problems = state.Problems,
        };

        private static KeyRingKeyDto Key(KeyRingKeyState key) => new()
        {
            Id = key.Id,
            CreatedAt = Wire.At(key.CreatedAt.UtcDateTime),
            ActivatesAt = Wire.At(key.ActivatesAt.UtcDateTime),
            ExpiresAt = Wire.At(key.ExpiresAt.UtcDateTime),
            IsActive = key.IsActive,
            IsRevoked = key.IsRevoked,
            Storage = key.Storage,
            Readable = key.Readable,
        };
    }
}
