using System.Security.Cryptography;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Controllers
{
    /// <summary>What the Client needs to render a launched activity.</summary>
    public record LaunchContextDto
    {
        /// <summary>The placement, so the Client can ask about its grades.</summary>
        public required string LinkId { get; init; }

        /// <summary>The activity to confine the interface to.</summary>
        public required string ActivitySlug { get; init; }

        /// <summary>Narrowed to one round, where the placement says so.</summary>
        public string? SeriesId { get; init; }

        /// <summary>What the course is called at the platform, for a heading.</summary>
        public string? ContextTitle { get; init; }

        /// <summary>The language the course is taken in (§5.4).</summary>
        public string? Locale { get; init; }

        /// <summary>Whether the platform framed this (§5.2).</summary>
        public required bool Embedded { get; init; }

        /// <summary>Where "back to the course" goes, when the platform offered one.</summary>
        public string? ReturnUrl { get; init; }
    }

    /// <summary>
    /// Turning the ticket a launch redirected with into the context the Client
    /// renders from.
    /// <para>
    /// <b>This exists so the embedded mode is not a URL parameter</b> (§5.2). The
    /// ticket is opaque, single-use, short-lived, issued by this Server and
    /// bound to the person the launch resolved to — which is the difference
    /// between "the session was established by a launch" and "the address bar
    /// says so".
    /// </para>
    /// </summary>
    [ApiController]
    [Route("lti/session")]
    [Authorize]
    public class LtiSessionController(
        LtiDbContext db,
        ApplicationDbContext core,
        ICurrentUserService currentUser,
        TimeProvider clock) : ControllerBase
    {
        /// <summary>
        /// Exchanges a ticket for its launch context, once.
        /// </summary>
        [HttpPost("claim")]
        [ProducesResponseType<LaunchContextDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<LaunchContextDto> Claim(
            [FromBody] ClaimInputDto input, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var userId = currentUser.UserId
                ?? throw new UnauthenticatedException();

            var ticket = await db.LaunchTickets.FirstOrDefaultAsync(
                t => t.Ticket == input.Ticket && t.ExpiresAt >= now, ct);

            // **Bound to its owner.** A ticket that leaked — a shared screen, a
            // referrer header, a proxy log — is useless to anybody else, because
            // the session claiming it has to be the one the launch resolved to.
            if (ticket is null || !string.Equals(ticket.UserId, userId, StringComparison.Ordinal))
            {
                throw new NotFoundException("Launch");
            }

            // Consumed by a delete, the same way the launch state is: two claims
            // race and exactly one wins.
            var consumed = await db.LaunchTickets
                .Where(t => t.Id == ticket.Id)
                .ExecuteDeleteAsync(ct);

            if (consumed == 0)
            {
                throw new NotFoundException("Launch");
            }

            // Expired tickets go with it. The table turns over once per launch
            // and a sweep of its own would be more machinery than the problem.
            await db.LaunchTickets.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(ct);

            var link = await db.ResourceLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == ticket.ResourceLinkId, ct)
                ?? throw new NotFoundException("Placement");

            var slug = await core.Activities.AsNoTracking()
                .Where(a => a.Id == link.ActivityId)
                .Select(a => a.Slug)
                .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Activity");

            return new LaunchContextDto
            {
                LinkId = Wire.Id(link.Id),
                ActivitySlug = slug,
                SeriesId = link.SeriesId is { } series ? Wire.Id(series) : null,
                ContextTitle = link.ContextTitle,
                Locale = ticket.Locale,
                Embedded = ticket.Embedded,
                ReturnUrl = ticket.ReturnUrl,
            };
        }

        public record ClaimInputDto
        {
            public required string Ticket { get; init; }
        }
    }

    /// <summary>Issuing a ticket, for the launch endpoint to redirect with.</summary>
    public interface ILaunchTickets
    {
        Task<string> IssueAsync(
            Guid resourceLinkId, string userId, string? locale, bool embedded,
            string? returnUrl, CancellationToken ct);
    }

    public class LaunchTickets(LtiDbContext db, TimeProvider clock) : ILaunchTickets
    {
        /// <summary>
        /// One redirect and one render. Long enough for a slow browser, short
        /// enough that a URL in somebody's history is worth nothing.
        /// </summary>
        private static readonly TimeSpan Life = TimeSpan.FromMinutes(2);

        public async Task<string> IssueAsync(
            Guid resourceLinkId, string userId, string? locale, bool embedded,
            string? returnUrl, CancellationToken ct)
        {
            var ticket = new LaunchTicket
            {
                Ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('='),
                UserId = userId,
                ResourceLinkId = resourceLinkId,
                Locale = locale,
                Embedded = embedded,
                ReturnUrl = returnUrl,
                ExpiresAt = clock.GetUtcNow().UtcDateTime + Life,
            };

            db.LaunchTickets.Add(ticket);
            await db.SaveChangesAsync(ct);
            return ticket.Ticket;
        }
    }
}
