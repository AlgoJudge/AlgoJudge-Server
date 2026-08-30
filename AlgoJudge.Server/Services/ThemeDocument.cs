using System.Text.RegularExpressions;
using AlgoJudge.Server.Utils;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// One colour scheme's worth of an instance's theme. <b>Every key optional,
    /// and absent means the product's default</b> — never black, never empty.
    /// </summary>
    public sealed class ThemeColours
    {
        /* Brand. One hex each; the Client generates the ten shades Mantine wants,
           which is why one field reaches a pale tile, a rule and dark text on it. */
        public string? Primary { get; set; }
        public string? Secondary { get; set; }
        public string? Accent { get; set; }

        /// <summary>
        /// Its own key rather than a shade of <see cref="Primary"/>: in an
        /// identity system a link is usually a <b>different hue</b>, not a
        /// lighter version of the brand colour.
        /// </summary>
        public string? Link { get; set; }

        /* Surface and text. */
        public string? Body { get; set; }
        public string? Surface { get; set; }
        public string? Text { get; set; }
        public string? Dimmed { get; set; }
        public string? Border { get; set; }

        /* The shell — where an installation is actually recognised. The hover and
           muted steps are mixed from these in CSS rather than asked for, so they
           track whatever is set here and the form stays short. */
        public string? NavBackground { get; set; }
        public string? NavText { get; set; }
        public string? NavActiveBackground { get; set; }
        public string? NavActiveText { get; set; }
        public string? HeaderBackground { get; set; }
        public string? HeaderText { get; set; }
    }

    /// <summary>One face: a file, and what it is to be used as.</summary>
    public sealed class ThemeFontFace
    {
        public string? Family { get; set; }
        /// <summary>100 to 900, in hundreds. Absent is 400.</summary>
        public int? Weight { get; set; }
        /// <summary><c>normal</c> or <c>italic</c>. Absent is normal.</summary>
        public string? Style { get; set; }
        /// <summary>
        /// The name the file was published under — <c>POST /instance/fonts</c>,
        /// or <c>fonts/&lt;name&gt;</c> in a pre-configuration directory. <b>Never
        /// a URL</b>: the operator names a stored file and this Server builds the
        /// address, so nothing an operator types reaches a stylesheet as one.
        /// </summary>
        public string? File { get; set; }
    }

    /// <summary>
    /// What an instance's theme file states.
    /// <para>
    /// <b>YAML, for the reason the product has already given three times</b> — a
    /// package's <c>config.yml</c>, statement front matter and
    /// <c>algojudge.yml</c>. A fourth serialisation would be a fourth set of
    /// traps for a file a person edits by hand.
    /// </para>
    /// </summary>
    public sealed class ThemeRoot
    {
        public string? Format { get; set; }
        public int? Version { get; set; }
        public ThemeColours? Light { get; set; }
        public ThemeColours? Dark { get; set; }
        public string? FontFamily { get; set; }
        public string? FontFamilyHeadings { get; set; }
        public List<ThemeFontFace>? Fonts { get; set; }
    }

    /// <summary>
    /// Reading, refusing and writing an instance's theme.
    /// <para>
    /// <b>The validation here is a security boundary, not tidiness.</b> Every
    /// value in this document ends up inside a stylesheet the Client builds, so a
    /// field that accepted <c>red; } body { …</c> would be CSS injection through
    /// an installation's own configuration file. A colour is six hexadecimal
    /// digits and nothing else — no <c>rgb()</c>, no keyword, no <c>var()</c> —
    /// and a family name is letters, digits, spaces, hyphens and underscores.
    /// </para>
    /// <para>
    /// <b>An unknown key is refused and named</b>, as
    /// <see cref="Preconfiguration.PreconfigurationFile"/> already refuses one.
    /// A typo quietly ignored is how a configuration file comes to claim
    /// something that is not in force.
    /// </para>
    /// </summary>
    public static class ThemeDocument
    {
        public const string Format = "algojudge-theme";
        public const int Version = 1;

        /// <summary>The name the theme file is published under.</summary>
        public const string ReferenceName = "theme";

        /// <summary>Enough for a family in four weights and their italics.</summary>
        public const int MaxFaces = 12;

        /// <summary>
        /// A theme is a few hundred bytes of colours. The ceiling is here so a
        /// file that is not one is refused before it is parsed rather than after.
        /// </summary>
        public const int MaxBytes = 64 * 1024;

        /// <summary>
        /// The generic families a theme may name without shipping a face for it.
        /// Anything else must be uploaded, because a family this Server has never
        /// heard of resolves to whatever the reader's machine happens to have.
        /// </summary>
        public static readonly IReadOnlyList<string> GenericFamilies =
            ["system-ui", "sans-serif", "serif", "monospace"];

        private static readonly Regex Colour =
            new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        private static readonly Regex FamilyName =
            new("^[A-Za-z0-9 _-]{1,64}$", RegexOptions.Compiled);

        /// <summary>A file name and nothing that could leave the directory.</summary>
        private static readonly Regex FontFileName =
            new(@"^[A-Za-z0-9._-]{1,96}\.woff2$", RegexOptions.Compiled);

        private static readonly HashSet<string> RootKeys =
            ["format", "version", "light", "dark", "fontFamily", "fontFamilyHeadings", "fonts"];

        private static readonly HashSet<string> FaceKeys = ["family", "weight", "style", "file"];

        public static readonly IReadOnlyList<string> ColourKeys = typeof(ThemeColours)
            .GetProperties()
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .ToList();

        private static readonly HashSet<string> ColourKeySet = ColourKeys.ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// <c>wOF2</c>. Checked on the bytes rather than on the declared type,
        /// because the declared type is whatever the uploader said it was.
        /// </summary>
        public static bool IsWoff2(ReadOnlySpan<byte> bytes) =>
            bytes.Length >= 4 && bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x32;

        public const string FontMimeType = "font/woff2";

        /// <summary>
        /// The name a face is published under, or a refusal. <b>A name, never a
        /// path</b>: this string is what a theme's <c>file:</c> matches and what a
        /// pre-configuration directory reads off disk, so anything that could
        /// leave a directory is turned away here rather than further down.
        /// </summary>
        public static string FontFileNameOrRefuse(string name)
        {
            var trimmed = name.Trim();
            if (FontFileName.IsMatch(trimmed)) return trimmed;

            throw new ValidationException(
                $"'{name}' is not a face's name. It is letters, digits, dots, hyphens and "
                + "underscores, ending .woff2 — a name, never a path and never a URL.",
                "font.name");
        }

        /// <summary>
        /// Reads a theme file, or refuses it. <paramref name="stored"/> is the set
        /// of font file names this instance has published — a face naming one that
        /// is not there is refused rather than silently dropped, because a theme
        /// that half applies is worse than one that is turned away.
        /// </summary>
        public static ThemeRoot Parse(string text, IReadOnlySet<string> stored)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            // Loosely first, so this Server names the unknown key rather than the
            // parser reporting a line number for it.
            Dictionary<string, object?>? loose;
            ThemeRoot? root;
            try
            {
                loose = deserializer.Deserialize<Dictionary<string, object?>>(text);
                root = deserializer.Deserialize<ThemeRoot>(text);
            }
            catch (YamlException error)
            {
                throw new ValidationException(
                    $"The theme is not readable as YAML: {error.Message}", "theme.syntax");
            }

            if (loose is null || root is null)
            {
                throw new ValidationException("The theme file is empty.", "theme.empty");
            }

            Unknown(loose.Keys, RootKeys, "");

            if (root.Format != Format)
            {
                throw new ValidationException(
                    $"The theme states format '{root.Format}'. This Server reads '{Format}'.",
                    "theme.format");
            }

            if (root.Version != Version)
            {
                throw new ValidationException(
                    $"The theme states version {root.Version?.ToString() ?? "nothing"}, and this "
                    + $"Server reads version {Version}. Refused rather than guessed: a version it "
                    + "does not know may mean something different by a key it recognises.",
                    "theme.version");
            }

            Section(loose, "light");
            Section(loose, "dark");

            Colours(root.Light, "light");
            Colours(root.Dark, "dark");

            var families = Faces(root, loose, stored);

            root.FontFamily = Family(root.FontFamily, "fontFamily", families);
            root.FontFamilyHeadings = Family(root.FontFamilyHeadings, "fontFamilyHeadings", families);

            return root;

            void Section(Dictionary<string, object?> document, string name)
            {
                if (document.TryGetValue(name, out var section)
                    && section is IDictionary<object, object?> keys)
                {
                    Unknown(keys.Keys.Select(key => key?.ToString() ?? ""), ColourKeySet, name + ".");
                }
            }
        }

        /// <summary>
        /// Writes the canonical form: the one the panel's own form produces, so a
        /// theme saved from a screen and a theme written by hand are the same
        /// document once they have been through here.
        /// </summary>
        public static string Serialise(ThemeRoot theme)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();

            // Stated rather than taken from whatever arrived, so a document this
            // Server wrote always names the format and version it wrote it for.
            theme.Format = Format;
            theme.Version = Version;
            if (theme.Fonts is { Count: 0 }) theme.Fonts = null;

            return serializer.Serialize(theme);
        }

        private static void Unknown(
            IEnumerable<string> present, IReadOnlySet<string> accepted, string prefix)
        {
            if (present.FirstOrDefault(key => !accepted.Contains(key)) is not { } unknown) return;

            throw new ValidationException(
                $"The theme states '{prefix}{unknown}', which this Server does not read. Accepted "
                + $"here: {string.Join(", ", accepted.OrderBy(key => key, StringComparer.Ordinal))}.",
                "theme.key");
        }

        private static void Colours(ThemeColours? colours, string scheme)
        {
            if (colours is null) return;

            foreach (var property in typeof(ThemeColours).GetProperties())
            {
                if (property.GetValue(colours) is not string value) continue;

                var trimmed = value.Trim();
                var key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

                if (trimmed.Length == 0)
                {
                    // Empty is absent. The panel's form sends every field, and an
                    // untouched one is the default rather than a colour.
                    property.SetValue(colours, null);
                    continue;
                }

                if (!Colour.IsMatch(trimmed))
                {
                    throw new ValidationException(
                        $"{scheme}.{key} is '{trimmed}', which is not a colour this Server stores. "
                        + "Six hexadecimal digits after a hash, and nothing else — not a keyword, "
                        + "not rgb(), not var(). These values are written into a stylesheet, and a "
                        + "field that took anything else would let a configuration file carry CSS.",
                        "theme.colour");
                }

                property.SetValue(colours, trimmed.ToLowerInvariant());
            }
        }

        /// <summary>The faces, and the families they make available.</summary>
        private static HashSet<string> Faces(
            ThemeRoot root, Dictionary<string, object?> loose, IReadOnlySet<string> stored)
        {
            var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.Fonts is not { Count: > 0 }) return families;

            if (root.Fonts.Count > MaxFaces)
            {
                throw new ValidationException(
                    $"The theme states {root.Fonts.Count} faces; this Server stores at most {MaxFaces}.",
                    "theme.fonts");
            }

            if (loose.TryGetValue("fonts", out var section) && section is IEnumerable<object?> entries)
            {
                foreach (var entry in entries)
                {
                    if (entry is IDictionary<object, object?> keys)
                    {
                        Unknown(keys.Keys.Select(key => key?.ToString() ?? ""), FaceKeys, "fonts[].");
                    }
                }
            }

            for (var index = 0; index < root.Fonts.Count; index++)
            {
                var face = root.Fonts[index];
                var where = $"fonts[{index}]";

                var family = face.Family?.Trim();
                if (family is not { Length: > 0 } || !FamilyName.IsMatch(family))
                {
                    throw new ValidationException(
                        $"{where}.family is '{face.Family}'. A family is letters, digits, spaces, "
                        + "hyphens and underscores, up to sixty-four of them.",
                        "theme.font.family");
                }

                var file = face.File?.Trim();
                if (file is not { Length: > 0 } || !FontFileName.IsMatch(file))
                {
                    throw new ValidationException(
                        $"{where}.file is '{face.File}'. It names a stored .woff2 file — a name, "
                        + "never a path and never a URL.",
                        "theme.font.file");
                }

                if (!stored.Contains(file))
                {
                    throw new ValidationException(
                        $"{where}.file names '{file}', which this installation has not stored. "
                        + "Upload the face before the theme that uses it: a theme that half applies "
                        + "is worse than one that is turned away.",
                        "theme.font.missing");
                }

                var weight = face.Weight ?? 400;
                if (weight < 100 || weight > 900 || weight % 100 != 0)
                {
                    throw new ValidationException(
                        $"{where}.weight is {weight}. A weight is 100 to 900, in hundreds.",
                        "theme.font.weight");
                }

                var style = (face.Style ?? "normal").Trim().ToLowerInvariant();
                if (style is not ("normal" or "italic"))
                {
                    throw new ValidationException(
                        $"{where}.style is '{face.Style}'. It is normal or italic.",
                        "theme.font.style");
                }

                face.Family = family;
                face.File = file;
                face.Weight = weight;
                face.Style = style;
                families.Add(family);
            }

            return families;
        }

        private static string? Family(string? value, string key, IReadOnlySet<string> declared)
        {
            var family = value?.Trim();
            if (family is not { Length: > 0 }) return null;

            if (!FamilyName.IsMatch(family))
            {
                throw new ValidationException(
                    $"{key} is '{family}'. A family is letters, digits, spaces, hyphens and "
                    + "underscores, up to sixty-four of them.",
                    "theme.font.family");
            }

            if (!declared.Contains(family) && !GenericFamilies.Contains(family, StringComparer.OrdinalIgnoreCase))
            {
                throw new ValidationException(
                    $"{key} names '{family}', for which the theme states no face. Either upload one "
                    + $"and declare it under fonts, or name one of: {string.Join(", ", GenericFamilies)}. "
                    + "A family nothing ships resolves to whatever the reader's machine happens to "
                    + "have, which is not a theme.",
                    "theme.font.undeclared");
            }

            return family;
        }
    }
}
