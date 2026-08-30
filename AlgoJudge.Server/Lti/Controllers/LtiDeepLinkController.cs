using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>
    /// The choosing half of Deep Linking, as the Client drives it.
    ///
    /// <para>
    /// <b>Signed in, and it must be.</b> Unlike the registration endpoints, the
    /// person here is somebody with an account: the launch resolved them before
    /// this code was ever issued. The code says which choosing, not who.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/deep-link")]
    [Authorize]
    public class LtiDeepLinkController(IDeepLinkService deepLinks) : ControllerBase
    {
        /// <summary>What may be placed, and how the platform will take it.</summary>
        [HttpGet("{code}")]
        [ProducesResponseType<DeepLinkChoosingDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<DeepLinkChoosingDto> Open(string code, CancellationToken ct) =>
            deepLinks.OpenAsync(code, ct);

        /// <summary>
        /// Answers with a signed response and the address to post it at. The
        /// Client submits it as a form, because the platform expects the person's
        /// own browser and its own cookie.
        /// </summary>
        [HttpPost("{code}/response")]
        [ProducesResponseType<DeepLinkAnswerDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public Task<DeepLinkAnswerDto> Respond(
            string code, [FromBody] DeepLinkChoiceDto choice, CancellationToken ct) =>
            deepLinks.RespondAsync(code, choice.ActivityIds ?? [], ct);
    }

    public record DeepLinkChoiceDto
    {
        /// <summary>What was picked, in the order it will be placed.</summary>
        public IReadOnlyList<string>? ActivityIds { get; init; }
    }
}
