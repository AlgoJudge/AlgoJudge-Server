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
