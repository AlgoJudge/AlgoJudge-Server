using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// What this installation calls itself and how it admits people — one row,
    /// for ever.
    /// <para>
    /// A table rather than configuration because an operator edits it from the
    /// manager panel while the Server is running, and because the documents it
    /// publishes need something to hang off. The singleton is enforced by a check
    /// constraint on a fixed primary key, so a second row cannot be inserted by a
    /// migration, a seed or a mistake.
    /// </para>
    /// </summary>
    public class Instance
    {
        /// <summary>The one and only row. Fixed, so the constraint can name it.</summary>
        public static readonly Guid SingletonId = new("00000000-0000-7000-8000-000000000001");

        public Guid Id { get; set; } = SingletonId;

        /// <summary>
        /// What the operator calls this installation, beside the product's own
        /// name. <b>Null is a real state</b>, not a missing value: an installation
        /// nobody has named shows the product's name alone rather than a made-up
        /// one, and a default here would make "not named" indistinguishable from
        /// "named AlgoJudge".
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Shipped <b>off</b>: accounts are created by an organiser or arrive by
        /// SSO. There is no self-service registration in v1.
        /// </summary>
        public bool LocalRegistrationEnabled { get; set; }

        /// <summary>Whether the registration form must collect an address.</summary>
        public bool RequireEmail { get; set; }

        /// <summary>
        /// Whether an address must be confirmed before the account can sign in.
        /// Off by default, and it has to be: there is no mail sender in v1, so an
        /// instance that required confirmation could admit nobody.
        /// </summary>
        public bool RequireConfirmedEmail { get; set; }

        /// <summary>Whether the mark appears in the application shell.</summary>
        public bool ShowLogo { get; set; } = true;

        /// <summary>
        /// The documents this installation publishes and its logo, as references.
        /// Publishing <b>adds</b> a revision with a <c>ValidFrom</c> rather than
        /// replacing the last one, so "which policy was in force on the third of
        /// August" stays answerable — a question that gets asked about a privacy
        /// policy for real, and by somebody who is owed an answer.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();
    }
}
