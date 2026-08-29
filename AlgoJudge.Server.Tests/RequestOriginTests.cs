using System.Net;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Whose word the Server takes for a visitor's address, and what it stores.
/// <para>
/// Both halves are here because neither is any use alone: an address nobody may
/// forge, stored in a shape that cannot answer "was this inside the room",
/// buys nothing — and so does the reverse.
/// </para>
/// </summary>
public class RequestOriginTests
{
    private static IConfiguration Configured(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    /// <summary>
    /// <para>
    /// The options were previously built with <c>KnownProxies.Clear()</c>, which
    /// does not mean "no proxies" — it means the middleware stops checking and
    /// believes whoever sent the header. That was a log line's problem until
    /// 2026-08-23, when the address became something a judge is shown and asked
    /// to draw a conclusion from.
    /// </para>
    /// </summary>
    [Fact]
    public void An_installation_that_trusts_no_named_proxy_does_not_start()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => TrustedProxies.Apply(new ForwardedHeadersOptions(), Configured()));

        // The message has to say what to set, because it is the last thing an
        // operator sees before the container exits.
        Assert.Contains("Forwarded__KnownProxies", refused.Message);
        Assert.Contains("proxy_add_x_forwarded_for", refused.Message);
    }

    /// <summary>
    /// Comma-separated, because the array spelling an environment file needs —
    /// <c>__0</c>, <c>__1</c> — is one nobody remembers under pressure.
    /// </summary>
    [Fact]
    public void Several_proxies_may_be_named_in_one_setting()
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxies.Apply(options, Configured(
            ("Forwarded:KnownProxies", "172.20.0.1, 172.18.0.1")));

        Assert.Equal(
            [IPAddress.Parse("172.20.0.1"), IPAddress.Parse("172.18.0.1")],
            options.KnownProxies);
    }

    /// <summary>Or as a configuration array, for a file that has one.</summary>
    [Fact]
    public void Proxies_may_also_arrive_as_an_array()
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxies.Apply(options, Configured(
            ("Forwarded:KnownProxies:0", "10.1.2.3"),
            ("Forwarded:KnownProxies:1", "2001:db8::1")));

        Assert.Equal(2, options.KnownProxies.Count);
    }

    /// <summary>
    /// Networks in CIDR, and <b>both families</b>: a room reachable over IPv6
    /// and described only in IPv4 reads as somewhere else entirely.
    /// </summary>
    [Fact]
    public void A_network_may_be_named_instead_and_in_either_family()
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxies.Apply(options, Configured(
            ("Forwarded:KnownNetworks", "172.20.0.0/16,2001:db8::/32")));

        Assert.Equal(2, options.KnownIPNetworks.Count);
        Assert.Empty(options.KnownProxies);
    }

    /// <summary>
    /// A Server with nothing in front of it says so, and is believed about the
    /// socket instead.
    /// <para>
    /// The rule asks for a decision, not for a proxy. Without this the
    /// development stack — which is reached directly — would have to name one it
    /// does not have, and a rule people satisfy with a lie protects nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void A_server_reached_directly_says_so_and_reads_no_forwarded_header()
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxies.Apply(options, Configured(("Forwarded:KnownProxies", "none")));

        // Off entirely, rather than left running with an empty trust list. The
        // two behave alike; only one of them says what was meant.
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }

    /// <summary>
    /// A value that is not an address stops the Server rather than being
    /// dropped. A silently ignored proxy is a Server that trusts one hop fewer
    /// than the operator believes, which is the failure this whole file exists
    /// to prevent.
    /// </summary>
    [Fact]
    public void Something_that_is_not_an_address_is_refused_rather_than_skipped()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => TrustedProxies.Apply(new ForwardedHeadersOptions(), Configured(
                ("Forwarded:KnownProxies", "nginx"))));
        Assert.Contains("nginx", refused.Message);

        var network = Assert.Throws<InvalidOperationException>(
            () => TrustedProxies.Apply(new ForwardedHeadersOptions(), Configured(
                ("Forwarded:KnownNetworks", "172.20.0.0"))));
        Assert.Contains("CIDR", network.Message);
    }

    /// <summary>
    /// A range with bits below its prefix is refused, and the message says what
    /// to write instead.
    /// <para>
    /// <b>New on 2026-08-29, and a tightening.</b> The deprecated
    /// <c>HttpOverrides.IPNetwork</c> accepted <c>172.20.0.5/16</c> and quietly
    /// meant <c>172.20.0.0/16</c>; <c>System.Net.IPNetwork</c> refuses it. The
    /// refusal is kept rather than masked, because this list decides whose word
    /// is taken for every visitor's address — a range nobody meant is the whole
    /// internet inside the room. The same typo is refused for a round's address
    /// rules, where it costs a laboratory rather than an installation.
    /// </para>
    /// </summary>
    [Fact]
    public void A_network_with_bits_below_its_prefix_is_refused_and_named()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => TrustedProxies.Apply(new ForwardedHeadersOptions(), Configured(
                ("Forwarded:KnownNetworks", "172.20.0.5/16"))));

        Assert.Contains("172.20.0.5/16", refused.Message);
        // Not merely "that is wrong": the message carries the network it would
        // have silently become, so the operator can choose it or a longer prefix.
        Assert.Contains("172.20.0.0/16", refused.Message);
    }

    /// <summary>
    /// The address is stored the way it will later be asked about.
    /// <para>
    /// <b>Kestrel on a dual-stack socket hands back the mapped form</b>, and
    /// PostgreSQL calls <c>::ffff:10.0.5.17</c> family 6 — so
    /// <c>&lt;&lt;= '10.0.5.0/24'</c> is <b>false</b>, silently. Measured on the
    /// development installation on 2026-08-23: 85 session rows of 85 held the
    /// mapped form. This is the one line that stops that.
    /// </para>
    /// </summary>
    [Fact]
    public void A_mapped_address_is_stored_as_the_address_it_actually_is()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:10.0.5.17");

        var origin = new RequestOrigin(new HttpContextAccessor { HttpContext = context });

        Assert.Equal(IPAddress.Parse("10.0.5.17"), origin.Address);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, origin.Address!.AddressFamily);
    }

    /// <summary>And one that needs no mapping is left alone, both families.</summary>
    [Theory]
    [InlineData("10.0.5.17")]
    [InlineData("2001:db8:abcd::1")]
    public void An_ordinary_address_passes_through(string declared)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(declared);

        var origin = new RequestOrigin(new HttpContextAccessor { HttpContext = context });

        Assert.Equal(IPAddress.Parse(declared), origin.Address);
    }

    /// <summary>
    /// The session id comes off the cookie, and anything else is nobody's
    /// session rather than a parse failure somewhere further in.
    /// </summary>
    [Fact]
    public void The_session_is_read_from_the_cookie_or_is_absent()
    {
        var id = Guid.NewGuid();

        var carrying = new DefaultHttpContext();
        carrying.Request.Headers.Cookie = $"aj_session={id}";
        Assert.Equal(
            id,
            new RequestOrigin(new HttpContextAccessor { HttpContext = carrying }).SessionId);

        var nonsense = new DefaultHttpContext();
        nonsense.Request.Headers.Cookie = "aj_session=not-a-uuid";
        Assert.Null(
            new RequestOrigin(new HttpContextAccessor { HttpContext = nonsense }).SessionId);

        Assert.Null(new RequestOrigin(new HttpContextAccessor()).SessionId);
    }

    /// <summary>
    /// The device id is a UUID or it is nothing.
    /// <para>
    /// <b>It is text a page wrote</b>, so anybody using the browser can put
    /// anything in it. Storing whatever arrived would put an unvalidated string
    /// in front of a judge; parsing it means the column can only ever hold the
    /// one shape the product understands.
    /// </para>
    /// </summary>
    [Fact]
    public void A_device_that_is_not_a_uuid_is_no_device_at_all()
    {
        var id = Guid.NewGuid();

        var declared = new DefaultHttpContext();
        declared.Request.Headers["Device-Id"] = id.ToString();
        Assert.Equal(
            id,
            new RequestOrigin(new HttpContextAccessor { HttpContext = declared }).DeviceId);

        foreach (var nonsense in new[] { "not-a-uuid", "", "<script>alert(1)</script>" })
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["Device-Id"] = nonsense;
            Assert.Null(
                new RequestOrigin(new HttpContextAccessor { HttpContext = context }).DeviceId);
        }

        Assert.Null(new RequestOrigin(new HttpContextAccessor()).DeviceId);
    }
}

