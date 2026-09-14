using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Services.Models;
using Microsoft.EntityFrameworkCore;

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

/// <summary>
/// A filter narrows what is paged — not the page that has already been taken.
/// <para>
/// <c>ListSubmissionsAsync</c> read the state and the verdict off the newest
/// attempt and applied them to the page <b>after</b> the database had produced
/// it. So a filter answered with whichever matches happened to fall on the page
/// asked for: a manager who narrowed an activity down to one submission was
/// shown an empty first page with the match on the second, and the pager offered
/// pages that were empty by construction because <c>total</c> counted rows the
/// filter would have removed. Reported from production on 2026-09-08.
/// </para>
/// <para>
/// A page of one is the whole of the reproduction: the match is the older of two
/// submissions, so an unfiltered listing puts it on page two.
/// </para>
/// </summary>
[Collection("server-2")]
public class FilteredListingTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id.ToString();
    }

    /// <summary>Judged, so the row carries a verdict and a finished state.</summary>
    private async Task<string> JudgedAsync(HttpClient participant, string slug, string verdict)
    {
        var submitted = await Build.SubmitAsync(participant, slug, $"print(1)  # {verdict}\n");
        var id = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(id);
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!,
            verdict: verdict);

        return id;
    }

    [Fact]
    public async Task A_match_beyond_the_first_page_is_on_the_first_page_once_filtered()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var admin = await AdminAsync(server);

        var wanted = await JudgedAsync(participant, slug, "Wrong answer");
        await JudgedAsync(participant, slug, "Accepted");

        var activityId = await ActivityIdAsync(slug);
        var page = await Build.GetAsync(
            admin,
            $"/api/v1/submissions?page=1&pageSize=1&activityId={activityId}&verdict=Wrong%20answer");

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(wanted, row.GetProperty("id").GetString());

        // The count is of what matched, so the pager offers the one page there is.
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The same for the state, which is the filter the report came in about, and
    /// which is read off the newest attempt rather than off a column.
    /// </summary>
    [Fact]
    public async Task The_state_filter_counts_and_pages_what_matched()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var admin = await AdminAsync(server);

        await JudgedAsync(participant, slug, "Accepted");
        await JudgedAsync(participant, slug, "Accepted");
        var activityId = await ActivityIdAsync(slug);

        var completed = await Build.GetAsync(
            admin, $"/api/v1/submissions?page=1&pageSize=1&activityId={activityId}&state=completed");

        Assert.Single(completed.GetProperty("items").EnumerateArray());
        Assert.Equal(2, completed.GetProperty("total").GetInt32());

        // Nothing here is waiting, so the filter that would once have answered
        // with whatever sat on page one answers with nothing at all.
        var queued = await Build.GetAsync(
            admin, $"/api/v1/submissions?page=1&pageSize=20&activityId={activityId}&state=queued");

        Assert.Empty(queued.GetProperty("items").EnumerateArray());
        Assert.Equal(0, queued.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// A state the product has no name for matches nothing. It is not read as
    /// the default, which would answer a question nobody asked.
    /// </summary>
    [Fact]
    public async Task A_state_nothing_can_be_in_matches_nothing()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var admin = await AdminAsync(server);

        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var activityId = await ActivityIdAsync(slug);
        var page = await Build.GetAsync(
            admin, $"/api/v1/submissions?page=1&pageSize=20&activityId={activityId}&state=nonsense");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// <b>A verdict is one string however many commas are in it.</b>
    /// <para>
    /// The closed vocabularies beside it are comma-separated, so that every
    /// address anybody has pasted into a message keeps working. This one cannot
    /// be: the Server stores a verdict and never parses it, so that a problem
    /// type may invent one without a Server release — and choosing a separator
    /// for it would be parsing it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_verdict_carrying_a_comma_is_one_verdict()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var admin = await AdminAsync(server);

        var wanted = await JudgedAsync(participant, slug, "Wrong answer, test 3");
        await JudgedAsync(participant, slug, "Accepted");

        var activityId = await ActivityIdAsync(slug);
        var page = await Build.GetAsync(
            admin,
            $"/api/v1/submissions?activityId={activityId}&verdict=Wrong%20answer%2C%20test%203");

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(wanted, row.GetProperty("id").GetString());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }
}
