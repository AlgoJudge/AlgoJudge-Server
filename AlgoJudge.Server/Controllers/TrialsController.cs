using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// Trial runs: a package somebody wants timed, attached to no problem.
    /// <para>
    /// **At the top level rather than under an activity**, because a trial
    /// belongs to a person. An activity is where permission to ask for one may
    /// be granted — not what the trial is part of — and a manager calibrating a
    /// problem in the library has no activity to be under. Scoping the path to
    /// one would have left that case with nowhere to post.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("trials")]
    [Authorize]
    public class TrialsController(ITrialService trials) : ControllerBase
    {
        /// <summary>
        /// Asks for a package to be run.
        /// <para>
        /// **Not a submission** (D-9): it produces timings rather than a
        /// verdict, belongs to nobody's standing and appears on no board. The
        /// bytes are uploaded through the file API first and named here by id,
        /// the same two steps every other stored file uses — which is what lets
        /// the checksum be verified before anything is queued.
        /// </para>
        /// <para>
        /// `activityIdOrSlug` says where permission is asked for. **Absent means
        /// the library**, and then `trial:run` must be held globally.
        /// </para>
        /// </summary>
        [HttpPost]
        [ProducesResponseType<TrialDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        // `new`, not a rename: the action's name is its `operationId`, so renaming
        // it would move the contract to silence a warning about a name.
        public new Task<TrialDto> Request([FromBody] NewTrialDto input, CancellationToken ct) =>
            trials.RequestAsync(input.ActivityIdOrSlug, input.ProblemType, input.PackageFileId, ct);

        /// <summary>
        /// One trial, to whoever asked for it — or to somebody who reads
        /// everybody's work in the scope it was asked for.
        /// <para>
        /// Answers **404 rather than 403** to anybody else: that a private
        /// measurement was taken at all is something its owner did not publish.
        /// </para>
        /// </summary>
        [HttpGet("{trialId:guid}")]
        [ProducesResponseType<TrialDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public Task<TrialDto> Get(Guid trialId, CancellationToken ct) =>
            trials.GetAsync(trialId, ct);
    }
}
