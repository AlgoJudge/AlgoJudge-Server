using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// What a series lets through — the Server's copy of the Client's
    /// `api/seriesState.ts`, and the authoritative one.
    /// <para>
    /// The Client applies these rules to decide what to <b>offer</b>. The Server
    /// applies them to decide what to <b>serve</b>, and only the second is a
    /// refusal: a rule the Client performs alone is a rule anybody can turn off
    /// with a devtools console.
    /// </para>
    /// </summary>
    public interface ISeriesGate
    {
        /// <summary>Whether the statements may be sent at all.</summary>
        bool MayReadProblems(Series series, Activity activity);

        /// <summary>Whether a submission may be accepted right now.</summary>
        bool MaySubmit(Series series);
    }

    public class SeriesGate : ISeriesGate
    {
        /// <summary>
        /// Openness is <b>read from the series</b>, not computed from its dates
        /// (decided 2026-08-08). The scheduler owns the transition; anything that
        /// recomputed it here would be a second answer to one question, and the
        /// two would disagree in exactly the minute that matters.
        /// </summary>
        public bool MayReadProblems(Series series, Activity activity)
        {
            // A round that has ended stays readable unless the activity says
            // otherwise — a round that is over is over, not secret.
            if (!series.IsOpen)
            {
                if (series.PausedAt is not null) return !series.HideProblemsWhilePaused;
                if (series.EndDate is not null && series.StartAnnouncedAt is not null)
                {
                    return !activity.HideEndedSeriesProblems;
                }
                // Never opened: a series that has not started does not disclose
                // what it holds.
                return false;
            }
            return true;
        }

        public bool MaySubmit(Series series) => series.IsOpen && series.PausedAt is null;
    }
}
