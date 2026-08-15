using AlgoJudge.Server.Authorization;
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
        ILaunchTickets tickets,
        AlgoJudge.Server.Services.IActivityService activities,
        AlgoJudge.Server.Services.IPermissionService permissions,
        SignInManager<AlgoJudge.Server.Database.Models.User> signIn,
        Data.LtiDbContext db,
        TimeProvider clock,
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
                var completed = await launches.CompleteAsync(
                    form["state"].FirstOrDefault(), form["id_token"].FirstOrDefault(), ct);

                // **Two errands arrive here.** A platform opening Deep Linking is
                // asking what to place, and there is nothing to run yet: no
                // resource link, no activity, no grant. It shares everything up
                // to this point and nothing after it.
                if (completed is Launched.ToChoose choosing)
                {
                    return await ChooseAsync(choosing.Request, ct);
                }

                var launch = ((Launched.ToRun)completed).Message;

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
                            "/lti/sign-in?returnTo=" + Uri.EscapeDataString(Landing(link))));

                    case Resolution.Resolved resolved:
                        // **Checked after the person is known**, because the
                        // answer differs by who they are: whoever may edit the
                        // activity is preparing it and launches into it, and
                        // everybody else is told it is not open yet.
                        if (!await activities.IsPublishedAsync(link.ActivityId, ct)
                            && !await permissions.HasAsync(
                                Permissions.ActivityUpdate, link.ActivityId, ct))
                        {
                            return Failed(LtiLaunchException.NotPublished);
                        }

                        await enrolment.EnrolAsync(
                            link, launch.Platform.ProviderId, resolved.User.Id, launch.Roles, ct);

                        var embedded = string.Equals(launch.DocumentTarget, "iframe",
                            StringComparison.OrdinalIgnoreCase);

                        // The session that carries them into the application.
                        // **Inside a frame it is a third-party cookie**, and a
                        // browser drops one written `SameSite=Lax` without
                        // saying so to anybody but its own console — measured in
                        // Chrome, 2026-08-14. Asking for an embedded session is
                        // the core's own idea; this is the only thing here that
                        // knows a launch is what put us in the frame.
                        await signIn.SignInAsync(resolved.User,
                            EmbeddedSessions.Properties(embedded, isPersistent: true));

                        // **A ticket, not a mode.** §5.2 wants the embedded
                        // presentation entered because of how the session was
                        // established rather than because a URL said so; this is
                        // opaque, single-use and bound to the person the launch
                        // resolved to, and the Client exchanges it for the
                        // context. Anybody may write a query parameter; nobody
                        // else can produce one of these.
                        var ticket = await tickets.IssueAsync(
                            link.Id, resolved.User.Id, launch.Locale, embedded,
                            launch.ReturnUrl, ct);

                        return Redirect(AppUrl(
                            "/lti/launched?ticket=" + Uri.EscapeDataString(ticket)));

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
        /// Where a launch lands when nobody could be resolved: the sign-in offer
        /// returns here afterwards, and the placement is already bound so coming
        /// back is a redirect rather than a second launch.
        /// </summary>
        private static string Landing(Data.ResourceLink link) =>
            "/lti/launched?link=" + link.Id.ToString("D");

        /// <summary>
        /// Where the platform sends the token. It has to be an absolute address
        /// and it has to match what was registered on the platform's side, so it
        /// is built the same way the registration screen builds it.
        /// </summary>
        /// <summary>
        /// Sends whoever is choosing into the application to choose.
        ///
        /// <para>
        /// <b>The same identity rules as a launch, and no weaker.</b> Somebody
        /// picking what to place is placing links into a course; if this tool
        /// cannot say who they are, it says so rather than showing them a list of
        /// activities. A conflict is reported the same way it is on a launch.
        /// </para>
        /// </summary>
        private async Task<IActionResult> ChooseAsync(DeepLinkRequest request, CancellationToken ct)
        {
            var resolution = await identities.ResolveAsync(request, ct);

            switch (resolution)
            {
                case Resolution.Conflict conflict:
                    return Redirect(AppUrl(
                        "/lti/conflict"
                        + "?stored=" + Uri.EscapeDataString(conflict.Stored)
                        + "&asserted=" + Uri.EscapeDataString(conflict.Asserted)));

                case Resolution.NeedsSignIn:
                    // Nowhere to come back to but here, and this request is spent
                    // by then: the platform has to open Deep Linking again. Said
                    // plainly on the page rather than looking like a failure.
                    return Redirect(AppUrl("/lti/sign-in?returnTo="
                        + Uri.EscapeDataString("/lti/choose-again")));

                case Resolution.Resolved resolved:
                    var embedded = string.Equals(request.DocumentTarget, "iframe",
                        StringComparison.OrdinalIgnoreCase);

                    await signIn.SignInAsync(resolved.User,
                        EmbeddedSessions.Properties(embedded, isPersistent: true));

                    var session = DeepLinkService.Begin(
                        request, resolved.User.Id, embedded, clock.GetUtcNow().UtcDateTime);

                    db.DeepLinkSessions.Add(session);
                    await db.SaveChangesAsync(ct);

                    return Redirect(AppUrl(
                        "/lti/choose?code=" + Uri.EscapeDataString(session.Code)));

                default:
                    return Failed(LtiLaunchException.BadToken);
            }
        }

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
