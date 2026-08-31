using System.Net;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The handler that checks the address rather than the name.
/// <para>
/// No fixture and no host: what is under test is whether a connection is opened
/// at all, which is decided before any of this product's code runs.
/// </para>
/// </summary>
public class GuardedHttpTests
{
    /// <summary>
    /// Loopback is where this Server's own operator surface deliberately lives,
    /// so its own fetcher must not be a way to knock on it.
    /// </summary>
    [Fact]
    public async Task An_address_the_predicate_refuses_is_never_dialled()
    {
        using var client = new HttpClient(
            GuardedHttp.Handler(PublicAddress.IsPublicOrPrivateNetwork));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://127.0.0.1:1/"));

        Assert.True(GuardedHttp.Refused(thrown), thrown.ToString());
    }

    /// <summary>
    /// <b>And the discrimination matters as much as the refusal.</b> If
    /// <see cref="GuardedHttp.Refused"/> answered true for an ordinary failure,
    /// every unreachable platform would be reported to its administrator as an
    /// address this Server declines to dial — which is a different problem with a
    /// different fix.
    /// </summary>
    [Fact]
    public async Task An_ordinary_connection_failure_is_not_read_as_a_refusal()
    {
        using var client = new HttpClient(GuardedHttp.Handler(_ => true));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://127.0.0.1:1/"));

        Assert.False(GuardedHttp.Refused(thrown), thrown.ToString());
    }

    /// <summary>
    /// A redirect is a second address chosen by whoever answered the first, and
    /// it can change the scheme — which the caller's own https rule cannot
    /// follow. Refused rather than checked.
    /// </summary>
    [Fact]
    public void Redirects_are_not_followed()
    {
        using var handler = GuardedHttp.Handler(_ => true);
        Assert.False(handler.AllowAutoRedirect);
    }
}
