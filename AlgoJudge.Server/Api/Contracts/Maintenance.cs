namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// How far the Server has withdrawn from service.
    /// <para>
    /// Carried by `/health`, which is why it is a document rather than a status
    /// code: **health answers 200 at every level**. It is the one endpoint that
    /// stays up, so it is the only thing a Client or a Runner can ask while
    /// everything else refuses — and a container healthcheck that started
    /// failing during a planned window would stop the container the operator is
    /// trying to keep alive.
    /// </para>
    /// </summary>
    public record MaintenanceDto
    {
        /// <summary>
        /// `open` | `draining` | `closed`.
        /// <para>
        /// A word rather than a number on the wire, and **ordered** all the same:
        /// `draining` refuses new work and admits a report, `closed` refuses
        /// everything but this.
        /// </para>
        /// </summary>
        public required string Level { get; init; }

        /// <summary>When the withdrawal was asked for. Absent when open.</summary>
        public string? Since { get; init; }

        /// <summary>Free text from whoever threw the switch. Absent when open.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>What `/health` answers, at every level.</summary>
    public record HealthDto
    {
        /// <summary>`ok`, always. The process answering at all is the news.</summary>
        public required string Status { get; init; }

        /// <summary>
        /// Present **only while withdrawn**. Absent is the ordinary state, so a
        /// reader that has never heard of maintenance sees exactly what it saw
        /// before this existed.
        /// </summary>
        public MaintenanceDto? Maintenance { get; init; }

        /// <summary>
        /// `ok` or `degraded`, and never more than that.
        /// <para>
        /// **One word on purpose.** This endpoint is anonymous, so anything it
        /// says is said to the internet: a store id, a bucket, a host or a path
        /// here would be this product disclosing its own infrastructure to
        /// whoever asks (A65c). Which store, and what is wrong with it, is on
        /// `/admin/storage`, behind the loopback interface and a token.
        /// </para>
        /// <para>
        /// `degraded` does **not** mean the installation is down. Files in a
        /// healthy store are still served; the ones in a broken one answer 503.
        /// </para>
        /// </summary>
        public required string Storage { get; init; }
    }

    /// <summary>
    /// Throwing the switch, from the machine itself.
    /// <para>
    /// <b>The body is optional, and the same two values may arrive in the query
    /// string.</b> Not a convenience: the shipped image has no <c>curl</c> and no
    /// <c>wget</c> — the container's own healthcheck is written with bash's
    /// <c>/dev/tcp</c> for exactly that reason — so the operator throwing this
    /// through <c>docker exec</c> is writing an HTTP request by hand. A form
    /// needing a body means counting bytes for <c>Content-Length</c> and getting
    /// it right under pressure, at the moment somebody is trying to take a
    /// broken installation off the air. A query string needs no body at all.
    /// </para>
    /// </summary>
    public record MaintenanceInputDto
    {
        public required bool On { get; init; }

        /// <summary>
        /// Why, for whoever finds the Server closed later. Optional, and worth
        /// filling in: the alternative is an operator guessing whether a closed
        /// Server is a backup or an incident.
        /// </summary>
        public string? Reason { get; init; }
    }
}
