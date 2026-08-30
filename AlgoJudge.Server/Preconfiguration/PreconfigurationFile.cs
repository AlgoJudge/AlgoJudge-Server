using System.Text;
using System.Text.RegularExpressions;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AlgoJudge.Server.Preconfiguration
{
    /// <summary>
    /// The settings a file may state, all of them optional.
    /// <para>
    /// <b>Absent means leave alone</b>, never "reset to the default" — the rule
    /// the whole feature rests on. A <c>null</c> written out explicitly means the
    /// same thing, because apply never withdraws and there is nothing for a third
    /// meaning to do.
    /// </para>
    /// </summary>
    public sealed class InstanceSection
    {
        public string? Name { get; set; }
        public bool? LocalRegistrationEnabled { get; set; }
        public bool? RequireEmail { get; set; }
        public bool? RequireConfirmedEmail { get; set; }
        public bool? ShowLogo { get; set; }
        public bool? ShowLocalSignIn { get; set; }
        public bool? AccountDeletionEnabled { get; set; }
        public bool? ExternalJudgingEnabled { get; set; }
        public bool? SeriesRestrictionsEnabled { get; set; }
        public List<string>? ExternalFetchHosts { get; set; }
    }

    public sealed class PreconfigurationRoot
    {
        public string? Format { get; set; }
        public int? Version { get; set; }
        public InstanceSection? Instance { get; set; }
    }

    /// <summary>
    /// One file on disk, read and checksummed.
    /// <para>
    /// <c>Kind</c> is the name it is <b>published under</b>, which is the
    /// document kind for a page (<c>welcome</c>) and the mark's own name for a
    /// logo (<c>logo-en</c>) — so for a logo the language appears twice, once
    /// inside <c>Kind</c> and once beside it. That is
    /// <c>IDocumentService.PublishAsync</c>'s existing convention, not a
    /// second one.
    /// </para>
    /// </summary>
    public sealed record PreconfigurationFileBytes(
        string FileName, string Kind, string? Language, string MimeType, byte[] Bytes, string Sha256);

    /// <summary>Everything the directory holds, validated.</summary>
    public sealed record PreconfigurationSource(
        string Directory,
        InstanceSection Instance,
        IReadOnlyList<PreconfigurationFileBytes> Pages,
        IReadOnlyList<PreconfigurationFileBytes> Logos,
        IReadOnlyList<PreconfigurationFileBytes> Fonts,
        PreconfigurationFileBytes? Theme,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Reads the directory an installation is configured from, and refuses it
    /// rather than guessing.
    /// <para>
    /// Everything here fails at read time, while somebody is watching the
    /// deployment — an unknown key, a page named after nothing, a variable that
    /// resolves to nothing. The one thing that is a <b>warning</b> rather than a
    /// refusal is front matter, because the Server does not parse stored content
    /// and must not start now.
    /// </para>
    /// </summary>
    public static class PreconfigurationFile
    {
        /// <summary><c>AJ_Preconfiguration__Path</c>, by the prefix the Server already reads.</summary>
        public const string PathSetting = "Preconfiguration:Path";

        public const string Format = "algojudge-preconfiguration";
        public const int Version = 1;
        public const string FileName = "algojudge.yml";
        public const string PagesDirectory = "pages";

        /// <summary>
        /// The colours and typeface, beside <c>pages/</c> rather than under
        /// <c>instance:</c>. It is its own document with its own format and
        /// version — the same reason a page is a file rather than a string in a
        /// key — and the file here is the file the panel publishes, byte for
        /// byte, so the checksum comparison that makes re-applying safe works on
        /// it unchanged.
        /// </summary>
        public const string ThemeFileName = "theme.yml";

        /// <summary>The faces the theme draws with, one file per face.</summary>
        public const string FontsDirectory = "fonts";

        /// <summary>The file name a mark is read from, and the reference name it lands on.</summary>
        public static readonly string LogoStem = DocumentService.LogoName(null);

        /// <summary>
        /// What an <b>instance</b> may publish. <c>rules</c> is missing on
        /// purpose: it belongs to an activity, not to the installation.
        /// </summary>
        public static readonly IReadOnlyList<string> Kinds =
            ["welcome", "home", "terms", "privacy", "cookies", "accessibility"];

        private static readonly Dictionary<string, string> LogoTypes = new()
        {
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
        };

        private static readonly HashSet<string> RootKeys = ["format", "version", "instance"];

        private static readonly HashSet<string> InstanceKeys = typeof(InstanceSection)
            .GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);

        /// <summary><c>${VAR}</c>, and nothing cleverer. No defaults, no nesting.</summary>
        private static readonly Regex Variable =
            new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

        public static PreconfigurationSource Read(string directory)
        {
            if (!Directory.Exists(directory))
            {
                throw new ValidationException(
                    $"{PathSetting} names '{directory}', which is not a directory this Server can "
                    + "see. On a container it is a mount, so this is usually a volume declared in "
                    + "one place and not the other.",
                    "preconfiguration.directory");
            }

            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                throw new ValidationException(
                    $"'{directory}' holds no {FileName}. That file states the format and the "
                    + "version, so a directory without it cannot be read at all.",
                    "preconfiguration.missing");
            }

            var root = Parse(File.ReadAllText(path));
            var warnings = new List<string>();
            var fonts = Fonts(directory);

            return new PreconfigurationSource(
                directory,
                Expanded(root.Instance ?? new InstanceSection()),
                Pages(directory, warnings),
                Logos(directory),
                fonts,
                Theme(directory, fonts),
                warnings);
        }

        /// <summary>
        /// The theme, refused here rather than at publish time — everything in
        /// this directory fails while somebody is watching the deployment.
        /// </summary>
        private static PreconfigurationFileBytes? Theme(
            string directory, List<PreconfigurationFileBytes> fonts)
        {
            var path = Path.Combine(directory, ThemeFileName);
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > ThemeDocument.MaxBytes)
            {
                throw new ValidationException(
                    $"'{ThemeFileName}' is {bytes.Length} bytes and the ceiling is "
                    + $"{ThemeDocument.MaxBytes}. A theme is a few hundred bytes of colours.",
                    "preconfiguration.theme");
            }

            // Against the faces this directory carries, not against what the
            // installation already holds: a directory has to be readable on its
            // own, or an apply would depend on what happened to be there first.
            ThemeDocument.Parse(
                Encoding.UTF8.GetString(bytes),
                fonts.Select(font => font.Kind).ToHashSet(StringComparer.Ordinal));

            return new PreconfigurationFileBytes(
                ThemeFileName, ThemeDocument.ReferenceName, null, "application/yaml", bytes,
                IFileService.Checksum(bytes));
        }

        private static List<PreconfigurationFileBytes> Fonts(string directory)
        {
            var fonts = new List<PreconfigurationFileBytes>();
            var folder = Path.Combine(directory, FontsDirectory);
            if (!Directory.Exists(folder)) return fonts;

            foreach (var path in Directory
                .EnumerateFiles(folder)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                var bytes = File.ReadAllBytes(path);

                // The same two refusals the endpoint makes, for the same reasons:
                // the name is what a theme calls the face by, and the type is
                // read off the bytes because every visitor's browser fetches it.
                var stem = ThemeDocument.FontFileNameOrRefuse(name);

                if (!ThemeDocument.IsWoff2(bytes))
                {
                    throw new ValidationException(
                        $"'{FontsDirectory}/{name}' does not begin wOF2, so it is not a WOFF2 face "
                        + "whatever it is called.",
                        "preconfiguration.font");
                }

                fonts.Add(new PreconfigurationFileBytes(
                    name, stem, null, ThemeDocument.FontMimeType, bytes, IFileService.Checksum(bytes)));
            }

            return fonts;
        }

        private static PreconfigurationRoot Parse(string text)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            // Read loosely first, so an unknown key is named by this Server
            // rather than by the parser. A typo silently ignored is the classic
            // way a configuration file lies about what is in force.
            Dictionary<string, object?>? loose;
            PreconfigurationRoot? root;
            try
            {
                loose = deserializer.Deserialize<Dictionary<string, object?>>(text);
                root = deserializer.Deserialize<PreconfigurationRoot>(text);
            }
            catch (YamlException error)
            {
                throw new ValidationException(
                    $"{FileName} is not readable as YAML: {error.Message}",
                    "preconfiguration.syntax");
            }

            if (loose is null || root is null)
            {
                throw new ValidationException($"{FileName} is empty.", "preconfiguration.empty");
            }

            Unknown(loose.Keys, RootKeys, "");

            if (root.Format != Format)
            {
                throw new ValidationException(
                    $"{FileName} states format '{root.Format}'. This Server reads '{Format}'.",
                    "preconfiguration.format");
            }

            if (root.Version != Version)
            {
                throw new ValidationException(
                    $"{FileName} states version {root.Version?.ToString() ?? "nothing"}, and this "
                    + $"Server reads version {Version}. Refused rather than guessed: a version it "
                    + "does not know may mean something different by a key it recognises.",
                    "preconfiguration.version");
            }

            if (loose.TryGetValue("instance", out var section)
                && section is IDictionary<object, object?> keys)
            {
                Unknown(keys.Keys.Select(key => key?.ToString() ?? ""), InstanceKeys, "instance.");
            }

            return root;
        }

        private static void Unknown(
            IEnumerable<string> present, IReadOnlySet<string> accepted, string prefix)
        {
            if (present.FirstOrDefault(key => !accepted.Contains(key)) is not { } unknown) return;

            throw new ValidationException(
                $"{FileName} states '{prefix}{unknown}', which this Server does not read. Accepted "
                + $"here: {string.Join(", ", accepted.OrderBy(key => key, StringComparer.Ordinal))}.",
                "preconfiguration.key");
        }

        /// <summary>
        /// <c>${VAR}</c> from the environment, in the string values only.
        /// <para>
        /// <b>The file names a secret; it never carries one.</b> Nothing in this
        /// version of the format is secret, and the mechanism is here anyway — so
        /// the rule exists before the first secret arrives rather than after
        /// somebody has already committed one.
        /// </para>
        /// <para>
        /// Expanded <b>after</b> parsing rather than over the text, so a value
        /// that happens to contain a colon or a hash cannot change the shape of
        /// the document it lands in.
        /// </para>
        /// </summary>
        private static InstanceSection Expanded(InstanceSection instance)
        {
            instance.Name = Expand(instance.Name, "instance.name");
            instance.ExternalFetchHosts = instance.ExternalFetchHosts
                ?.Select((host, index) => Expand(host, $"instance.externalFetchHosts[{index}]")!)
                .ToList();
            return instance;
        }

        private static string? Expand(string? value, string field)
        {
            if (value is null) return null;

            return Variable.Replace(value, match =>
            {
                var name = match.Groups[1].Value;
                return Environment.GetEnvironmentVariable(name) is { Length: > 0 } resolved
                    ? resolved
                    : throw new ValidationException(
                        $"{field} names ${{{name}}}, and this Server's environment has no such "
                        + "variable. Refused rather than stored as written: an installation whose "
                        + "settings carry the text of a variable name is worse than one that would "
                        + "not start.",
                        "preconfiguration.variable");
            });
        }

        private static List<PreconfigurationFileBytes> Pages(string directory, List<string> warnings)
        {
            var pages = new List<PreconfigurationFileBytes>();
            var folder = Path.Combine(directory, PagesDirectory);
            if (!Directory.Exists(folder)) return pages;

            foreach (var path in Directory
                .EnumerateFiles(folder, "*.md")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var (kind, language) = Split(Path.GetFileNameWithoutExtension(path));

                if (!Kinds.Contains(kind))
                {
                    throw new ValidationException(
                        $"'{PagesDirectory}/{Path.GetFileName(path)}' is named after no document "
                        + $"this installation publishes. The kinds are: {string.Join(", ", Kinds)}. "
                        + "A translation is <kind>-<language>.md.",
                        "preconfiguration.kind");
                }

                var bytes = File.ReadAllBytes(path);
                pages.Add(new PreconfigurationFileBytes(
                    Path.GetFileName(path), kind, language, "text/markdown", bytes,
                    IFileService.Checksum(bytes)));

                // A warning, not a refusal. The Client refuses a statement whose
                // front matter states no version, and an operator should hear
                // that while somebody is still watching — but the Server does not
                // parse stored content and does not start here.
                if (!HasFrontMatter(bytes))
                {
                    warnings.Add(
                        $"{PagesDirectory}/{Path.GetFileName(path)} states no front matter version. "
                        + "The Client refuses to render a document without one. See "
                        + "docs/specs/CONTENT_FORMAT.md.");
                }
            }

            return pages;
        }

        private static List<PreconfigurationFileBytes> Logos(string directory)
        {
            var logos = new List<PreconfigurationFileBytes>();

            foreach (var path in Directory
                .EnumerateFiles(directory, LogoStem + "*")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var (stem, language) = Split(Path.GetFileNameWithoutExtension(path));
                if (stem != LogoStem) continue;

                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (!LogoTypes.TryGetValue(extension, out var mimeType))
                {
                    throw new ValidationException(
                        $"'{Path.GetFileName(path)}' is a mark this Server will not store: the "
                        + $"types are {string.Join(", ", LogoTypes.Keys)}.",
                        "preconfiguration.logo");
                }

                var bytes = File.ReadAllBytes(path);
                logos.Add(new PreconfigurationFileBytes(
                    Path.GetFileName(path), DocumentService.LogoName(language), language, mimeType, bytes,
                    IFileService.Checksum(bytes)));
            }

            return logos;
        }

        /// <summary>
        /// The stem, then the language — split on the <b>first</b> hyphen, so a
        /// subtag carrying one of its own (<c>pt-BR</c>) survives whole.
        /// </summary>
        private static (string Stem, string? Language) Split(string name)
        {
            var hyphen = name.IndexOf('-');
            return hyphen < 0
                ? (name, null)
                : (name[..hyphen], name[(hyphen + 1)..] is { Length: > 0 } language ? language : null);
        }

        /// <summary>
        /// Whether the document opens with front matter stating a version.
        /// <b>Shape, not parsing</b>: the delimiters and one key.
        /// </summary>
        private static bool HasFrontMatter(byte[] bytes)
        {
            var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
            if (!text.StartsWith("---", StringComparison.Ordinal)) return false;

            var end = text.IndexOf("\n---", StringComparison.Ordinal);
            return end > 0 && text[..end].Contains("version:", StringComparison.Ordinal);
        }
    }
}
