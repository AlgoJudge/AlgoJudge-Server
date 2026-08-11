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
    public class AccountController(
        ICurrentUserService currentUser,
        IAccountService accounts,
        Microsoft.AspNetCore.Identity.SignInManager<Database.Models.User> signIn
    ) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status401Unauthorized)]
        public async Task<SessionDto> Get(CancellationToken ct) =>
            Projections.Session(await currentUser.RequireAsync(ct));

        [HttpPut]
        [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<SessionDto> Update([FromBody] ProfileInputDto input, CancellationToken ct) =>
            accounts.UpdateProfileAsync(input, ct);

        [HttpPost("password")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> ChangePassword(
            [FromBody] ChangePasswordInputDto input, CancellationToken ct)
        {
            await accounts.ChangePasswordAsync(input.CurrentPassword, input.NewPassword, ct);
            return NoContent();
        }

        /// <summary>
        /// The ways this person can sign in, so their account screen can name
        /// the provider and link to where it manages — and ends — the identity.
        /// </summary>
        [HttpGet("links")]
        [ProducesResponseType<IReadOnlyList<AccountLinkDto>>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status401Unauthorized)]
        public Task<IReadOnlyList<AccountLinkDto>> Links(CancellationToken ct) =>
            accounts.LinksAsync(ct);

        /// <summary>Everything held about the signed-in person, as a document they can keep.</summary>
        [HttpGet("export")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Export(CancellationToken ct)
        {
            var document = await accounts.ExportAsync(ct);
            return File(document, "application/json", "algojudge-export.json");
        }

        /// <summary>
        /// Anonymises the account and ends the session. Immediate and
        /// irreversible: submissions and results survive under an identifier that
        /// no longer names anybody.
        /// <para>
        /// A POST, not a DELETE, because it carries the password in a body — and
        /// because it is not a deletion.
        /// </para>
        /// </summary>
        [HttpPost("delete")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> Delete(
            [FromBody] DeleteAccountInputDto input, CancellationToken ct)
        {
            await accounts.DeleteAsync(input.Password, ct);
            await signIn.SignOutAsync();
            return NoContent();
        }
    }

    /// <summary>
    /// Signing out.
    /// <para>
    /// Not part of `MapIdentityApi`, which gives registration and sign-in and
    /// nothing else — so without this, signing out only forgets the session in
    /// one tab while the cookie stays valid everywhere.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("identity")]
    [Authorize]
    public class SignOutController(
        Microsoft.AspNetCore.Identity.SignInManager<Database.Models.User> signIn
    ) : ControllerBase
    {
        [HttpPost("logout")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Logout()
        {
            await signIn.SignOutAsync();
            return NoContent();
        }
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
                Systemic = d.Systemic,
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
