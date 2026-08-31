using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Opening the event socket as somebody.
/// <para>
/// The suite had no WebSocket client at all until 2026-08-31, which is why the
/// endpoint's own loop went untested and carried a defect that any client frame
/// would have hit. Sibling to <see cref="Sign"/> and <see cref="Build"/>.
/// </para>
/// </summary>
public static class Socket
{
    public static async Task<WebSocket> OpenAsync(
        ServerFixture server, string login, string password, CancellationToken ct = default)
    {
        // **The cookie is lifted by hand.** `Sign.InAsync` leaves it inside the
        // factory's own handler, and the WebSocket client is a different client
        // with a different jar — so the sign-in is done here, without cookie
        // handling, purely to read `Set-Cookie` off the response.
        var client = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true", new { email = login, password }, ct);
        await Sign.Succeeded(response);

        var cookies = string.Join(
            "; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var ws = server.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers["Cookie"] = cookies;

        // Under the path base, like everything else this Server serves.
        return await ws.ConnectAsync(new Uri(server.Server.BaseAddress, "api/v1/ws"), ct);
    }

    /// <summary>Whatever the Client would send, which in the product is nothing.</summary>
    public static Task SendAsync(WebSocket socket, string text, CancellationToken ct = default) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>
    /// Reads frames until one carries <paramref name="type"/>, or gives up.
    /// <para>
    /// Gives up by throwing, so a test that waits for an event which never
    /// arrives fails on its own deadline rather than on the runner's.
    /// </para>
    /// </summary>
    public static async Task<JsonElement> ReadAsync(WebSocket socket, string type, TimeSpan within)
    {
        using var deadline = new CancellationTokenSource(within);
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, deadline.Token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Xunit.Sdk.XunitException(
                    $"the socket closed while waiting for {type}: {result.CloseStatus}");
            }

            var frame = JsonSerializer.Deserialize<JsonElement>(
                Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (frame.GetProperty("type").GetString() == type) return frame;
        }
    }
}
