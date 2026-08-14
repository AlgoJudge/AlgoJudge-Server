using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Lti.Data;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>Where a launch said its gradebook lives.</summary>
    public record AgsEndpoint(string? LineItems, string? LineItem, IReadOnlyList<string> Scopes)
    {
        /// <summary>
        /// Reads the claim, tolerating a platform that sends only the one line
        /// item this link is bound to rather than the container.
        /// </summary>
        public static AgsEndpoint? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                return new AgsEndpoint(
                    root.TryGetProperty("lineitems", out var many) ? many.GetString() : null,
                    root.TryGetProperty("lineitem", out var one) ? one.GetString() : null,
                    root.TryGetProperty("scope", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                        ? scopes.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String)
                            .Select(s => s.GetString()!).ToList()
                        : []);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>What the platform currently holds for one person in one column.</summary>
    public record AgsResult(string UserId, double? ResultScore, double? ResultMaximum);

    public interface IAgsClient
    {
        /// <summary>
        /// The line item for this assignment, created if the platform does not
        /// already have one. Returns its URL.
        /// </summary>
        Task<string> EnsureLineItemAsync(
            Platform platform, string lineItemsUrl, string resourceLinkId,
            string resourceId, string label, double scoreMaximum, CancellationToken ct);

        /// <summary>Posts one score. The timestamp is the caller's, and must rise.</summary>
        Task PostScoreAsync(
            Platform platform, string lineItemUrl, string subject,
            double score, double scoreMaximum, DateTime timestamp,
            bool graded, CancellationToken ct);

        /// <summary>What the platform holds, for the verifier.</summary>
        Task<IReadOnlyList<AgsResult>> ReadResultsAsync(
            Platform platform, string lineItemUrl, CancellationToken ct);
    }

    /// <summary>
    /// Assignment and Grade Services, over HTTP.
    /// <para>
    /// Three media types and none of them is <c>application/json</c>: AGS
    /// identifies its payloads by content type, and a platform answers 400 to the
    /// generic one. They are spelled out here because that failure reads as "the
    /// tool sent rubbish" rather than as a header.
    /// </para>
    /// </summary>
    public class AgsClient(IHttpClientFactory clients, IPlatformTokens tokens) : IAgsClient
    {
        private const string LineItemType = "application/vnd.ims.lis.v2.lineitem+json";
        private const string LineItemContainerType = "application/vnd.ims.lis.v2.lineitemcontainer+json";
        private const string ScoreType = "application/vnd.ims.lis.v1.score+json";
        private const string ResultContainerType = "application/vnd.ims.lis.v2.resultcontainer+json";

        public async Task<string> EnsureLineItemAsync(
            Platform platform, string lineItemsUrl, string resourceLinkId,
            string resourceId, string label, double scoreMaximum, CancellationToken ct)
        {
            var http = await AuthorisedAsync(platform, ct);

            // Asked for by `resourceId` first. A platform that already has our
            // column returns it, and creating a second one for the same
            // assignment would give the course two columns that disagree.
            var query = lineItemsUrl
                + (lineItemsUrl.Contains('?') ? '&' : '?')
                + "resource_link_id=" + Uri.EscapeDataString(resourceLinkId)
                + "&resource_id=" + Uri.EscapeDataString(resourceId);

            using var lookup = new HttpRequestMessage(HttpMethod.Get, query);
            lookup.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LineItemContainerType));

            var existing = await SendAsync(http, lookup, ct);
            if (existing.IsSuccessStatusCode)
            {
                var found = FirstId(await existing.Content.ReadAsStringAsync(ct));
                if (found is not null)
                {
                    return found;
                }
            }

            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["scoreMaximum"] = scoreMaximum,
                ["label"] = label,
                // **What lets this column be found again**, and what §6.5 asks
                // for: our own identifier for the assignment, carried by the
                // platform and returned with every result.
                ["resourceId"] = resourceId,
                ["resourceLinkId"] = resourceLinkId,
                ["tag"] = "algojudge",
            });

            using var create = new HttpRequestMessage(HttpMethod.Post, lineItemsUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, LineItemType),
            };
            create.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LineItemType));

            var created = await SendAsync(http, create, ct);
            var payload = await created.Content.ReadAsStringAsync(ct);

            if (!created.IsSuccessStatusCode)
            {
                throw new AgsException(
                    $"The platform refused a line item ({(int)created.StatusCode}): {Trim(payload)}");
            }

            return Id(payload)
                ?? throw new AgsException("The platform created a line item and returned no id");
        }

        public async Task PostScoreAsync(
            Platform platform, string lineItemUrl, string subject,
            double score, double scoreMaximum, DateTime timestamp,
            bool graded, CancellationToken ct)
        {
            var http = await AuthorisedAsync(platform, ct);

            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["userId"] = subject,
                ["scoreGiven"] = score,
                ["scoreMaximum"] = scoreMaximum,
                // **The trap, and it is silent** (§6.4). A platform rejects a
                // score whose timestamp is not newer than the one it holds — by
                // answering success and changing nothing. The caller stamps this
                // monotonically per person per column, which is the only reason a
                // retry does anything at all.
                ["timestamp"] = timestamp.ToUniversalTime().ToString("O"),
                // §6.5, both adopted. Together they are what makes Moodle show
                // "submitted, awaiting a grade" instead of an empty cell that
                // reads as "nothing was handed in".
                ["activityProgress"] = "Completed",
                ["gradingProgress"] = graded ? "FullyGraded" : "Pending",
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, Scores(lineItemUrl))
            {
                Content = new StringContent(body, Encoding.UTF8, ScoreType),
            };

            var response = await SendAsync(http, request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new AgsException(
                    $"The platform refused a score ({(int)response.StatusCode}): "
                    + Trim(await response.Content.ReadAsStringAsync(ct)));
            }
        }

        public async Task<IReadOnlyList<AgsResult>> ReadResultsAsync(
            Platform platform, string lineItemUrl, CancellationToken ct)
        {
            var http = await AuthorisedAsync(platform, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, Results(lineItemUrl));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ResultContainerType));

            var response = await SendAsync(http, request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new AgsException(
                    $"The platform refused to show its results ({(int)response.StatusCode}): {Trim(payload)}");
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                return document.RootElement.EnumerateArray()
                    .Select(item => new AgsResult(
                        item.TryGetProperty("userId", out var user) ? user.GetString() ?? "" : "",
                        item.TryGetProperty("resultScore", out var score) && score.TryGetDouble(out var s)
                            ? s : null,
                        item.TryGetProperty("resultMaximum", out var max) && max.TryGetDouble(out var m)
                            ? m : null))
                    .Where(r => r.UserId.Length > 0)
                    .ToList();
            }
            catch (JsonException)
            {
                throw new AgsException("The platform's results were not readable JSON");
            }
        }

        /// <summary>
        /// <c>/scores</c> goes on the line item's path and <b>before its query
        /// string</b>. Moodle's line item URLs carry their identifiers as query
        /// parameters, so appending naively produces a URL that 404s while
        /// looking correct.
        /// </summary>
        private static string Scores(string lineItemUrl) => WithSuffix(lineItemUrl, "/scores");

        private static string Results(string lineItemUrl) => WithSuffix(lineItemUrl, "/results");

        private static string WithSuffix(string url, string suffix)
        {
            var split = url.IndexOf('?');
            return split < 0 ? url + suffix : url[..split] + suffix + url[split..];
        }

        private async Task<HttpClient> AuthorisedAsync(Platform platform, CancellationToken ct)
        {
            var http = clients.CreateClient(nameof(AgsClient));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await tokens.GetAsync(platform, AgsScopes.All, ct));
            return http;
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpClient http, HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await http.SendAsync(request, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new AgsException($"The platform could not be reached at {request.RequestUri}");
            }
        }

        private static string? Id(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? FirstId(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id)) return id.GetString();
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Trim(string body) => body.Length <= 300 ? body : body[..300] + "…";
    }

    /// <summary>
    /// A platform refusing something in the gradebook. Carries the platform's own
    /// words, because "synchronisation failed" is not something an operator can
    /// act on.
    /// </summary>
    public class AgsException(string message) : Exception(message);
}
