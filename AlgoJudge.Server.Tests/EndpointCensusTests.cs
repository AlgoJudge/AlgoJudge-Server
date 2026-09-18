using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Every address this Server answers without a session, named one at a time.
///
/// <para>
/// <b>The default is closed, and this is what makes that true rather than
/// intended.</b> `Program` sets a fallback policy, so an endpoint carrying
/// neither attribute is refused before it runs; this asserts the other half —
/// that the set which opts out is the set somebody chose, and that nothing is
/// left leaning on a default at all.
/// </para>
/// <para>
/// <b>Open is not unauthorized.</b> Most of the list below authorizes itself in
/// its handler, because what it checks is not something a policy can say: a file
/// is readable if any reference to it is, a Runner proves a key it registered, a
/// platform arrives with a signed launch and no account yet. Adding a line here
/// is a decision; the comment beside it is where the decision is written down.
/// </para>
/// <para>
/// <b>One address is answered without being an endpoint</b>, and so cannot
/// appear below: <c>/robots.txt</c>, which middleware answers in front of both
/// routing and the API-base guard because a robots file is only ever read at the
/// root of a host. <c>CrawlerSurfaceTests</c> is its census.
/// </para>
/// </summary>
[Collection("server-1")]
public class EndpointCensusTests(ServerFixture server)
{
    /// <summary>
    /// What a reader has to be able to reach with no account at all.
    /// </summary>
    private static readonly string[] Public =
    [
        // Liveness. A health check that needs a session cannot tell "the process
        // is up" from "the database that holds sessions is down".
        "GET /health",
        // The login and registration screens change shape with it, so it is read
        // before anybody has signed in.
        "GET /instance",
        // Authorized by the rule, not the attribute: readable when any reference
        // to the file is readable by the caller, and 404 when it is not. The
        // terms of service are a file, and the registration form links them.
        "GET /files/{id:guid}",
        "GET /files/{id:guid}/meta",
    ];

    /// <summary>
    /// Signing in, and the parts of <c>MapIdentityApi</c> that survive
    /// <c>UseIdentitySurfaceRules</c> — which refuses several of these outright,
    /// on grounds this census has nothing to say about.
    /// </summary>
    private static readonly string[] SigningIn =
    [
        "POST /identity/login",
        "POST /identity/refresh",
        "POST /identity/register",
        "GET /identity/confirmEmail",
        "POST /identity/forgotPassword",
        "POST /identity/resendConfirmationEmail",
        "POST /identity/resetPassword",
        // A federated sign-in leaves and comes back. Neither hop can carry a
        // session: the first is what starts one.
        "GET /identity/providers/{slug}/challenge",
        "GET /identity/providers/{slug}/signed-in",
        // The provider's back channel, telling us an account was deleted there.
        // Authorized by the shared secret the provider was registered with.
        "POST /identity/providers/{providerId:guid}/deletion-requests",
    ];

    /// <summary>
    /// A Runner is a machine with a key, not a person with a session. It proves
    /// the key it registered; a cookie policy has nothing to check.
    /// </summary>
    private static readonly string[] Runners =
    [
        "POST /runner/register",
        "POST /runner/auth/challenge",
        "POST /runner/auth/token",
        "POST /runner/heartbeat",
        "POST /runner/jobs/claim",
        "POST /runner/jobs/leases",
        "POST /runner/jobs/releases",
        "POST /runner/jobs/{jobId:guid}/lease",
        "POST /runner/jobs/{jobId:guid}/progress",
        "POST /runner/jobs/{jobId:guid}/release",
        "POST /runner/jobs/{jobId:guid}/report",
        "POST /runner/jobs/{jobId:guid}/files",
        "POST /runner/trials/claim",
        "POST /runner/trials/{trialId:guid}/lease",
        "POST /runner/trials/{trialId:guid}/report",
        "POST /runner/files",
        "POST /runner/files/attach",
        "GET /runner/files/{id:guid}",
    ];

    /// <summary>
    /// A platform calls these, before there is anybody to be signed in as. Each
    /// is authorized by what the platform signed.
    /// </summary>
    private static readonly string[] Lti =
    [
        "GET /lti/jwks.json",
        "GET /lti/login",
        "POST /lti/login",
        "POST /lti/launch",
        "GET /lti/register",
    ];

    /// <summary>
    /// The operator's surface, which is not on the network: it answers only on
    /// the Server's own loopback interface and only with the configured token.
    /// <c>AdminSurfaceTests</c> holds both halves; anonymous here means there is
    /// no account to sign in as, which is the situation it exists to rescue.
    /// </summary>
    private static readonly string[] Operator =
    [
        "GET /admin/config",
        "POST /admin/config/apply",
        "GET /admin/keyring",
        "POST /admin/keyring/rotate",
        "POST /admin/keyring/revoke",
        "GET /admin/maintenance",
        "POST /admin/maintenance",
        "POST /admin/password",
        "GET /admin/storage",
        "POST /admin/storage/migration",
        "DELETE /admin/storage/migration",
    ];

    /// <summary>
    /// The event socket, which refuses an anonymous handshake with 401 in the
    /// handler — where it has to be, because that is the refusal the Client is
    /// written to expect.
    /// </summary>
    private static readonly string[] Socket = ["* /ws"];

    private static string Describe(Endpoint endpoint)
    {
        var route = endpoint is RouteEndpoint routed ? routed.RoutePattern.RawText : endpoint.DisplayName;
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var verbs = methods is null || methods.Count == 0 ? "*" : string.Join("|", methods);
        return $"{verbs} /{route?.TrimStart('/')}";
    }

    private IReadOnlyList<Endpoint> Endpoints() =>
        server.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Nothing_is_open_that_nobody_named()
    {
        string[] chosen = [.. Public, .. SigningIn, .. Runners, .. Lti, .. Operator, .. Socket];

        var open = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Describe)
            .Distinct()
            .ToHashSet();

        var unnamed = open.Except(chosen).Order(StringComparer.Ordinal).ToList();
        Assert.True(unnamed.Count == 0,
            $"reachable without a session and not named here:\n  {string.Join("\n  ", unnamed)}");

        var gone = chosen.Except(open).Order(StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0,
            $"named here but no longer open — delete the line:\n  {string.Join("\n  ", gone)}");
    }

    /// <summary>
    /// The half that keeps the list above honest: with no fallback policy an
    /// endpoint that says nothing is open, so a controller added without
    /// <c>[Authorize]</c> would never appear in the census either.
    /// </summary>
    [Fact]
    public async Task An_endpoint_that_says_nothing_is_refused()
    {
        var policies = server.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallback = await policies.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    /// <summary>
    /// And that nothing is leaning on the fallback by accident: every endpoint
    /// states which it is, so the census reads the intention rather than the
    /// outcome.
    /// </summary>
    [Fact]
    public void Every_endpoint_says_which_it_is()
    {
        var silent = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null
                     && e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Describe)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(silent.Count == 0,
            "closed by the fallback rather than by a decision — say which it is:\n  "
            + string.Join("\n  ", silent));
    }
}
