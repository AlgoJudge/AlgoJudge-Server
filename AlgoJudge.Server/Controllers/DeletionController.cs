using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The channel an account owned by a provider leaves through.
    /// <para>
    /// Not a rename of <c>POST /account/delete</c>, which stays exactly as it
    /// was for a local account. This one asks for no password — an SSO account
    /// has none — and what it removes is a <b>way of signing in</b>. Whether the
    /// account is then emptied depends on whether that was the last one.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("account/deletion-requests")]
    [Authorize]
    public class AccountDeletionController(IAccountDeletionService deletion) : ControllerBase
    {
        [HttpPost]
        [ProducesResponseType<DeletionRequestDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        // `new` for the reason `TrialsController.Request` carries: the name is the
        // `operationId`, and renaming it would move the contract.
        public new Task<DeletionRequestDto> Request(
            [FromBody] HolderDeletionInputDto input, CancellationToken ct)
        {
            Guid? providerId = input.ProviderId is { } raw && Guid.TryParse(raw, out var parsed)
                ? parsed
                : null;

            return deletion.FromHolderAsync(providerId, ct);
        }
    }

    /// <summary>
    /// The provider's back channel: "this person no longer exists here".
    /// <para>
    /// <b>Anonymous to the session and authenticated to the provider</b>, by a
    /// secret held on its registration. Nobody is signed in — the person it is
    /// about has just been deleted somewhere else — so the only thing that can
    /// vouch for the request is the shared secret.
    /// </para>
    /// <para>
    /// It answers the same way whether or not anything here matched the subject.
    /// A 404 would let a provider learn who has an account in this installation
    /// by asking about them one at a time.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("identity/providers/{providerId:guid}/deletion-requests")]
    [AllowAnonymous]
    public class ProviderDeletionController(
        ApplicationDbContext context,
        IAccountDeletionService deletion
    ) : ControllerBase
    {
        /// <summary>The header the shared secret arrives in.</summary>
        public const string SecretHeader = "X-AlgoJudge-Provider-Secret";

        [HttpPost]
        [ProducesResponseType<DeletionRequestDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<DeletionRequestDto> Report(
            Guid providerId, [FromBody] ProviderDeletionInputDto input, CancellationToken ct)
        {
            var provider = await context.IdentityProviders
                .FirstOrDefaultAsync(p => p.Id == providerId, ct);

            // **Every refusal here is a 404**, including a wrong secret. An
            // endpoint that answered 401 would confirm that this provider id is
            // real and that the channel is open on it, which is the first thing
            // somebody probing would want to know.
            if (provider is null
                || !provider.DeletionChannelEnabled
                || !AccountDeletionService.SecretMatches(
                    Request.Headers[SecretHeader].ToString(), provider.DeletionSecret))
            {
                throw new NotFoundException("Identity provider");
            }

            return await deletion.FromProviderAsync(provider, input, ct);
        }
    }

    /// <summary>
    /// The administrator's queue.
    /// <para>
    /// It exists because of two rules that both end in "somebody has to look at
    /// this": a machine request waits a day before it is carried out, and an
    /// account holding system-scope permissions is never emptied automatically.
    /// A webhook that could silence an administrator is an attack vector, not a
    /// feature.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("account-deletion-requests")]
    [Authorize]
    public class DeletionQueueController(IAccountDeletionService deletion) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<DeletionRequestDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<DeletionRequestDto>> List(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? state = null,
            CancellationToken ct = default) =>
            deletion.ListAsync(new PageQuery { Page = page, PageSize = pageSize }, state, ct);

        /// <summary>
        /// Stops one inside its window. Refused once the window has closed —
        /// what it was holding has already happened, and an undo that does not
        /// exist should not be offered.
        /// </summary>
        [HttpPost("{id:guid}/halt")]
        [ProducesResponseType<DeletionRequestDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<DeletionRequestDto> Halt(Guid id, CancellationToken ct) =>
            deletion.HaltAsync(id, ct);
    }
}