/// <summary>
/// And the column can answer the question it exists for.
/// <para>
/// The two assertions above stop a mapped address being stored. This one is the
/// other half: that what is stored is an <c>inet</c> and not a string, so
/// "was this inside the examination room's network" is a containment test
/// rather than a comparison of spellings. It is the query the eventual
/// per-activity whitelist is made of, asked one release early.
/// </para>
/// </summary>
[Collection("server-1")]
public class SessionAddressTests(ServerFixture server)
{
    [Theory]
    [InlineData("10.0.5.17", "10.0.5.0/24", true)]
    [InlineData("10.0.6.17", "10.0.5.0/24", false)]
    [InlineData("2001:db8:abcd::1", "2001:db8::/32", true)]
    [InlineData("2001:dead::1", "2001:db8::/32", false)]
    public async Task An_address_is_stored_so_a_network_can_be_asked_about_it(
        string address, string network, bool inside)
    {
        // Touched first so the host starts: migrations run on start-up, and a
        // context opened before that finds an empty database.
        _ = server.Services;

        await using var context = server.NewContext();

        var user = await context.Users.FirstAsync();
        var session = new Database.Models.UserSession
        {
            UserId = user.Id,
            IpAddress = System.Net.IPAddress.Parse(address),
        };
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();

        // Raw, because EF has no operator for this and the point is that
        // PostgreSQL does. `<<=` is contained-within-or-equal.
        var found = await context.Database
            .SqlQueryRaw<Guid>(
                """SELECT "Id" AS "Value" FROM "UserSessions" WHERE "Id" = {0} AND "IpAddress" <<= {1}::inet""",
                session.Id, network)
            .ToListAsync();

        Assert.Equal(inside, found.Count == 1);
    }
}
