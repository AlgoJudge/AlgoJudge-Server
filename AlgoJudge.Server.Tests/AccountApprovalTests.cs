using System.Net;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// An account nobody decided on does not sign in.
/// <para>
/// <b>Two halves, the same shape as expiry.</b> Refusing the sign-in closes the
/// door; <c>BlockedGate</c> is what stops somebody who registered themselves
/// before the rule existed and still holds a cookie.
/// </para>
/// <para>
/// <b>It gates one path and only one.</b> Every other way an account comes into
/// being stamps <c>ApprovedAt</c> — a provider's first sign-in, one staff
/// created, a temporary login — so what is left is somebody registering
/// themselves at <c>/identity/register</c>.
/// </para>
/// </summary>
[Collection("server-1")]
public class AccountApprovalTests(ServerFixture server)
{
    private async Task<(HttpClient Client, string Id, string Login)> PersonAsync()
    {
        var login = "a-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);

        await using var context = server.NewContext();
        var id = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
        return (client, id, login);
    }

    private async Task ApprovedAsync(string userId, DateTime? at)
    {
        await using var context = server.NewContext();
        await context.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ApprovedAt, at));
    }

    private async Task ConfirmedAsync(string userId, bool confirmed, bool temporary = false)
    {
        await using var context = server.NewContext();
        await context.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.EmailConfirmed, confirmed)
                .SetProperty(x => x.IsTemporary, temporary));
    }

    private async Task RequireConfirmedAsync(bool required)
    {
        await using var context = server.NewContext();
        await context.Instance
            .ExecuteUpdateAsync(i => i.SetProperty(x => x.RequireConfirmedEmail, required));
    }

    [Fact]
    public async Task An_account_nobody_approved_cannot_sign_in()
    {
        var (_, id, login) = await PersonAsync();
        await ApprovedAsync(id, null);

        Assert.Null(await Sign.TryInAsync(server, login, Sign.Password));
    }

    /// <summary>The half a sign-in check cannot do.</summary>
    [Fact]
    public async Task An_account_nobody_approved_stops_working_mid_session()
    {
        var (person, id, _) = await PersonAsync();
        Assert.True((await person.GetAsync("/api/v1/activities")).IsSuccessStatusCode);

        await ApprovedAsync(id, null);

        var refused = await person.GetAsync("/api/v1/activities");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("account.pendingApproval", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>And approving it gives the account back, without anything else changing.</summary>
    [Fact]
    public async Task Approving_it_gives_the_account_back()
    {
        var (_, id, login) = await PersonAsync();
        await ApprovedAsync(id, null);
        Assert.Null(await Sign.TryInAsync(server, login, Sign.Password));

        await ApprovedAsync(id, DateTime.UtcNow);

        Assert.NotNull(await Sign.TryInAsync(server, login, Sign.Password));
    }

    /// <summary>
    /// A confirmed address is asked for only when the instance says so — and
    /// then it really is asked for.
    /// </summary>
    [Fact]
    public async Task A_confirmed_address_is_required_only_when_the_instance_says_so()
    {
        var (_, id, login) = await PersonAsync();
        await ConfirmedAsync(id, confirmed: false);

        // Off: an unconfirmed address is nobody's business.
        Assert.NotNull(await Sign.TryInAsync(server, login, Sign.Password));

        try
        {
            await RequireConfirmedAsync(true);
            Assert.Null(await Sign.TryInAsync(server, login, Sign.Password));

            await ConfirmedAsync(id, confirmed: true);
            Assert.NotNull(await Sign.TryInAsync(server, login, Sign.Password));
        }
        finally
        {
            await RequireConfirmedAsync(false);
        }
    }

    /// <summary>
    /// <b>A temporary login has no address at all.</b> It is the permanent
    /// exception to end-user passwords, handed out on a slip of paper, and
    /// refusing it over a mailbox it was never given would break that case for
    /// a rule that cannot apply to it.
    /// </summary>
    [Fact]
    public async Task A_temporary_login_is_not_asked_for_an_address_it_does_not_have()
    {
        var (_, id, login) = await PersonAsync();
        await ConfirmedAsync(id, confirmed: false, temporary: true);

        try
        {
            await RequireConfirmedAsync(true);
            Assert.NotNull(await Sign.TryInAsync(server, login, Sign.Password));
        }
        finally
        {
            await RequireConfirmedAsync(false);
        }
    }
}
