using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One address range a series may be reached from.
    /// <para>
    /// A single machine is a <c>/32</c>, a laboratory is a <c>/24</c>; the
    /// column is <c>cidr</c>, so PostgreSQL refuses a malformed range and one
    /// with host bits set on the way in. Containment is compared in .NET
    /// against the address <see cref="Services.RequestOrigin"/> has already
    /// un-mapped — a mapped <c>::ffff:10.0.5.17</c> is family 6 and matches no
    /// IPv4 network, silently.
    /// </para>
    /// <para>
    /// <b>Only as truthful as the proxy configuration.</b> An installation that
    /// believes the wrong hop reads every visitor as the proxy — and then the
    /// whole internet is inside the room. That is why
    /// <see cref="Authorization.TrustedProxies"/> refuses to start without an
    /// answer, and why this could not have been built before it.
    /// </para>
    /// </summary>
    public class SeriesAddressRule
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid SeriesId { get; set; }
        public Series? Series { get; set; }

        /// <summary>
        /// The range: <c>10.0.5.0/24</c>, <c>2001:db8::/32</c>.
        /// <para>
        /// <b><see cref="System.Net.IPNetwork"/>, which is what Npgsql 10 maps
        /// <c>cidr</c> to natively.</b> On Npgsql 8 it reaches the column
        /// through a converter to <c>NpgsqlCidr</c> — the type that version maps
        /// by default and that a later one deletes. Written this way round so
        /// the dependency upgrade removes a converter rather than changing the
        /// model, the contract and every reader of it.
        /// </para>
        /// </summary>
        public required System.Net.IPNetwork Network { get; set; }

        /// <summary>What a manager calls it — a room number, a building.</summary>
        public string? Note { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
