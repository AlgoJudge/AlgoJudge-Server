using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Signing in, and the two things every test does after it. Shared so a test
/// reads as what it is checking rather than as the ceremony to get there.
/// </summary>
public static class Sign
{
    /// <summary>Long enough for the twelve-character rule, and obviously a test's.</summary>
    public const string Password = "test-password-for-tests";

    public static async Task<HttpClient> InAsync(ServerFixture server, string login, string password)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true", new { email = login, password });
        await Succeeded(response);
        return client;
    }

    /// <summary>
    /// Signs in, or answers <c>null</c> because the credentials were refused.
    ///
    /// <para>
    /// Its own method rather than a flag, because failing to sign in is what
    /// some tests are <b>asserting</b> — an old password that must stop working,
    /// a lockout that must be in force. <see cref="InAsync"/> throws on a
    /// refusal, which is right everywhere else and useless here.
    /// </para>
    /// </summary>
    public static async Task<HttpClient?> TryInAsync(ServerFixture server, string login, string password)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true", new { email = login, password });
        return response.IsSuccessStatusCode ? client : null;
    }

    /// <summary>
    /// A fresh account, created through the user manager rather than the
    /// registration endpoint — which is refused, because this instance does not
    /// accept sign-ups and that is the point of another test.
    /// </summary>
    public static async Task<HttpClient> NewAccountAsync(ServerFixture server, string login)
    {
        using (var scope = server.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            if (await users.FindByNameAsync(login) is null)
            {
                var created = await users.CreateAsync(new User
                {
                    UserName = login,
                    Email = $"{login}@example.invalid",
                    EmailConfirmed = true,
                    ApprovedAt = DateTime.UtcNow,
                }, Password);

                if (!created.Succeeded)
                {
                    throw new XunitException(
                        "Could not create " + login + ": "
                        + string.Join("; ", created.Errors.Select(e => e.Description)));
                }
            }
        }

        return await InAsync(server, login, Password);
    }

    public static async Task<JsonElement> SubmitAsync(HttpClient client, string language, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new StringContent(language), "language" },
            { new StringContent(source), "code" },
            { new StringContent(checksum), "sha256" },
        };

        var response = await client.PostAsync(
            "/api/v1/activities/DEV-2026/problems/A/submissions", content);
        await Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Fails with the body, not just the status. `EnsureSuccessStatusCode` says
    /// "500" and throws away the one thing that would identify the fault.
    /// </summary>
    public static async Task Succeeded(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw new XunitException(
            $"{(int)response.StatusCode} {response.ReasonPhrase} from "
            + $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}\n{body}");
    }
}
