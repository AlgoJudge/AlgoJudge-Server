namespace AlgoJudge.Server.Tests;

/// <summary>
/// The participant's own submissions list, and the three filters that were not
/// there.
/// <para>
/// Reported 2026-09-14: choosing a series, a problem or a status on the
/// participant's submissions screen changed nothing. <b>The screen was never at
/// fault.</b> The Client had been sending all three on the query string all
/// along, and the action bound <c>page</c> and <c>pageSize</c> and nothing else
/// — so ASP.NET Core discarded them and answered <b>200 with the whole list</b>.
/// Nothing anywhere reported a fault, and <c>total</c> counted the rows the
/// filter should have removed, so even the pager agreed with the wrong answer.
/// </para>
/// <para>
/// The ids these tests filter by are read <b>off the rows</b> rather than out of
/// the database, which holds the filter and the projection to one another.
/// <c>problemId</c> is the assignment rather than the library problem, and a
/// test that fetched it from anywhere else would pass while the screen sent
/// something the query could never match.
/// </para>
/// <para>
/// The vocabulary rules these lean on are stated once in
/// <c>FilterVocabularyTests</c>: null is every, empty is nothing.
/// </para>
/// </summary>
[Collection("server-2")]
public class ParticipantFilterTests(ServerFixture server)
{
    /// <summary>Judged, so the row carries a finished state rather than a queued one.</summary>
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

    /// <summary>The reported defect, in the smallest shape that shows it.</summary>
    [Fact]
    public async Task A_participant_narrows_their_own_submissions_by_round()
    {
        var (slug, firstRound) = await Build.ActivityAsync(server);
        var secondRound = await Build.SecondRoundAsync(server, slug);
        var participant = await Build.ParticipantAsync(server, slug);

        await Build.SubmitAsync(participant, slug, "print(1)\n");
        await Build.SubmitAsync(participant, slug, "print(2)\n", problemSlug: "B");

        var all = await Build.GetAsync(participant, $"/api/v1/activities/{slug}/submissions");
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var one = await Build.GetAsync(
            participant, $"/api/v1/activities/{slug}/submissions?seriesId={firstRound}");

        var row = Assert.Single(one.GetProperty("items").EnumerateArray());
        Assert.Equal(firstRound, row.GetProperty("seriesId").GetString());
        Assert.Equal(1, one.GetProperty("total").GetInt32());

        // Two rounds answer with more than either alone — which a filter that
        // took only the first value it was handed could not do.
        var both = await Build.GetAsync(
            participant,
            $"/api/v1/activities/{slug}/submissions?seriesId={firstRound}&seriesId={secondRound}");

        Assert.Equal(2, both.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_participant_narrows_their_own_submissions_by_problem()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await Build.SecondRoundAsync(server, slug);
        var participant = await Build.ParticipantAsync(server, slug);

        var wanted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        await Build.SubmitAsync(participant, slug, "print(2)\n", problemSlug: "B");

        // The id the screen's problem picker carries, taken from the row itself.
        var problemId = wanted.GetProperty("problemId").GetString();

        var page = await Build.GetAsync(
            participant, $"/api/v1/activities/{slug}/submissions?problemId={problemId}");

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(wanted.GetProperty("id").GetString(), row.GetProperty("id").GetString());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_participant_narrows_their_own_submissions_by_state()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var judged = await JudgedAsync(participant, slug, "Accepted");
        await Build.SubmitAsync(participant, slug, "print(2)\n");

        var completed = await Build.GetAsync(
            participant, $"/api/v1/activities/{slug}/submissions?state=completed");

        var row = Assert.Single(completed.GetProperty("items").EnumerateArray());
        Assert.Equal(judged, row.GetProperty("id").GetString());
        Assert.Equal(1, completed.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// And two of them answer with both, whichever way they were asked for.
    /// <para>
    /// Repeated keys are what the Client puts on the wire; the comma is the form
    /// every address a manager has pasted into a message already uses. Both have
    /// to arrive as two, or one of the two audiences breaks.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_states_answer_with_strictly_more_than_either_alone()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        await JudgedAsync(participant, slug, "Accepted");
        await Build.SubmitAsync(participant, slug, "print(2)\n");

        var url = $"/api/v1/activities/{slug}/submissions";
        var completed = await Build.GetAsync(participant, $"{url}?state=completed");
        var queued = await Build.GetAsync(participant, $"{url}?state=queued");
        var repeated = await Build.GetAsync(participant, $"{url}?state=completed&state=queued");
        var joined = await Build.GetAsync(participant, $"{url}?state=completed,queued");

        Assert.Equal(1, completed.GetProperty("total").GetInt32());
        Assert.Equal(1, queued.GetProperty("total").GetInt32());
        Assert.Equal(2, repeated.GetProperty("total").GetInt32());
        Assert.Equal(2, joined.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The participant's list answers a word nobody named the way the manager's
    /// does. Its sibling in <c>FilteredListingTests</c> is what makes this a
    /// statement about both rather than about one.
    /// </summary>
    [Fact]
    public async Task A_participant_filter_the_Server_cannot_read_matches_nothing()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var page = await Build.GetAsync(
            participant, $"/api/v1/activities/{slug}/submissions?state=nonsense");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// And a control that was cleared is not a word nobody named. It sends its
    /// key with nothing in it, and that asks for everything — the distinction the
    /// whole rule rests on, and the one that decides whether clearing a filter
    /// empties the screen.
    /// </summary>
    [Fact]
    public async Task A_participant_filter_that_was_cleared_asks_for_everything()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var page = await Build.GetAsync(
            participant, $"/api/v1/activities/{slug}/submissions?state=&seriesId=&problemId=");

        Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }
}
