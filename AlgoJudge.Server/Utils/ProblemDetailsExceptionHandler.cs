using System.Text.Json;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// One place where a thrown refusal becomes a response.
    /// <para>
    /// An <see cref="IExceptionHandler"/> rather than an MVC action filter, so it
    /// covers the minimal-API endpoints and the WebSocket handshake as well as
    /// the controllers — a filter would leave <c>/identity/*</c> and the socket
    /// answering in a different shape from everything else.
    /// </para>
    /// <para>
    /// The body is RFC 9457 <c>application/problem+json</c> with a stable
    /// <c>code</c> member, which is what
    /// <c>AlgoJudge-Client/src/api/ApiError.ts</c> reads to tell a rejected value
    /// from a name already taken.
    /// </para>
    /// </summary>
    public class ProblemDetailsExceptionHandler(
        IProblemDetailsService problems,
        ILogger<ProblemDetailsExceptionHandler> logger
    ) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext http, Exception exception, CancellationToken ct)
        {
            var (status, title, code, fields) = Describe(exception);

            // An expected refusal is not an incident. Logging a 404 at Error
            // level trains everybody to ignore the error log, which is how a real
            // fault goes unnoticed.
            if (status >= StatusCodes.Status500InternalServerError)
            {
                logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                    http.Request.Method, http.Request.Path);
            }
            else
            {
                logger.LogDebug("{Status} on {Method} {Path}: {Message}",
                    status, http.Request.Method, http.Request.Path, exception.Message);
            }

            http.Response.StatusCode = status;

            var details = new ProblemDetails
            {
                Status = status,
                Title = title,
                // The message is safe to show for our own refusals; for anything
                // unexpected it is not, because it may carry a connection string
                // or a file path.
                Detail = status >= StatusCodes.Status500InternalServerError ? null : exception.Message,
            };
            if (code is not null) details.Extensions["code"] = code;
            if (fields is not null) details.Extensions["errors"] = fields;

            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                Exception = exception,
                ProblemDetails = details,
            });
        }

        private static (int Status, string Title, string? Code, IDictionary<string, string[]>? Fields) Describe(
            Exception exception) => exception switch
        {
            ApiException api => (api.Status, TitleFor(api.Status), api.Code, api.Fields),

            // A cancelled request is the caller hanging up, not a fault. 499 is
            // not in the RFC but is what every proxy logs it as, and it keeps
            // these out of the 5xx count that pages somebody.
            OperationCanceledException => (499, "Client Closed Request", "cancelled", null),

            BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status413PayloadTooLarge =>
                (StatusCodes.Status413PayloadTooLarge, "Payload Too Large", "payload_too_large", null),

            JsonException => (StatusCodes.Status400BadRequest, "Bad Request", "malformed_json", null),

            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error", null, null),
        };

        private static string TitleFor(int status) => status switch
        {
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "Not Found",
            StatusCodes.Status409Conflict => "Conflict",
            StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
            StatusCodes.Status422UnprocessableEntity => "Unprocessable Content",
            _ => "Error",
        };
    }
}
