using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Realtime
{
    /// <summary>
    /// What the people in a round are told when it moves.
    /// <para>
    /// <see cref="SeriesChangedData.Change"/> has always declared five kinds —
    /// <c>opened | closed | paused | resumed | rescheduled</c> — and only the
    /// first two were ever sent, both from <c>SeriesScheduler</c>. Pausing,
    /// resuming and shifting announced <c>managerSeriesChanged</c>, whose
    /// audience is <c>activity:update</c>, so a contestant's clock counted down a
    /// round that was standing still and the first they knew of it was a refused
    /// submission.
    /// </para>
    /// <para>
    /// The payload was built inside the worker that happened to need it first,
    /// which is why the manager's writes could not send it. It lives here now, on
    /// its own, so the next caller finds it rather than writing a third one.
    /// </para>
    /// </summary>
    public interface ISeriesAnnouncer
    {
        /// <summary>
        /// Announces a round by <b>id</b>.
        /// <para>
        /// <b>Never by entity, and that is the whole point of the signature.</b>
        /// The payload withholds the statements unless
        /// <see cref="ISeriesGate.MayReadProblems"/> allows them, and that reads
        /// <c>Activity</c> and <c>SeriesProblems</c> — neither of which
        /// <c>ManagerWriteService.Round()</c> includes, because it loads what a
        /// write needs. Handed such a round this would answer "not open", send an
        /// empty one, and every screen would redraw as though the statements were
        /// still hidden. Nothing would throw and no test that did not look at the
        /// payload would notice.
        /// </para>
        /// </summary>
        Task AnnounceAsync(Guid seriesId, string change, bool late, CancellationToken ct);
    }

    public class SeriesAnnouncer(
        ApplicationDbContext context,
        IEventHub events,
        IEventAudience audience,
        ISeriesGate gate
    ) : ISeriesAnnouncer
    {
        public async Task AnnounceAsync(Guid seriesId, string change, bool late, CancellationToken ct)
        {
            var round = await context.Series
                .Include(s => s.Activity)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == seriesId, ct);

            // A round deleted between the write and this is not an error: there
            // is simply nothing left to describe.
            if (round?.Activity is null) return;

            var members = await audience.InActivityAsync(round.ActivityId, Permissions.ActivityRead, ct);
            if (members.Count == 0) return;

            // The same disclosure the endpoint applies: a round that has opened
            // carries its problems, and one that has not does not — so the event
            // cannot leak what a fetch would have withheld.
            var open = gate.MayReadProblems(round, round.Activity);

            var payload = new SeriesChangedData
            {
                ActivityId = Wire.Id(round.ActivityId),
                Change = change,
                Late = late ? true : null,
                Series = new SeriesDto
                {
                    Id = Wire.Id(round.Id),
                    Slug = round.Slug,
                    Name = round.Name,
                    StartDate = Wire.At(round.StartDate),
                    EndDate = Wire.At(round.EndDate),
                    IsOpen = round.IsOpen,
                    PausedAt = Wire.At(round.PausedAt),
                    RankingVisibleFrom = Wire.At(round.RankingVisibleFrom),
                    RankingVisibleTo = Wire.At(round.RankingVisibleTo),
                    ProblemCount = open || round.RevealProblemCount
                        ? round.SeriesProblems.Count
                        : null,
                    Problems = open
                        ? round.SeriesProblems
                            .OrderBy(sp => sp.Order).ThenBy(sp => sp.Id)
                            .Select(sp => new ProblemSummaryDto
                            {
                                Id = Wire.Id(sp.Id),
                                Slug = sp.Slug,
                                Name = sp.Name ?? sp.Problem?.Name ?? sp.Slug,
                                // Nobody's own standing: this goes to everybody,
                                // so it carries what is true of the problem and
                                // nothing that is true of one reader.
                                Status = "untouched",
                                Attempts = 0,
                            })
                            .ToList()
                        : null,
                },
            };

            await events.SendToUsersAsync(members, EventTypes.SeriesChanged, payload, ct);
        }
    }
}
