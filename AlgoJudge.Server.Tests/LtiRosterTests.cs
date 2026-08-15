using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Reading a course's roster: what the client asks for, and what it makes of
/// the answer.
///
/// <para>
/// Everything here is against <see cref="FakeRoster"/>, which is shaped from a
/// roster measured against Moodle 5.2.2 — including the two things that are not
/// in the specification and cost a wrong reading each: the username lives in
/// <c>ext_user_username</c>, and it is only sent when the request names a
/// resource link.
/// </para>
/// </summary>
public class LtiRosterTests
{
    [Fact]
    public async Task The_username_is_read_from_where_a_platform_actually_puts_it()
    {
        var (client, roster, platform) = Build();
        roster.Members = [FakeRoster.Member("3", username: "jkowalski", name: "Jan Kowalski")];

        var read = await client.ReadAsync(platform, FakeRoster.MembershipsUrl, "1", default);

        var member = Assert.Single(read.Members);
        Assert.Equal("3", member.UserId);
        Assert.Equal("jkowalski", member.Username);
        Assert.Equal("Jan Kowalski", member.Name);
    }

    /// <summary>
    /// The request carries the placement, because a platform may disclose
    /// per-link data only when asked that way — Moodle does, and without it the
    /// username is simply absent from an otherwise complete roster.
    /// </summary>
    [Fact]
    public async Task The_roster_is_asked_for_one_link_when_the_caller_knows_it()
    {
        var (client, roster, platform) = Build();
        roster.Members = [FakeRoster.Member("3", username: "jkowalski")];

        await client.ReadAsync(platform, FakeRoster.MembershipsUrl, "42", default);

        var asked = roster.Requested.Last(r => r.Contains("memberships"));
        Assert.Contains("rlid=42", asked);
    }

    [Fact]
    public async Task A_roster_without_a_link_asks_for_the_whole_course()
    {
        var (client, roster, platform) = Build();
        roster.Members = [FakeRoster.Member("3")];

        await client.ReadAsync(platform, FakeRoster.MembershipsUrl, null, default);

        var asked = roster.Requested.Last(r => r.Contains("memberships"));
        Assert.DoesNotContain("rlid", asked);
    }

    /// <summary>
    /// <b>Paging is in a header.</b> The container has no "next" field, so a
    /// reader that only parses the body takes the first page for the roster —
    /// and a course of two hundred quietly becomes a course of fifty.
    /// </summary>
    [Fact]
    public async Task Every_page_is_read_and_not_only_the_first()
    {
        var (client, roster, platform) = Build();
        roster.Members = [FakeRoster.Member("1", username: "one")];
        roster.SecondPage = [FakeRoster.Member("2", username: "two")];

        var read = await client.ReadAsync(platform, FakeRoster.MembershipsUrl, "1", default);

        Assert.Equal(2, read.Members.Count);
        Assert.Contains(read.Members, m => m.UserId == "2");
    }

    [Fact]
    public async Task A_platform_that_refuses_says_so_rather_than_answering_an_empty_course()
    {
        var (client, roster, platform) = Build();
        roster.Members = [];

        // The fake answers 404 for anything but its own two addresses, which is
        // what a tool without the membership scope meets.
        var refused = await Assert.ThrowsAsync<NrpsException>(() =>
            client.ReadAsync(platform, "https://platform.invalid/nowhere", "1", default));

        Assert.Contains("refused", refused.Message);
    }

    /// <summary>
    /// A member with no username at all is still read. Deciding what to do about
    /// that is enrolment's business, and it needs to see them to say so.
    /// </summary>
    [Fact]
    public async Task Somebody_the_platform_will_not_name_is_still_in_the_roster()
    {
        var (client, roster, platform) = Build();
        roster.Members = [FakeRoster.Member("7", name: "Anonymous Somebody")];

        var read = await client.ReadAsync(platform, FakeRoster.MembershipsUrl, "1", default);

        var member = Assert.Single(read.Members);
        Assert.Null(member.Username);
        Assert.Equal("7", member.UserId);
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private static (INrpsClient Client, FakeRoster Roster, Platform Platform) Build()
    {
        var roster = new FakeRoster();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IToolKeyService>(new StubbedToolKeyForRoster());
        services.AddSingleton<IPlatformTokens, PlatformTokens>();
        services.AddScoped<INrpsClient, NrpsClient>();

        services.AddHttpClient(nameof(PlatformTokens))
            .ConfigurePrimaryHttpMessageHandler(() => roster);
        services.AddHttpClient(nameof(NrpsClient))
            .ConfigurePrimaryHttpMessageHandler(() => roster);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        return (
            scope.ServiceProvider.GetRequiredService<INrpsClient>(),
            roster,
            new Platform
            {
                DisplayName = "Fake",
                Issuer = "https://platform.invalid",
                ClientId = "client-1",
                DeploymentId = "1",
                KeySetUrl = "https://platform.invalid/certs",
                AuthTokenUrl = FakeRoster.TokenUrl,
                AuthLoginUrl = "https://platform.invalid/auth",
            });
    }
}

/// <summary>
/// A real RSA key and nothing else, so the client assertion is genuinely signed
/// without dragging the module's database into a test about parsing JSON.
/// </summary>
file sealed class StubbedToolKeyForRoster : IToolKeyService
{
    private readonly System.Security.Cryptography.RSA rsa =
        System.Security.Cryptography.RSA.Create(2048);

    public Task<ToolKey> CurrentAsync(CancellationToken ct) =>
        Task.FromResult(new ToolKey
        {
            Kid = "test",
            PublicPem = rsa.ExportSubjectPublicKeyInfoPem(),
            PrivatePem = rsa.ExportPkcs8PrivateKeyPem(),
        });

    public Task<object> KeySetAsync(CancellationToken ct) =>
        Task.FromResult<object>(new { keys = Array.Empty<object>() });

    public Task<Microsoft.IdentityModel.Tokens.SigningCredentials> CredentialsAsync(CancellationToken ct) =>
        Task.FromResult(new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "test" },
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));

    public Task<IReadOnlyList<ToolKeyView>> ListAsync(CancellationToken ct) =>
        throw new NotSupportedException("the roster tests do not rotate keys");

    public Task<ToolKeyView> RotateAsync(CancellationToken ct) =>
        throw new NotSupportedException("the roster tests do not rotate keys");

    public Task WithdrawAsync(string kid, CancellationToken ct) =>
        throw new NotSupportedException("the roster tests do not rotate keys");
}
