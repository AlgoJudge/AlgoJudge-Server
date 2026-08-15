using System.Net;
using System.Text;
using System.Text.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A platform's roster service, answering whatever a test needs it to.
///
/// <para>
/// <b>Shaped from a real one.</b> The fields and their spelling — including
/// Moodle's non-standard <c>ext_user_username</c> and its short role names —
/// come from a roster measured against Moodle 5.2.2 on 2026-08-15, recorded in
/// <c>AlgoJudge-Moodle/docs/FINDINGS.md</c>. A fake invented from the
/// specification alone would have carried the username in a place no platform
/// puts it, and every test built on it would have agreed with itself.
/// </para>
/// </summary>
public sealed class FakeRoster : HttpMessageHandler
{
    public const string TokenUrl = "https://platform.invalid/mod/lti/token.php";
    public const string MembershipsUrl =
        "https://platform.invalid/mod/lti/services.php/CourseSection/2/bindings/1/memberships";

    /// <summary>Every request that reached it, so a test can assert on `rlid`.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>The people this course holds. Written by the test.</summary>
    public List<object> Members { get; set; } = [];

    /// <summary>
    /// A second page, where the test is about paging. The first response then
    /// carries a `Link` header pointing at it — which is where paging lives, not
    /// in the body.
    /// </summary>
    public List<object>? SecondPage { get; set; }

    /// <summary>Builds one member the way Moodle describes one.</summary>
    public static object Member(
        string subject,
        string? username = null,
        string? name = null,
        string? email = null,
        string status = "Active",
        params string[] roles) =>
        new Dictionary<string, object?>
        {
            ["user_id"] = subject,
            ["roles"] = roles.Length > 0 ? roles : ["Learner"],
            ["status"] = status,
            ["name"] = name,
            ["email"] = email,
            ["ext_user_username"] = username,
        }.Where(pair => pair.Value is not null)
         .ToDictionary(pair => pair.Key, pair => pair.Value!);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);

        // **Any token endpoint, not only its own.** This fake stands in for one
        // whole platform, and a `FakePlatform` mints its own issuer per test —
        // so matching the constant alone left the token request unanswered and
        // the failure read as the roster being unreachable.
        if (url.Contains("/token.php", StringComparison.Ordinal))
        {
            return Json("""{"access_token":"roster-token","token_type":"Bearer","expires_in":3600}""");
        }

        if (!url.StartsWith(MembershipsUrl, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var second = url.Contains("page=2", StringComparison.Ordinal);
        var members = second ? SecondPage ?? [] : Members;

        var body = JsonSerializer.Serialize(new
        {
            id = url,
            context = new { id = "2", title = "A course" },
            members,
        });

        var response = Json(body);
        if (!second && SecondPage is not null)
        {
            response.Headers.TryAddWithoutValidation(
                "Link", $"<{MembershipsUrl}?page=2>; rel=\"next\"");
        }

        await Task.CompletedTask;
        return response;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
