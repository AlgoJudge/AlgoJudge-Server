using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Services
{
    /// <summary>What a stored key was exchanged for, and until when.</summary>
    public record MintedCredential(string Value, DateTime ExpiresAt);

    /// <summary>
    /// Exchanges a stored long-lived key for a short-lived one, where the
    /// service behind the name offers that.
    /// <para>
    /// <b>Why this exists.</b> A stored key used to be handed to a manager's
    /// browser whole, and the picker put it in the iframe address. The
    /// credential that crosses is now one that dies within the hour, and the
    /// long-lived key never leaves this process.
    /// </para>
    /// <para>
    /// <b>Nothing is cached.</b> One call mints one credential, and the caller
    /// asks again when it expires. A cache shared between managers would add a
    /// state that is wrong for a few minutes near every expiry, and would not be
    /// shared anyway once this Server runs as more than one process.
    /// </para>
    /// </summary>
    public interface IAccessKeyMinting
    {
        /// <summary>Whether a key of this name is handed out as a short-lived credential.</summary>
        bool Mints(string name);

        /// <summary>
        /// Exchanges the stored key. Throws <see cref="UpstreamException"/> when
        /// the far end refuses or does not answer — <b>never falls back to the
        /// stored key</b>, which would put it back in a browser with nobody
        /// noticing.
        /// </summary>
        Task<MintedCredential> MintAsync(string name, string storedKey, CancellationToken ct);
    }

    public class AccessKeyMinting(
        IHttpClientFactory clients,
        IConfiguration configuration,
        TimeProvider clock
    ) : IAccessKeyMinting
    {
        /// <summary>
        /// Where the problem picker's tokens come from.
        /// <para>
        /// <b>A literal, and the exception is written down rather than left to be
        /// noticed.</b> <c>Permissions.cs</c> says the reserved-archive list is
        /// configuration because the Server must not learn the name of any
        /// particular archive. This is that knowledge. It does not cross a line
        /// that was intact — the permission gate in <c>PanelController</c> has
        /// switched on this same name since access keys arrived — and
        /// <c>UvaExplorer__Origin</c> moves it for a self-hosted deployment.
        /// </para>
        /// </summary>
        private const string DefaultOrigin = "https://uvaexplorer.algojudge.app";

        public const string OriginSetting = "UvaExplorer:Origin";

        public bool Mints(string name) => Endpoint(name) is not null;

        private Uri? Endpoint(string name)
        {
            if (name != "uvaexplorer") return null;

            var origin = configuration[OriginSetting]?.Trim();
            var root = string.IsNullOrEmpty(origin) ? DefaultOrigin : origin;
            return new Uri(new Uri(root.TrimEnd('/') + "/"), "api/access/token");
        }

        public async Task<MintedCredential> MintAsync(
            string name, string storedKey, CancellationToken ct)
        {
            var endpoint = Endpoint(name)
                ?? throw new InvalidOperationException($"{name} is not minted; call Mints first");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", storedKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await clients.CreateClient(nameof(AccessKeyMinting))
                    .SendAsync(request, ct);
            }
            // A cancelled request is the caller leaving, not the far end failing.
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      && !ct.IsCancellationRequested)
            {
                throw Unreachable();
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode) throw Refusal(response.StatusCode);

                var body = await response.Content.ReadAsStringAsync(ct);
                return Read(body);
            }
        }

        /// <summary>
        /// Reads the answer, and refuses anything it cannot make a credential of.
        /// <para>
        /// A blank token or an unreadable body is the same outcome as a refusal:
        /// there is nothing to hand over. Returning one anyway would send the
        /// browser a credential the picker cannot use, and the failure would show
        /// up as an empty archive rather than as a message.
        /// </para>
        /// </summary>
        private MintedCredential Read(string body)
        {
            JsonElement json;
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch (JsonException)
            {
                throw Unreachable();
            }

            if (json.ValueKind is not JsonValueKind.Object
                || !json.TryGetProperty("accessToken", out var token)
                || token.ValueKind is not JsonValueKind.String
                || token.GetString() is not { Length: > 0 } value)
            {
                throw Unreachable();
            }

            return new MintedCredential(value, ExpiryOf(json));
        }

        /// <summary>
        /// When the credential dies, from the instant the answer states.
        /// <para>
        /// <c>expiresIn</c> is the fallback rather than the first choice: it is
        /// measured from when the far end answered, and by the time this is read
        /// some of it has been spent in transit. An absolute instant does not
        /// drift.
        /// </para>
        /// </summary>
        private DateTime ExpiryOf(JsonElement json)
        {
            if (json.TryGetProperty("expiresAt", out var at)
                && at.ValueKind is JsonValueKind.String
                && DateTime.TryParse(at.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }

            var seconds = json.TryGetProperty("expiresIn", out var give)
                && give.ValueKind is JsonValueKind.Number
                && give.TryGetInt32(out var value) && value > 0
                ? value
                : 3600;

            return clock.GetUtcNow().UtcDateTime.AddSeconds(seconds);
        }

        private static UpstreamException Unreachable() => new(
            "The problem archive did not answer with a usable credential",
            "accessKey.mintFailed");

        private static UpstreamException Refusal(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => new UpstreamException(
                "The problem archive rejected this installation's key", "accessKey.rejected"),
            HttpStatusCode.Forbidden => new UpstreamException(
                "The problem archive refused this installation's address", "accessKey.originRefused"),
            // Not a fault and worth waiting out, so it is the one that says
            // "later" rather than "broken".
            HttpStatusCode.TooManyRequests => new UpstreamException(
                "The problem archive has no credential to spare right now",
                "accessKey.tokenLimit", StatusCodes.Status503ServiceUnavailable),
            _ => Unreachable(),
        };
    }
}
