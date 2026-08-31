using System.Net.Http.Json;
using System.Net.WebSockets;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The socket endpoint's own loop.
/// <para>
/// Untested until 2026-08-31, and it held a defect that the first frame from any
/// client would trip: <c>PumpAsync</c> asked <c>PeriodicTimer</c> for the next
/// tick while its previous ask was still outstanding, which that type refuses
/// with <c>InvalidOperationException</c> — a type the endpoint's catch does not
/// match, so it escaped and killed the socket. Nothing shipped sends a frame,
/// which is exactly why nobody met it and why it needed a test rather than a
/// reader.
/// </para>
/// </summary>
[Collection("server-2")]
public class EventSocketTests(ServerFixture server)
{
    /// <summary>
    /// The whole defect. The frame is ignored on purpose — what matters is that
    /// the socket is still there afterwards and still carries what it is for.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_frame_from_the_client_is_ignored_and_does_not_kill_the_socket()
    {
        using var socket = await Socket.OpenAsync(
            server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await Socket.SendAsync(socket, """{"type":"whatever"}""");

        // Something the signed-in account is told about, so the assertion is
        // that the socket still delivers rather than merely that it is open.
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        string adminId;
        await using (var context = server.NewContext())
        {
            adminId = (await context.Users.FirstAsync(u => u.UserName == Seeder.AdminLogin)).Id;
        }

        var updated = await admin.PutAsJsonAsync(
            $"/api/v1/users/{adminId}", new { note = "socket-" + Guid.NewGuid().ToString("N")[..8] });
        await Sign.Succeeded(updated);

        var frame = await Socket.ReadAsync(socket, EventTypes.UserChanged, TimeSpan.FromSeconds(20));
        Assert.Equal(EventTypes.UserChanged, frame.GetProperty("type").GetString());

        Assert.Equal(WebSocketState.Open, socket.State);
    }

    /// <summary>
    /// A socket nobody is signed in for is refused at the handshake, which is
    /// where the Client already expects it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_socket_nobody_is_signed_in_for_is_refused()
    {
        var ws = server.Server.CreateWebSocketClient();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ws.ConnectAsync(new Uri(server.Server.BaseAddress, "api/v1/ws"), default));
    }
}
