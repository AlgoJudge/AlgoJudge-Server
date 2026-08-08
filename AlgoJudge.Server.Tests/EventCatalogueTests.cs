using System.Reflection;
using System.Text.Json;
using AlgoJudge.Server.Api.Contracts;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Keeps <c>events.json</c> honest about what goes on the socket.
///
/// <para>
/// Events are not HTTP operations, so <c>openapi.json</c> cannot carry them —
/// and until 2026-08-08 nothing carried them at all. The two sides each held
/// their own list of names, agreed by hand, and drifted: fourteen names the
/// Server declared were never sent, one name it did send was declared nowhere,
/// and one name meant two different payloads. None of that could fail a build.
/// </para>
/// <para>
/// This is the Server's half of the fix. The Client's half is
/// <c>npm run check:events -- ../AlgoJudge-Server/events.json</c>, which diffs
/// its own records against this file. Between them, a name added on one side
/// only stops being something you find by watching a screen not update.
/// </para>
/// </summary>
public class EventCatalogueTests
{
    private sealed record Catalogue(string[] Events, string[] Transport);

    [Fact]
    public void The_committed_catalogue_names_exactly_what_the_Server_declares()
    {
        var declared = typeof(EventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var catalogue = Read();
        var committed = catalogue.Events.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var missing = declared.Except(committed).ToArray();
        var extra = committed.Except(declared).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"events.json has drifted from EventTypes. Declared and uncommitted: "
            + $"[{string.Join(", ", missing)}]. Committed and undeclared: [{string.Join(", ", extra)}]. "
            + "Regenerate the file rather than editing it by hand.");
    }

    /// <summary>
    /// The keep-alive is written as a raw literal outside the envelope, so no
    /// constant can be reflected over. Naming it here is what keeps it from
    /// being a wire name nobody wrote down.
    /// </summary>
    [Fact]
    public void The_keep_alive_is_named_and_is_not_an_event()
    {
        var catalogue = Read();
        Assert.Contains("ping", catalogue.Transport);
        Assert.DoesNotContain("ping", catalogue.Events);
    }


    /// <summary>
    /// A declared name that nothing sends is a screen waiting for a frame no
    /// code path produces.
    ///
    /// <para>
    /// Fifteen of these were live at once on 2026-08-08 — every manager write
    /// was silent, so two people working on one activity never saw each other's
    /// changes — and nothing could fail a build over it. The catalogue test above
    /// keeps the two sides naming the same things; this one keeps a name from
    /// being a promise nobody keeps.
    /// </para>
    /// <para>
    /// The four exceptions are named rather than tolerated, and each says why.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_declared_event_has_something_that_sends_it()
    {
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["systemMessage"] = "raised by the Client's own transport when a call fails",
            ["sessionExpired"] = "raised by the Client's own transport on a 401",
            ["activityCreated"] = "carries a per-reader `Activity`, which cannot be computed once",
            ["activityUpdated"] = "the same, and for the same reason",
        };

        var root = Root();
        var declared = typeof(EventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToArray();

        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "AlgoJudge.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "Events.cs")
            .Select(File.ReadAllText)
            .ToArray();

        var unsent = declared
            .Where(f => !exempt.ContainsKey((string)f.GetRawConstantValue()!))
            .Where(f => !sources.Any(s => s.Contains($"EventTypes.{f.Name}", StringComparison.Ordinal)))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unsent.Length == 0,
            "Declared and never sent: [" + string.Join(", ", unsent) + "]. "
            + "Either something has to send it, or it should not be declared — a name "
            + "the Client listens for and nothing produces is a screen that never updates.");
    }

    /// <summary>The repository root, found by the file that sits in it.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "events.json")))
        {
            directory = directory.Parent;
        }
        Assert.True(directory is not null, "events.json was not found above the test assembly");
        return directory!.FullName;
    }

    private static Catalogue Read()
    {
        // Up from `bin/Release/net8.0` to the repository root, where the file
        // sits beside `openapi.json`.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "events.json")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "events.json was not found above the test assembly");

        var json = File.ReadAllText(Path.Combine(directory!.FullName, "events.json"));
        return JsonSerializer.Deserialize<Catalogue>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
