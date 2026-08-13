using AlgoJudge.Server.Controllers;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>
    /// The two endpoints a platform talks to when somebody clicks the activity.
    /// <para>
    /// <b>Anonymous, both of them, and they have to be.</b> A launch is how
    /// somebody arrives; requiring a session first would mean requiring them to
    /// have already signed in to the thing they are being launched into. What
    /// stands in for authentication is the platform's signature over the
    /// <c>id_token</c>, checked in <see cref="ILaunchService"/>.
    /// </para>
    /// <para>
    /// <b>Every refusal is a redirect carrying a code.</b> The browser is
    /// mid-journey and arrived from a course page; there is nobody to read a
    /// problem+json body. This is the same shape federated sign-in already uses
    /// for a refused provider, and for the same reason.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti")]
    public class LtiLaunchController(
        ILaunchService launches, IConfiguration configuration) : ControllerBase
    {
        /// <summary>
        /// Third-party-initiated login. The platform sends the browser here, and
        /// this sends it back to the platform's authorization endpoint.
        /// <para>
        /// <b>Both verbs.</b> The specification allows either, and Moodle uses
        /// POST — but a launch that answers only one of them fails against the
        /// next platform with a message that says nothing about verbs.
        /// </para>
        /// </summary>
        [HttpGet("login")]
        [HttpPost("login")]
        public async Task<IActionResult> Login(CancellationToken ct)
        {
            var form = Request.HasFormContentType ? await Request.ReadFormAsync(ct) : null;

            string? Value(string name) =>
                form?[name].FirstOrDefault() ?? Request.Query[name].FirstOrDefault();

            var issuer = Value("iss");
            if (string.IsNullOrWhiteSpace(issuer))
            {
                return Failed(LtiLaunchException.UnknownPlatform);
            }

            try
            {
                var target = await launches.BeginAsync(
                    new LoginInitiation
                    {
                        Issuer = issuer,
                        ClientId = Value("client_id"),
                        DeploymentId = Value("lti_deployment_id"),
                        LoginHint = Value("login_hint"),
                        MessageHint = Value("lti_message_hint"),
                        TargetLinkUri = Value("target_link_uri"),
                    },
                    RedirectUri(),
                    ct);

                return Redirect(target);
            }
            catch (LtiLaunchException failure)
            {
                return Failed(failure.Code);
            }
        }

        /// <summary>
        /// The launch itself: the platform posts an <c>id_token</c> back here.
        /// <para>
        /// Milestone 1 stops after validation. Resolving who launched, and the
        /// session that follows, is the next stage — until then a validated
        /// launch lands on the Client with what it resolved, which is enough to
        /// tell a working registration from a broken one.
        /// </para>
        /// </summary>
        [HttpPost("launch")]
        public async Task<IActionResult> Launch(CancellationToken ct)
        {
            if (!Request.HasFormContentType)
            {
                return Failed(LtiLaunchException.BadToken);
            }

            var form = await Request.ReadFormAsync(ct);

            try
            {
                var launch = await launches.CompleteAsync(
                    form["state"].FirstOrDefault(), form["id_token"].FirstOrDefault(), ct);

                var query = new List<string>
                {
                    "context=" + Uri.EscapeDataString(launch.ContextId),
                    "resourceLink=" + Uri.EscapeDataString(launch.ResourceLinkId),
                };
                if (launch.Locale is not null)
                {
                    // §5.4: the platform knows what language the course is being
                    // taken in, and the Client should not have to guess.
                    query.Add("locale=" + Uri.EscapeDataString(launch.Locale));
                }
                if (launch.ActivitySlug is not null)
                {
                    query.Add("activity=" + Uri.EscapeDataString(launch.ActivitySlug));
                }

                return Redirect(AppUrl("/lti/launched?" + string.Join('&', query)));
            }
            catch (LtiLaunchException failure)
            {
                return Failed(failure.Code);
            }
        }

        /// <summary>
        /// Where the platform sends the token. It has to be an absolute address
        /// and it has to match what was registered on the platform's side, so it
        /// is built the same way the registration screen builds it.
        /// </summary>
        private string RedirectUri()
        {
            var apiUrl = (configuration["PublicApiUrl"]
                ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}").TrimEnd('/');
            return apiUrl + "/lti/launch";
        }

        /// <summary>
        /// A local path on the application's origin, which is a different origin
        /// from this one in every deployment this product plans.
        /// </summary>
        private string AppUrl(string localPath)
        {
            var appBase = (configuration[FederatedSignInController.AppBaseUrlSetting] ?? "")
                .TrimEnd('/');
            return appBase.Length == 0 ? localPath : appBase + localPath;
        }

        /// <summary>
        /// A refusal a person can act on. The code travels rather than a
        /// sentence, because the Client renders it in the reader's language —
        /// and because a student mid-lab reading "invalid_grant" has been told
        /// nothing at all.
        /// </summary>
        private IActionResult Failed(string code) =>
            Redirect(AppUrl("/lti/failed?reason=" + Uri.EscapeDataString(code)));
    }
}
