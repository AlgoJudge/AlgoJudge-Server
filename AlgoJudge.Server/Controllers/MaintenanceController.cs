using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The switch, thrown from the machine the Server is running on.
    /// <para>
    /// <b>Not a permission.</b> Taking the installation off the air is an
    /// operator's act, not a role somebody can be granted — and the moment it is
    /// a permission it is also something a compromised administrator session can
    /// do. What guards it instead is <see cref="AdminSurface"/>, over the whole
    /// <c>/admin</c> group: a caller on the loopback interface <b>and</b> the
    /// configured token.
    /// </para>
    /// <para>
    /// <b>Anonymous on purpose</b>, and that is safe only because of the group
    /// rule. An operator locked out by the very outage they are fixing is the
    /// case this has to work in, so it cannot need a session — the database that
    /// holds sessions may be the thing being backed up.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("admin/maintenance")]
    [AllowAnonymous]
    public class MaintenanceController(IMaintenanceService maintenance) : ControllerBase
    {
        /// <summary>What the switch is set to now.</summary>
        [HttpGet]
        [ProducesResponseType<MaintenanceDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<MaintenanceDto> Get(CancellationToken ct)
        {
            Local();
            return MaintenanceWire.Dto(await maintenance.StateAsync(ct));
        }

        /// <summary>
        /// Throws it. <c>on</c> begins draining; <c>off</c> opens immediately.
        /// <para>
        /// There is no way to ask for <c>closed</c> directly, and that is
        /// deliberate: closing while a Runner is halfway through marking
        /// somebody's work would throw that work away. The drainer decides when
        /// draining is done.
        /// </para>
        /// </summary>
        /// <param name="input">
        /// The ordinary form, for anything that can send a body.
        /// </param>
        /// <param name="on">
        /// The same flag in the query string, which is how an operator throws
        /// this by hand. It <b>wins over the body</b> where both are given —
        /// there is no sensible reading of a request that says both, and the one
        /// typed at the end of the URL is the one somebody meant.
        /// </param>
        /// <param name="reason">Why, in the query string.</param>
        [HttpPost]
        [ProducesResponseType<MaintenanceDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<MaintenanceDto> Set(
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MaintenanceInputDto? input,
            [FromQuery] bool? on,
            [FromQuery] string? reason,
            CancellationToken ct)
        {
            Local();

            // Neither said anything. Refused rather than assumed: guessing wrong
            // in one direction serves requests during a backup, and in the other
            // takes an installation off the air nobody asked to close.
            var asked = on ?? input?.On
                ?? throw new ValidationException("on is required, in the body or in the query string");

            return MaintenanceWire.Dto(
                await maintenance.SetAsync(asked, reason ?? input?.Reason, ct));
        }

        /// <summary>
        /// Refuses anybody who is not on this machine — <b>404, not 403</b>.
        /// <para>
        /// <b>Said twice on purpose.</b> <see cref="AdminSurface"/> already
        /// refuses this and more, and this is what survives somebody removing or
        /// reordering that middleware — the same reason
        /// <c>RunnerService.ClaimAsync</c> checks the maintenance level itself
        /// rather than trusting the gate in front of it. A duplicated rule costs
        /// a line; a missing one opens the endpoint that takes the installation
        /// off the air.
        /// </para>
        /// <para>
        /// The address comes from <see cref="Peer"/>, captured before
        /// <c>UseForwardedHeaders</c> rewrites it. Reading
        /// <c>Connection.RemoteIpAddress</c> here instead would read the proxy's
        /// claim, and <c>X-Forwarded-For: 127.0.0.1</c> would open the switch to
        /// the internet.
        /// </para>
        /// </summary>
        private void Local()
        {
            if (!Peer.IsLoopback(HttpContext)) throw new NotFoundException("Endpoint");
        }
    }
}
