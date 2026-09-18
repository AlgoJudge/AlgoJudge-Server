using System.Net.Http.Json;
using System.Text.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Sorting the questions list by the column the reader clicked.
/// <para>
/// The screen has had three clickable headers since it was written, and sent
/// <c>sortBy</c> and <c>order</c> on every request. <b>Nothing bound either.</b>
/// The ordering was hard-coded to newest-first — directly beneath a comment
/// arguing that sorting belongs on the Server because "sorting in the Client
/// would order the twenty rows it happens to hold", which was right, and which
/// is why the Client correctly did not sort locally. So the arrow moved, the
/// list refetched, and the rows came back in the order they were already in.
/// </para>
/// <para>
/// <b>A sort is not a filter.</b> A word with no column behind it hides nothing
/// and narrows nothing, so it falls back to the documented default rather than
/// answering with an empty list — the one deliberate exception to the rule
/// <c>FilterVocabularyTests</c> states.
/// </para>
/// </summary>
[Collection("server-2")]
public class QuestionSortTests(ServerFixture server)
{
    private async Task<(string Slug, HttpClient Asker)> ActivityWithTwoRoundsAsync()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await Build.SecondRoundAsync(server, slug);
        return (slug, await Build.ParticipantAsync(server, slug));
    }

    private static async Task AskAsync(HttpClient asker, string slug, string topic, string? seriesId = null)
    {
        var asked = await asker.PostAsJsonAsync($"/api/v1/activities/{slug}/questions", new
        {
            topic,
            body = $"Body of {topic}",
            seriesId,
        });
        await Sign.Succeeded(asked);
    }

    /// <summary>The round each row names, in the order the list gave them.</summary>
    private static async Task<List<string>> RoundsAsync(HttpClient reader, string slug, string query)
    {
        var page = await Build.GetAsync(reader, $"/api/v1/activities/{slug}/questions?pageSize=50&{query}");
        return page.GetProperty("items").EnumerateArray()
            .Select(row => row.TryGetProperty("seriesName", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()!
                : string.Empty)
            .ToList();
    }

    /// <summary>
    /// Read as <b>an ordering</b> rather than as a change. "It came back
    /// different" passes on a screen that shuffles.
    /// </summary>
    [Fact]
    public async Task Questions_are_ordered_by_the_column_the_reader_clicked()
    {
        var (slug, asker) = await ActivityWithTwoRoundsAsync();

        var rounds = await Build.GetAsync(asker, $"/api/v1/activities/{slug}/series");
        var names = rounds.EnumerateArray()
            .Select(r => (Id: r.GetProperty("id").GetString()!, Name: r.GetProperty("name").GetString()!))
            .ToList();
        Assert.Equal(2, names.Count);

        // Asked newest-last against the round order, so the default sort and the
        // round sort cannot agree by accident.
        foreach (var round in names.OrderByDescending(r => r.Name))
        {
            await AskAsync(asker, slug, $"About {round.Name}", round.Id);
        }

        var ascending = await RoundsAsync(asker, slug, "sortBy=series&order=asc");
        Assert.Equal(ascending.OrderBy(name => name, StringComparer.Ordinal), ascending);

        var descending = await RoundsAsync(asker, slug, "sortBy=series&order=desc");
        Assert.Equal(descending.OrderByDescending(name => name, StringComparer.Ordinal), descending);

        // And the two are not the same list, which is what a bound parameter
        // that reached a hard-coded ordering would still have produced.
        Assert.NotEqual(ascending, descending);
    }

    /// <summary>
    /// A question about the activity at large is the least specific one there
    /// is, and belongs at the far end of a scope sort. PostgreSQL's own default
    /// puts it there — nulls last ascending — which is the same place the
    /// Client's fake puts it.
    /// </summary>
    [Fact]
    public async Task A_question_about_the_whole_activity_sorts_at_the_far_end()
    {
        var (slug, asker) = await ActivityWithTwoRoundsAsync();

        var rounds = await Build.GetAsync(asker, $"/api/v1/activities/{slug}/series");
        var first = rounds.EnumerateArray().First().GetProperty("id").GetString()!;

        await AskAsync(asker, slug, "About the activity");
        await AskAsync(asker, slug, "About a round", first);

        var ascending = await RoundsAsync(asker, slug, "sortBy=series&order=asc");
        Assert.Equal(string.Empty, ascending[^1]);

        var descending = await RoundsAsync(asker, slug, "sortBy=series&order=desc");
        Assert.Equal(string.Empty, descending[0]);
    }

    /// <summary>
    /// Two rows that compare equal still come back as two rows across two pages.
    /// <para>
    /// <b>What this does not prove.</b> Removing <c>.ThenBy(q =&gt; q.Id)</c> from
    /// the series arm leaves it green: with two rows PostgreSQL returned them in
    /// a stable order anyway. That is not luck to be engineered around — a query
    /// with no total order is <i>permitted</i> to be stable, so no behavior test
    /// can establish that the tiebreaker is there. It was measured on
    /// 2026-09-14 rather than assumed, and it is written down here so that a
    /// green run is not later read as evidence the tiebreaker is covered.
    /// </para>
    /// <para>
    /// What it does catch is the gross form: an ordering that repeats a row on
    /// both pages, which is what a reader actually sees when the order collapses.
    /// The tiebreaker itself is held by the comment beside it in
    /// <c>QuestionService</c>, and by review.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_questions_in_one_round_keep_a_stable_order_across_pages()
    {
        var (slug, asker) = await ActivityWithTwoRoundsAsync();

        var rounds = await Build.GetAsync(asker, $"/api/v1/activities/{slug}/series");
        var round = rounds.EnumerateArray().First().GetProperty("id").GetString()!;

        await AskAsync(asker, slug, "First in the round", round);
        await AskAsync(asker, slug, "Second in the round", round);

        var url = $"/api/v1/activities/{slug}/questions?pageSize=1&sortBy=series&order=asc";
        var one = await Build.GetAsync(asker, $"{url}&page=1");
        var two = await Build.GetAsync(asker, $"{url}&page=2");

        var firstId = one.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString();
        var secondId = two.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString();

        Assert.NotEqual(firstId, secondId);
    }

    /// <summary>
    /// And a sort nobody named falls back to the documented default rather than
    /// to nothing: it narrows no answer, so there is nothing for it to hide.
    /// </summary>
    [Fact]
    public async Task A_sort_the_Server_cannot_read_falls_back_to_newest_first()
    {
        var (slug, asker) = await ActivityWithTwoRoundsAsync();
        await AskAsync(asker, slug, "Only question");

        var nonsense = await Build.GetAsync(
            asker, $"/api/v1/activities/{slug}/questions?pageSize=50&sortBy=nonsense");
        var byDate = await Build.GetAsync(
            asker, $"/api/v1/activities/{slug}/questions?pageSize=50&sortBy=createdAt");

        Assert.Equal(1, nonsense.GetProperty("total").GetInt32());
        Assert.Equal(
            byDate.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetString()),
            nonsense.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
    }
}
