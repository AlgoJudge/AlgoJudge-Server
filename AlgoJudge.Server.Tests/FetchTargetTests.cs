using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The half of "may this be fetched" that a string can answer.
/// <para>
/// Every case here is one somebody has used to walk past an allowlist in a real
/// product. None of them needs a network, which is why they are cheap enough to
/// have all of.
/// </para>
/// </summary>
public class FetchTargetTests
{
    private static readonly string[] Allowed = ["onlinejudge.org"];

    [Fact]
    public void An_allowed_host_over_https_is_fetched()
    {
        var decision = FetchTarget.Check("https://onlinejudge.org/external/1/100.pdf", Allowed);

        Assert.True(decision.Allowed);
        Assert.Equal("onlinejudge.org", decision.Target!.Host);
    }

    /// <summary>
    /// **The one that matters most.** A suffix match would admit this, it looks
    /// right at a glance, and the host belongs to somebody else.
    /// </summary>
    [Theory]
    [InlineData("https://onlinejudge.org.example.invalid/100.pdf")]
    [InlineData("https://evil.example.invalid/onlinejudge.org/100.pdf")]
    [InlineData("https://notonlinejudge.org/100.pdf")]
    [InlineData("https://sub.onlinejudge.org/100.pdf")]
    public void A_host_that_merely_resembles_an_allowed_one_is_refused(string url)
    {
        Assert.Equal("fetch.host.notAllowed", FetchTarget.Check(url, Allowed).Refusal);
    }

    /// The same host, spelled two ways people actually spell it.
    [Theory]
    [InlineData("https://OnlineJudge.ORG/100.pdf")]
    [InlineData("https://onlinejudge.org./100.pdf")]
    public void The_same_host_spelled_differently_is_the_same_host(string url)
    {
        Assert.True(FetchTarget.Check(url, Allowed).Allowed);
    }

    /// <summary>
    /// Credentials in an address move the host to after the `@`. Readers
    /// disagree about this often enough that the form is refused rather than
    /// parsed and trusted.
    /// </summary>
    [Fact]
    public void An_address_carrying_credentials_is_refused()
    {
        Assert.Equal(
            "fetch.url.userinfo",
            FetchTarget.Check("https://onlinejudge.org@evil.example.invalid/x.pdf", Allowed).Refusal);
    }

    /// <summary>
    /// A list of names cannot vouch for an address literal — including the
    /// loopback interface and whatever a cloud serves on its metadata address.
    /// </summary>
    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/admin")]
    [InlineData("https://[::1]/admin")]
    public void An_address_literal_is_refused_whatever_it_points_at(string url)
    {
        Assert.Equal("fetch.url.address", FetchTarget.Check(url, Allowed).Refusal);
    }

    /// Anything that is not HTTPS, including the schemes that make a fetcher
    /// worth attacking in the first place.
    [Theory]
    [InlineData("http://onlinejudge.org/100.pdf")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://onlinejudge.org/100.pdf")]
    [InlineData("gopher://onlinejudge.org/100.pdf")]
    public void Anything_but_https_is_refused(string url)
    {
        Assert.Equal("fetch.url.scheme", FetchTarget.Check(url, Allowed).Refusal);
    }

    /// The list names hosts, not services.
    [Fact]
    public void An_allowed_host_on_another_port_is_refused()
    {
        Assert.Equal(
            "fetch.url.port",
            FetchTarget.Check("https://onlinejudge.org:22/100.pdf", Allowed).Refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Something_that_is_not_an_address_is_refused(string? url)
    {
        Assert.Equal("fetch.url.malformed", FetchTarget.Check(url, Allowed).Refusal);
    }

    /// <summary>
    /// A path with no host, refused — and **the refusal is asserted, not the
    /// reason for it**.
    /// <para>
    /// `Uri.TryCreate` disagrees with itself across platforms here:
    /// Windows says this is not an absolute address at all, Linux reads it as
    /// `file:///relative/path.pdf` and it fails the scheme check instead. Both
    /// refuse it, which is the property that matters; pinning the code pinned
    /// the operating system the suite happened to run on, and CI said so.
    /// </para>
    /// </summary>
    [Fact]
    public void A_path_with_no_host_is_refused_whatever_the_platform_calls_it()
    {
        Assert.False(FetchTarget.Check("/relative/path.pdf", Allowed).Allowed);
    }

    /// <summary>
    /// An installation that allows nothing fetches nothing. The empty list is
    /// the state an operator reaches by removing the entry the product ships,
    /// and it has to mean what it says.
    /// </summary>
    [Fact]
    public void An_empty_list_allows_nothing()
    {
        Assert.Equal(
            "fetch.host.notAllowed",
            FetchTarget.Check("https://onlinejudge.org/100.pdf", []).Refusal);
    }
}
