namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// A refusal the Client understands.
    /// <para>
    /// Every one of these becomes an <c>application/problem+json</c> body
    /// (RFC 9457) carrying a <b>stable <see cref="Code"/></b>. The status alone
    /// is not enough: the Client tells a rejected value from a name already taken
    /// by the code, and a 409 with no code leaves it able to say only "that did
    /// not work". See <c>AlgoJudge-Client/src/api/ApiError.ts</c>.
    /// </para>
    /// </summary>
    public abstract class ApiException(int status, string message, string? code = null) : Exception(message)
    {
        public int Status { get; } = status;

        /// <summary>Machine-readable and stable across releases. The Client switches on it.</summary>
        public string? Code { get; } = code;

        /// <summary>Per-field messages, for a validation failure.</summary>
        public IDictionary<string, string[]>? Fields { get; init; }
    }

    /// <summary>Nobody is signed in. The Client drops its session and stops asking.</summary>
    public class UnauthenticatedException(string message = "Authentication is required")
        : ApiException(StatusCodes.Status401Unauthorized, message, "unauthenticated");

    /// <summary>
    /// Signed in, and not allowed.
    /// <para>
    /// Carries the permission that was missing. That is for the log and for a
    /// developer — <see cref="AccessDeniedException"/> is thrown by
    /// <c>PermissionService.RequireAsync</c>, and knowing which check failed is
    /// the difference between a five-minute diagnosis and an afternoon.
    /// </para>
    /// </summary>
    public class AccessDeniedException(string? permission = null)
        : ApiException(StatusCodes.Status403Forbidden,
            permission is null ? "Access denied" : $"Access denied: {permission} is required",
            "forbidden")
    {
        public string? Permission { get; } = permission;
    }

    /// <summary>
    /// Signed in, allowed in principle, and refused because of <b>when</b> or
    /// <b>what</b> — a closed round, a language the activity does not accept, a
    /// submission ceiling already reached.
    /// <para>
    /// Distinct from <see cref="AccessDeniedException"/>: that one is about a
    /// permission somebody does not hold and is fixed by granting it. This one
    /// no grant would change, and the Client shows the two differently.
    /// </para>
    /// </summary>
    public class ForbiddenActionException(string message, string code)
        : ApiException(StatusCodes.Status403Forbidden, message, code);

    /// <summary>
    /// It is not there — or it is there and this caller may not know that.
    /// <para>
    /// Both cases answer the same way on purpose. A problem in a series that has
    /// not opened answers 404 rather than 403, because a guessable address that
    /// answered 403 would confirm the problem exists and how many there are.
    /// </para>
    /// </summary>
    public class NotFoundException(string what)
        : ApiException(StatusCodes.Status404NotFound, $"{what} does not exist", "not_found");

    /// <summary>
    /// Understood, well formed, and refused because of what is already there —
    /// a slug in use, an assignment that has submissions. Separate from
    /// validation because the caller's input was not wrong.
    /// </summary>
    public class ConflictException(string message, string? code = null)
        : ApiException(StatusCodes.Status409Conflict, message, code ?? "conflict");

    /// <summary>The input was wrong. <see cref="ApiException.Fields"/> says where, when it can.</summary>
    public class ValidationException(string message, string? code = null)
        : ApiException(StatusCodes.Status422UnprocessableEntity, message, code ?? "validation_failed");

    /// <summary>
    /// The bytes did not match the checksum that came with them.
    /// <para>
    /// Its own type because its remedy is its own: not "correct the form" but
    /// "send it again". The Client has a matching class keyed on exactly this
    /// code string.
    /// </para>
    /// </summary>
    public class ChecksumMismatchException(string message = "The file did not match its checksum and was not stored")
        : ApiException(StatusCodes.Status422UnprocessableEntity, message, "checksum_mismatch");

    /// <summary>
    /// The Server is up and declining service.
    /// <para>
    /// **The first 5xx in this hierarchy, and it is not a fault.** Every other
    /// entry here says the caller got something wrong; this one says the Server
    /// did something deliberate. That distinction is why it carries a code the
    /// Runner and the Client can switch on: a 503 from a proxy whose upstream
    /// died looks identical on the wire, and only the code tells "come back in a
    /// minute" from "something is broken".
    /// </para>
    /// <para>
    /// <see cref="RetryAfterSeconds"/> becomes the header of the same name. It
    /// is a hint, not a promise — the handler writes it so that a caller with no
    /// backoff of its own still does something sensible.
    /// </para>
    /// </summary>
    public class ServiceUnavailableException(
        string message = "The Server is not accepting requests at the moment",
        int retryAfterSeconds = 30)
        : ApiException(StatusCodes.Status503ServiceUnavailable, message, "server.maintenance")
    {
        public int RetryAfterSeconds { get; } = retryAfterSeconds;
    }

    /// <summary>The request body was larger than the ceiling that applies to it.</summary>
    public class PayloadTooLargeException(long limitBytes)
        : ApiException(StatusCodes.Status413PayloadTooLarge,
            $"The upload is larger than the {limitBytes} byte limit", "payload_too_large")
    {
        public long LimitBytes { get; } = limitBytes;
    }
}
