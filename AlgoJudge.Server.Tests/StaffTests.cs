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
}
