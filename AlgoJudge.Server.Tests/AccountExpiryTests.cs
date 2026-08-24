using System.Net;
using System.Net.Http.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// An account that has run out stops working.
/// <para>
/// <b>Two halves, and one of them is easy to forget.</b> Refusing the sign-in
/// closes the door; it does nothing about somebody who was already through it
/// when the date passed. That is the defect `BlockedGate` was built for, and
/// expiry rides the same check.
/// </para>
/// <para>
/// <b>Nothing is written.</b> The date is the only thing that says an account
/// has run out — no lockout is set from it — so moving the date is enough to
/// give the account back, and unblocking cannot defeat the expiry.
/// </para>
/// </summary>
[Collection("server")]
public class AccountExpiryTests(ServerFixture server)
{
    private async Task<(HttpClient Client, string Id, string Login)> PersonAsync()
    {
        var login = "x-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);

        await using var context = server.NewContext();
        var id = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
        return (client, id, login);
    }

    private async Task ExpireAsync(string userId, DateTime? at)
    {
        await using var context = server.NewContext();
        await context.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAt, at));
    }

    /// <summary>
    /// <b>The half a sign-in check cannot do.</b> Somebody signed in before the
    /// date passes keeps their cookie; without a per-request check they carry on
    /// until Identity next revalidates it, which is half an hour by default.
    /// </summary>
    [Fact]
    public async Task An_account_that_runs_out_stops_working_mid_session()
    {
        var (person, id, _) = await PersonAsync();
        Assert.True((await person.GetAsync("/api/v1/activities")).IsSuccessStatusCode);

        await ExpireAsync(id, DateTime.UtcNow.AddMinutes(-1));

        var refused = await person.GetAsync("/api/v1/activities");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("account.expired", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>And it does not sign in again.</summary>
    [Fact]
    public async Task An_expired_account_cannot_sign_in()
    {
        var (_, id, login) = await PersonAsync();
        await ExpireAsync(id, DateTime.UtcNow.AddMinutes(-1));

        Assert.Null(await Sign.TryInAsync(server, login, Sign.Password));
    }

    /// <summary>
    /// <b>A date still ahead changes nothing.</b> The obvious way to get this
    /// wrong is to treat any date as an expiry.
    /// </summary>
    [Fact]
    public async Task An_account_with_a_date_still_ahead_works()
    {
        var (person, id, login) = await PersonAsync();
        await ExpireAsync(id, DateTime.UtcNow.AddDays(1));

        Assert.True((await person.GetAsync("/api/v1/activities")).IsSuccessStatusCode);
        Assert.NotNull(await Sign.TryInAsync(server, login, Sign.Password));
    }

    /// <summary>
    /// <b>Moving the date gives the account back, at once.</b> This is what a
    /// stored lockout would have cost: a block written from the clock stays
    /// behind when somebody extends the account, and nothing says why it is
    /// still dead.
    /// </summary>
    [Fact]
    public async Task Extending_the_date_revives_the_account()
    {
        var (person, id, login) = await PersonAsync();
        await ExpireAsync(id, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync("/api/v1/activities")).StatusCode);

        await ExpireAsync(id, DateTime.UtcNow.AddDays(7));

        Assert.True((await person.GetAsync("/api/v1/activities")).IsSuccessStatusCode);
        Assert.NotNull(await Sign.TryInAsync(server, login, Sign.Password));
    }

    /// <summary>
    /// <b>Expiry and blocking stay tellable apart</b>, because the manager
    /// screen has drawn them as different states since before either was
    /// enforced. A refusal that called them both "blocked" would make the grey
    /// badge a lie.
    /// </summary>
    [Fact]
    public async Task The_two_reasons_answer_different_codes()
    {
        var (expired, expiredId, _) = await PersonAsync();
        var (blocked, blockedId, _) = await PersonAsync();

        await ExpireAsync(expiredId, DateTime.UtcNow.AddMinutes(-1));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/users/{blockedId}/blocked", new { blocked = true, reason = "Na wniosek" }));

        Assert.Contains("account.expired",
            await (await expired.GetAsync("/api/v1/activities")).Content.ReadAsStringAsync());
        Assert.Contains("account.blocked",
            await (await blocked.GetAsync("/api/v1/activities")).Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Nothing is written, so nothing can be unwritten: unblocking an expired
    /// account does not let it back in.
    /// </summary>
    [Fact]
    public async Task Unblocking_does_not_defeat_an_expiry()
    {
        var (person, id, _) = await PersonAsync();
        await ExpireAsync(id, DateTime.UtcNow.AddMinutes(-1));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/users/{id}/blocked", new { blocked = false, reason = (string?)null }));

        var refused = await person.GetAsync("/api/v1/activities");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("account.expired", await refused.Content.ReadAsStringAsync());
    }
}
