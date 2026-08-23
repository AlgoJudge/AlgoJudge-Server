using System.Net;
using AlgoJudge.Server.Realtime;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Where the request in hand came from.
    /// </summary>
    public interface IRequestOrigin
    {
        /// <summary>
        /// The visitor's address, or null outside a request.
        /// <para>
        /// Only as truthful as <c>Forwarded__KnownProxies</c> — see
        /// <see cref="Authorization.TrustedProxies"/>.
        /// </para>
        /// </summary>
        IPAddress? Address { get; }

        /// <summary>
        /// The browser's session, from the cookie it was given, or null before it
        /// has one.
        /// </summary>
        Guid? SessionId { get; }
    }

    /// <summary>
    /// Reads it off the current request, and normalises the address.
    /// <para>
    /// <b>The normalisation is the point, and it lives here so that it lives in
    /// exactly one place.</b> Kestrel on a dual-stack socket hands back an
    /// IPv4-mapped IPv6 address — <c>::ffff:172.20.0.1</c> — and PostgreSQL calls
    /// that <b>family 6</b>. So
    /// <c>'::ffff:10.0.5.17'::inet &lt;&lt;= '10.0.5.0/24'</c> is <b>false</b>,
    /// silently, never an error: an address inside the room reads as an address
    /// somewhere else, and nothing anywhere says why.
    /// </para>
    /// <para>
    /// Measured 2026-08-23 on the development installation: <b>85 of 85</b>
    /// session rows held the mapped form and not one held a plain IPv4 address.
    /// This is not a precaution against something that might happen.
    /// </para>
    /// <para>
    /// PostgreSQL cannot undo it — no function returning <c>inet</c> un-maps, and
    /// <c>host()</c>, <c>set_masklen</c> and a cast to <c>cidr</c> all keep family
    /// 6 — so it has to happen before the value is stored, which is here.
    /// </para>
    /// </summary>
    public class RequestOrigin(IHttpContextAccessor accessor) : IRequestOrigin
    {
        public IPAddress? Address
        {
            get
            {
                var address = accessor.HttpContext?.Connection.RemoteIpAddress;
                return address is { IsIPv4MappedToIPv6: true } mapped
                    ? mapped.MapToIPv4()
                    : address;
            }
        }

        public Guid? SessionId =>
            accessor.HttpContext?.Request.Cookies
                .TryGetValue(SessionTrackingMiddleware.SessionCookie, out var raw) == true
            && Guid.TryParse(raw, out var id)
                ? id
                : null;
    }
}
