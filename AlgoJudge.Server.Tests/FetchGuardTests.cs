using System.Net;
using System.Text;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The guards that only matter once bytes are moving.
/// <para>
/// The address checks are decidable from a string and are tested as such. These
/// three are not: they are about what the far end does after the request goes
/// out, and each is written so that it can be exercised without one — because a
/// guard that needs a hostile server to test is a guard nobody tests.
/// </para>
/// </summary>
public class FetchGuardTests
{
    /// <summary>
    /// **A redirect is a second address, chosen after every check was made
    /// about the first.** It is refused rather than followed.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public void A_redirect_is_refused_rather_than_followed(HttpStatusCode status)
    {
        var refused = Assert.Throws<ValidationException>(
            () => ExternalFetchService.RefuseUnlessUsable(status));

        Assert.Equal("fetch.redirect", refused.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void An_answer_that_is_not_a_document_is_refused(HttpStatusCode status)
    {
        var refused = Assert.Throws<ValidationException>(
            () => ExternalFetchService.RefuseUnlessUsable(status));

        Assert.Equal("fetch.status", refused.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public void An_answer_that_is_a_document_is_taken(HttpStatusCode status)
    {
        ExternalFetchService.RefuseUnlessUsable(status);
    }

    /// <summary>
    /// **Counted while reading.** A sender that wanted to fill this disk would
    /// declare whatever `Content-Length` got past the check, so the header is
    /// never what decides.
    /// </summary>
    [Fact]
    public async Task A_body_past_the_ceiling_stops_being_read()
    {
        var body = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 5_000)));
        await using var counted = new CountedStream(body, ceiling: 1_000);

        var refused = await Assert.ThrowsAsync<ValidationException>(
            async () => await counted.CopyToAsync(new MemoryStream()));

        Assert.Equal("fetch.tooLarge", refused.Code);
    }

    /// <summary>
    /// And a body inside the ceiling arrives whole — the ceiling must not be a
    /// truncation nobody notices.
    /// </summary>
    [Fact]
    public async Task A_body_inside_the_ceiling_arrives_whole()
    {
        var body = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 900)));
        await using var counted = new CountedStream(body, ceiling: 1_000);

        var received = new MemoryStream();
        await counted.CopyToAsync(received);

        Assert.Equal(900, received.Length);
    }
}
