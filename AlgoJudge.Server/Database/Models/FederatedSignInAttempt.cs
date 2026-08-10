using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    public enum FederatedSignInOutcome
    {
        /// <summary>Admitted, and the account already existed.</summary>
        Admitted = 0,

        /// <summary>Admitted, and the account was created by this sign-in.</summary>
        Provisioned = 1,

        /// <summary>
        /// Refused. <b>This may still have changed what somebody may do</b> — see
        /// <see cref="FederatedSignInAttempt"/>.
        /// </summary>
        Refused = 2,
    }

    /// <summary>
    /// One attempt to sign in through an identity provider.
    /// <para>
    /// It exists because of one rule that would otherwise leave no trace: under
    /// <c>deny</c>, a token carrying no mapped value <b>withdraws the
    /// contribution that provider had made</b> — and then refuses the sign-in.
    /// A refusal that changes what somebody may do, and says nothing anywhere, is
    /// the kind of thing that gets diagnosed as "the panel is broken" three weeks
    /// later.
    /// </para>
    /// <para>
    /// This is the federated slice of the sign-in log the product decided to keep
    /// for a year (2026-08-04). <b>The general one still does not exist</b> —
    /// local sign-ins are not recorded here, and this is not a substitute for it.
    /// </para>
    /// </summary>
    public class FederatedSignInAttempt
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ProviderId { get; set; }
        public IdentityProvider? Provider { get; set; }

        /// <summary>
        /// The provider's <c>sub</c>. Kept even when no account was created, so a
        /// refusal can be traced back to the person who hit it.
        /// </summary>
        public required string Subject { get; set; }

        /// <summary>Null when nothing was matched to an account.</summary>
        public string? UserId { get; set; }

        public FederatedSignInOutcome Outcome { get; set; }

        /// <summary>
        /// <b>True when this attempt changed a contribution</b>, whether or not it
        /// admitted anybody. The one field worth querying.
        /// </summary>
        public bool ChangedPermissions { get; set; }

        /// <summary>The claim values that matched a rule, as a jsonb array.</summary>
        public string Matched { get; set; } = "[]";

        /// <summary>A sentence for a person reading the log. Never a stack trace.</summary>
        public string? Detail { get; set; }

        public DateTime At { get; set; } = DateTime.UtcNow;
    }
}
