using System.Text.Encodings.Web;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Utils;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>
    /// Expecting a platform to register itself, and the address it registers at.
    ///
    /// <para>
    /// <b>Two audiences, one flow.</b> Everything under <c>/lti/registrations</c>
    /// is a manager here arranging it; <c>/lti/register</c> is the platform's own
    /// half, opened by <i>its</i> administrator in an iframe on <i>their</i> site.
    /// The second is anonymous of necessity, which is why it does nothing without
    /// a live invitation from the first.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/registrations")]
    [Authorize]
    public class LtiRegistrationsController(IDynamicRegistrationService registrations) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<RegistrationInvitationDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<RegistrationInvitationDto>> List(CancellationToken ct) =>
            registrations.ListAsync(ct);

        /// <summary>
        /// Expects one registration and answers with the address to hand over.
        /// </summary>
        [HttpPost]
        [ProducesResponseType<RegistrationInvitationDto>(StatusCodes.Status200OK)]
        public Task<RegistrationInvitationDto> Invite(
            [FromBody] InvitationInputDto? input, CancellationToken ct) =>
            registrations.InviteAsync(input?.Note, ct);

        /// <summary>Calls it off. The row stays, expired, so the list still says it happened.</summary>
        [HttpPost("{id:guid}/revoke")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
        {
            await registrations.RevokeAsync(id, ct);
            return NoContent();
        }
    }

    public record InvitationInputDto
    {
        /// <summary>What to call it while waiting — "WMiI Moodle", say.</summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// Where a platform registers itself.
    ///
    /// <para>
    /// <b>Anonymous, and it has to be</b>: the browser here belongs to whoever
    /// administers the platform, and they have no account with us. What stands in
    /// for authentication is the invitation code in the address, which a manager
    /// here created and handed over — see
    /// <see cref="Data.RegistrationInvitation"/> for why an open endpoint would
    /// not do.
    /// </para>
    ///
    /// <para>
    /// <b>It answers HTML, not JSON.</b> This is rendered inside an iframe on the
    /// platform's own configuration screen, and the person reading it is standing
    /// in Moodle wondering whether it worked.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/register")]
    public class LtiRegisterController(
        IDynamicRegistrationService registrations,
        ILogger<LtiRegisterController> logger) : ControllerBase
    {
        [HttpGet]
        [Produces("text/html")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Register(
            [FromQuery] string? code,
            [FromQuery(Name = "openid_configuration")] string? openidConfiguration,
            [FromQuery(Name = "registration_token")] string? registrationToken,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(openidConfiguration))
            {
                return Page(
                    "This address is incomplete",
                    "It should have been opened by a platform's own registration screen, "
                    + "carrying its configuration address. Opening it by hand does nothing.",
                    close: false);
            }

            try
            {
                var outcome = await registrations.RegisterAsync(
                    code, openidConfiguration, registrationToken, ct);

                return Page(
                    $"{outcome.PlatformName} is registered",
                    $"AlgoJudge now knows {outcome.Issuer}. It is switched off until somebody at "
                    + "AlgoJudge enables it, and it may not say who anybody is until somebody "
                    + "there decides that separately.",
                    close: true);
            }
            // `ConflictException` belongs here too: "that deployment is already
            // registered" is an outcome of this flow, not a fault in it, and the
            // person reading needs to be told which of the two it was.
            catch (Exception e) when (e is NotFoundException or ValidationException or ConflictException)
            {
                // The reason, to whoever is standing in the platform's admin
                // screen — they cannot read our logs, and "something went wrong"
                // sends them to us for a message we already have.
                logger.LogWarning(e, "A dynamic registration was refused");
                return Page("That did not work", e.Message, close: false);
            }
            // **Nothing here may answer anything but a page.** This action is
            // read inside the platform's own iframe and declares itself
            // `text/html`; without this, an unhandled fault reached the global
            // handler and rendered `problem+json` into it. Never `e.Message`:
            // the catch above already gives the reader what is theirs to have.
            catch (Exception e)
            {
                logger.LogError(e, "A dynamic registration failed");
                return Page(
                    "That did not work",
                    "Something went wrong at the AlgoJudge end. Nothing was registered, and the "
                    + "invitation has not been used — try again, and tell whoever sent it if it "
                    + "keeps happening.",
                    close: false);
            }
        }

        /// <summary>
        /// A page the platform can close.
        ///
        /// <para>
        /// <c>org.imsglobal.lti.close</c> is what Moodle listens for — measured in
        /// <c>mod/lti/amd/src/tool_configure_controller.js</c>, 5.2.2 — and
        /// without it the iframe simply stays open with a finished registration
        /// behind it, which reads as a hang.
        /// </para>
        ///
        /// <para>
        /// Built here rather than from a template because it is four lines and a
        /// message, and everything variable in it goes through
        /// <see cref="HtmlEncoder"/>: the text includes an issuer and an error
        /// the platform itself supplied.
        /// </para>
        /// </summary>
        private ContentResult Page(string heading, string message, bool close)
        {
            var encode = HtmlEncoder.Default;
            var script = close
                ? "<script>try{window.parent.postMessage({subject:'org.imsglobal.lti.close'},'*')}catch(e){}</script>"
                : "";

            return Content(
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + $"<title>{encode.Encode(heading)}</title>"
                + "<style>body{font:16px/1.5 system-ui,sans-serif;margin:2rem;max-width:34rem}"
                + "h1{font-size:1.25rem}</style></head><body>"
                + $"<h1>{encode.Encode(heading)}</h1><p>{encode.Encode(message)}</p>"
                + script
                + "</body></html>",
                "text/html",
                System.Text.Encoding.UTF8);
        }
    }
}
