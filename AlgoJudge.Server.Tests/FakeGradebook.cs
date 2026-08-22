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
/// It refuses a stale timestamp <b>the way Moodle does</b>, measured 2026-08-14
/// against 4.5.13, 5.2.2 and 5.3dev: <c>409</c>, with "Refusing score with an
/// earlier timestamp", and the comparison made through <c>strtotime</c> — so it
/// resolves to <b>whole seconds</b>, and two posts inside one second collide.
/// </para>
/// <para>
/// <see cref="DropsStaleSilently"/> switches to the other behaviour §6.4 of
/// `LMS_INTEGRATION.md` describes — accepted, ignored, reported as success. No
/// platform here does that, but the specification allows it and the tool must
/// survive both.
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

    /// <summary>
    /// How many score postings each person has had.
    ///
    /// <para>
    /// <b>Per person, because counting them all does not work.</b> The grade
    /// sweep is global: it walks every pending row in the database, and the
    /// tests share one. So a sweep started by one test posts whatever an earlier
    /// test left behind — through <i>this</i> stub, because it is the handler
    /// registered on the host doing the sweeping. A test counting every
    /// <c>/scores</c> URL is therefore counting other tests' leftovers, and goes
    /// red depending on what ran before it.
    /// </para>
    /// </summary>
    public Dictionary<string, int> Posts { get; } = [];

    /// <summary>What every line item request will be answered with.</summary>
    public string LineItemUrl { get; set; } =
        "https://platform.invalid/mod/lti/services.php/2/lineitems/7/lineitem?type_id=1";

    /// <summary>Set to make the gradebook refuse, for the failure paths.</summary>
    public HttpStatusCode? RefuseScoresWith { get; set; }

    /// <summary>
    /// Accept a stale timestamp and quietly keep the old score, instead of
    /// answering 409. Not what Moodle does; what the specification permits.
    /// </summary>
    public bool DropsStaleSilently { get; set; }

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
            Posts[user] = Posts.GetValueOrDefault(user) + 1;
            var score = document.RootElement.GetProperty("scoreGiven").GetDouble();
            var stamp = document.RootElement.GetProperty("timestamp").GetDateTime();

            // Seconds, because that is the resolution Moodle compares at —
            // `strtotime` on the incoming timestamp against the grade's
            // `timemodified`. Sub-second differences do not exist here.
            var held = Held.TryGetValue(user, out var current);
            var stale = held && Second(stamp) <= Second(current.At);

            if (stale && !DropsStaleSilently)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        "Refusing score with an earlier timestamp for item 1 and user " + user),
                };
            }

            if (!stale)
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

    private static long Second(DateTime value) =>
        value.Ticks / TimeSpan.TicksPerSecond;

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
