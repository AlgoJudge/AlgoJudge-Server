using AlgoJudge.Server.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>
    /// The tool's public key set.
    /// <para>
    /// <b>Anonymous, and it has to be.</b> A platform fetches this before any
    /// trust exists between the two sides — that is what the fetch is for — and
    /// there is nothing secret in it: a public key is public or it is useless.
    /// </para>
    /// <para>
    /// The private half is not reachable from here or from anywhere else. §9 of
    /// <c>LMS_INTEGRATION.md</c> makes that one of the approved decisions, and
    /// <c>ToolKeyDisclosureTests</c> is what holds it.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti")]
    public class LtiKeysController(IToolKeyService keys) : ControllerBase
    {
        /// <summary>
        /// Named <c>jwks.json</c> rather than served from <c>/.well-known/</c>
        /// because every path in this Server lives under its fixed API base, and
        /// a platform is given this URL explicitly at registration rather than
        /// discovering it. A tool key set has no discovery convention to break.
        /// </summary>
        [HttpGet("jwks.json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public Task<object> KeySet(CancellationToken ct) => keys.KeySetAsync(ct);
    }

    /// <summary>
    /// Rotating that key, which is <b>two deliberate acts and no schedule</b>
    /// (decided 2026-08-15).
    ///
    /// <para>
    /// A platform caches a key set on its own terms and refetches when it feels
    /// like it. So rotating mints a new key and leaves the old one published —
    /// signatures already in flight still verify — and a <i>second</i> act, taken
    /// once somebody can see every platform has refetched, closes the overlap.
    /// Automating either would put the failure in somebody else's installation at
    /// a moment nobody chose.
    /// </para>
    ///
    /// <para>
    /// A separate class from the key set above because these are the opposite
    /// thing: that one is anonymous by necessity, these are behind
    /// <c>provider:manage</c>. Nothing here answers with a private key, and
    /// <see cref="ToolKeyDto"/> has nowhere to put one.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/keys")]
    [Authorize]
    public class LtiKeyRotationController(IToolKeyService keys) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<ToolKeyDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<ToolKeyDto>> List(CancellationToken ct) => keys.ListAsync(ct);

        [HttpPost("rotate")]
        [ProducesResponseType<ToolKeyDto>(StatusCodes.Status200OK)]
        public Task<ToolKeyDto> Rotate(CancellationToken ct) => keys.RotateAsync(ct);

        /// <summary>
        /// Closes the overlap for one retired key. Refused for the key that is
        /// still signing — see <see cref="IToolKeyService.WithdrawAsync"/>.
        /// </summary>
        [HttpPost("{kid}/withdraw")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Withdraw(string kid, CancellationToken ct)
        {
            await keys.WithdrawAsync(kid, ct);
            return NoContent();
        }
    }
}
