namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// Setting the administrator's password from the machine itself.
    /// <para>
    /// <b>A body, not a query string.</b> A password in a URL is written into
    /// proxy access logs and shell history, and this one is chosen by a person
    /// under pressure rather than generated — so it is likely to be reused
    /// somewhere else too. The cost is that an operator writing the request by
    /// hand has to count bytes for <c>Content-Length</c>; that is the trade the
    /// product takes, and <c>docs/specs/MAINTENANCE.md</c> shows the shell that
    /// counts them.
    /// </para>
    /// </summary>
    public record AdminPasswordInputDto
    {
        public required string Password { get; init; }
    }

    /// <summary>
    /// Which account was changed. <b>Never the password</b>, which the caller
    /// already knows and nothing else should ever see again.
    /// </summary>
    public record AdminPasswordDto
    {
        public required string Username { get; init; }
    }
}
