using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Preconfiguration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The installation's own settings, as files on a disk somebody mounted.
    /// <para>
    /// <b>Two endpoints, one walk.</b> The first works out what the files would
    /// change and writes nothing; the second does it and answers the same shape.
    /// An operator is meant to run them in that order, and the first is safe to
    /// run at any time on any installation.
    /// </para>
    /// <para>
    /// Guarded like the rest of <c>/admin</c>: the loopback interface <b>and</b>
    /// the configured token, checked by <see cref="Authorization.AdminSurface"/>
    /// for the whole group. Not a permission — this reconfigures the
    /// installation from a file only somebody standing on the machine can put
    /// there, which is not something a stolen administrator session should reach.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("admin/config")]
    [AllowAnonymous]
    public class AdminConfigController(IPreconfiguration preconfiguration) : ControllerBase
    {
        /// <summary>What the files hold, and what differs from the database.</summary>
        [HttpGet]
        [ProducesResponseType<PreconfigurationPlanDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<PreconfigurationPlanDto> Get(CancellationToken ct) =>
            Plan(await preconfiguration.PlanAsync(ct));

        /// <summary>
        /// Applies what differs.
        /// <para>
        /// <b>No confirmation word</b>, unlike revoking a key ring: this adds and
        /// never withdraws, the endpoint above shows exactly what it will do, and
        /// a document it republishes supersedes rather than replaces — so there
        /// is nothing here that cannot be read back afterwards.
        /// </para>
        /// </summary>
        [HttpPost("apply")]
        [ProducesResponseType<PreconfigurationPlanDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<PreconfigurationPlanDto> Apply(CancellationToken ct) =>
            Plan(await preconfiguration.ApplyAsync(ct));

        private static PreconfigurationPlanDto Plan(PreconfigurationPlan plan) => new()
        {
            Directory = plan.Directory ?? "",
            Applied = plan.Applied,
            Changes = plan.Changes.Select(change => new PreconfigurationChangeDto
            {
                Target = change.Target,
                Current = change.Current,
                Proposed = change.Proposed,
            }).ToList(),
            Warnings = plan.Warnings,
        };
    }
}
