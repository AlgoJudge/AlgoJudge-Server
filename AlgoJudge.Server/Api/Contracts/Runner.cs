namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The Server–Runner wire contract.
    /// <para>
    /// `docs/protocols/SERVER_RUNNER_API.md` is a <b>proposal</b>, not an
    /// accepted specification, and it leaves leasing and idempotency explicitly
    /// open. What is here is the smallest surface the end-to-end path needs, with
    /// the four things the proposal lacks: a single-use nonce, a token with a
    /// lifetime, authorization against the <b>job</b> rather than against being a
    /// Runner, and the lease itself.
    /// </para>
    /// </summary>

    /// <summary>
    /// A Runner presenting itself for the first time. Registration is not
    /// approval: nothing is evaluated until an administrator approves the key.
    /// </summary>
    public record RunnerRegisterDto
    {
        public required string Name { get; init; }
        public string? Product { get; init; }
        public string? Version { get; init; }
        /// <summary>Ed25519 public key, base64. Generated once and never rotated.</summary>
        public required string PublicKey { get; init; }
        /// <summary>
        /// What this Runner can evaluate, as problem-type discriminators. The
        /// Server matches jobs against this <b>by equality only</b> and never
        /// parses an entry.
        /// </summary>
        public required IReadOnlyList<string> ProblemTypes { get; init; }
        /// <summary>
        /// Whether this Runner sends submissions outside the installation.
        /// <para>
        /// Optional, and absent means <c>false</c>: a Runner written before this
        /// existed evaluates locally, which is what it always did. Declaring it
        /// is how a Runner that forwards work says so, and the Server hands out
        /// external work to nobody else.
        /// </para>
        /// </summary>
        public bool External { get; init; }
        /// <summary>
        /// Which pools this Runner belongs to, from its own configuration.
        /// <para>
        /// <b>A seed, honoured on the first registration only.</b> Every other
        /// field here is refreshed whenever a Runner registers again, which is
        /// how it reports a restart; this one is not. Afterwards the operator
        /// owns it, because a Runner that could re-declare its tags on restart
        /// would move itself into an examination's queue without anybody
        /// approving that.
        /// </para>
        /// <para>Optional, and absent means the default pool.</para>
        /// </summary>
        public IReadOnlyList<string>? Tags { get; init; }
        /// <summary>Host facts, stored opaquely and shown in the panel.</summary>
        public object? Machine { get; init; }
    }

    public record RunnerRegisteredDto
    {
        public required string RunnerId { get; init; }
        public required string Fingerprint { get; init; }
        /// <summary>`pendingApproval` | `approved` | `revoked`.</summary>
        public required string State { get; init; }
    }

    /// <summary>Step one of the handshake: ask for something to sign.</summary>
    public record RunnerChallengeRequestDto
    {
        public required string Fingerprint { get; init; }
    }

    public record RunnerChallengeDto
    {
        /// <summary>
        /// Single-use and short-lived. The proposal left both open, and a nonce
        /// that is neither is a signature somebody can replay.
        /// </summary>
        public required string Nonce { get; init; }
        public required string ExpiresAt { get; init; }
    }

    /// <summary>Step two: prove the private key, get a token.</summary>
    public record RunnerTokenRequestDto
    {
        public required string Fingerprint { get; init; }
        public required string Nonce { get; init; }
        /// <summary>Ed25519 signature over the nonce, base64.</summary>
        public required string Signature { get; init; }
    }

    public record RunnerTokenDto
    {
        public required string Token { get; init; }
        public required string ExpiresAt { get; init; }
    }

    /// <summary>
    /// What the Runner asks for when it wants work. Empty means "nothing
    /// matched", which is a normal answer and not an error.
    /// </summary>
    public record ClaimRequestDto
    {
        /// <summary>
        /// How long the Runner expects to need. The Server clamps it: a lease is
        /// a correctness mechanism, so it may not be shortened below what a real
        /// evaluation takes, nor extended without bound by a Runner that dies.
        /// </summary>
        public int? LeaseSeconds { get; init; }
    }

    /// <summary>
    /// A claimed job. The <see cref="LeaseToken"/> is what makes reporting
    /// idempotent — the Runner may safely resend, and a Runner whose lease has
    /// been reclaimed is refused rather than allowed to overwrite a newer attempt.
    /// </summary>
    public record ClaimedJobDto
    {
        public required string JobId { get; init; }
        public required string SubmissionId { get; init; }
        public required int Attempt { get; init; }
        public required string LeaseToken { get; init; }
        public required string LeaseExpiresAt { get; init; }

        public required string ProblemType { get; init; }
        public required string ProblemVersionId { get; init; }
        /// <summary>
        /// The package, and the submitted source. Fetched from the file API and
        /// readable <b>only while this lease is held</b>.
        /// </summary>
        public required string PackageFileId { get; init; }
        public required string PackageSha256 { get; init; }
        public required IReadOnlyList<SubmissionFileDto> Files { get; init; }
        /// <summary>
        /// What the participant declared beside the bytes, the language among it.
        /// <b>Opaque</b>: the Server passes it through without reading a member.
        /// <para>
        /// It was a `language` field the Server read, so the allowed set was
        /// checked here and a file extension for pasted source came from a table
        /// of seven languages compiled into a controller. The Runner decides both
        /// now — it is what knows a language catalogue — and the Server needs no
        /// release when one is added.
        /// </para>
        /// </summary>
        public object? Props { get; init; }
        /// <summary>
        /// What the problem type needs about the pinned <b>version</b> — `uva@1`'s
        /// archive problem number, for instance.
        /// <para>
        /// <b>Identity, not settings.</b> It is not a layer of the configuration
        /// chain, which is the package and then the assignment; it says which
        /// problem this is, and no assignment should have to restate that.
        /// </para>
        /// <para>
        /// Absent where the type needs none. A Runner that needs it and finds
        /// none reports an infrastructure failure naming what is missing — the
        /// Server cannot check, because it does not read this and must not
        /// branch on a problem type.
        /// </para>
        /// </summary>
        public object? ProblemVersionProps { get; init; }
        /// <summary>
        /// The assignment's configuration, laid over the package's own by the
        /// Runner. Opaque here: the Server never read it and merges nothing —
        /// the chain is two layers.
        /// </summary>
        public object? Config { get; init; }
    }

    /// <summary>
    /// What a Runner reports when it is done.
    /// <para>
    /// Four fields are all the Server reads. The per-test table and the log are
    /// <b>attachments</b>, uploaded through the ordinary file API first and named
    /// here, because a per-test table is a hundred and twenty bytes a row times
    /// two thousand tests times every attempt.
    /// </para>
    /// </summary>
    public record ReportResultDto
    {
        public required string LeaseToken { get; init; }
        /// <summary>
        /// True when the evaluation itself failed — a package whose checksum did
        /// not match, a sandbox that would not start. <b>Not</b> a wrong answer,
        /// and it must never be scored as one.
        /// </summary>
        public bool InfrastructureFailure { get; init; }
        public string? FailureReason { get; init; }

        /// <summary>The Runner's own scale, before the assignment's rescaling.</summary>
        public double? Score { get; init; }
        public double? MaxScore { get; init; }
        public string? Verdict { get; init; }
        public string? RunnerVersion { get; init; }

        /// <summary>
        /// Whatever else the problem type wants a board to have. Public by
        /// construction, an object or absent, and bounded at 2 kB because it
        /// rides in a list — over it is refused, never truncated.
        /// </summary>
        public object? Extra { get; init; }

        /// <summary>
        /// What the type wants shown beside <b>this</b> participant's own result —
        /// the toolchain that compiled it, a per-language note.
        /// <para>
        /// The pair to <see cref="Extra"/>, and the difference is the audience,
        /// not the content: <c>extra</c> is on a board everyone reads, so it is
        /// bounded at a hundredth of this one and nothing private belongs in it.
        /// </para>
        /// </summary>
        public object? Props { get; init; }

        /// <summary>
        /// Named attachments for this attempt — `log`, `details`. Already
        /// uploaded; this names them.
        /// </summary>
        public IReadOnlyList<ReportedFileDto>? Files { get; init; }
    }

    public record ReportedFileDto
    {
        /// <summary>The name within the attempt: `log`, `details`.</summary>
        public required string Name { get; init; }
        public required string FileId { get; init; }
    }

    public record LeaseDto
    {
        public required string JobId { get; init; }
        public required string LeaseToken { get; init; }
        public required string LeaseExpiresAt { get; init; }
    }

    public record LeaseRequestDto
    {
        public required string LeaseToken { get; init; }
        public int? LeaseSeconds { get; init; }
    }

    /// <summary>Naming a file the Runner has already uploaded, on the attempt it holds.</summary>
    public record AttachToJobDto
    {
        public required string LeaseToken { get; init; }
        public required string FileId { get; init; }
        /// <summary>`log`, `details`. The role within the attempt, not the file name.</summary>
        public required string Name { get; init; }
    }

    /// <summary>Naming a file the Runner uploaded about itself. Replaces the name.</summary>
    public record AttachToSelfDto
    {
        public required string FileId { get; init; }
        /// <summary>`runner.log`, `lscpu.txt`.</summary>
        public required string Name { get; init; }
    }

    public record ReportAcceptedDto
    {
        /// <summary>
        /// Absent when nothing was stored, which is an infrastructure failure
        /// the Server is going to have judged again: a stored result is what
        /// makes a repeat answer <c>duplicate</c>, so keeping one from a failed
        /// attempt would hand the next Runner's honest work back to it as a
        /// duplicate of the failure. <c>state</c> says <c>queued</c> there.
        /// </summary>
        public string? ResultId { get; init; }
        public required string State { get; init; }
        /// <summary>
        /// True when this report was a repeat and the stored result was returned
        /// unchanged. The Runner needs to be able to tell, and so does anybody
        /// reading a log wondering why a job was reported twice.
        /// </summary>
        public required bool Duplicate { get; init; }
    }
}
