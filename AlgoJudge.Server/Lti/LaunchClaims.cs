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

        /// <summary>The only message type milestone 1 accepts.</summary>
        public const string ResourceLinkRequest = "LtiResourceLinkRequest";

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
        private const string Membership = "http://purl.imsglobal.org/vocab/lis/v2/membership#";
        private const string System = "http://purl.imsglobal.org/vocab/lis/v2/system/person#";
        private const string Institution = "http://purl.imsglobal.org/vocab/lis/v2/institution/person#";

        public const string Instructor = Membership + "Instructor";
        public const string ContentDeveloper = Membership + "ContentDeveloper";
        public const string Mentor = Membership + "Mentor";
        public const string Learner = Membership + "Learner";
        public const string Administrator = System + "Administrator";
        public const string InstitutionInstructor = Institution + "Instructor";

        /// <summary>
        /// Whether this set of roles runs the course rather than takes part in it.
        /// <para>
        /// <b>A launch decides membership, not privilege.</b> What the resulting
        /// grant actually carries comes from a permission template an operator
        /// chose, the same as every other grant — so this answers one question
        /// only: which of the two templates the platform's roles point at.
        /// </para>
        /// <para>
        /// <c>Administrator</c> is deliberately <b>not</b> here. A system role at
        /// the platform says what somebody may do in Moodle; reading it as
        /// authority inside AlgoJudge would let a claim mint privilege, which is
        /// the one thing the permission model forbids everywhere else.
        /// </para>
        /// </summary>
        /// <para>
        /// <b>Both spellings are accepted, because platforms send both.</b> A
        /// launch carries the full vocabulary IRI; Moodle's roster service
        /// answers with the bare term — <c>Learner</c>, <c>Instructor</c> —
        /// measured on 5.2.2, 2026-08-15. Matching IRIs alone would read every
        /// instructor on a roster as a participant, silently, which is the same
        /// shape of defect as the flattened roles claim that made every launch a
        /// learner.
        /// </para>
        public static bool RunsTheCourse(IEnumerable<string> roles) =>
            roles.Any(role => Term(role) is "Instructor" or "ContentDeveloper" or "Mentor");

        /// <summary>
        /// The role itself, with any vocabulary in front of it removed.
        /// <para>
        /// <c>InstitutionInstructor</c> is the institution vocabulary's own
        /// <c>Instructor</c>, so trimming the namespace makes the two one term —
        /// which is what the list above wants anyway.
        /// </para>
        /// </summary>
        private static string Term(string role)
        {
            var hash = role.LastIndexOf('#');
            var term = hash >= 0 ? role[(hash + 1)..] : role;
            // A context role may arrive as `Instructor#TeachingAssistant`; the
            // part before the sub-role is the one that decides.
            var slash = term.LastIndexOf('/');
            return slash >= 0 ? term[(slash + 1)..] : term;
        }
    }
}
