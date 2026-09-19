using System.Net;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a crawler is told, by the two mechanisms that are not the same thing.
///
/// <para>
/// A <c>Disallow</c> stops a URL from being <b>fetched</b>; <c>X-Robots-Tag</c> stops
/// one from being <b>indexed</b>, and only the second works on a page somebody linked
/// from elsewhere. They are also read in different places: the robots file only
/// governs the host that served it, so an installation whose API has a host of
/// its own is the case the header exists for.
/// </para>
/// </summary>
[Collection("server-2")]
public class CrawlerSurfaceTests(ServerFixture server)
{
    /// <summary>
    /// **At the root, not under the API prefix.** Nothing looks for it anywhere
    /// else, and everything else this Server answers lives under `/api/v1`.
    /// </summary>
    [Fact]
    public async Task The_robots_file_is_at_the_root_of_the_host()
    {
        var response = await server.CreateClient().GetAsync("/robots.txt");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("# This host serves an API.", body);
    }

    /// <summary>
    /// The whole of the API is withheld except the stored files a page on the
    /// application's own host is drawn from — the operator's logo and published
    /// documents. A crawler that may not fetch those renders those pages
    /// without them.
    /// </summary>
    [Fact]
    public async Task It_withholds_the_api_and_keeps_the_stored_files_fetchable()
    {
        var body = await server.CreateClient().GetStringAsync("/robots.txt");
        var rules = body.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.Contains("User-agent: *", rules);
        Assert.Contains("Disallow: /", rules);
        // Longer than the `Disallow`, which is what makes it win: a crawler
        // applies the most specific rule that matches.
        Assert.Contains("Allow: /api/v1/files/", rules);
    }

    /// <summary>
    /// Every answer, not only the successful ones — a 404 and a refusal are
    /// exactly the addresses somebody else is most likely to have linked.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v1/nope")]
    [InlineData("/api/v1/account")]
    // Raised rather than returned: this one is thrown in a controller and shaped
    // by `UseExceptionHandler`, which is a different path through the response
    // than a status code somebody assigned.
    [InlineData("/api/v1/files/2f1c9e6a-0000-0000-0000-000000000000")]
    [InlineData("/robots.txt")]
    [InlineData("/health")]
    public async Task Nothing_it_answers_asks_to_be_indexed(string path)
    {
        var response = await server.CreateClient().GetAsync(path);

        Assert.True(response.Headers.TryGetValues("X-Robots-Tag", out var values),
            $"{path} answered {(int)response.StatusCode} with no X-Robots-Tag");
        Assert.Equal("noindex", Assert.Single(values));
    }

    /// <summary>
    /// The one that would go missing quietly: <c>UseExceptionHandler</c> clears
    /// the response before it writes a failure, so a header set on the way in
    /// leaves with it. This is why the header is written from `OnStarting`.
    /// </summary>
    [Fact]
    public async Task A_refusal_keeps_it_too()
    {
        var response = await server.CreateClient().GetAsync("/api/v1/account");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
    }
}
