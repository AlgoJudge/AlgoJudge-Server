namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The event envelope, exactly as `AlgoJudge-Client/src/api/Event.ts` already
    /// declares it: <c>{ "type": "...", "data": { ... } }</c>.
    /// <para>
    /// No wrapper, no batching, no ids, no acknowledgements in v1. The payload of
    /// each type is the one declared beside it in the Client's API modules, and
    /// <b>those declarations are the schema</b> — this file names the types and
    /// the payload shapes the Server fills, it does not invent them.
    /// </para>
    /// <para>
    /// There are no subscribe frames. The Server sends an event to a session only
    /// if that session may read what the event names, judged by the same
    /// permission model that guards the REST endpoint carrying the same data. A
    /// subscribe protocol was rejected because it would restate the permission
    /// model on the wire.
    /// </para>
    /// </summary>
    public record EventEnvelopeDto
    {
        public required string Type { get; init; }
        public required object Data { get; init; }
    }

    /// <summary>The event names the Client already routes. Adding one is additive.</summary>
    public static class EventTypes
    {
        // Core — everybody with a session.
        public const string SystemMessage = "systemMessage";
        public const string SessionExpired = "sessionExpired";

        // Participant.
        public const string ActivityCreated = "activityCreated";
        public const string ActivityUpdated = "activityUpdated";
        public const string ActivityDeleted = "activityDeleted";
        public const string ActivityTimesChanged = "activityTimesChanged";
        public const string SeriesChanged = "seriesChanged";
        public const string ProblemStatusChanged = "problemStatusChanged";
        public const string SubmissionStateChanged = "submissionStateChanged";
        public const string RankingChanged = "rankingChanged";
        public const string QuestionAnswered = "questionAnswered";
        public const string QuestionPublished = "questionPublished";
        public const string AnnouncementPublished = "announcementPublished";

        // Manager.
        public const string PermissionTemplateChanged = "permissionTemplateChanged";
        public const string GrantChanged = "grantChanged";
        public const string ProblemChanged = "problemChanged";
        public const string ActivityChanged = "activityChanged";
        public const string ManagerSeriesChanged = "seriesChanged";
        public const string SubmissionChanged = "submissionChanged";
        public const string QuestionChanged = "questionChanged";
        public const string UserChanged = "userChanged";
        public const string RunnerChanged = "runnerChanged";
        public const string InstanceChanged = "instanceChanged";
    }

    /// <summary>
    /// `submissionStateChanged` — the one event the M1 slice sends.
    /// <para>
    /// Sent as a job is claimed, finishes, or is cancelled. The submission is
    /// carried whole so a screen redraws from what arrived rather than patching
    /// it, and it is the <b>participant's</b> projection: rescaled score, no
    /// per-test document, no other person's submission.
    /// </para>
    /// </summary>
    public record SubmissionStateChangedData
    {
        public required string ActivityId { get; init; }
        public required SubmissionSummaryDto Submission { get; init; }
    }

    /// <summary>
    /// `seriesChanged` — one event rather than five, differing only in `change`.
    /// `opened` carries the problems that were withheld until then.
    /// </summary>
    public record SeriesChangedData
    {
        public required string ActivityId { get; init; }
        public required SeriesDto Series { get; init; }
        /// <summary>`opened` | `closed` | `paused` | `resumed` | `rescheduled`.</summary>
        public required string Change { get; init; }
        /// <summary>
        /// The Server was down when this was due and is catching up. Not part of
        /// the Client's declared payload today, and therefore <b>proposed</b>:
        /// without it a round that opened an hour ago arrives looking like it
        /// opened now, and the countdown lies.
        /// </summary>
        public bool? Late { get; init; }
    }

    /// <summary>
    /// `rankingChanged`. `unfrozen` and `windowOpened` carry no result on
    /// purpose — they mean "what you hold is now incomplete", and the screen
    /// refetches.
    /// </summary>
    public record RankingChangedData
    {
        public required string ActivityId { get; init; }
        /// <summary>`result` | `unfrozen` | `windowOpened`.</summary>
        public required string Change { get; init; }
        public string? SeriesId { get; init; }
        /// <summary>Only on `result`, and already through the disclosure filter.</summary>
        public object? Result { get; init; }
    }

    /// <summary>`instanceChanged` — carries the whole answer, because that is what every reader holds.</summary>
    public record InstanceChangedData
    {
        public required InstanceInfoDto Instance { get; init; }
    }

    /// <summary>`systemMessage` — a message to show, dispatched by the transport.</summary>
    public record SystemMessageData
    {
        public required string Message { get; init; }
        /// <summary>`success` | `info` | `warning` | `error`.</summary>
        public required string Type { get; init; }
    }
}
