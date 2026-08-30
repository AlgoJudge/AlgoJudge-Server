using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>
    /// One person on the course's roster, and what this installation can say
    /// about them.
    /// </summary>
    public record RosterEntryDto
    {
        /// <summary>The platform's own subject for them. Always present.</summary>
        public required string Subject { get; init; }

        public required IReadOnlyList<string> Roles { get; init; }

        /// <summary>What the platform is willing to disclose. Any of it may be absent.</summary>
        public string? Name { get; init; }
        public string? Email { get; init; }

        /// <summary>
        /// The username the <b>platform</b> asserts for them, where it sends one.
        ///
        /// <para>
        /// Named for who said it, not for what it is — <c>UserName</c> below is
        /// this installation's own login for the same person, and the two are
        /// only equal when §4.3 holds. They also collided as one JSON field when
        /// both were called some spelling of "username", which is how the
        /// distinction got made explicit rather than merely meant.
        /// </para>
        /// </summary>
        public string? AssertedUsername { get; init; }

        /// <summary>
        /// <c>active</c>, <c>Inactive</c>, <c>Deleted</c> — the platform's word for
        /// it, unchanged. A roster carries people who have left, and deciding
        /// what that means here is not this reader's business.
        /// </summary>
        public string? Status { get; init; }

        /// <summary>
        /// The AlgoJudge account this person is already known to be, where one is
        /// known. Null means nobody has been matched to them yet.
        /// </summary>
        public string? UserId { get; init; }
        public string? UserName { get; init; }

        /// <summary>
        /// How firmly. <c>confirmed</c> came from a launch this person made
        /// themselves; <c>provisional</c> was inferred from a roster.
        /// </summary>
        public string? Strength { get; init; }
    }

    /// <summary>
    /// A course's roster as read from the platform, with what could be matched.
    /// </summary>
    public record RosterViewDto
    {
        public required string ContextId { get; init; }
        public required string ContextTitle { get; init; }
        public required string ReadAt { get; init; }
        public required int Total { get; init; }

        /// <summary>How many already have an AlgoJudge account behind them.</summary>
        public required int Known { get; init; }

        public required IReadOnlyList<RosterEntryDto> Members { get; init; }

        /// <summary>
        /// What the platform actually disclosed, counted rather than assumed.
        ///
        /// <para>
        /// <b>This is the field milestone 2 is being designed against.</b> Whether
        /// a provisional link can be made at all depends on whether the roster
        /// carries anything to make it from, and that is an installation's
        /// configuration rather than a property of LTI. Counting it here means the
        /// answer comes from the platform in front of us instead of from a guess.
        /// </para>
        /// </summary>
        public required RosterDisclosureDto Disclosed { get; init; }
    }

    public record RosterDisclosureDto
    {
        public required int WithUsername { get; init; }
        public required int WithEmail { get; init; }
        public required int WithName { get; init; }
    }

    /// <summary>What a roster enrolment did, and what it declined to do.</summary>
    public record RosterEnrolmentDto
    {
        public required int Read { get; init; }

        /// <summary>People newly linked to an account, provisionally.</summary>
        public required int Linked { get; init; }

        /// <summary>People put into the activity, including already-linked ones.</summary>
        public required int Granted { get; init; }

        /// <summary>
        /// People the roster named and this installation could not place, by
        /// reason. <b>Reported rather than guessed at</b>: every one of these is
        /// somebody a teacher may be expecting to see.
        /// </summary>
        public required IReadOnlyList<RosterSkipDto> Skipped { get; init; }
    }

    public record RosterSkipDto
    {
        public required string Subject { get; init; }
        public string? Name { get; init; }

        /// <summary>
        /// <c>noUsername</c> — the platform disclosed none, so there is nothing to
        /// match on. <c>unknownAccount</c> — no account here carries it.
        /// <c>outsideNamespace</c> — an account does, but it did not come through
        /// the directory this platform may assert for. <c>inactive</c> — the
        /// platform says they are no longer in the course.
        /// </summary>
        public required string Reason { get; init; }
    }

    public interface IRosterService
    {
        Task<RosterViewDto> ReadAsync(Guid resourceLinkId, CancellationToken ct);

        /// <summary>
        /// Puts the course's roster into the activity, linking whoever can be
        /// linked.
        ///
        /// <para>
        /// <b>Deliberate, never on a timer</b> (decided 2026-08-15), and this is
        /// the writing half of that: a manager asks, and a university's Moodle is
        /// read once.
        /// </para>
        /// </summary>
        Task<RosterEnrolmentDto> EnrolAsync(Guid resourceLinkId, CancellationToken ct);
    }

    /// <summary>
    /// Reading a course's roster, and saying what is in it.
    ///
    /// <para>
    /// <b>It links nobody yet.</b> This is the half of milestone 2 that can be
    /// built without deciding §13 #2 — what a provisional link may be made from —
    /// and it is deliberately first, because that decision should be taken
    /// against a roster somebody has actually looked at.
    /// </para>
    /// </summary>
    public class RosterService(
        LtiDbContext db,
        ApplicationDbContext core,
        INrpsClient nrps,
        IIdentityResolver identities,
        ILtiEnrolmentService enrolment,
        IPermissionService permissions,
        TimeProvider clock
    ) : IRosterService
    {
        public async Task<RosterViewDto> ReadAsync(Guid resourceLinkId, CancellationToken ct)
        {
            var link = await db.ResourceLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == resourceLinkId, ct)
                ?? throw new NotFoundException("Placement");

            // The same permission that reads everybody's results in an activity:
            // a roster is a list of who is in it, which is no less disclosing.
            await permissions.RequireAsync(Permissions.ResultReadAll, link.ActivityId, ct);

            if (link.NrpsMembershipsUrl is not { Length: > 0 } url)
            {
                throw new ConflictException(
                    "This platform offered no roster service for that course. The tool may be "
                    + "registered without the membership scope, or the service switched off",
                    "lti.roster.unavailable");
            }

            var platform = await db.Platforms.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == link.PlatformId, ct)
                ?? throw new NotFoundException("Platform");

            var roster = await nrps.ReadAsync(platform, url, link.PlatformResourceLinkId, ct);

            // Who is already linked, so the screen can say which of these people
            // this installation would recognise.
            var subjects = roster.Members.Select(m => m.UserId).ToList();
            var known = await db.ExternalIdentities.AsNoTracking()
                .Where(i => i.PlatformId == platform.Id && subjects.Contains(i.Subject))
                .ToListAsync(ct);

            var userIds = known.Select(i => i.UserId).Distinct().ToList();
            var names = await core.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.UserName, ct);

            var bySubject = known.ToDictionary(i => i.Subject);

            var members = roster.Members.Select(member =>
            {
                bySubject.TryGetValue(member.UserId, out var identity);
                return new RosterEntryDto
                {
                    Subject = member.UserId,
                    Roles = member.Roles,
                    Name = member.Name,
                    Email = member.Email,
                    AssertedUsername = member.Username,
                    Status = member.Status,
                    UserId = identity?.UserId,
                    UserName = identity is null ? null : names.GetValueOrDefault(identity.UserId),
                    Strength = identity?.Strength.ToString().ToLowerInvariant(),
                };
            }).ToList();

            return new RosterViewDto
            {
                ContextId = roster.ContextId is { Length: > 0 } id ? id : link.ContextId,
                ContextTitle = link.ContextTitle ?? "",
                ReadAt = Wire.At(clock.GetUtcNow().UtcDateTime),
                Total = members.Count,
                Known = members.Count(m => m.UserId is not null),
                Members = members,
                Disclosed = new RosterDisclosureDto
                {
                    WithUsername = members.Count(m => !string.IsNullOrWhiteSpace(m.AssertedUsername)),
                    WithEmail = members.Count(m => !string.IsNullOrWhiteSpace(m.Email)),
                    WithName = members.Count(m => !string.IsNullOrWhiteSpace(m.Name)),
                },
            };
        }

        public async Task<RosterEnrolmentDto> EnrolAsync(Guid resourceLinkId, CancellationToken ct)
        {
            var link = await db.ResourceLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == resourceLinkId, ct)
                ?? throw new NotFoundException("Placement");

            // Putting people into an activity is enrolling them, and that is the
            // permission that governs it everywhere else.
            await permissions.RequireAsync(Permissions.ActivityEnroll, link.ActivityId, ct);

            if (link.NrpsMembershipsUrl is not { Length: > 0 } url)
            {
                throw new ConflictException(
                    "This platform offered no roster service for that course",
                    "lti.roster.unavailable");
            }

            var platform = await db.Platforms.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == link.PlatformId, ct)
                ?? throw new NotFoundException("Platform");

            // **Without identity authority a roster names nobody this
            // installation may claim** (§4.5). The read is still allowed — a
            // manager may look at who is in the course — but turning names into
            // accounts is exactly what the flag governs.
            if (!platform.IsIdentityAuthority
                || string.IsNullOrWhiteSpace(platform.IdentityNamespace))
            {
                throw new ConflictException(
                    "This platform may not say who somebody is, so its roster cannot enrol anybody. "
                    + "Trust it for a directory first, or let people arrive by launching",
                    "lti.roster.notAuthority");
            }

            var roster = await nrps.ReadAsync(platform, url, link.PlatformResourceLinkId, ct);

            var skipped = new List<RosterSkipDto>();
            var linked = 0;
            var granted = 0;

            foreach (var member in roster.Members)
            {
                // The platform's own word for somebody who has left. Enrolling
                // them would put a person back into an activity the course says
                // they are no longer in.
                if (member.Status is { Length: > 0 } status
                    && !status.Equals("Active", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(Skip(member, "inactive"));
                    continue;
                }

                var existing = await db.ExternalIdentities.FirstOrDefaultAsync(
                    i => i.PlatformId == platform.Id && i.Subject == member.UserId, ct);

                string? userId = existing?.UserId;

                if (existing is null)
                {
                    if (string.IsNullOrWhiteSpace(member.Username))
                    {
                        // Nothing to match on. **Not the address**: correlating
                        // automatically on an unverified email is account
                        // takeover, which the identity rules forbid outright.
                        skipped.Add(Skip(member, "noUsername"));
                        continue;
                    }

                    var candidate = await identities.MatchAsync(platform, member.Username, ct);
                    if (candidate is null)
                    {
                        // Two different failures, and a teacher can act on the
                        // difference: nobody here has that username at all, or
                        // somebody does and they came in through another door.
                        var exists = await core.Users.AsNoTracking()
                            .AnyAsync(u => u.NormalizedUserName == member.Username.ToUpperInvariant(), ct);
                        skipped.Add(Skip(member, exists ? "outsideNamespace" : "unknownAccount"));
                        continue;
                    }

                    db.ExternalIdentities.Add(new ExternalIdentity
                    {
                        PlatformId = platform.Id,
                        Subject = member.UserId,
                        UserId = candidate.Id,
                        // **Provisional**, and that word is load-bearing (§4.4):
                        // nobody has authenticated here. A launch by this person
                        // raises it, and until then it is a link this
                        // installation inferred rather than one it witnessed.
                        Strength = LinkStrength.Provisional,
                        AssertedUsername = member.Username.Trim(),
                    });
                    await db.SaveChangesAsync(ct);

                    userId = candidate.Id;
                    linked++;
                }

                if (userId is null) continue;

                await enrolment.EnrolAsync(link, platform.ProviderId, userId, member.Roles, ct);
                granted++;
            }

            return new RosterEnrolmentDto
            {
                Read = roster.Members.Count,
                Linked = linked,
                Granted = granted,
                Skipped = skipped,
            };
        }

        private static RosterSkipDto Skip(RosterMember member, string reason) => new()
        {
            Subject = member.UserId,
            Name = member.Name,
            Reason = reason,
        };
    }
}
