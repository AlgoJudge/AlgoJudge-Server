namespace AlgoJudge.Server.Lti
{
    /// <summary>
    /// A launch that cannot be completed, with a reason the Client can turn into
    /// a sentence.
    /// <para>
    /// <b>Deliberately not an <c>ApiException</c>.</b> Those become a
    /// problem+json body, and there is nobody to read one: a launch is a browser
    /// mid-redirect, arriving from a course page. The controller turns this into
    /// a redirect carrying the code, exactly as federated sign-in already does
    /// for a refused provider.
    /// </para>
    /// <para>
    /// The codes are stable strings rather than sentences, because the Client
    /// renders them in the reader's language — and because a student mid-lab
    /// reading "invalid_grant" has been told nothing.
    /// </para>
    /// </summary>
    public class LtiLaunchException(string code, string detail) : Exception(detail)
    {
        /// <summary>What the Client shows a sentence for.</summary>
        public string Code { get; } = code;

        // ── The reasons a launch is refused ──────────────────────────────────

        /// <summary>No platform is registered for that issuer and client.</summary>
        public const string UnknownPlatform = "unknownPlatform";

        /// <summary>Registered, and switched off by an operator.</summary>
        public const string PlatformDisabled = "platformDisabled";

        /// <summary>
        /// The `state` was missing, unknown, expired or already used. Also what a
        /// replayed launch looks like, and the two are not distinguished on
        /// purpose — telling an attacker which of the two they hit is free
        /// information.
        /// </summary>
        public const string BadState = "badState";

        /// <summary>The token did not validate: signature, issuer, audience, expiry.</summary>
        public const string BadToken = "badToken";

        /// <summary>Validated, and says something this tool does not accept.</summary>
        public const string UnsupportedMessage = "unsupportedMessage";

        /// <summary>The platform's key set could not be reached or read.</summary>
        public const string PlatformUnreachable = "platformUnreachable";

        /// <summary>
        /// The launch names no activity, or names one that does not exist. The
        /// commonest configuration mistake there is: the custom parameter was
        /// left out of the activity in Moodle.
        /// </summary>
        public const string NoActivity = "noActivity";

        /// <summary>
        /// The activity is already placed in another course and nobody has
        /// accepted that it should be reachable from two.
        /// </summary>
        public const string SharingNotAcknowledged = "sharingNotAcknowledged";
    }
}
