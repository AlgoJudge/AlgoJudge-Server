using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// A choosing in progress: the platform asked this tool what to place, and
    /// somebody here is picking.
    ///
    /// <para>
    /// <b>It exists because the answer travels through a person.</b> The platform
    /// signs a request, we sign a response, and in between a browser leaves for
    /// the application and comes back — so everything needed to build that
    /// response has to survive the round trip somewhere other than in a URL a
    /// person could edit. <c>deep_link_return_url</c> especially: it carries the
    /// platform's own session key, and a response posted anywhere else is a
    /// response posted at a stranger.
    /// </para>
    ///
    /// <para>
    /// <b>Bound to the person the launch resolved to</b>, and spent once. A code
    /// that could be replayed would let somebody place activities into a course
    /// they never opened.
    /// </para>
    /// </summary>
    public class DeepLinkSession
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>Opaque, and the only thing the Client is given.</summary>
        public required string Code { get; set; }

        public Guid PlatformId { get; set; }
        public Platform? Platform { get; set; }

        /// <summary>Who is choosing. Nobody else may read or spend this.</summary>
        public required string UserId { get; set; }

        /// <summary>The course the placement is going into, for the screen to name.</summary>
        public required string ContextId { get; set; }
        public string? ContextTitle { get; set; }

        /// <summary>Where the signed response is posted. Never taken from a request.</summary>
        public required string ReturnUrl { get; set; }

        /// <summary>The platform's opaque string, echoed back untouched.</summary>
        public string? Data { get; set; }

        /// <summary>
        /// Whether the platform said it would take more than one item. Honoured
        /// rather than assumed: a platform sent two links where it expected one
        /// silently keeps the first in some implementations and none in others.
        /// </summary>
        public bool AcceptMultiple { get; set; }

        public string? Locale { get; set; }

        /// <summary>Whether the choosing is happening inside the platform's frame.</summary>
        public bool Embedded { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
    }
}
