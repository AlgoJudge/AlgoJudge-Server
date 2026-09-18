using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a filter value means before any query has been written.
/// <para>
/// Three list endpoints disagreed about a word this Server has no name for. The
/// printouts queue <b>ignored the filter</b> and answered with everything; the
/// submissions list answered with nothing; the runners list answered with the
/// unapproved, which is a question nobody had asked. The first of those was a
/// hole rather than a preference: the queue emits <c>printing</c> and its parser
/// had no arm for it, so the one filter an operator uses at a printer returned
/// the entire queue.
/// </para>
/// <para>
/// The rule that replaced all three: <b>null is every, empty is nothing</b>. A
/// caller who sent no words narrowed nothing; a caller whose every word is
/// unknown asked something whose answer is empty. A filter that cannot be
/// honored must never widen what it answers with.
/// </para>
/// </summary>
public class FilterVocabularyTests
{
    /// <summary>
    /// <b>The test that keeps the hole shut for states nobody has invented yet.</b>
    /// <para>
    /// Written over the enum rather than over a list of words, so a member added
    /// later is covered the moment it exists — which is exactly what did not
    /// happen when <c>PrintoutState.Printing</c> was appended.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_state_the_Server_writes_is_a_state_it_can_read_back()
    {
        foreach (var state in Enum.GetValues<EvaluationJobState>())
        {
            Assert.Equal(state, Assert.Single(Projections.JobStates([Projections.Wire(state)])!));
        }

        foreach (var state in Enum.GetValues<PrintoutState>())
        {
            Assert.Equal(state, Assert.Single(Projections.PrintoutStates([Projections.Wire(state)])!));
        }

        foreach (var state in Enum.GetValues<RunnerState>())
        {
            Assert.Equal(state, Assert.Single(Projections.RunnerStates([Projections.Wire(state)])!));
        }
    }

    /// <summary>A cleared control sends the key with nothing in it, or not at all.</summary>
    [Fact]
    public void A_filter_value_nobody_sent_asks_for_everything()
    {
        Assert.Null(Filter.Words(null));
        Assert.Null(Filter.Words([""]));
        Assert.Null(Filter.Words(["  "]));
        Assert.Null(Filter.Words([","]));

        // `?state=` binds as an array holding one null rather than an empty one,
        // which is what a cleared control sends and what used to answer 500.
        Assert.Null(Filter.Words([null!]));
        Assert.Null(Filter.Ids([null!]));
        Assert.Null(Filter.Exact([null!]));
        Assert.Null(Filter.Ids(null));
        Assert.Null(Filter.Exact([" "]));
        Assert.Null(Projections.JobStates(null));
    }

    /// <summary>
    /// And a word with nothing behind it is not the same thing as no word at
    /// all. Both arrive as a narrowing; only one of them narrows to everything.
    /// </summary>
    [Fact]
    public void A_filter_of_words_the_product_has_no_name_for_matches_nothing()
    {
        Assert.Empty(Projections.JobStates(["nonsense"])!);
        Assert.Empty(Projections.PrintoutStates(["nonsense"])!);
        Assert.Empty(Projections.RunnerStates(["nonsense"])!);

        // One it knows beside one it does not narrows by the one it knows.
        Assert.Equal(
            [EvaluationJobState.Queued],
            Projections.JobStates(["queued", "nonsense"])!);

        Assert.Empty(Filter.Ids(["not-a-uuid"])!);
    }

    /// <summary>
    /// <b>A verdict is one string however many commas are in it.</b>
    /// <para>
    /// The Server stores a verdict and never parses it, so that a problem type
    /// may invent one without a Server release. Choosing a separator for it
    /// would be parsing it — and `Wrong answer, test 3` would become two words
    /// matching nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void A_verdict_is_never_split_on_a_comma()
    {
        Assert.Equal(["Wrong answer, test 3"], Filter.Exact(["Wrong answer, test 3"])!);

        // Repeated keys still arrive as several, which is how two are asked for.
        Assert.Equal(
            ["Wrong answer, test 3", "Accepted"],
            Filter.Exact(["Wrong answer, test 3", "Accepted"])!);

        // And the closed vocabularies beside it do split, which is what keeps
        // every address anybody has pasted into a message working.
        Assert.Equal(["queued", "running"], Filter.Words(["queued,running"])!);
    }
}
