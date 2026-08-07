using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// What a signed-out screen may know. Anonymous on purpose: the login and
    /// registration screens change shape with it, so it has to be readable
    /// before anybody has signed in.
    /// </summary>
    [ApiController]
    [Route("instance")]
    public class InstanceController(IInstanceService instances) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        public Task<InstanceInfoDto> Get(CancellationToken ct) => instances.GetAsync(ct);
    }

    /// <summary>
    /// The signed-in account.
    /// <para>
    /// The cookie is the truth: the Client asks for this once on load rather than
    /// remembering a session of its own, which is how a reload used to sign
    /// somebody out of the interface while their session was still valid.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("account")]
    [Authorize]
    public class AccountController(ICurrentUserService currentUser) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status401Unauthorized)]
        public async Task<SessionDto> Get(CancellationToken ct) =>
            Projections.Session(await currentUser.RequireAsync(ct));
    }

    /// <summary>The permission catalogue, and what the caller holds.</summary>
    [ApiController]
    [Route("permissions")]
    [Authorize]
    public class PermissionsController(IPermissionService permissions) : ControllerBase
    {
        /// <summary>
        /// The whole vocabulary. Served rather than hard-coded in the Client,
        /// because the Server is what enforces it — and an installation that adds
        /// an entry should not need a Client release to show it.
        /// </summary>
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<PermissionDefinitionDto>>(StatusCodes.Status200OK)]
        public IReadOnlyList<PermissionDefinitionDto> Catalogue() =>
            Permissions.Catalogue.Select(d => new PermissionDefinitionDto
            {
                Key = d.Key,
                Scope = d.Scope switch
                {
                    PermissionScope.Global => "global",
                    PermissionScope.Activity => "activity",
                    _ => "both",
                },
                Group = d.Group,
                Participant = d.Participant,
            }).ToList();

        /// <summary>What the caller holds in one scope. Null activity is the system scope.</summary>
        [HttpGet("mine")]
        [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<string>> Mine([FromQuery] Guid? activityId, CancellationToken ct) =>
            (await permissions.EffectiveAsync(activityId, ct)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        /// <summary>
        /// What the caller holds <b>anywhere</b> — a different question, and
        /// deliberately a separate call: somebody who manages one course and
        /// nothing else still needs the panel that course lives in.
        /// </summary>
        [HttpGet("mine/anywhere")]
        [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<string>> Anywhere(CancellationToken ct) =>
            (await permissions.AnywhereAsync(ct)).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }
}
