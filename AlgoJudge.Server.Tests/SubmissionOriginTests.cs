using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a submission remembers about where it was sent from, and who may read
/// it.
/// <para>
/// The question all of this answers is a judge's: <b>was this solution sent
/// from outside the examination room?</b> Which is why the address is stored as
/// an address rather than hashed, and why it is on the submission rather than
/// reached through the session — <c>submission:read:all</c> is already scoped
/// per activity, and the session list answers to a system-scope permission.
/// </para>
/// </summary>
[Collection("server-1")]
public class SubmissionOriginTests(ServerFixture server)
{
    /// <summary>
    /// A submission says where it came from, and the address is one a network
    /// query can find.
    /// </summary>
    [Fact]
    public async Task A_submission_records_the_address_it_arrived_from()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var id = Guid.Parse(submitted.GetProperty("id").GetString()!);

        await using var context = server.NewContext();
        var submission = await context.Submissions.FirstAsync(s => s.Id == id);

        Assert.NotNull(submission.IpAddress);
        // Un-mapped, so it is comparable with a network anybody writes down.
        // `TestServer` reports loopback, which is inside 127.0.0.0/8 — the
        // assertion is about the family and the shape, not about the value.
        Assert.Equal(
            System.Net.Sockets.AddressFamily.InterNetwork,
            submission.IpAddress!.AddressFamily);

        var inside = await context.Database
            .SqlQueryRaw<Guid>(
                """SELECT "Id" AS "Value" FROM "Submissions" WHERE "Id" = {0} AND "IpAddress" <<= '127.0.0.0/8'::inet""",
                id)
            .ToListAsync();
        Assert.Single(inside);
    }

    /// <summary>
    /// And which browser sent it, when the browser said.
    /// </summary>
    [Fact]
    public async Task A_submission_records_the_browser_and_the_session()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var device = Guid.NewGuid();
        participant.DefaultRequestHeaders.Add("Device-Id", device.ToString());

        var submitted = await Build.SubmitAsync(participant, slug, "print(2)\n");
        var id = Guid.Parse(submitted.GetProperty("id").GetString()!);

        await using var context = server.NewContext();
        var submission = await context.Submissions.FirstAsync(s => s.Id == id);

        Assert.Equal(device, submission.DeviceId);
        // The cookie exists by now: signing in and enrolling came first, which
        // is what a browser does too. It is null only on the very first
        // authenticated request a new browser makes, because the cookie is
        // minted after the response.
        Assert.NotNull(submission.SessionId);
    }

    /// <summary>
    /// A judge reads it in the detail, and finds nothing in the list.
    /// <para>
    /// <b>Both halves are the assertion.</b> A column of addresses across two
    /// hundred rows is exposure for a question nobody asked of most of them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_judge_reads_it_in_the_detail_and_not_in_the_list()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(3)\n");
        var id = submitted.GetProperty("id").GetString()!;

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/submissions/{id}");
        Assert.False(string.IsNullOrEmpty(detail.GetProperty("ipAddress").GetString()));

        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/v1/submissions");
        var row = listed.GetProperty("items").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == id);
        Assert.False(row.TryGetProperty("ipAddress", out _), "the list carries no address");
    }

    /// <summary>
    /// Erasing an account leaves the work and takes where it was sent from.
    /// <para>
    /// A submission survives erasure by design — it is somebody's mark in a
    /// contest — but the address is a fact about the person rather than about
    /// the work.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Erasing_an_account_leaves_the_submission_and_takes_its_origin()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = (await participant.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;
        var submitted = await Build.SubmitAsync(participant, slug, "print(4)\n");
        var id = Guid.Parse(submitted.GetProperty("id").GetString()!);

        using var scope = server.Services.CreateScope();
        var deletion = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
        await using (var context = scope.ServiceProvider
            .GetRequiredService<Database.ApplicationDbContext>())
        {
            var user = await context.Users.FirstAsync(u => u.Id == who);
            await deletion.AnonymiseAsync(user, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        await using (var context = server.NewContext())
        {
            var submission = await context.Submissions.FirstOrDefaultAsync(s => s.Id == id);
            Assert.NotNull(submission);
            Assert.Null(submission!.IpAddress);
            Assert.Null(submission.DeviceId);
        }
    }

    /// <summary>
    /// Past a year the origin goes and the submission stays.
    /// </summary>
    [Fact]
    public async Task A_submission_past_its_window_keeps_its_row_and_loses_its_origin()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var stale = Guid.Parse(
            (await Build.SubmitAsync(participant, slug, "print(5)\n"))
                .GetProperty("id").GetString()!);
        var fresh = Guid.Parse(
            (await Build.SubmitAsync(participant, slug, "print(6)\n"))
                .GetProperty("id").GetString()!);

        await using (var context = server.NewContext())
        {
            var old = await context.Submissions.FirstAsync(s => s.Id == stale);
            old.CreatedDate = DateTime.UtcNow.AddDays(-400);
            await context.SaveChangesAsync();
        }

        await server.Services.GetRequiredService<AddressSweeper>()
            .SweepSubmissionsAsync(CancellationToken.None);

        await using (var context = server.NewContext())
        {
            var swept = await context.Submissions.FirstAsync(s => s.Id == stale);
            Assert.Null(swept.IpAddress);
            Assert.Null(swept.DeviceId);
            // The session stays: once the session's own fields have been swept
            // it names nothing about a person, and it still answers "these came
            // from one browser session".
            Assert.NotNull(swept.SessionId);

            // One inside its window is untouched, or the sweep is just a delete.
            var kept = await context.Submissions.FirstAsync(s => s.Id == fresh);
            Assert.NotNull(kept.IpAddress);
        }
    }
}
