using System.Net;
using System.Net.Sockets;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// Whether an address is one out on the internet, or one inside.
    /// <para>
    /// <b>The half of the allowlist that a name cannot answer.</b> A host on the
    /// list resolves to whatever its owner — or whoever controls the answer —
    /// says it resolves to, and that answer can be <c>127.0.0.1</c>, the
    /// container network, or the address a cloud serves credentials on. Checking
    /// the name and then connecting to the address is two different questions
    /// asked as if they were one; the gap between them is DNS rebinding, and it
    /// is minutes wide.
    /// </para>
    /// <para>
    /// So this is applied to the address actually being connected to, at the
    /// moment of connecting, and nothing here trusts a name.
    /// </para>
    /// </summary>
    public static class PublicAddress
    {
        /// <summary>
        /// Whether this Server may open a connection to it.
        /// <para>
        /// Written as a list of what is <b>refused</b> rather than of what is
        /// allowed, and that is the wrong way round for a security control — but
        /// the allowed set here is "the public internet", which has no
        /// enumeration. The refusals are therefore exhaustive by range rather
        /// than by example, and an address family this does not recognise is
        /// refused outright.
        /// </para>
        /// </summary>
        public static bool IsPublic(IPAddress address)
        {
            // An IPv4 address wearing an IPv6 coat. Left unwrapped, `::ffff:127.0.0.1`
            // is not IPv4 loopback to any check that only looks at IPv6 ranges —
            // and it reaches exactly the same place.
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

            return address.AddressFamily switch
            {
                AddressFamily.InterNetwork => IsPublicV4(address),
                AddressFamily.InterNetworkV6 => IsPublicV6(address),
                // Unix sockets and everything else: not something a fetch of a
                // web address has any business reaching.
                _ => false,
            };
        }

        private static bool IsPublicV4(IPAddress address)
        {
            var b = address.GetAddressBytes();

            return b[0] switch
            {
                // 0.0.0.0/8 — "this network", and on many stacks a synonym for
                // the local host.
                0 => false,
                // 10.0.0.0/8
                10 => false,
                // 127.0.0.0/8
                127 => false,
                // 100.64.0.0/10, the carrier-grade NAT range.
                100 when b[1] >= 64 && b[1] <= 127 => false,
                // 169.254.0.0/16 — link-local, and the address every major cloud
                // answers instance credentials on.
                169 when b[1] == 254 => false,
                // 172.16.0.0/12
                172 when b[1] >= 16 && b[1] <= 31 => false,
                // 192.168.0.0/16, and 192.0.0.0/24 and 192.0.2.0/24 which are
                // reserved and documentation ranges.
                192 when b[1] == 168 || (b[1] == 0 && (b[2] == 0 || b[2] == 2)) => false,
                // 198.18.0.0/15 benchmarking, 198.51.100.0/24 documentation.
                198 when (b[1] == 18 || b[1] == 19) || (b[1] == 51 && b[2] == 100) => false,
                // 203.0.113.0/24 documentation.
                203 when b[1] == 0 && b[2] == 113 => false,
                // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved, which together
                // are everything from 224 up — including 255.255.255.255.
                >= 224 => false,
                _ => true,
            };
        }

        private static bool IsPublicV6(IPAddress address)
        {
            if (IPAddress.IPv6Loopback.Equals(address)) return false;
            if (IPAddress.IPv6Any.Equals(address)) return false;
            if (address.IsIPv6LinkLocal) return false;
            if (address.IsIPv6SiteLocal) return false;
            if (address.IsIPv6Multicast) return false;
            // Unique local addresses, fc00::/7 — the IPv6 answer to 10.0.0.0/8,
            // and not covered by any of the properties above.
            if ((address.GetAddressBytes()[0] & 0xFE) == 0xFC) return false;

            return true;
        }
    }
}
