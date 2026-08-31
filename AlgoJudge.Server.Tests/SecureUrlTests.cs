using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// One rule for an address an administrator writes out and this installation
/// then trusts — an identity provider's issuer, a platform's key set.
/// <para>
/// It was two rules until 2026-08-31, and both were loose in a different way.
/// </para>
/// </summary>
public class SecureUrlTests
{
    [Theory]
    [InlineData("https://moodle.example")]
    [InlineData("https://moodle.example/mod/lti/certs.php")]
    [InlineData("https://moodle.example:8443/mod/lti/token.php")]
    public void An_https_address_is_accepted(string url)
    {
        Assert.True(SecureUrl.IsHttpsOrLoopback(url));
    }

    /// <summary>
    /// Narrow on purpose: a development stack has to be registrable, and the
    /// address reaches nothing but the machine somebody typed it on.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:8451")]
    [InlineData("http://127.0.0.1:8451/mod/lti/certs.php")]
    [InlineData("http://[::1]:8451/")]
    public void Plain_http_is_accepted_on_loopback_and_only_there(string url)
    {
        Assert.True(SecureUrl.IsHttpsOrLoopback(url));
    }

    /// <summary>
    /// <b>The one the rewrite was for.</b> Written as "https, or anything on
    /// loopback" — which is what `IdentityProviderService` said inline —
    /// <c>Uri.IsLoopback</c> is true of a <c>file:</c> URL, because it has no
    /// host at all. Naming the scheme is what closes it.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://localhost/etc/passwd")]
    public void A_scheme_with_no_host_does_not_ride_in_on_loopback(string url)
    {
        Assert.False(SecureUrl.IsHttpsOrLoopback(url));
    }

    [Theory]
    [InlineData("http://moodle.example")]                  // plain http, off loopback
    [InlineData("http://10.0.0.5/mod/lti/certs.php")]       // a private address is not loopback
    [InlineData("https://moodle.example@evil.example/")]    // credentials, and the host is after the @
    [InlineData("ftp://moodle.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/mod/lti/certs.php")]                      // not absolute
    [InlineData("moodle.example:8451")]                     // no scheme
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused(string? url)
    {
        Assert.False(SecureUrl.IsHttpsOrLoopback(url));
    }
}
