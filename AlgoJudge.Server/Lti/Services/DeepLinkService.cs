using System.Security.Cryptography;
using System.Text.Json;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>What the person choosing is shown.</summary>
    public record DeepLinkView
    {
        /// <summary>The course the placement is going into, as the platform names it.</summary>
        public required string ContextTitle { get; init; }

        public required bool AcceptMultiple { get; init; }
        public required bool Embedded { get; init; }
        public string? Locale { get; init; }

        /// <summary>What this person may place, which is what they manage.</summary>
        public required IReadOnlyList<DeepLinkCandidate> Activities { get; init; }
    }

    public record DeepLinkCandidate
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
    }

    /// <summary>
    /// The answer, ready to be posted at the platform.
    ///
    /// <para>
    /// Handed to the Client as a form to submit rather than followed here: the
    /// platform expects the person's own browser, carrying the platform's own
    /// cookie, at an address that checks its session key.
    /// </para>
    /// </summary>
    public record DeepLinkResponseView
    {
        public required string ReturnUrl { get; init; }
        public required string Jwt { get; init; }
    }

    public interface IDeepLinkService
    {
        Task<DeepLinkView> OpenAsync(string code, CancellationToken ct);

        Task<DeepLinkResponseView> RespondAsync(
            string code, IReadOnlyList<string> activityIds, CancellationToken ct);
    }

    public class DeepLinkService(
        LtiDbContext db,
        IActivityService activities,
        ICurrentUserService current,
        IToolKeyService keys,
        IConfiguration configuration,
        IHttpContextAccessor http,
        TimeProvider clock
    ) : IDeepLinkService
    {
        /// <summary>
        /// Long enough to look through a list of activities and short enough that
        /// a browser left open over lunch cannot place anything.
        /// </summary>
        private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Enough to choose from without paging, which this screen does not do.
        /// Somebody managing more than this has a search problem the platform's
        /// own screen cannot help with either.
        /// </summary>
        private const int Candidates = 200;

        public async Task<DeepLinkView> OpenAsync(string code, CancellationToken ct)
        {
            var session = await MineAsync(code, ct);

            var managed = await activities.ListManagedAsync(
                new PageQuery { Page = 1, PageSize = Candidates }, null, includeArchived: false, ct);

            return new DeepLinkView
            {
                ContextTitle = session.ContextTitle ?? "",
                AcceptMultiple = session.AcceptMultiple,
                Embedded = session.Embedded,
                Locale = session.Locale,
                Activities = managed.Items
                    .Select(a => new DeepLinkCandidate { Id = a.Id, Slug = a.Slug, Name = a.Name })
                    .ToList(),
            };
        }

        public async Task<DeepLinkResponseView> RespondAsync(
            string code, IReadOnlyList<string> activityIds, CancellationToken ct)
        {
            var session = await MineAsync(code, ct);

            if (activityIds.Count == 0)
            {
                throw new ValidationException(
                    "Nothing was chosen", "lti.deepLink.empty");
            }

            if (activityIds.Count > 1 && !session.AcceptMultiple)
            {
                throw new ValidationException(
                    "This platform asked for one item and would drop the rest without saying so",
                    "lti.deepLink.multiple");
            }

            // **Read through the activity service, so the permission check is the
            // one that governs activities everywhere else.** Nothing here decides
            // who may place what; a person who cannot manage an activity gets a
            // refusal from the same code that refuses them elsewhere.
            var chosen = new List<ManagedActivityDto>();
            foreach (var id in activityIds)
            {
                chosen.Add(await activities.GetManagedAsync(id, ct));
            }

            var now = clock.GetUtcNow().UtcDateTime;

            // Spent by a conditional update, for the same reason the invitation
            // is: two submissions of one choosing would place twice.
            var spent = await db.DeepLinkSessions
                .Where(s => s.Id == session.Id && s.UsedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.UsedAt, now), ct);

            if (spent == 0)
            {
                throw new NotFoundException("Deep link session");
            }

            var platform = session.Platform!;
            var jwt = Sign(platform, session, chosen, await keys.CredentialsAsync(ct), now);

            return new DeepLinkResponseView { ReturnUrl = session.ReturnUrl, Jwt = jwt };
        }

        /// <summary>
        /// The response, signed as this tool.
        ///
        /// <para>
        /// <b>Every item is an <c>ltiResourceLink</c></b>, because that is the
        /// only type all three reference Moodles accept — measured 2026-08-15,
        /// where `accept_types` is the literal string <c>ltiResourceLink</c>.
        /// A `link` or `html` item would be dropped without a message.
        /// </para>
        ///
        /// <para>
        /// The activity travels as the same <c>custom</c> parameter a launch
        /// already reads, so a link placed this way and one typed by hand arrive
        /// identically — and placing one still decides nothing about
        /// authorization: the resource link is created, and accepted, at the
        /// first real launch.
        /// </para>
        /// </summary>
        private string Sign(
            Platform platform,
            DeepLinkSession session,
            IReadOnlyList<ManagedActivityDto> chosen,
            SigningCredentials credentials,
            DateTime now)
        {
            var items = chosen.Select(activity => new Dictionary<string, object?>
            {
                ["type"] = "ltiResourceLink",
                ["title"] = activity.Name,
                ["url"] = ApiBase() + "/lti/launch",
                ["custom"] = new Dictionary<string, string>
                {
                    ["activity"] = activity.Slug,
                    ["username"] = "$User.username",
                    ["context_history"] = "$Context.id.history",
                },
                // **No `lineItem`, deliberately.** A placed item may ask the
                // platform to make a grade column, and this tool does not want
                // one: it scores per problem, and those line items are created
                // over AGS when a series exists and has a maximum worth
                // reporting. Asking here would make a column for the activity as
                // a whole that nothing ever writes to, sitting in somebody's
                // gradebook next to the ones that do.
            }).ToList();

            var claims = new Dictionary<string, object>
            {
                [LtiClaims.MessageType] = LtiClaims.DeepLinkingResponse,
                [LtiClaims.Version] = LtiClaims.SupportedVersion,
                [LtiClaims.DeploymentId] = platform.DeploymentId,
                [LtiClaims.ContentItems] = items,
                // Against replay at the platform's end, and required of every
                // message this tool signs.
                ["nonce"] = Opaque(),
            };

            if (!string.IsNullOrEmpty(session.Data))
            {
                claims[LtiClaims.DeepLinkingData] = session.Data;
            }

            var descriptor = new SecurityTokenDescriptor
            {
                // The tool is the issuer here, and it is the client id: that is
                // what the platform knows this tool as.
                Issuer = platform.ClientId,
                Audience = platform.Issuer,
                Claims = claims,
                IssuedAt = now,
                NotBefore = now.AddMinutes(-1),
                Expires = now.AddMinutes(5),
                SigningCredentials = credentials,
            };

            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        /// <summary>
        /// The session, if it is this person's and still live.
        ///
        /// <para>
        /// <b>Not found rather than forbidden when it belongs to somebody else.</b>
        /// Telling a stranger that a code exists but is not theirs tells them the
        /// code is real.
        /// </para>
        /// </summary>
        private async Task<DeepLinkSession> MineAsync(string code, CancellationToken ct)
        {
            var userId = current.UserId
                ?? throw new ForbiddenActionException("Not signed in", "auth.required");
            var now = clock.GetUtcNow().UtcDateTime;

            return await db.DeepLinkSessions
                       .Include(s => s.Platform)
                       .FirstOrDefaultAsync(
                           s => s.Code == code && s.UserId == userId
                               && s.UsedAt == null && s.ExpiresAt > now, ct)
                   ?? throw new NotFoundException("Deep link session");
        }

        /// <summary>
        /// Where a placed link points. The same address the registration hands
        /// the platform, and for the same reason: a browser reaches this Server
        /// there, whatever a container calls it.
        /// </summary>
        private string ApiBase()
        {
            var request = http.HttpContext?.Request;
            var configured = configuration["PublicApiUrl"];
            return (string.IsNullOrWhiteSpace(configured)
                ? request is null ? "" : $"{request.Scheme}://{request.Host}{request.PathBase}"
                : configured).TrimEnd('/');
        }

        private static string Opaque() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        /// <summary>
        /// Starts a choosing and hands back the code the Client is given.
        /// Separate from the interface because only the launch may open one.
        /// </summary>
        public static DeepLinkSession Begin(
            DeepLinkRequest request, string userId, bool embedded, DateTime now) => new()
            {
                Code = Opaque() + Opaque(),
                PlatformId = request.Platform.Id,
                UserId = userId,
                ContextId = request.ContextId,
                ContextTitle = request.ContextTitle,
                ReturnUrl = request.ReturnUrl,
                Data = request.Data,
                AcceptMultiple = request.AcceptMultiple,
                Locale = request.Locale,
                Embedded = embedded,
                CreatedAt = now,
                ExpiresAt = now + Life,
            };
    }
}
