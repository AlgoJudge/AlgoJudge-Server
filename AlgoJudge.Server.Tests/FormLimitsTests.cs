using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a form may carry.
/// <para>
/// <c>Program</c> used to set <c>FormOptions.ValueLengthLimit</c> to
/// <c>int.MaxValue</c>, uncommented and unexplained, while the request body
/// ceiling stayed at 128 MB for the sake of problem packages. The two LTI
/// endpoints that read a form are anonymous and read it <b>before</b> anything
/// authenticates, and <c>ReadFormAsync</c> materialises every value as a string
/// — so one unauthenticated request could ask the Server to hold a quarter of a
/// gigabyte, and nothing in this product rate-limits.
/// </para>
/// </summary>
[Collection("server-2")]
public class FormLimitsTests(ServerFixture server)
{
    [Fact]
    public async Task A_form_value_larger_than_the_framework_allows_is_refused_rather_than_buffered()
    {
        var client = server.CreateClient();

        // Above the framework's 4 MB default and far below the 128 MB body
        // ceiling, so the only thing that can refuse this is the value limit.
        using var content = new StringContent(
            "iss=" + new string('x', 8 * 1024 * 1024),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        var response = await client.PostAsync("/api/v1/lti/login", content);
        var body = await response.Content.ReadAsStringAsync();

        // With the body, not just the status: what it refuses with is the half
        // that says whether it was refused for the right reason.
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {body}");

        // **The number is the assertion.** Model binding reads the form before
        // the action does and reports which limit stopped it, so this pins the
        // framework's 4 MiB default being in force — which is exactly what
        // `ValueLengthLimit = int.MaxValue` removed. Asserting the status alone
        // would pass just as happily against a Server that had buffered all 8 MB
        // and then refused for some other reason.
        Assert.True(body.Contains("value length limit 4194304"), body);
    }

    /// <summary>
    /// The guard above must not have closed the door on a real launch: an
    /// `id_token` is a signed JWT and is the largest thing this endpoint
    /// legitimately receives.
    /// </summary>
    [Fact]
    public async Task An_ordinary_launch_form_is_still_read()
    {
        var client = server.CreateClient();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["iss"] = "https://moodle.algojudge.invalid",
            ["login_hint"] = new string('h', 64 * 1024),
        });

        var response = await client.PostAsync("/api/v1/lti/login", content);

        // Whatever the launch itself answers, it is not the form parser refusing.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
