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
        /// Whether this installation may send submissions to a service it does
        /// not run.
        /// <para>
        /// <b>Shipped off</b>, and that is the decision rather than an accident
        /// of defaulting. Sending somebody's work to a third party is a thing an
        /// operator should choose, because the privacy paragraph it needs is
        /// then the duty of whoever turned it on rather than of every
        /// installation from the day it is deployed.
        /// </para>
        /// <para>
        /// While this is off the Server hands out no work from a
        /// <see cref="Problem"/> marked external. It does not hide such problems
        /// and it does not refuse a submission — those already exist and would
        /// simply stop being judged, which is a state the queue already has a
        /// meaning for. Nothing is lost: turning the switch on lets the queue
        /// drain.
        /// </para>
        /// </summary>
        public bool ExternalJudgingEnabled { get; set; }

        /// <summary>
        /// The hosts this Server may fetch a file from on somebody's say-so.
        /// <para>
        /// <b>Data, and the whole of the Server's opinion about them.</b> It
        /// stores strings and compares them; it never parses one, never asks
        /// what a host serves, and cannot tell you why any entry is here. That
        /// is what keeps "fetching content from elsewhere" a facility rather
        /// than an integration with a named service.
        /// </para>
        /// <para>
        /// <b>Compared on the whole host, never on a suffix.</b> Matching by
        /// ending would admit <c>onlinejudge.org.example.invalid</c>, which is a
        /// host somebody else owns — the classic way past a list like this.
        /// </para>
        /// <para>
        /// Ships with <c>onlinejudge.org</c>, the archive the product actually
        /// integrates with, and it is inert until
        /// <see cref="ExternalJudgingEnabled"/> is turned on. An operator who
        /// wants none removes it; nothing in the code knows it went.
        /// </para>
        /// </summary>
        public List<string> ExternalFetchHosts { get; set; } = ["onlinejudge.org"];

        /// <summary>
        /// Whether the sign-in screen offers the login-and-password form.
        /// <para>
        /// **Presentation, and the name says so.** Switching it off hides the
        /// form; it does not close the endpoint behind it, and it cannot: an
        /// installation whose people all arrive through a provider still has
        /// administrators, local and temporary accounts, and they sign in with a
        /// password. The sign-in screen keeps a way back to the form —
        /// `?admin=true` — which is a convenience and **not a secret**. Anybody
        /// who reads a URL can type it, and anybody who can post to the endpoint
        /// never needed the form at all.
        /// </para>
        /// <para>
        /// So this is worth having for the reason it looks like it is: an
        /// installation where everybody signs in through their university should
        /// not present a password box that almost nobody can use. It is not
        /// worth having as a security control, and treating it as one would be
        /// the same mistake as believing an unlinked flow is a disabled one.
        /// </para>
        /// <para>
        /// Defaults to <c>true</c>, so an installation that never touches it
        /// looks exactly as it did.
        /// </para>
        /// </summary>
        public bool ShowLocalSignIn { get; set; } = true;

        /// <summary>
        /// Whether a person may remove their own account from the Client.
        /// <para>
        /// One setting for both user-facing channels — the local form and the
        /// de-registration an SSO account uses — because they are the same
        /// question asked of two kinds of account. The provider's back channel is
        /// enabled per provider instead: trusting one directory to say "this
        /// person is gone" says nothing about trusting another.
        /// </para>
        /// <para>
        /// <b>Shipped on.</b> It is a data-protection right before it is a
        /// feature, and an installation that has a reason to close it can, but
        /// should have to choose to.
        /// </para>
        /// </summary>
        public bool AccountDeletionEnabled { get; set; } = true;

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
