using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Workers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit.Sdk;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The lease, and what a Runner may reach through it.
/// <para>
/// Every one of these is about the same idea: a Runner is authorized against
/// <b>the job it is holding</b>, not against being a Runner, and the lease is
/// what makes a job survive a Runner that died.
/// </para>
/// </summary>
[Collection("server-2")]
public class RunnerTests(ServerFixture server)
{
    [Fact]
    public async Task A_runner_reads_the_package_of_a_job_it_holds_and_nothing_else()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);

        // The package it was handed: readable.
        var packageId = job.GetProperty("packageFileId").GetString()!;
        var package = await runner.Client.GetAsync($"/api/v1/runner/files/{packageId}");
        Assert.Equal(HttpStatusCode.OK, package.StatusCode);

        // A file belonging to another problem entirely: not readable, and
        // answered 404 rather than 403 — a file id is opaque, and a 403 would
        // confirm the bytes exist.
        var (otherSlug, _) = await Build.ActivityAsync(server);
        var otherPackage = await Build.PackageIdOfAsync(server, otherSlug);

        var refused = await runner.Client.GetAsync($"/api/v1/runner/files/{otherPackage}");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!);
    }

    [Fact]
    public async Task A_runner_holding_nothing_reads_nothing()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var packageId = await Build.PackageIdOfAsync(server, slug);

        // Registered, approved, and holding no job at all.
        var idle = await Build.RunnerAsync(server);

        var response = await idle.Client.GetAsync($"/api/v1/runner/files/{packageId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_lease_can_be_renewed_while_the_work_is_still_going()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(2)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        var first = DateTime.Parse(job.GetProperty("leaseExpiresAt").GetString()!).ToUniversalTime();

        var renewed = await Build.PostAsync(runner.Client, $"/api/v1/runner/jobs/{jobId}/lease", new
        {
            leaseToken,
            leaseSeconds = 3600,
        });

        var extended = DateTime.Parse(renewed.GetProperty("leaseExpiresAt").GetString()!).ToUniversalTime();
        Assert.True(extended > first, "renewing pushes the deadline out");

        await runner.ReportAsync(jobId, leaseToken);
    }

    /// <summary>
    /// The renewal that loses the race, forced rather than waited for.
    ///
    /// <para>
    /// A Runner renewing reads the job, then writes it. If the lease is reclaimed
    /// in between — which is what <c>LeaseReaper</c> does the instant a deadline
    /// passes — the write finds a row that has moved and EF raises
    /// <c>DbUpdateConcurrencyException</c>. Left unhandled that leaves the
    /// endpoint answering <b>500</b>, and a Runner cannot tell "try again in a
    /// moment" from "you have lost this job, stop computing" — two opposite
    /// actions behind one status.
    /// </para>
    ///
    /// <para>
    /// §5 of the accepted Server–Runner API already names the answer:
    /// <b>the lease has already been reclaimed → <c>runner.lease.stale</c></b>.
    /// So this is the contract being met, not a contract being changed.
    /// </para>
    ///
    /// <para>
    /// Found as a test that failed about one full run in four and passed alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_renewal_that_loses_the_race_says_the_lease_is_gone()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(4)\n");

        var thief = new ReclaimWhileSaving(server.ConnectionString);
        using var host = HostRacing(thief);

        var runner = await Build.RunnerAsync(server, host);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        // Armed only now, so the claim itself is an ordinary one.
        thief.JobId = Guid.Parse(jobId);

        var response = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/lease", new { leaseToken, leaseSeconds = 3600 });

        Assert.True(thief.Fired, "the race never happened, so this test proved nothing");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("runner.lease.stale", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// The same race on the other endpoint that goes through the same read.
    /// Fixing one and not the other leaves the identical defect at a second
    /// address, which is the way this kind of fix usually half-lands.
    /// </summary>
    [Fact]
    public async Task Reporting_progress_that_loses_the_race_says_the_lease_is_gone()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(5)\n");

        var thief = new ReclaimWhileSaving(server.ConnectionString);
        using var host = HostRacing(thief);

        var runner = await Build.RunnerAsync(server, host);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        thief.JobId = Guid.Parse(jobId);

        var response = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/progress", new { leaseToken });

        Assert.True(thief.Fired, "the race never happened, so this test proved nothing");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("runner.lease.stale", problem.GetProperty("code").GetString());
    }


    /// <summary>
    /// A host whose database context carries the interceptor.
    ///
    /// <b>Registered on the options rather than left to be discovered.</b> EF
    /// Core will resolve an <c>IInterceptor</c> out of the application container
    /// in some configurations, and it did not in this one — the interceptor
    /// simply never ran, and the test passed by not racing. Which is why it
    /// asserts that it fired.
    /// </summary>
    private WebApplicationFactory<Program> HostRacing(ReclaimWhileSaving thief) =>
        server.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(server.ConnectionString).AddInterceptors(thief));
        }));

    /// <summary>
    /// The whole recovery story: the Runner is stateless, so nobody is coming
    /// back for the work, and the Server gives it to somebody else.
    /// </summary>
    [Fact]
    public async Task An_expired_lease_returns_the_job_to_the_queue()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(3)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var first = await Build.RunnerAsync(server);
        var job = await first.ClaimUntilAsync(submissionId);
        var jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);
        var staleToken = job.GetProperty("leaseToken").GetString()!;

        // The Runner dies. Its lease runs out.
        await using (var context = server.NewContext())
        {
            var stored = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            stored.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }

        var reaped = await server.Services.GetRequiredService<LeaseReaper>()
            .SweepAsync(CancellationToken.None);
        Assert.True(reaped >= 1);

        await using (var context = server.NewContext())
        {
            var stored = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            Assert.Equal(EvaluationJobState.Queued, stored.State);
            Assert.Null(stored.RunnerId);
            Assert.Null(stored.LeaseToken);
        }

        // A second Runner picks it up — the same job, not a new attempt. A
        // reclaim is the same work handed to somebody else; only a rejudge adds
        // an attempt.
        var second = await Build.RunnerAsync(server);
        var again = await second.ClaimUntilAsync(submissionId);

        Assert.Equal(job.GetProperty("jobId").GetString(), again.GetProperty("jobId").GetString());
        Assert.Equal(1, again.GetProperty("attempt").GetInt32());
        Assert.NotEqual(staleToken, again.GetProperty("leaseToken").GetString());

        await using (var context = server.NewContext())
        {
            var stored = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            // Counted, because a job that keeps being handed out is a job that
            // keeps killing Runners.
            Assert.Equal(2, stored.Deliveries);
        }

        // …and the first one, waking up, is refused rather than allowed to
        // overwrite the job the second is now holding.
        var late = await first.Client.PostAsJsonAsync($"/api/v1/runner/jobs/{jobId}/report", new
        {
            leaseToken = staleToken,
            score = 100,
            maxScore = 100,
            verdict = "Accepted",
        });
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        var problem = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runner.lease.stale", problem.GetProperty("code").GetString());

        await second.ReportAsync(
            again.GetProperty("jobId").GetString()!,
            again.GetProperty("leaseToken").GetString()!);
    }

    /// <summary>
    /// A job that keeps being reclaimed is a job that keeps killing Runners.
    /// Retrying it for ever is how one bad package stops an installation.
    /// </summary>
    [Fact]
    public async Task A_job_nobody_can_finish_is_given_up_on_rather_than_retried_for_ever()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print('poison')\n");
        var submissionId = Guid.Parse(submitted.GetProperty("id").GetString()!);

        var reaper = server.Services.GetRequiredService<LeaseReaper>();
        var runner = await Build.RunnerAsync(server);

        Guid jobId = default;
        for (var round = 0; round < AlgoJudge.Server.Services.RunnerService.DeliveryCap + 1; round++)
        {
            var job = await runner.TryClaimForAsync(submitted.GetProperty("id").GetString()!);
            if (job is null) break;

            jobId = Guid.Parse(job.Value.GetProperty("jobId").GetString()!);
            await using var context = server.NewContext();
            var stored = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            stored.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();

            await reaper.SweepAsync(CancellationToken.None);
        }

        await using (var context = server.NewContext())
        {
            var job = await context.EvaluationJobs.FirstAsync(j => j.SubmissionId == submissionId);
            Assert.Equal(EvaluationJobState.Failed, job.State);
            Assert.Contains("Given up", job.FailureReason);
        }
    }

    [Fact]
    public async Task A_runner_attaches_its_log_to_the_attempt_and_replaces_its_own()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(4)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        // On the attempt: named once, and a second name collides.
        var log = await runner.UploadAsync("compile.txt", "warning: unused variable\n");
        var attached = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/files", new { leaseToken, fileId = log, name = "log" });
        Assert.Equal(HttpStatusCode.NoContent, attached.StatusCode);

        var again = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/files", new { leaseToken, fileId = log, name = "log" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // On itself: the name is replaced rather than added to, so "old
        // versions" means earlier ones under the same name.
        var first = await runner.UploadAsync("runner.log", "boot 1\n");
        await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/files/attach", new { fileId = first, name = "runner.log" });
        var second = await runner.UploadAsync("runner.log", "boot 2\n");
        await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/files/attach", new { fileId = second, name = "runner.log" });

        await using (var context = server.NewContext())
        {
            var current = await context.FileReferences
                .Where(r => r.OwnerKind == FileOwnerKind.Runner
                    && r.Name == "runner.log" && r.SupersededAt == null)
                .ToListAsync();
            // Exactly one current revision per name, and the superseded one is
            // marked rather than deleted — deleting would orphan its file.
            Assert.Single(current.Where(r => r.FileId == Guid.Parse(second)));

            var superseded = await context.FileReferences
                .CountAsync(r => r.FileId == Guid.Parse(first) && r.SupersededAt != null);
            Assert.Equal(1, superseded);
        }

        await runner.ReportAsync(jobId, leaseToken);
    }

    [Fact]
    public async Task Cancelling_an_attempt_stops_the_runner_reporting_on_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(5)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);
        var jobId = job.GetProperty("jobId").GetString()!;
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var cancelled = await admin.PostAsync(
            $"/api/v1/submissions/{submissionId}/attempts/{jobId}/cancel", null);
        await Sign.Succeeded(cancelled);

        // The lease went with the cancellation, so the Runner is refused.
        var late = await runner.Client.PostAsJsonAsync($"/api/v1/runner/jobs/{jobId}/report", new
        {
            leaseToken, score = 100, maxScore = 100, verdict = "Accepted",
        });
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);

        // And a finished attempt cannot be cancelled twice.
        var twice = await admin.PostAsync(
            $"/api/v1/submissions/{submissionId}/attempts/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    /// <summary>
    /// Saying "still working" renews the lease this job was granted, not the
    /// Server's own default.
    /// <para>
    /// <b>It used to substitute the default, and nothing on either side said
    /// so.</b> A Runner asking for three hundred seconds was granted three
    /// hundred, was <i>told</i> three hundred in the claim answer, and held six
    /// hundred the moment it reported progress. Found from the far end on
    /// 2026-08-22: <c>AlgoJudge-Runner-UVa</c> reports progress the instant it
    /// takes a job, so every lease it ever held was ten minutes, and its own
    /// start-up guards — a lease that must outlast the archive timeout, a poll
    /// interval that must fit four times inside it — were arithmetic on a number
    /// this Server had already overridden.
    /// </para>
    /// <para>
    /// The harm is not a shortened deadline: <c>Later</c> forbids that. It is a
    /// Runner reasoning about a lease it does not have.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reporting_progress_renews_by_the_lease_the_job_was_granted()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(7)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        // The helper asks for three hundred seconds, which is the point: it is
        // under the ten-minute default, so a default applied here is visible.
        var job = await runner.ClaimUntilAsync(submissionId);
        var jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);
        var leaseToken = job.GetProperty("leaseToken").GetString()!;

        var progress = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/jobs/{jobId}/progress", new { leaseToken });
        await Sign.Succeeded(progress);

        await using var context = server.NewContext();
        var stored = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);

        Assert.Equal(300, stored.LeaseSeconds);
        var held = stored.LeaseExpiresAt!.Value - DateTime.UtcNow;
        Assert.True(
            held < TimeSpan.FromSeconds(330),
            $"progress pushed the deadline {held.TotalSeconds:F0}s out on a 300s lease, "
                + "so the Runner is holding a lease it never asked for and cannot see");
    }

    [Fact]
    public async Task Revoking_a_runner_gives_back_what_it_was_holding()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(6)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);
        var jobId = Guid.Parse(job.GetProperty("jobId").GetString()!);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var revoked = await admin.PostAsJsonAsync(
            $"/api/v1/runners/{runner.Id}/revoke", new { reason = "leaked key" });
        await Sign.Succeeded(revoked);

        await using (var context = server.NewContext())
        {
            var stored = await context.EvaluationJobs.FirstAsync(j => j.Id == jobId);
            // The job is not lost because a Runner was.
            Assert.Equal(EvaluationJobState.Queued, stored.State);
            Assert.Null(stored.RunnerId);
        }

        // And the revoked Runner's token stops working at once, not at expiry.
        var response = await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/jobs/claim", new { leaseSeconds = 60 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Two endpoints, two readers, two shapes — and the shapes must not be
    /// swapped.
    /// <para>
    /// Registering tells a Runner who it now is: three members, nothing about
    /// the installation. Approving tells a <b>manager</b> what the row shows,
    /// because that is who is refreshing one. Approving answered the
    /// registration shape until 2026-08-08, which no test caught: every one of
    /// them checked that the call succeeded and none checked what came back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Approving_answers_the_row_a_manager_shows_not_the_registration()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pub = Convert.ToBase64String(
            ((Ed25519PublicKeyParameters)generator.GenerateKeyPair().Public).GetEncoded());

        var registration = await server.CreateClient().PostAsJsonAsync("/api/v1/runner/register", new
        {
            name = "shape-check",
            product = "AlgoJudge-Runner-Stub",
            version = "0.0.1",
            publicKey = pub,
            problemTypes = new[] { "standard-io@1" },
        });
        await Sign.Succeeded(registration);
        var registered = await registration.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(registered.TryGetProperty("runnerId", out _));
        Assert.False(registered.TryGetProperty("name", out _));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var response = await admin.PostAsync(
            $"/api/v1/runners/{registered.GetProperty("runnerId").GetString()}/approve", null);
        await Sign.Succeeded(response);
        var approved = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Keyed on `id`, not `runnerId`: this is the manager's row, and it is
        // what `ManagerApiHttp.approveRunner` puts straight back into the table.
        Assert.Equal("approved", approved.GetProperty("state").GetString());
        Assert.Equal("shape-check", approved.GetProperty("name").GetString());

        foreach (var member in new[] { "id", "problemTypes", "tags", "registeredAt", "approvedAt" })
        {
            Assert.True(approved.TryGetProperty(member, out _), $"the row is missing {member}");
        }
    }
}
