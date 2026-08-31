using System.Net;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The address a name resolved to, judged on its own.
/// <para>
/// A host on the allowlist can resolve wherever its owner points it, so this is
/// the check that stands between "a name we approved" and "a machine inside the
/// building". Every refused case below is somewhere a fetch has actually been
/// pointed in a published write-up.
/// </para>
/// </summary>
public class PublicAddressTests
{
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void An_address_out_on_the_internet_is_reachable(string address)
    {
        Assert.True(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    /// <summary>
    /// **The one the whole check exists for.** A cloud answers instance
    /// credentials here, to anything that asks from inside.
    /// </summary>
    [Fact]
    public void The_metadata_address_is_refused()
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse("169.254.169.254")));
    }

    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("127.9.9.9")]      // the rest of 127/8, which is also loopback
    [InlineData("0.0.0.0")]        // this network, and a synonym for here
    [InlineData("10.1.2.3")]       // private
    [InlineData("172.16.0.1")]     // private, bottom of the range
    [InlineData("172.31.255.254")] // private, top of the range
    [InlineData("192.168.1.1")]    // private
    [InlineData("100.64.0.1")]     // carrier-grade NAT
    [InlineData("224.0.0.1")]      // multicast
    [InlineData("255.255.255.255")]// broadcast
    public void An_address_inside_is_refused(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    /// The edges of 172.16.0.0/12, which is the range people get wrong.
    [Theory]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    public void The_addresses_just_outside_the_private_range_are_reachable(string address)
    {
        Assert.True(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("::1")]            // loopback
    [InlineData("::")]             // unspecified
    [InlineData("fe80::1")]        // link-local
    [InlineData("fc00::1")]        // unique local
    [InlineData("fd12:3456::1")]   // unique local, the half people actually use
    [InlineData("ff02::1")]        // multicast
    public void An_ipv6_address_inside_is_refused(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    /// <summary>
    /// **An IPv4 address wearing an IPv6 coat.** Left unwrapped it passes every
    /// IPv6 range check there is, and reaches the same loopback interface.
    /// </summary>
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void An_ipv4_address_mapped_into_ipv6_is_judged_as_ipv4(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    // ── the wider rule, for a platform an installation is paired with ────────

    /// <summary>
    /// A university runs its Moodle and its AlgoJudge on one private network, and
    /// so does this project's own reference stack. Refusing those refuses the
    /// ordinary deployment and protects nothing.
    /// </summary>
    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("fd12:3456::1")]     // unique local
    [InlineData("::ffff:10.0.0.1")]  // and wearing an IPv6 coat
    [InlineData("1.1.1.1")]          // the public internet is still reachable
    public void A_platform_may_be_on_the_operators_own_network(string address)
    {
        Assert.True(PublicAddress.IsPublicOrPrivateNetwork(IPAddress.Parse(address)));
    }

    /// <summary>
    /// <b>Widening to a private network is not widening to everything.</b> No
    /// platform is ever at these, and two of them are the reason the guard
    /// exists at all: the cloud answers instance credentials on the first, and
    /// this Server's own operator surface deliberately lives on the second.
    /// </summary>
    [Theory]
    [InlineData("169.254.169.254")]      // instance credentials
    [InlineData("169.254.0.1")]          // the rest of link-local
    [InlineData("127.0.0.1")]            // this Server's own admin surface
    [InlineData("127.9.9.9")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]           // carrier NAT
    [InlineData("224.0.0.1")]            // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]                  // IPv6 loopback
    [InlineData("fe80::1")]              // IPv6 link-local
    [InlineData("ff02::1")]              // IPv6 multicast
    [InlineData("::ffff:127.0.0.1")]     // loopback in an IPv6 coat
    [InlineData("::ffff:169.254.169.254")]
    public void The_wider_rule_still_refuses_what_no_platform_is_at(string address)
    {
        Assert.False(PublicAddress.IsPublicOrPrivateNetwork(IPAddress.Parse(address)));
    }
}
