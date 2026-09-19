namespace AlgoJudge.Server.Lti
{
    /// <summary>
    /// The claim names an LTI 1.3 launch carries.
    /// <para>
    /// Spelled out as constants rather than inlined, because they are long URIs
    /// that differ from each other by one path segment — <c>lti</c>,
    /// <c>lti-ags</c>, <c>lti-nrps</c> — and a typo in one produces a launch that
    /// validates and then behaves as though the platform sent nothing.
    /// </para>
    /// </summary>
    public static class LtiClaims
    {
        private const string Lti = "https://purl.imsglobal.org/spec/lti/claim/";

        public const string MessageType = Lti + "message_type";
        public const string Version = Lti + "version";
        public const string DeploymentId = Lti + "deployment_id";
        public const string TargetLinkUri = Lti + "target_link_uri";
        public const string ResourceLink = Lti + "resource_link";
        public const string Context = Lti + "context";
        public const string Roles = Lti + "roles";
        public const string Custom = Lti + "custom";
        public const string LaunchPresentation = Lti + "launch_presentation";

        /// <summary>Assignment and Grade Services — where line items and scores go.</summary>
        public const string AgsEndpoint = "https://purl.imsglobal.org/spec/lti-ags/claim/endpoint";

        /// <summary>Names and Role Provisioning — the roster. Read from milestone 2.</summary>
        public const string NrpsService =
            "https://purl.imsglobal.org/spec/lti-nrps/claim/namesroleservice";

        /// <summary>
        /// Deep Linking, which is its own namespace — <c>lti-dl</c>, not
        /// <c>lti</c>. Measured in Moodle's `locallib.php` on 4.5.13 and 5.2.2
        /// (2026-08-15), where a claim carrying the <c>dl</c> suffix is built by
        /// appending it to the prefix the other claims share.
        /// </summary>
        private const string DeepLinking = "https://purl.imsglobal.org/spec/lti-dl/claim/";

        /// <summary>What the platform will accept back, and where to send it.</summary>
        public const string DeepLinkingSettings = DeepLinking + "deep_linking_settings";

        /// <summary>What this tool chose, in the response.</summary>
        public const string ContentItems = DeepLinking + "content_items";

        /// <summary>
        /// The platform's own opaque string, echoed back untouched.
        ///
        /// <para>
        /// <b>Moodle reads it from inside <c>deep_linking_settings</c></b> rather
        /// than from here, and then ignores it: `contentitem_return.php` reads
        /// only the items, the message type and the version. Sent at the address
        /// the specification gives, because a platform that does check it is the
        /// one this matters to.
        /// </para>
        /// </summary>
        public const string DeepLinkingData = DeepLinking + "data";

        /// <summary>A line for the platform to show whoever picked.</summary>
        public const string DeepLinkingMessage = DeepLinking + "msg";

        /// <summary>The message type milestone 1 accepts.</summary>
        public const string ResourceLinkRequest = "LtiResourceLinkRequest";

        /// <summary>"Choose something to place here", and what answers it.</summary>
        public const string DeepLinkingRequest = "LtiDeepLinkingRequest";
        public const string DeepLinkingResponse = "LtiDeepLinkingResponse";

        public const string SupportedVersion = "1.3.0";
    }

    /// <summary>
    /// The roles a launch may carry, as the specification spells them.
    /// <para>
    /// <b>Only what is read is named.</b> The vocabulary is long and most of it
    /// says nothing about what somebody may do here; a list of every role would
    /// suggest the module understands distinctions it does not.
    /// </para>
    /// </summary>
    public static class LtiRoles
    {
        /// <summary>The context vocabulary, without the separator a bare role adds.</summary>
        private const string MembershipRoot = "http://purl.imsglobal.org/vocab/lis/v2/membership";

        private const string Membership = MembershipRoot + "#";
        private const string System = "http://purl.imsglobal.org/vocab/lis/v2/system/person#";
        private const string Institution = "http://purl.imsglobal.org/vocab/lis/v2/institution/person#";

        public const string Instructor = Membership + "Instructor";
        public const string ContentDeveloper = Membership + "ContentDeveloper";
        public const string Mentor = Membership + "Mentor";
        public const string Learner = Membership + "Learner";
        public const string Administrator = System + "Administrator";
        public const string InstitutionInstructor = Institution + "Instructor";

        /// <summary>
        /// The values a platform's rules may be written against, for one set of
        /// roles: the principal role of each, and the principal plus its
        /// sub-role where one was sent.
        /// <para>
        /// <b>A launch decides membership, not privilege.</b> What the resulting
        /// grant carries comes from rules an operator wrote, the same as every
        /// other grant — so this answers one question: which values to match.
        /// </para>
        /// <para>
        /// <b>Context roles only.</b> An institution or system role says what
        /// somebody may do at the platform: an <c>Administrator</c> there
        /// administers Moodle, and an institution <c>Instructor</c> teaches
        /// somewhere in the institution, not in this course. Reading either as
        /// authority here would let a claim mint privilege, which is the one
        /// thing the permission model forbids everywhere else. The institution
        /// vocabulary was read as a context role until 2026-09-19, so a lecturer
        /// enrolled as a student in a colleague's course ran it.
        /// </para>
        /// <para>
        /// <b>Both spellings are accepted, because platforms send both.</b> A
        /// launch carries the full vocabulary IRI; Moodle's roster service
        /// answers with the bare term — <c>Learner</c>, <c>Instructor</c> —
        /// measured on 5.2.2, 2026-08-15. Matching IRIs alone would read every
        /// instructor on a roster as a participant, silently.
        /// </para>
        /// <para>
        /// A sub-role yields both values, most specific first:
        /// <c>Instructor#TeachingAssistant</c> and <c>Instructor</c>. Reading
        /// only the text after the <c>#</c> made a teaching assistant a
        /// participant and read the sub-role as though it were the role.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> Values(IEnumerable<string> roles)
        {
            var values = new List<string>();

            foreach (var role in roles)
            {
                if (role is null) continue;
                var trimmed = role.Trim();
                if (trimmed.Length == 0) continue;

                // A vocabulary IRI, or a bare term. Only the context vocabulary
                // — `.../membership` — and bare terms are read.
                var hash = trimmed.LastIndexOf('#');
                var vocabulary = hash >= 0 ? trimmed[..hash] : "";
                var tail = hash >= 0 ? trimmed[(hash + 1)..] : trimmed;

                if (vocabulary.Length > 0 && !vocabulary.StartsWith(MembershipRoot, StringComparison.Ordinal))
                {
                    continue;
                }

                // `.../membership/Instructor#TeachingAssistant` — the principal
                // role is the last segment of the vocabulary, the sub-role the
                // text after the hash. `.../membership#Instructor` has no
                // sub-role, and the vocabulary ends where the root does.
                var slash = vocabulary.LastIndexOf('/');
                var hasSubRole = vocabulary.Length > MembershipRoot.Length && slash >= 0;
                var principal = hasSubRole ? vocabulary[(slash + 1)..] : tail;
                var sub = hasSubRole ? tail : null;

                if (sub is not null) Add(values, $"{principal}#{sub}");
                Add(values, principal);
            }

            return values;

            static void Add(List<string> into, string value)
            {
                if (value.Length > 0 && !into.Contains(value, StringComparer.Ordinal)) into.Add(value);
            }
        }
    }
}
