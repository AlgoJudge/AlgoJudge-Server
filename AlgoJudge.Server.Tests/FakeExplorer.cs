using System.Net;
using System.Text;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The problem archive's token endpoint, shaped from the UVa Explorer Web 0.2.0
/// integration document: <c>POST /api/access/token</c>, no body, the long-lived
/// key as a bearer, and a <c>201</c> carrying an hourly token.
/// </summary>
public sealed class FakeExplorer : HttpMessageHandler
{
    public const string Origin = "https://explorer.invalid";
    public const string Token = "uexplt_short_lived";

    /// <summary>Every bearer that arrived, so a test can read what was sent.</summary>
    public List<string?> Bearers { get; } = [];

    /// <summary>Every path asked for, so a test can assert the endpoint rather than assume it.</summary>
    public List<string> Paths { get; } = [];

    /// <summary>What the archive will answer. Anything but 201 is a refusal.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

    /// <summary>Set to answer with this instead of the ordinary body.</summary>
    public string? Body { get; set; }

    /// <summary>Set to fail the connection outright, as an unreachable host does.</summary>
    public bool Unreachable { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Bearers.Add(request.Headers.Authorization?.Parameter);
        Paths.Add(request.RequestUri?.AbsolutePath ?? "");

        if (Unreachable) throw new HttpRequestException("the archive is not answering");

        var body = Body ?? $$"""
            {
              "accessToken": "{{Token}}",
              "tokenType": "Bearer",
              "expiresAt": "2026-08-26T13:00:00.000Z",
              "expiresIn": 3600,
              "features": { "privateDataset": true, "ai": true }
            }
            """;

        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
