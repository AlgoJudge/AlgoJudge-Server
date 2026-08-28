using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Taking the installation off the air without stopping it.
///
/// <para>
/// The rules worth defending are the ones an operator cannot see from a screen:
/// that the switch answers only to this machine, that a Runner halfway through
/// somebody's work can still hand the answer in, and that <c>/health</c> never
/// stops answering — because it is both the way back and the thing that keeps
/// the container alive.
/// </para>
/// </summary>
[Collection("server-1")]
public class MaintenanceTests(ServerFixture server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Nobody real, so no account's trial ceiling is spent by these.</summary>
    private const string Nobody = "maintenance-test";

    // ── holding the level still ──────────────────────────────────────────────

    /// <summary>
    /// Back to open, and both queues empty of anything this file left.
    ///
    /// <para>
    /// These share one database, and a Server left draining would fail every
    /// test after it for a reason that has nothing to do with what that test
    /// was checking.
    /// </para>
    /// </summary>
    private async Task OpenAsync()
    {
        await using var context = server.NewContext();

        context.Trials.RemoveRange(await context.Trials.Where(t => t.UserId == Nobody).ToListAsync());

        var state = await context.Maintenance.FirstOrDefaultAsync();
        if (state is not null)
        {
            state.Level = MaintenanceLevel.Open;
            state.RequestedAt = null;
            state.Reason = null;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Puts one running trial on the queue, and with it holds the level at
    /// <c>draining</c> for the length of a test.
    ///
    /// <para>
    /// <b>Not decoration.</b> <see cref="MaintenanceDrainer"/> is a hosted
    /// service here as it is in production, ticking every five seconds against
    /// the real clock — so a test that threw the switch and left both queues
    /// quiet would be closed underneath itself, somewhere between two of its own
    /// assertions, a few runs in every hundred. Work in flight is what the level
    /// waits for, so work in flight is what holds it still.
    /// </para>
    /// </summary>
    private async Task BusyAsync()
    {
        await using var context = server.NewContext();
        context.Trials.Add(new Trial
        {
            UserId = Nobody,
            ProblemType = "standard-io@1",
            State = EvaluationJobState.Running,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Empties both queues of anything still <c>Running</c>.
    ///
    /// <para>
    /// Only the two drain tests use it, and they have to: the suite shares a
    /// database, and a test that claimed a job and never reported one leaves it
    /// <c>Running</c> for ever. The drain is the one thing that reads across
    /// every other test's leavings, so it is the one thing that has to start
    /// from a Server nobody is using.
    /// </para>
    /// </summary>
    private async Task QuietAsync()
    {
        await using var context = server.NewContext();

        await context.EvaluationJobs
            .Where(j => j.State == EvaluationJobState.Running)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.State, EvaluationJobState.Completed));

        await context.Trials
            .Where(t => t.State == EvaluationJobState.Running)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.State, EvaluationJobState.Completed));
    }

    /// <summary>Absent means open — the row is created on first use, not by the migration.</summary>
    private async Task<MaintenanceLevel> LevelAsync()
    {
        await using var context = server.NewContext();
        var state = await context.Maintenance.FirstOrDefaultAsync();
        return state?.Level ?? MaintenanceLevel.Open;
    }

    /// <summary>
    /// A client carrying the admin token, which is half of what <c>/admin</c>
    /// asks for. The other half — being on the loopback interface — the fixture
    /// gives every client by default.
    /// </summary>
    private HttpClient Operator()
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(AdminSurface.TokenHeader, ServerFixture.AdminToken);
        return client;
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient client, bool on, string? reason = null) =>
        client.PostAsJsonAsync("/api/v1/admin/maintenance", new { on, reason }, Json);

    // ── the switch ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The one security-relevant line in the feature.</b>
    ///
    /// <para>
    /// <c>UseForwardedHeaders</c> runs first and rewrites the remote address
    /// from <c>X-Forwarded-For</c>, so a caller from anywhere could otherwise
    /// claim to be on the loopback interface and take the installation off the
    /// air. The switch reads the address captured <i>before</i> that rewrite.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_forged_forwarded_header_cannot_throw_the_switch()
    {
        await OpenAsync();
        // **With the right token**, so the only thing wrong with this request is
        // where it came from. Without it the test would pass because the token
        // was missing and prove nothing about the address at all — which is the
        // half it is named after.
        var client = Operator();
        // Genuinely from somewhere else, claiming to be here. Setting only the
        // forged header would prove nothing either: the socket would still be
        // loopback and the Server would be right to answer.
        client.DefaultRequestHeaders.Add(ServerFixture.PeerHeader, "203.0.113.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");

        var refused = await SwitchAsync(client, on: true);

        // 404, not 403: the one unauthenticated route that changes anything is
        // not worth confirming the existence of to a stranger.
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(MaintenanceLevel.Open, await LevelAsync());
    }

    [Fact]
    public async Task A_call_from_this_machine_throws_it_and_takes_it_back()
    {
        await OpenAsync();
        await BusyAsync();
        var client = Operator();

        var on = await SwitchAsync(client, on: true, reason: "nightly backup");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var state = await on.Content.ReadFromJsonAsync<JsonElement>(Json);

        // **Never straight to closed.** Work in flight is given its chance.
        Assert.Equal("draining", state.GetProperty("level").GetString());
        Assert.Equal("nightly backup", state.GetProperty("reason").GetString());

        var off = await SwitchAsync(client, on: false);
        Assert.Equal("open", (await off.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("level").GetString());

        await OpenAsync();
    }

    /// <summary>
    /// <b>The form an operator will actually use.</b>
    ///
    /// <para>
    /// The shipped image has no <c>curl</c> and no <c>wget</c> — the container's
    /// own healthcheck is written with bash's <c>/dev/tcp</c> for that reason —
    /// so throwing this through <c>docker exec</c> means writing the request by
    /// hand. A body would mean counting bytes for <c>Content-Length</c> and
    /// getting it right at the moment somebody is taking a broken installation
    /// off the air. This is the case the documentation prescribes, so it is the
    /// case that has to keep working.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_switch_can_be_thrown_without_a_body_at_all()
    {
        await OpenAsync();
        await BusyAsync();
        var client = Operator();

        var on = await client.PostAsync("/api/v1/admin/maintenance?on=true&reason=backup", null);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var state = await on.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("draining", state.GetProperty("level").GetString());
        Assert.Equal("backup", state.GetProperty("reason").GetString());

        var off = await client.PostAsync("/api/v1/admin/maintenance?on=false", null);
        Assert.Equal("open", (await off.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("level").GetString());

        // Saying nothing at all is refused rather than guessed at: assuming one
        // way serves requests during a backup, and the other closes an
        // installation nobody asked to close.
        var silent = await client.PostAsync("/api/v1/admin/maintenance", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, silent.StatusCode);

        await OpenAsync();
    }

    /// <summary>
    /// Asking twice must not restart the clock. <c>RequestedAt</c> is what the
    /// forced close is measured against, so a script calling this in a loop
    /// would otherwise hold the Server in <c>draining</c> for ever.
    /// </summary>
    [Fact]
    public async Task Asking_twice_does_not_restart_the_clock()
    {
        await OpenAsync();
        await BusyAsync();
        var client = Operator();

        await SwitchAsync(client, on: true, reason: "first");
        DateTime first;
        await using (var context = server.NewContext())
        {
            first = (await context.Maintenance.FirstAsync()).RequestedAt!.Value;
        }

        await SwitchAsync(client, on: true, reason: "second");
        await using (var context = server.NewContext())
        {
            var state = await context.Maintenance.FirstAsync();
            Assert.Equal(first, state.RequestedAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal("first", state.Reason);
        }

        await SwitchAsync(client, on: false);
        await OpenAsync();
    }

    // ── what each level does ─────────────────────────────────────────────────

    /// <summary>
    /// Draining refuses a participant and admits a Runner finishing its work.
    /// That asymmetry is the whole reason there are two levels rather than a
    /// flag: closing on a Runner mid-evaluation throws the evaluation away.
    /// </summary>
    [Fact]
    public async Task Draining_refuses_a_participant_and_admits_a_report()
    {
        await OpenAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        // Registered and approved before the switch, because neither is
        // something a Runner may do once the Server has begun withdrawing.
        var runner = await Build.RunnerAsync(server);

        await BusyAsync();
        await SwitchAsync(Operator(), on: true);

        var refused = await admin.GetAsync("/api/v1/activities");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("server.maintenance", await refused.Content.ReadAsStringAsync());
        // A hint for a caller with no backoff of its own.
        Assert.NotNull(refused.Headers.RetryAfter);

        // The Runner's own surface stays open: it may renew, upload and report.
        var beat = await runner.Client.PostAsJsonAsync("/api/v1/runner/heartbeat", new { }, Json);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, beat.StatusCode);

        // But it is handed no new work — as an empty queue, not as a refusal,
        // because 204 is what a Runner already does the right thing with.
        var claimed = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 300 }, Json);
        Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);

        var trial = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/trials/claim", new { leaseSeconds = 300 }, Json);
        Assert.Equal(HttpStatusCode.NoContent, trial.StatusCode);

        await SwitchAsync(Operator(), on: false);
        await OpenAsync();
    }

    [Fact]
    public async Task Closed_refuses_the_runner_too()
    {
        await OpenAsync();
        var runner = await Build.RunnerAsync(server);

        await BusyAsync();
        await SwitchAsync(Operator(), on: true);
        await using (var context = server.NewContext())
        {
            var state = await context.Maintenance.FirstAsync();
            state.Level = MaintenanceLevel.Closed;
            await context.SaveChangesAsync();
        }

        var beat = await runner.Client.PostAsJsonAsync("/api/v1/runner/heartbeat", new { }, Json);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, beat.StatusCode);

        await SwitchAsync(Operator(), on: false);
        await OpenAsync();
    }

    /// <summary>
    /// <b>The door that never closes.</b>
    ///
    /// <para>
    /// The container's healthcheck greps this for <c>200 OK</c>, so a 503 here
    /// would have Docker kill the process the operator is deliberately keeping
    /// alive — and it is what a Client and a Runner poll to learn they may come
    /// back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Health_answers_two_hundred_at_every_level_and_names_the_level()
    {
        await OpenAsync();
        var client = Operator();

        var open = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var document = await open.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ok", document.GetProperty("status").GetString());
        // Absent while open, so a reader that never heard of maintenance sees
        // the document it always saw.
        Assert.True(
            !document.TryGetProperty("maintenance", out var absent)
                || absent.ValueKind == JsonValueKind.Null,
            "an open Server names no maintenance");

        await BusyAsync();
        await SwitchAsync(client, on: true, reason: "backup");

        var draining = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, draining.StatusCode);
        var named = await draining.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ok", named.GetProperty("status").GetString());
        Assert.Equal("draining", named.GetProperty("maintenance").GetProperty("level").GetString());
        Assert.Equal("backup", named.GetProperty("maintenance").GetProperty("reason").GetString());

        // And at the level where nothing else answers at all.
        await using (var context = server.NewContext())
        {
            var state = await context.Maintenance.FirstAsync();
            state.Level = MaintenanceLevel.Closed;
            await context.SaveChangesAsync();
        }

        var closed = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal("closed", (await closed.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("maintenance").GetProperty("level").GetString());

        await SwitchAsync(client, on: false);
        await OpenAsync();
    }

    // ── the drain ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draining becomes closed when both queues are quiet — and <b>both</b> is
    /// the point: a trial holds a lease exactly as a job does, and a drain that
    /// counted one would call the Server quiet while a Runner was writing.
    /// </summary>
    [Fact]
    public async Task The_drain_waits_for_a_running_trial_and_then_closes()
    {
        await OpenAsync();
        await QuietAsync();
        var drainer = server.Services.GetRequiredService<MaintenanceDrainer>();

        await BusyAsync();
        await SwitchAsync(Operator(), on: true);

        Assert.False(await drainer.TickAsync(default), "a running trial holds the door open");
        Assert.Equal(MaintenanceLevel.Draining, await LevelAsync());

        await using (var context = server.NewContext())
        {
            var trial = await context.Trials.FirstAsync(t => t.UserId == Nobody);
            trial.State = EvaluationJobState.Completed;
            trial.FinishedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // The outcome, not this call's return value: the hosted drainer ticks
        // against the same database, and a test that insisted on being the one
        // to close the door would fail on the runs where the timer got there
        // first — for something that is not the rule being checked.
        await drainer.TickAsync(default);
        Assert.Equal(MaintenanceLevel.Closed, await LevelAsync());

        await SwitchAsync(Operator(), on: false);
        await OpenAsync();
    }

    /// <summary>
    /// A job that will not finish must not hold an operator hostage.
    ///
    /// <para>
    /// Nothing is lost when this fires: the abandoned job keeps its lease, the
    /// lease expires, and the reaper puts it back on the queue. The cost is one
    /// evaluation done twice; the cost of never forcing is a backup that never
    /// starts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_wedged_job_is_closed_over_after_the_deadline()
    {
        await OpenAsync();
        var drainer = server.Services.GetRequiredService<MaintenanceDrainer>();

        await BusyAsync();
        await SwitchAsync(Operator(), on: true);
        Assert.False(await drainer.TickAsync(default), "not yet — the deadline has not passed");

        // Asked for long enough ago that the deadline has gone by. Written
        // straight to the row rather than waiting five real minutes for it.
        await using (var context = server.NewContext())
        {
            var state = await context.Maintenance.FirstAsync();
            state.RequestedAt = DateTime.UtcNow.AddHours(-1);
            await context.SaveChangesAsync();
        }

        await drainer.TickAsync(default);
        Assert.Equal(MaintenanceLevel.Closed, await LevelAsync());

        // The trial is still running. That is the point of the case: it was
        // closed over, not waited for.
        await using (var context = server.NewContext())
        {
            Assert.Equal(EvaluationJobState.Running,
                (await context.Trials.FirstAsync(t => t.UserId == Nobody)).State);
        }

        await SwitchAsync(Operator(), on: false);
        await OpenAsync();
    }
}
