using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// Liveness, for the container and for CI — and the one door that stays open.
    /// <para>
    /// Replaces <c>GET /ping/ping</c> — a verb in the path, singular, unversioned
    /// — with the shape everything else uses. Anonymous on purpose: a health
    /// check that needs a session cannot tell "the process is up" from "the
    /// database that holds sessions is down".
    /// </para>
    /// <para>
    /// <b>It answers 200 at every maintenance level.</b> Two reasons, and both
    /// are load-bearing. The container's own healthcheck greps this for
    /// <c>200 OK</c>, so a 503 during a planned window would have Docker kill the
    /// process the operator is deliberately keeping alive. And it is what a
    /// Client and a Runner poll to learn they may come back — a door that closed
    /// with the rest would make the window impossible to leave.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("health")]
    public class HealthController(
        IMaintenanceService maintenance,
        Storage.IStorageHealth storage
    ) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<HealthDto>(StatusCodes.Status200OK)]
        public async Task<HealthDto> Get(CancellationToken ct)
        {
            var state = await maintenance.StateAsync(ct);

            return new HealthDto
            {
                Status = "ok",
                // **Absent while open**, so a reader that has never heard of
                // maintenance sees exactly the document it saw before this
                // existed — and so "is there a window on" is a question about
                // presence rather than about parsing a word.
                Maintenance = state.Level == MaintenanceLevel.Open
                    ? null
                    : MaintenanceWire.Dto(state),
                // Cached for a minute inside, because the container polls this
                // every ten seconds and the smoke test behind it writes a blob.
                Storage = await storage.SummaryAsync(ct),
            };
        }
    }
}
