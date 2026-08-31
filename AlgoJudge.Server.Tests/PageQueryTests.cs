using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Services.Models;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Paging, and the arithmetic behind it.
/// <para>
/// <c>PageSize</c> was bounded from the start because an unbounded one is a
/// denial-of-service parameter. <c>Page</c> was given only a floor — and the
/// product of the two is what reaches the database.
/// </para>
/// </summary>
public class PageQueryTests
{
    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(3, 200, 400)]
    public void An_ordinary_page_skips_what_came_before_it(int page, int size, int expected)
    {
        Assert.Equal(expected, new PageQuery { Page = page, PageSize = size }.Skip);
    }

    /// <summary>
    /// <b>The defect.</b> <c>(Page - 1) * PageSize</c> in <c>int</c> wraps, and
    /// what it wraps to is <b>negative</b> — which PostgreSQL refuses outright,
    /// so an absurd page number answered 500 instead of an empty page.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue, 200)]
    [InlineData(2000000000, 200)]
    [InlineData(int.MaxValue, 1)]
    public void A_page_number_that_would_overflow_still_skips_forwards(int page, int size)
    {
        var skip = new PageQuery { Page = page, PageSize = size }.Skip;

        Assert.True(skip >= 0, $"page {page} × size {size} skipped {skip}");
    }

    [Theory]
    [InlineData(0, 1)]          // below the floor, so it becomes page one
    [InlineData(-5, 1)]
    public void A_page_below_one_is_the_first_page(int page, int expected)
    {
        Assert.Equal(expected, new PageQuery { Page = page }.Page);
    }
}

/// <summary>
/// The same thing where it actually bit: a listing endpoint whose offset goes to
/// PostgreSQL.
/// </summary>
[Collection("server-2")]
public class PagingOverflowTests(ServerFixture server)
{
    /// <summary>
    /// <c>SubmissionService.ListAsync</c> puts <c>paging.Skip</c> straight into
    /// the query, so a negative one is <c>OFFSET -2147483448</c> and the request
    /// answers 500. A page past the end is not an error — it is a page with
    /// nothing on it.
    /// </summary>
    [Fact]
    public async Task A_page_number_nobody_meant_answers_an_empty_page()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var response = await participant.GetAsync(
            $"/api/v1/activities/{slug}/submissions?page={int.MaxValue}&pageSize=200");

        await Sign.Succeeded(response);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("items").EnumerateArray());

        // The count is of everything, not of the page, so it still reports the
        // submission that exists — which is how a client knows it overshot.
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
    }

    /// <summary>And the first page still carries what it should.</summary>
    [Fact]
    public async Task The_first_page_is_unaffected()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        await Build.SubmitAsync(participant, slug, "print(2)\n");

        var body = await Build.GetAsync(
            participant, $"/api/v1/activities/{slug}/submissions?page=1&pageSize=20");

        Assert.Single(body.GetProperty("items").EnumerateArray());
    }
}
