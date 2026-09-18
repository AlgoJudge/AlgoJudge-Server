using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Keeps <c>error-codes.json</c> honest about the words this Server uses for
/// what went wrong.
///
/// <para>
/// <b>The code is a promise.</b> <c>/en/server/api</c> publishes it as the one
/// member of a problem+json body that is stable across releases, and tells an
/// integrator to switch on it rather than on the status. A promise scattered
/// through a hundred and eighty-nine string literals is one nothing can keep:
/// renaming a concept renames whichever of them the sweep happened to reach.
/// </para>
/// <para>
/// That is not hypothetical. <c>PermissionTemplate</c> became <c>Role</c>, and
/// the sweep renamed <c>provider.rule.template.required</c> and
/// <c>provider.rule.template.unknown</c> and left
/// <c>provider.defaultTemplate.required</c> beside them — a code naming a thing
/// the product no longer had, next to two of its own siblings that had moved.
/// Nothing failed. This is what would have.
/// </para>
/// <para>
/// A rename is still allowed; it is a line in this file and a line in a release
/// note. What is no longer allowed is a rename nobody decided.
/// </para>
/// </summary>
public class ErrorCodeCensusTests
{
    private sealed record Catalogue(string[] Codes);

    /// <summary>
    /// The constructors that take a code. Anything raising a problem+json body
    /// goes through one of them.
    /// </summary>
    private static readonly Regex Raise = new(
        @"\b(ValidationException|ConflictException|NotFoundException|ForbiddenException"
        + @"|UnauthorizedException|ApiException|ChecksumException)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex Literal = new(
        @"""([a-z][a-zA-Z0-9]*(?:[._][a-zA-Z0-9]+)+)""",
        RegexOptions.Compiled);

    [Fact]
    public void The_committed_catalogue_names_exactly_what_the_Server_emits()
    {
        var emitted = Emitted();
        var committed = Read().Codes.OrderBy(c => c, StringComparer.Ordinal).ToArray();

        var missing = emitted.Except(committed).ToArray();
        var extra = committed.Except(emitted).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            "error-codes.json has drifted from the source. Emitted and uncommitted: "
            + $"[{string.Join(", ", missing)}]. Committed and never emitted: "
            + $"[{string.Join(", ", extra)}]. A code is published as stable, so either "
            + "put the new name here deliberately and say so in the release note, or "
            + "put the old name back.");
    }

    /// <summary>
    /// A code that names a concept the product has renamed is the defect this
    /// census exists for, and it is invisible to the test above once the
    /// catalogue has been regenerated. So the vocabulary itself is asserted.
    /// </summary>
    [Fact]
    public void No_code_names_a_concept_that_was_renamed_away()
    {
        var gone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["template"] = "`PermissionTemplate` became `Role` on 2026-09-13",
            ["task"] = "`Task` became `Problem` on 2026-08-03",
        };

        var offending = Emitted()
            .SelectMany(code => gone
                .Where(g => code.Contains(g.Key, StringComparison.OrdinalIgnoreCase))
                .Select(g => $"{code} ({g.Value})"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offending.Length == 0,
            "An error code names a concept this product renamed: ["
            + string.Join("; ", offending)
            + "]. An integrator switching on it reads a vocabulary the Server "
            + "itself refuses.");
    }

    /// <summary>Every code raised anywhere under <c>AlgoJudge.Server</c>.</summary>
    private static string[] Emitted()
    {
        var root = Root();
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root, "AlgoJudge.Server"), "*.cs", SearchOption.AllDirectories))
        {
            // Migrations carry model names rather than wire vocabulary, and a
            // historical migration is never edited.
            if (path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match raise in Raise.Matches(source))
            {
                var arguments = Arguments(source, raise.Index + raise.Length);
                foreach (Match literal in Literal.Matches(arguments))
                {
                    var value = literal.Groups[1].Value;
                    if (!value.Contains(' ', StringComparison.Ordinal))
                    {
                        codes.Add(value);
                    }
                }
            }
        }

        return [.. codes];
    }

    /// <summary>The argument list starting just inside its opening bracket.</summary>
    private static string Arguments(string source, int start)
    {
        var depth = 1;
        var i = start;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')') depth--;
            i++;
        }
        return source[start..Math.Max(start, i - 1)];
    }

    /// <summary>The repository root, found by the file that sits in it.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "error-codes.json")))
        {
            directory = directory.Parent;
        }
        Assert.True(directory is not null, "error-codes.json was not found above the test assembly");
        return directory!.FullName;
    }

    private static Catalogue Read()
    {
        var json = File.ReadAllText(Path.Combine(Root(), "error-codes.json"));
        return JsonSerializer.Deserialize<Catalogue>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
