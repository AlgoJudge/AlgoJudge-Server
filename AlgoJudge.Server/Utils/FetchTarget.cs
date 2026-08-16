namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// Whether an address somebody handed the Server is one it may fetch.
    /// <para>
    /// <b>This is half of the answer, and the cheap half.</b> Everything here is
    /// decidable from the string alone, which is why it is a pure function with
    /// tests that need no network. The other half — where the name actually
    /// resolves to, what a redirect points at, how many bytes arrive — cannot be
    /// known until the connection is made, and lives with the code that makes
    /// it. Neither half is sufficient on its own, and an allowlist checked only
    /// here is the shape of every SSRF write-up ever published.
    /// </para>
    /// </summary>
    public static class FetchTarget
    {
        /// <summary>The verdict, and the address to use if there is one.</summary>
        public readonly record struct Decision(Uri? Target, string? Refusal)
        {
            public bool Allowed => Refusal is null && Target is not null;
        }

        /// <summary>
        /// Reads the address and compares its host against what the installation
        /// allows.
        /// <para>
        /// Every refusal here is about the request rather than about the network,
        /// so each one is the same for everybody and discloses nothing: whether a
        /// host is on the list is a thing the operator who set the list already
        /// knows.
        /// </para>
        /// </summary>
        public static Decision Check(string? url, IReadOnlyCollection<string> allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return new Decision(null, "fetch.url.malformed");
            }

            // **HTTPS only.** The bytes become a statement this installation
            // serves to participants, so fetching them over a channel anybody on
            // the path can rewrite would launder somebody else's content into
            // ours. It also removes `file:`, `ftp:`, `gopher:` and the rest,
            // which are the schemes that make a fetcher interesting to attack.
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                return new Decision(null, "fetch.url.scheme");
            }

            // `https://allowed.example@evil.example/` — everything before the `@`
            // is credentials, and the host is the part after it. Readers that get
            // this wrong are common enough that the safe answer is to refuse the
            // form outright rather than to parse it correctly and hope the next
            // reader does too.
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return new Decision(null, "fetch.url.userinfo");
            }

            // A list of names is a list of names. An address literal is a way of
            // naming a machine the list never mentioned — including this one, and
            // including the metadata service of whatever cloud this runs in.
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                return new Decision(null, "fetch.url.address");
            }

            // The allowlist names hosts, not services. Left open, `:22` would
            // have the Server speak TLS at somebody's SSH daemon on its say-so.
            if (!uri.IsDefaultPort)
            {
                return new Decision(null, "fetch.url.port");
            }

            return Allows(allowedHosts, uri.Host)
                ? new Decision(uri, null)
                : new Decision(null, "fetch.host.notAllowed");
        }

        /// <summary>
        /// Whether the list names this host — <b>the whole host</b>.
        /// <para>
        /// Never a suffix match. <c>onlinejudge.org.example.invalid</c> ends with
        /// an allowed name and belongs to somebody else entirely, and that one
        /// character of difference is the whole distance between a list that
        /// works and a list that reads like it does.
        /// </para>
        /// </summary>
        public static bool Allows(IReadOnlyCollection<string> allowedHosts, string host)
        {
            var wanted = Normalise(host);
            if (wanted.Length == 0) return false;

            foreach (var allowed in allowedHosts)
            {
                if (Normalise(allowed) == wanted) return true;
            }
            return false;
        }

        /// <summary>
        /// One spelling of a host, so two spellings of the same one compare equal.
        /// <para>
        /// A trailing dot is the fully qualified form and names the same host, so
        /// it goes; case never distinguished two hosts either. Nothing else is
        /// touched — in particular no attempt is made to tidy an entry an
        /// operator typed, because a list that quietly rewrites what it was given
        /// is a list nobody can predict.
        /// </para>
        /// </summary>
        private static string Normalise(string host) =>
            host.Trim().TrimEnd('.').ToLowerInvariant();
    }
}
