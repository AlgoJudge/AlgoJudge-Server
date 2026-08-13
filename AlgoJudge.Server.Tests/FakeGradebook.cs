using System.Net;
using System.Text;
using System.Text.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A platform's token endpoint and gradebook, answered in memory.
/// <para>
/// <b>It records what was asked of it</b>, because that is where this
/// integration's traps are: the timestamp on a score, the content type on a line
/// item, and whether <c>/scores</c> landed before or after a query string. A stub
/// that only returned 200 would let all three through.
/// </para>
/// <para>
/// It also behaves like a real one where the behaviour matters — a score whose
/// timestamp is not newer than the last is <b>accepted and ignored</b>, which is
/// what AGS says a platform does and is the reason the tool has to stamp
/// monotonically.
/// </para>
/// </summary>
public sealed class FakeGradebook : HttpMessageHandler
{
    public const string TokenUrl = "https://platform.invalid/mod/lti/token.php";
    public const string LineItemsUrl = "https://platform.invalid/mod/lti/services.php/2/lineitems?type_id=1";

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    /// <summary>The scores the platform ended up holding, per person.</summary>
    public Dictionary<string, (double Score, DateTime At)> Held { get; } = [];

    /// <summary>What every line item request will be answered with.</summary>
    public string LineItemUrl { get; set; } =
        "https://platform.invalid/mod/lti/services.php/2/lineitems/7/lineitem?type_id=1";

    /// <summary>Set to make the gradebook refuse, for the failure paths.</summary>
    public HttpStatusCode? RefuseScoresWith { get; set; }

    /// <summary>How many times a token was minted, to prove the cache works.</summary>
    public int TokensIssued { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

        Requests.Add(request);
        Bodies.Add(body);

        if (url == TokenUrl)
        {
            TokensIssued++;
            return Json("{\"access_token\":\"token-" + TokensIssued + "\",\"expires_in\":3600}");
        }

        if (url.Contains("/scores"))
        {
            if (RefuseScoresWith is { } refusal)
            {
                return new HttpResponseMessage(refusal)
                {
                    Content = new StringContent("the gradebook says no"),
                };
            }

            using var document = JsonDocument.Parse(body);
            var user = document.RootElement.GetProperty("userId").GetString()!;
            var score = document.RootElement.GetProperty("scoreGiven").GetDouble();
            var stamp = document.RootElement.GetProperty("timestamp").GetDateTime();

            // **The real behaviour, and the reason it is worth reproducing.** A
            // platform answers success and changes nothing when the timestamp is
            // not newer than what it holds. A tool that reuses a timestamp on a
            // retry therefore believes it succeeded.
            if (!Held.TryGetValue(user, out var current) || stamp > current.At)
            {
                Held[user] = (score, stamp);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            };
        }

        if (url.Contains("/results"))
        {
            var results = Held.Select(entry => new Dictionary<string, object>
            {
                ["userId"] = entry.Key,
                ["resultScore"] = entry.Value.Score,
                ["resultMaximum"] = 100.0,
            });
            return Json(JsonSerializer.Serialize(results));
        }

        // A line item container query, or the creation that follows it.
        if (request.Method == HttpMethod.Get)
        {
            return Json("[]");
        }

        return Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = LineItemUrl,
            ["scoreMaximum"] = 100.0,
        }));
    }

    private static HttpResponseMessage Json(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    /// <summary>The body of the last request whose URL contains this fragment.</summary>
    public string BodyFor(string fragment)
    {
        for (var i = Requests.Count - 1; i >= 0; i--)
        {
            if (Requests[i].RequestUri!.ToString().Contains(fragment))
            {
                return Bodies[i];
            }
        }
        throw new InvalidOperationException($"nothing was ever sent to a URL containing {fragment}");
    }

    /// <summary>Every URL this gradebook was asked for, in order.</summary>
    public IEnumerable<string> Urls => Requests.Select(r => r.RequestUri!.ToString());
}
