using AlgoJudge.Server.Controllers;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Identity;
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
        ILaunchService launches,
        IResourceLinkService links,
        IIdentityResolver identities,
        ILtiEnrolmentService enrolment,
        SignInManager<AlgoJudge.Server.Database.Models.User> signIn,
        IConfiguration configuration) : ControllerBase
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
        /// Four ways out, and each is a different page rather than a different
        /// status code: the activity, a conflict to report, an offer to sign in
        /// through SSO, or a refusal naming what went wrong.
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

                // The placement first: a launch that names no activity, or one
                // shared into a second course without anybody accepting it, is
                // refused before anybody is signed in. Signing somebody in and
                // then telling them the tool is misconfigured is a worse order.
                var link = await links.ResolveAsync(launch, ct);

                var resolution = await identities.ResolveAsync(launch, ct);

                switch (resolution)
                {
                    case Resolution.Conflict conflict:
                        // Reported, never followed (§4.3). The two names travel
                        // so the page can say what disagrees with what.
                        return Redirect(AppUrl(
                            "/lti/conflict"
                            + "?stored=" + Uri.EscapeDataString(conflict.Stored)
                            + "&asserted=" + Uri.EscapeDataString(conflict.Asserted)));

                    case Resolution.NeedsSignIn:
                        // One action, and it finishes where the launch was going
                        // (§4.4). The return path is local and checked as one,
                        // because an open redirect on this endpoint would be a
                        // phishing primitive that really is ours.
                        return Redirect(AppUrl(
                            "/lti/sign-in?returnTo=" + Uri.EscapeDataString(Landing(launch, link))));

                    case Resolution.Resolved resolved:
                        await enrolment.EnrolAsync(
                            link, launch.Platform.ProviderId, resolved.User.Id, launch.Roles, ct);

                        // The session that carries them into the application. In
                        // an iframe this cookie is third-party, which is §5.3's
                        // open half and the thing measured in a browser rather
                        // than argued about here.
                        await signIn.SignInAsync(resolved.User, isPersistent: true);

                        return Redirect(AppUrl(Landing(launch, link)));

                    default:
                        return Failed(LtiLaunchException.BadToken);
                }
            }
            catch (LtiLaunchException failure)
            {
                return Failed(failure.Code);
            }
        }

        /// <summary>
        /// Where a finished launch lands in the application.
        /// <para>
        /// Everything the Client needs to enter embedded mode and to render in
        /// the right language, from the launch rather than from a guess. The
        /// resource link id is ours, not the platform's — the Client asks this
        /// Server about it, and a platform's own identifier would mean nothing
        /// to it.
        /// </para>
        /// </summary>
        private static string Landing(LaunchedMessage launch, Data.ResourceLink link)
        {
            var query = new List<string> { "link=" + link.Id.ToString("D") };

            if (launch.Locale is not null)
            {
                // §5.4: the platform knows what language the course is taken in,
                // and the Client should not have to guess.
                query.Add("locale=" + Uri.EscapeDataString(launch.Locale));
            }
            if (string.Equals(launch.DocumentTarget, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                // §5.2: embedded is the learner's default, and the mode is
                // entered because of how the session was established rather than
                // because a URL said so. This says how the platform framed it;
                // what the Client does with a session is the Client's rule.
                query.Add("embedded=1");
            }

            return "/lti/launched?" + string.Join('&', query);
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
