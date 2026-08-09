using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// Registering the identity providers this installation trusts.
    /// <para>
    /// Under <c>/identity</c> rather than beside the other panel screens,
    /// because the provider's own back channel lives at
    /// <c>/identity/providers/{id}/deletion-requests</c> and one concept reached
    /// by two unrelated paths is a surface nobody can hold in their head.
    /// </para>
    /// <para>
    /// Every action is behind <c>provider:manage</c>, checked in the service
    /// rather than by an attribute — this product's permissions are data, and an
    /// <c>[Authorize(Policy=…)]</c> would put a second, static copy of the model
    /// beside the one that is enforced.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("identity/providers")]
    [Authorize]
    public class IdentityProvidersController(IIdentityProviderService providers) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<IdentityProviderDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<IdentityProviderDto>> List(CancellationToken ct) =>
            providers.ListAsync(ct);

        [HttpGet("{id:guid}")]
        [ProducesResponseType<IdentityProviderDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<IdentityProviderDto> Get(Guid id, CancellationToken ct) =>
            providers.GetAsync(id, ct);

        [HttpPost]
        [ProducesResponseType<IdentityProviderDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public Task<IdentityProviderDto> Create(
            [FromBody] IdentityProviderInputDto input, CancellationToken ct) =>
            providers.CreateAsync(input, ct);

        [HttpPut("{id:guid}")]
        [ProducesResponseType<IdentityProviderDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public Task<IdentityProviderDto> Update(
            Guid id, [FromBody] IdentityProviderInputDto input, CancellationToken ct) =>
            providers.UpdateAsync(id, input, ct);

        /// <summary>
        /// Refused while accounts still sign in through it — disabling is the
        /// reversible act, and deleting one with people behind it would decide
        /// something about their accounts rather than about the registration.
        /// </summary>
        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await providers.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
