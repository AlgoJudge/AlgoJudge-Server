using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// Liveness, for the container and for CI.
    /// <para>
    /// Replaces <c>GET /ping/ping</c> — a verb in the path, singular, unversioned
    /// — with the shape everything else uses. Anonymous on purpose: a health
    /// check that needs a session cannot tell "the process is up" from "the
    /// database that holds sessions is down".
    /// </para>
    /// </summary>
    [ApiController]
    [Route("health")]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Get() => Ok(new { status = "ok" });
    }
}
