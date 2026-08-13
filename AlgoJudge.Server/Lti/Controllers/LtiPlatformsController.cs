using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Lti.Api;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>
    /// Registering the platforms this installation accepts launches from.
    /// <para>
    /// <b>Manual, by decision</b> (§10, milestone 1). Dynamic Registration
    /// arrives in milestone 3 and <b>does not confer identity authority</b> —
    /// that flag stays something a person sets, because it is the one that lets a
    /// platform say who somebody is.
    /// </para>
    /// <para>
    /// Behind <c>provider:manage</c>, checked in the service rather than by an
    /// attribute: this product's permissions are data, and registering a platform
    /// writes a provider row, so it is governed by the permission that governs
    /// those.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/platforms")]
    [Authorize]
    public class LtiPlatformsController(
        IPlatformService platforms, IConfiguration configuration) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<PlatformDto>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<PlatformDto>> List(CancellationToken ct) =>
            (await platforms.ListAsync(ct)).Select(Project).ToList();

        [HttpGet("{id:guid}")]
        [ProducesResponseType<PlatformDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<PlatformDto> Get(Guid id, CancellationToken ct) =>
            Project(await platforms.GetAsync(id, ct));

        [HttpPost]
        [ProducesResponseType<PlatformDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<PlatformDto> Register(
            [FromBody] PlatformInputDto input, CancellationToken ct) =>
            Project(await platforms.RegisterAsync(Input(input), ct));

        [HttpPut("{id:guid}")]
        [ProducesResponseType<PlatformDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<PlatformDto> Update(
            Guid id, [FromBody] PlatformInputDto input, CancellationToken ct) =>
            Project(await platforms.UpdateAsync(id, Input(input), ct));

        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await platforms.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// What to type into the platform. Reachable per platform because the
        /// values are the same for all of them — it is the platform's
        /// configuration screen that differs, and an operator is looking at one
        /// platform when they need this.
        /// </summary>
        [HttpGet("{id:guid}/registration")]
        [ProducesResponseType<ToolRegistrationDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<ToolRegistrationDto> Registration(Guid id, CancellationToken ct)
        {
            await platforms.GetAsync(id, ct);

            // The address a *browser* reaches this Server at, which is not
            // necessarily the one a container sees. Same reasoning as the
            // provider blueprint's `ALGOJUDGE_API_URL`: a mismatch fails at the
            // end of somebody's first launch, with an error from the platform.
            var apiUrl = (configuration["PublicApiUrl"]
                ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}").TrimEnd('/');

            return new ToolRegistrationDto
            {
                ToolUrl = $"{apiUrl}/lti/launch",
                LoginUrl = $"{apiUrl}/lti/login",
                RedirectUri = $"{apiUrl}/lti/launch",
                KeySetUrl = $"{apiUrl}/lti/jwks.json",
                CustomParameters =
                [
                    // The one §4.3 rests on. Measured 2026-08-13: Moodle
                    // substitutes it in 4.5.13, 5.2.2 and 5.3dev.
                    "username=$User.username",
                    // Carried from milestone 1 although nothing reads it until
                    // milestone 4: a course copied before the feature exists is a
                    // course whose history is gone by the time it does.
                    "context_history=$Context.id.history",
                ],
            };
        }

        private static Services.PlatformInput Input(PlatformInputDto input) => new()
        {
            DisplayName = input.DisplayName,
            Issuer = input.Issuer,
            ClientId = input.ClientId,
            DeploymentId = input.DeploymentId,
            KeySetUrl = input.KeySetUrl,
            AuthTokenUrl = input.AuthTokenUrl,
            AuthLoginUrl = input.AuthLoginUrl,
            IsIdentityAuthority = input.IsIdentityAuthority,
            IdentityNamespace = input.IdentityNamespace,
            UsernameClaim = input.UsernameClaim,
            Enabled = input.Enabled,
        };

        private static PlatformDto Project(Platform p) => new()
        {
            Id = Wire.Id(p.Id),
            DisplayName = p.DisplayName,
            Issuer = p.Issuer,
            ClientId = p.ClientId,
            DeploymentId = p.DeploymentId,
            KeySetUrl = p.KeySetUrl,
            AuthTokenUrl = p.AuthTokenUrl,
            AuthLoginUrl = p.AuthLoginUrl,
            IsIdentityAuthority = p.IsIdentityAuthority,
            IdentityNamespace = p.IdentityNamespace,
            UsernameClaim = p.UsernameClaim,
            Enabled = p.Enabled,
            ProviderId = Wire.Id(p.ProviderId),
            CreatedAt = Wire.At(p.CreatedAt),
        };
    }
}
