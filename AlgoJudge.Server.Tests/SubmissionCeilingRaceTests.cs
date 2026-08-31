using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The per-problem allowance, under two submissions at once.
/// <para>
/// It was a read-then-insert with no lock, no transaction and no constraint
/// behind it, so two requests sent together both counted the same number, both
/// passed <c>used &gt;= limit</c>, and both were written. In a contest with a
/// hard attempt limit that is a scoring defect, and it needed nothing more than
/// two parallel requests to reach.
/// </para>
/// <para>
/// Asserting the outcome is not enough on its own — a test that never actually
/// interleaved would pass against the broken code too — so the interceptor
/// reports whether the race happened, in the shape <c>ConcurrencyTests</c> uses.
/// </para>
/// </summary>
[Collection("server-2")]
public class SubmissionCeilingRaceTests(ServerFixture server)
{
    [Fact(Timeout = 120_000)]
    public async Task A_second_submission_cannot_slip_past_the_ceiling_while_the_first_is_counting()
    {
        var (slug, _) = await Build.ActivityAsync(server);

        Guid assignmentId;
        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxSubmissionsPerProblem = 1;
            await context.SaveChangesAsync();

            assignmentId = (await context.SeriesProblems
                .Include(sp => sp.Series)
                .FirstAsync(sp => sp.Series!.ActivityId == activity.Id)).Id;
        }

        var login = "cl-" + Guid.NewGuid().ToString("N")[..10];
        await Sign.NewAccountAsync(server, login);

        var saboteur = new HoldTheCeilingCount();
        using var host = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseNpgsql(server.ConnectionString).AddInterceptors(saboteur));
            }));

        // Two clients for one person, because one contestant sending twice at
        // once is the whole scenario. Signed in before arming, so the sign-in's
        // own queries cannot trip the interceptor.
        var first = await Sign.InAsync(host, login, Sign.Password);
        var second = await Sign.InAsync(host, login, Sign.Password);

        var joined = await first.PostAsJsonAsync($"/api/v1/activities/{slug}/enrolment", new { });
        await Sign.Succeeded(joined);

        saboteur.Armed = true;

        var a = Build.TrySubmitAsync(first, slug, "print(1)\n");

        // Wait for the first to be parked between its count and its insert, then
        // let the second arrive into exactly that window.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!saboteur.Fired && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
        }

        Assert.True(saboteur.Fired, "the first submission never reached its count, so this proved nothing");

        var b = Build.TrySubmitAsync(second, slug, "print(2)\n");

        // Long enough that a second request which was *not* blocked would have
        // finished, and short enough not to matter when it is.
        await Task.Delay(500);
        Assert.False(b.IsCompleted, "the second submission did not wait, so nothing serialised it");

        saboteur.Release();

        var responses = await Task.WhenAll(a, b);

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var refused = responses.Where(r => r.StatusCode == HttpStatusCode.Forbidden).ToList();

        Assert.Equal(1, accepted);
        Assert.Single(refused);

        var problem = await refused[0].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("submission.limit", problem.GetProperty("code").GetString());

        // Scoped to this test's assignment, and the assertion that would still
        // catch a refusal reported over a row that was written anyway.
        await using (var context = server.NewContext())
        {
            Assert.Equal(
                1,
                await context.Submissions.CountAsync(s => s.SeriesProblemId == assignmentId));
        }

        foreach (var response in responses) response.Dispose();
    }

    /// <summary>
    /// The lock is only taken where there is an allowance to spend, so an
    /// activity with no ceiling must be exactly as it was.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Without_a_ceiling_two_submissions_at_once_both_land()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var responses = await Task.WhenAll(
            Build.TrySubmitAsync(participant, slug, "print(1)\n"),
            Build.TrySubmitAsync(participant, slug, "print(2)\n"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        foreach (var response in responses) response.Dispose();
    }
}
