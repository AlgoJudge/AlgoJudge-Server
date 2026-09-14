using AlgoJudge.Server.Database;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Narrowing the activity list by what kind of thing an activity is.
/// <para>
/// The Client has offered Contest and Course chips since the screen was
/// written, and put <c>type=</c> on every request. <b>Nothing bound it.</b> The
/// action beside it comma-split <c>state</c> on the very next line, so the
/// convention existed and was simply not carried across — and because ASP.NET
/// Core discards a query key nothing binds, ticking the chip answered 200 with
/// the unfiltered list and the same page count.
/// </para>
/// <para>
/// <b>Asserted as a set of names rather than as a count.</b> The suite shares
/// one database and creates hundreds of activities, so a count here would be a
/// number nobody can predict and a test that passes alone and fails in a full
/// run. The same reason <c>LockdownTests.ListedRowAsync</c> pages.
/// </para>
/// </summary>
[Collection("server-2")]
public class ActivityListingTests(ServerFixture server)
{
    /// <summary>A published activity of a stated type, visible to anybody.</summary>
    private async Task<string> OfTypeAsync(string type)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();

        var created = await Build.PostAsync(admin, "/api/v1/activities", new
        {
            slug,
            name = $"Listing {type}",
            type,
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
        });

        // Nothing unpublished reaches the list, so a filter test against an
        // unpublished activity would be a filter test against nothing.
        var id = created.GetProperty("id").GetString();
        await Build.PostAsync(admin, $"/api/v1/activities/{id}/published", new { published = true });

        return slug;
    }

    /// <summary>The list answers a signed-in reader, so every test has one.</summary>
    private Task<HttpClient> ReaderAsync() =>
        Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>Whether the list, narrowed this way, carries this activity.</summary>
    private static async Task<bool> CarriesAsync(HttpClient client, string query, string slug)
    {
        for (var page = 1; page <= 40; page++)
        {
            var answer = await Build.GetAsync(client, $"/api/v1/activities?page={page}&pageSize=50&{query}");
            var items = answer.GetProperty("items").EnumerateArray().ToList();
            if (items.Count == 0) return false;
            if (items.Any(row => row.GetProperty("slug").GetString() == slug)) return true;
        }
        return false;
    }

    [Fact]
    public async Task An_activity_type_filter_shows_that_type_and_withholds_the_other()
    {
        var contest = await OfTypeAsync("contest@1");
        var course = await OfTypeAsync("course@1");
        var reader = await ReaderAsync();

        Assert.True(await CarriesAsync(reader, "type=contest", contest));
        Assert.False(await CarriesAsync(reader, "type=contest", course));

        Assert.True(await CarriesAsync(reader, "type=course", course));
        Assert.False(await CarriesAsync(reader, "type=course", contest));

        // Both types answer with both, which a filter taking only the first
        // word it was handed could not do.
        Assert.True(await CarriesAsync(reader, "type=contest&type=course", contest));
        Assert.True(await CarriesAsync(reader, "type=contest,course", course));
    }

    /// <summary>
    /// <b>The name, not the whole discriminator.</b>
    /// <para>
    /// <c>Activity.Type</c> is <c>name@version</c> and the chip offers
    /// <c>contest</c>, so comparing the two whole would match nothing at all —
    /// which is worse than the dead parameter it replaces, because the screen
    /// would look as though it were working. This is the test that says why the
    /// split is there, and the one that catches somebody simplifying it away.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_type_filter_survives_a_new_version_of_a_type()
    {
        var next = await OfTypeAsync("contest@2");
        var reader = await ReaderAsync();

        Assert.True(await CarriesAsync(reader, "type=contest", next));
    }

    /// <summary>
    /// A type nobody has published matches nothing rather than everything. The
    /// rule is stated once in <c>FilterVocabularyTests</c>; this is it reaching
    /// a list.
    /// </summary>
    [Fact]
    public async Task A_type_nothing_is_matches_nothing()
    {
        var contest = await OfTypeAsync("contest@1");
        var reader = await ReaderAsync();

        Assert.False(await CarriesAsync(reader, "type=nonsense", contest));
    }

    /// <summary>
    /// And a cleared chip group is not a type nobody has published: it sends the
    /// key with nothing in it, and that asks for every type there is.
    /// </summary>
    [Fact]
    public async Task A_type_filter_that_was_cleared_asks_for_every_type()
    {
        var contest = await OfTypeAsync("contest@1");
        var reader = await ReaderAsync();

        Assert.True(await CarriesAsync(reader, "type=", contest));
    }
}
