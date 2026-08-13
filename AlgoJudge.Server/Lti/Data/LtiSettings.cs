using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// What this installation allows the LTI module to do. One row.
    /// <para>
    /// <b>Instance-wide, and in the module's own table rather than on
    /// <c>Instance</c>.</b> The project owner settled the setting as
    /// instance-wide on 2026-08-13; putting the column on the core's
    /// <c>Instance</c> entity would have made a core table name LTI, which §8
    /// forbids and <c>LtiBoundaryTests</c> refuses. Same scope, different table.
    /// </para>
    /// </summary>
    public class LtiSettings
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// Whether a launch may create an account for somebody who has none.
        /// <para>
        /// <b>Off, and it ships off</b> (§4.6). No self-registration is accepted
        /// product behaviour and a launch must not route around it. Turned on, a
        /// launch from a platform that is an identity authority creates the
        /// account from what the platform asserted — which is the convenience a
        /// faculty may want, and is exactly the path §4.5 calls account takeover
        /// on the day that platform is compromised.
        /// </para>
        /// <para>
        /// With it off, a launch that resolves to nothing lands on a page
        /// offering one action: sign in through SSO and come back. One click,
        /// once, and the same row is written by a route nobody can forge.
        /// </para>
        /// </summary>
        public bool AccountCreationEnabled { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
