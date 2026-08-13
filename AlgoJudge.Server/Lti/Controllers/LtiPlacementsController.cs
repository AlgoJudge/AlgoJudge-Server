using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>
    /// What a manager reads about one placement's grades, and the one action they
    /// can take about it.
    /// <para>
    /// §6.4 asked for this to be <b>a plain count on the activity's screen with a
    /// resync action, rather than a log nobody reads</b> — because "the grades
    /// are not going through" is something a teacher discovers from a student
    /// otherwise.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/placements")]
    [Authorize]
    public class LtiPlacementsController(IGradeVerifier verifier) : ControllerBase
    {
        /// <summary>
        /// How the grades stand.
        /// </summary>
        /// <param name="verify">
        /// Ask the platform what it actually holds, rather than trusting what we
        /// last sent. <b>Off by default because it costs a round trip per
        /// column</b>, and a screen that reloads should not hammer somebody
        /// else's Moodle — but it is the only thing that catches a score the
        /// platform accepted and silently dropped, so the button exists.
        /// </param>
        [HttpGet("{id:guid}/grades")]
        [ProducesResponseType<GradeSummary>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<GradeSummary> Grades(
            Guid id, [FromQuery] bool verify, CancellationToken ct) =>
            verifier.SummariseAsync(id, verify, ct);

        /// <summary>
        /// Sends everything postable again.
        /// <para>
        /// Deliberate rather than scheduled: a teacher who edited a grade by hand
        /// should not have it overwritten by a sweep they did not ask for.
        /// </para>
        /// </summary>
        [HttpPost("{id:guid}/grades/resync")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Resync(Guid id, CancellationToken ct) =>
            Ok(new { queued = await verifier.ResyncAsync(id, ct) });
    }
}
