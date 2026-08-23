using System.Net;
using Microsoft.AspNetCore.Identity;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// An account. ASP.NET Core Identity supplies the credential half — login,
    /// address, password hash, lockout — and this adds what the product needs.
    /// <para>
    /// Identity stays in the Server for the MVP by decision (2026-08-03), so the
    /// invariant "no end-user passwords here" is deliberately suspended and
    /// permanently narrowed for administrator, local and temporary accounts. It
    /// is not an oversight to correct.
    /// </para>
    /// </summary>
    public class User : IdentityUser
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }

        /// <summary>A sentence about the account, written by staff. Not a tag.</summary>
        public string? Note { get; set; }

        /// <summary>Free display metadata, never queried. Opaque <c>jsonb</c>.</summary>
        public string? Tags { get; set; }

        /// <summary>
        /// When staff approved the account. Absent means <b>pending</b>.
        /// <para>
        /// Deliberately not the same fact as <see cref="IdentityUser.EmailConfirmed"/>.
        /// Confirming an address proves somebody reads that mailbox; approving an
        /// account is a decision a person made. One state holding both would make
        /// an instance that confirms no addresses unable to approve anybody.
        /// </para>
        /// </summary>
        public DateTime? ApprovedAt { get; set; }

        /// <summary>
        /// A bulk account handed out on a slip of paper — the permanent exception
        /// to "no end-user passwords in the Server".
        /// </summary>
        public bool IsTemporary { get; set; }

        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Why the account is blocked. Blocking itself is
        /// <see cref="IdentityUser.LockoutEnd"/> and <b>never a second
        /// boolean</b>: two fields answering "is this account blocked" is two
        /// fields that can disagree, and Identity already enforces the first one
        /// at sign-in.
        /// </summary>
        public string? BlockedReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastSeenAt { get; set; }

        /// <summary>
        /// The account has been deleted, which here means emptied.
        /// <para>
        /// <see cref="Submission"/> and <see cref="Result"/> reference this row's
        /// id and it has to stay resolvable, so deletion anonymises in place —
        /// immediately, with no grace period. The flag exists so a screen can say
        /// "deleted account" rather than showing a blank name and leaving a
        /// reader to guess.
        /// </para>
        /// </summary>
        public bool Anonymized { get; set; }

        public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
    }

    /// <summary>
    /// One sign-in, which is neither a person nor a browser tab.
    /// <para>
    /// The number of open WebSockets for a session is <b>counted live</b> from
    /// the connection registry and never stored: a count written to a row is a
    /// count that survives a crash and tells the users screen somebody is present
    /// who left hours ago.
    /// </para>
    /// </summary>
    public class UserSession
    {
        public Guid Id { get; set; } = Utils.Uuid.New();

        public required string UserId { get; set; }
        public User? User { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastRequestAt { get; set; }

        /// <summary>An API path, not the screen somebody was looking at.</summary>
        public string? LastRequestPath { get; set; }

        /// <summary>
        /// Where this browser reached the Server from.
        /// <para>
        /// <b><c>inet</c>, not text</b>, because the question anybody will ever
        /// ask of it — "is this inside the examination room's network" — is a
        /// containment test, and over a string it is a string comparison.
        /// PostgreSQL answers it with <c>&lt;&lt;=</c> and indexes it with GiST;
        /// one column holds both families.
        /// </para>
        /// <para>
        /// Normalised before it gets here. See <see cref="Services.RequestOrigin"/>
        /// for what happens when it is not.
        /// </para>
        /// </summary>
        public IPAddress? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public DateTime? ExpiresAt { get; set; }

        /// <summary>Set when the session ended, so history survives a sign-out.</summary>
        public DateTime? EndedAt { get; set; }
    }
}
