using System.Text.RegularExpressions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Keeps the LTI module deletable.
///
/// <para>
/// §8 of <c>docs/specs/LMS_INTEGRATION.md</c> puts the whole integration in one
/// module inside this Server, and states the invariant as being about dependency
/// rather than location: <b>nothing outside the module names LTI.</b> No
/// <c>ltiUserId</c> on <c>User</c>, no LTI branch in the results path, no LTI
/// column on <c>Activity</c>. The test of whether that held is whether the
/// module can be deleted in one commit touching nothing else — and the
/// specification says to check it rather than assert it.
/// </para>
/// <para>
/// This is that check, and it is a source scan rather than a comment because the
/// leak this guards against is the easy kind: one <c>if (launch is not null)</c>
/// in a service that already exists, added because it was the shortest way to
/// make a launch work. By the time anybody notices, the module is not a module.
/// </para>
/// </summary>
public class LtiBoundaryTests
{
    /// <summary>
    /// The two lines the module is allowed outside its own directory, quoted
    /// exactly as <c>Program.cs</c> carries them.
    /// </summary>
    private static readonly string[] AllowedInProgram =
    [
        "AlgoJudge.Server.Lti.LtiModule.AddLti(builder.Services, builder.Configuration);",
        "AlgoJudge.Server.Lti.LtiModule.MapLti(app);",
    ];

    [Fact]
    public void Nothing_outside_the_module_names_LTI()
    {
        var root = Path.Combine(Root(), "AlgoJudge.Server");
        var module = Path.Combine(root, "Lti") + Path.DirectorySeparatorChar;

        // Word-ish rather than substring: `buiLTIn` is a real property name in
        // the core's model snapshot, and a naive contains-check calls it a leak.
        var mentions = new Regex(@"\bLti|\bLTI\b|""lti", RegexOptions.None);

        var leaks = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.StartsWith(module, StringComparison.Ordinal))
            {
                continue;
            }
            // Generated: the core context's snapshot and designer files are
            // written by EF, and they are exactly what would carry a leak if the
            // module's entities ever landed in `ApplicationDbContext`. Scanned,
            // not skipped.
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!mentions.IsMatch(line))
                {
                    continue;
                }
                if (AllowedInProgram.Any(allowed => line.Contains(allowed, StringComparison.Ordinal))
                    || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                leaks.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(
            leaks.Count == 0,
            "LTI is named outside AlgoJudge.Server/Lti/, so the module is no longer deletable "
            + "in one commit. §8 of LMS_INTEGRATION.md makes that the test of the boundary.\n"
            + string.Join("\n", leaks));
    }

    /// <summary>
    /// The two lines are two lines. Counted, because "roughly two" is how a
    /// module acquires a third.
    /// </summary>
    [Fact]
    public void Program_carries_exactly_the_two_lines_the_module_is_allowed()
    {
        var program = File.ReadAllLines(
            Path.Combine(Root(), "AlgoJudge.Server", "Program.cs"));

        var naming = program
            .Where(line => line.Contains("Lti", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToArray();

        Assert.Equal(AllowedInProgram, naming);
    }

    /// <summary>
    /// The module's tables are the module's. If any of them reached
    /// <c>ApplicationDbContext</c>, deleting the directory would leave a model
    /// snapshot describing tables no code creates — and every developer's next
    /// migration would carry a drop of somebody else's schema.
    /// </summary>
    [Fact]
    public void The_core_model_snapshot_describes_no_LTI_table()
    {
        var snapshot = File.ReadAllText(Path.Combine(
            Root(), "AlgoJudge.Server", "Database", "Migrations",
            "ApplicationDbContextModelSnapshot.cs"));

        foreach (var table in new[]
        {
            "LtiPlatforms", "LtiToolKeys", "LtiResourceLinks", "LtiLineItems",
            "LtiGradeSyncStates", "LtiExternalIdentities", "LtiLaunchStates",
        })
        {
            Assert.DoesNotContain(table, snapshot, StringComparison.Ordinal);
        }
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
}
