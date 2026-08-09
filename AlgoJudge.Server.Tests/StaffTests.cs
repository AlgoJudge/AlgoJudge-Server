using AlgoJudge.Server.Authorization;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Who counts as staff, and therefore who a ranking leaves out.
///
/// <para>
/// This is measured from the permissions somebody holds, which means adding a
/// permission can silently change who appears on a board. That is a quiet
/// enough failure to be worth its own tests: nothing errors, a name is simply
/// missing.
/// </para>
/// </summary>
public class StaffTests
{
    [Fact]
    public void An_ordinary_participant_is_not_staff()
    {
        Assert.False(Permissions.IsStaff(Permissions.Participant));
    }

    [Fact]
    public void Anything_that_reaches_somebody_elses_work_is_staff()
    {
        Assert.True(Permissions.IsStaff([Permissions.ActivityRead, Permissions.SubmissionReadAll]));
        Assert.True(Permissions.IsStaff([Permissions.ProblemCreate]));
        Assert.True(Permissions.IsStaff([Permissions.SystemAdministrator]));
    }

    /// <summary>
    /// The one that would have emptied a board.
    /// <para>
    /// `trial:run` lets somebody time their own package. Measured against the
    /// participant template alone — which is what `IsStaff` used to do — holding
    /// it made a participant staff, and staff do not appear in a ranking. An
    /// activity that opened trials to its participants would have lost every one
    /// of them from its board, with nothing failing anywhere.
    /// </para>
    /// </summary>
    [Fact]
    public void A_participant_allowed_to_run_a_trial_is_still_a_participant()
    {
        var theirs = Permissions.Participant.Append(Permissions.TrialRun);

        Assert.False(
            Permissions.IsStaff(theirs),
            "spending your own time on your own package is not seeing anybody else's work");
    }

    /// <summary>
    /// It is not in the template, so opening trials stays a manager's decision
    /// in one activity rather than a property of the installation.
    /// </summary>
    [Fact]
    public void Running_a_trial_is_not_something_a_participant_gets_by_default()
    {
        Assert.DoesNotContain(Permissions.TrialRun, Permissions.Participant);
    }

    [Fact]
    public void The_catalogue_describes_every_key_including_the_new_one()
    {
        Assert.Contains(Permissions.Catalogue, d => d.Key == Permissions.TrialRun);
        Assert.Empty(Permissions.Unknown([Permissions.TrialRun]));
    }

    /// <summary>
    /// The catalogue is the rule, not a description of it.
    /// <para>
    /// The Client draws the systemic switch from what this endpoint publishes,
    /// and until the catalogue carried <c>systemic</c> it had to infer the
    /// answer by negating <c>participant</c>. That inference is right for every
    /// key that existed when it was written and wrong for `trial:run` — the
    /// screen would grey the switch on and force it, while the Server stored it
    /// off. Nobody would see an error; the dialog would simply state something
    /// untrue about the ranking.
    /// </para>
    /// <para>
    /// So the flag is asserted against <see cref="Permissions.IsStaff"/> itself,
    /// key by key. A permission added to one list and not the other fails here
    /// rather than in somebody's activity.
    /// </para>
    /// </summary>
    [Fact]
    public void What_the_catalogue_publishes_is_what_the_Server_enforces()
    {
        Assert.All(Permissions.Catalogue, definition =>
            Assert.Equal(Permissions.IsStaff([definition.Key]), definition.Systemic));
    }

    /// <summary>
    /// And the two flags are genuinely two: `trial:run` is the key that told
    /// them apart, so it is the one worth naming.
    /// </summary>
    [Fact]
    public void Outside_the_default_template_is_not_the_same_as_systemic()
    {
        var trial = Permissions.Catalogue.Single(d => d.Key == Permissions.TrialRun);

        Assert.False(trial.Participant, "it is not granted by default");
        Assert.False(trial.Systemic, "and holding it does not take somebody off the board");
    }
}
