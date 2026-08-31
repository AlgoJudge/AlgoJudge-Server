namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// One rule for an address an administrator writes out and this installation
    /// then trusts: an identity provider's issuer, a platform's key set.
    /// <para>
    /// <b>HTTPS is the specification, not a preference.</b> OpenID Connect
    /// Discovery requires an issuer to be an <c>https</c> URL and the LTI 1.3
    /// security framework requires TLS throughout. Over plain HTTP, whoever
    /// answers first decides who your users are.
    /// </para>
    /// <para>
    /// <b>Loopback is exempted so a development stack can be registered</b>, and
    /// only there. It is a narrow exemption on purpose: the address is one a
    /// person with the permission to write platforms typed themselves, and it
    /// reaches nothing but the machine they typed it on.
    /// </para>
    /// <para>
    /// <b>This is not the rule for an address a stranger supplies.</b> Where the
    /// address arrives in a request — LTI dynamic registration — loopback is
    /// refused rather than exempted, because there it means this Server's own
    /// container and never the platform. See <see cref="GuardedHttp"/>.
    /// </para>
    /// </summary>
    public static class SecureUrl
    {
        /// <summary>
        /// Whether the value is an absolute <c>https</c> URL, or an <c>http</c>
        /// one on loopback.
        /// <para>
        /// The scheme is named rather than inferred from
        /// <see cref="Uri.IsLoopback"/> alone, and that is load-bearing:
        /// <c>IsLoopback</c> is true of a <c>file:</c> URL, which has no host at
        /// all. A rule written as "https, or anything on loopback" therefore
        /// admits <c>file:///etc/passwd</c>.
        /// </para>
        /// </summary>
        public static bool IsHttpsOrLoopback(string? value) =>
            Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
    }
}
