using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Keeps <c>error-codes.json</c> honest about the words this Server uses for
/// what went wrong.
///
/// <para>
/// <b>The code is a promise.</b> <c>/en/server/api</c> publishes it as the one
/// member of a problem+json body that is stable within a release line, and
/// tells an integrator to switch on it rather than on the status. A promise
/// scattered through string literals all over the Server is one nothing can
/// keep: renaming a concept renames whichever of them the sweep happened to
/// reach.
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
    private sealed record Catalog(string[] Codes);

    /// <summary>
    /// The constructors that take a code, and the base-class calls in
    /// <c>ApiException.cs</c> that fix one. <c>NotFoundException</c> is not here:
    /// its argument names the missing thing, not a code.
    /// </summary>
    private static readonly Regex Raise = new(
        @"\b(ValidationException|ConflictException|ForbiddenActionException"
        + @"|UpstreamException|ApiException)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// A code of several words joined by <c>.</c> or <c>_</c>, wherever it
    /// stands. A message has a space or a capital, so it never matches.
    /// </summary>
    private static readonly Regex Literal = new(
        @"""([a-z][a-zA-Z0-9]*(?:[._][a-zA-Z0-9]+)+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// A one-word code, only as a whole argument or tuple element —
    /// <c>"forbidden"</c>, <c>code ?? "conflict"</c>. A word inside a message
    /// (<c>x ? "neither" : "both"</c>) is part of a sentence, not a code.
    /// </summary>
    private static readonly Regex Word = new(
        @"(?:^|[(,]|\?\?)\s*""([a-z][a-zA-Z0-9]*)""\s*(?=[,)]|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The switch in <c>ProblemDetailsExceptionHandler</c> that answers for
    /// exceptions this Server did not raise — a closed connection, a body that
    /// would not parse. Its codes sit in tuples, not in a constructor.
    /// </summary>
    private static readonly Regex Describe = new(
        @"static\s*\([^)]*\)\s*Describe\(",
        RegexOptions.Compiled);

    [Fact]
    public void The_committed_catalog_names_exactly_what_the_Server_emits()
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
    /// Words the product has stopped using: concepts it renamed, and the British
    /// spellings it replaced with American ones in 0.2.
    /// </summary>
    private static readonly (Regex Word, string Why)[] Renamed =
    [
        (new(@"template", RegexOptions.IgnoreCase), "`PermissionTemplate` became `Role` on 2026-09-13"),
        (new(@"task", RegexOptions.IgnoreCase), "`Task` became `Problem` on 2026-08-03"),
    ];

    /// <summary>
    /// Also read against the whole contract, where <c>isTemplate</c> is a live
    /// field and the concept list above would be wrong.
    /// </summary>
    private static readonly (Regex Word, string Why)[] British =
    [
        // american-english: keep-start — the British spellings these tests forbid
        // No second l may follow, so the American spelling passes.
        (new(@"enrol(?!l)", RegexOptions.IgnoreCase), "American English since 0.2: enroll"),
        (new(@"colour", RegexOptions.IgnoreCase), "American English since 0.2: color"),
        (new(@"catalogue", RegexOptions.IgnoreCase), "American English since 0.2: catalog"),
        (new(@"cancelled|cancelling", RegexOptions.IgnoreCase), "American English since 0.2: canceled"),
        (new(@"licence", RegexOptions.IgnoreCase), "American English since 0.2: license"),
        (new(@"behaviour", RegexOptions.IgnoreCase), "American English since 0.2: behavior"),
        (new(@"(organ|normal|serial|synchron|recogn|local|author|summar|sanit|optim|anonym)is(e|ed|es|ing|er|ation)", RegexOptions.IgnoreCase),
            "American English since 0.2: -ize"),
        // american-english: keep-end
    ];

    /// <summary>
    /// A code that names a concept the product has renamed is the defect this
    /// census exists for, and it is invisible to the test above once the
    /// catalog has been regenerated. So the vocabulary itself is asserted.
    /// </summary>
    [Fact]
    public void No_code_names_a_concept_that_was_renamed_away()
    {
        var offending = Emitted()
            .SelectMany(code => Renamed.Concat(British)
                .Where(g => g.Word.IsMatch(code))
                .Select(g => $"{code} ({g.Why})"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offending.Length == 0,
            "An error code names a concept this product renamed: ["
            + string.Join("; ", offending)
            + "]. An integrator switching on it reads a vocabulary the Server "
            + "itself refuses.");
    }

    /// <summary>
    /// The routes, fields and schema names a client is generated from. The census
    /// above reads codes only; this reads the whole published contract.
    /// </summary>
    [Fact]
    public void The_published_contract_uses_none_of_those_words()
    {
        var contract = File.ReadAllText(Path.Combine(Root(), "openapi.json"));
        var found = British
            .SelectMany(g => g.Word.Matches(contract).Select(m => $"{m.Value} ({g.Why})"))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(found.Length == 0,
            "openapi.json uses a word this product no longer does: ["
            + string.Join("; ", found)
            + "]. A generated client would carry it into somebody else's code.");
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
            var describe = Describe.Match(source);
            if (describe.Success)
            {
                Collect(source[describe.Index..], codes);
            }

            foreach (Match raise in Raise.Matches(source))
            {
                Collect(Arguments(source, raise.Index + raise.Length), codes);
            }
        }

        return [.. codes];
    }

    /// <summary>
    /// The codes in a stretch of source: several-word ones anywhere, one-word
    /// ones only where they stand as a whole argument.
    /// </summary>
    private static void Collect(string text, ISet<string> codes)
    {
        foreach (Match literal in Literal.Matches(text)) codes.Add(literal.Groups[1].Value);
        foreach (Match word in Word.Matches(text)) codes.Add(word.Groups[1].Value);
    }

    /// <summary>The argument list starting just inside its opening parenthesis.</summary>
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

    private static Catalog Read()
    {
        var json = File.ReadAllText(Path.Combine(Root(), "error-codes.json"));
        return JsonSerializer.Deserialize<Catalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
