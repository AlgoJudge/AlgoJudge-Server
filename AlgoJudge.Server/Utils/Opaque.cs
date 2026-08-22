using System.Text.Json;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// The ceilings on values the Server stores and never reads, in one place so
    /// that four write paths cannot disagree about what a document is.
    /// <para>
    /// The two numbers are set by <b>fan-out, not by importance</b>
    /// (<c>docs/specs/OPAQUE_DOCUMENTS.md</c>): a document is carried one at a
    /// time, while <c>extra</c> rides the results feed once per submission per
    /// contestant — a contest of 300 teams over 11 problems with 8 attempts is
    /// 26 000 rows in one response, already ~52 MB at the smaller ceiling.
    /// </para>
    /// </summary>
    public static class OpaqueLimits
    {
        /// <summary>Carried one at a time: <c>config</c>, <c>props</c>, <c>spec</c>.</summary>
        public const int Document = 256 * 1024;

        /// <summary>Carried in a list: <c>extra</c>, on every row of a board.</summary>
        public const int Board = 2 * 1024;
    }

    /// <summary>
    /// The one gate in front of every value the Server stores without reading.
    /// <para>
    /// <b>Specified 2026-08-07 and unimplemented until 2026-08-22.</b>
    /// `OPAQUE_DOCUMENTS.md` has required both of these since it was accepted:
    /// an envelope that is an envelope, and a ceiling. Only <c>extra</c>'s 2 kB
    /// existed, in one method, so a manager could store a hundred megabytes of
    /// JSON on an assignment — the storage and denial-of-service surface that
    /// specification was written to close, left open by the specification's own
    /// implementation.
    /// </para>
    /// <para>
    /// <b>Neither check reads the document.</b> One asks whether the envelope is
    /// an envelope; the other asks how big it is. What is inside stays the
    /// problem type's business, which is the whole point of the field.
    /// </para>
    /// </summary>
    public static class Opaque
    {
        /// <summary>
        /// Checks a value and returns what to store, or throws.
        /// <para>
        /// Returns <c>null</c> for an absent value and never <c>"{}"</c>: an
        /// empty object is not a second way of saying nothing, and two spellings
        /// of one fact drift.
        /// </para>
        /// </summary>
        /// <param name="value">The value as it was bound from the request.</param>
        /// <param name="field">Its name on the wire, for the message a caller reads.</param>
        /// <param name="ceiling">One of <see cref="OpaqueLimits"/>.</param>
        public static string? Store(object? value, string field, int ceiling = OpaqueLimits.Document)
        {
            if (value is null) return null;
            return Checked(JsonSerializer.SerializeToUtf8Bytes(value), field, ceiling);
        }

        /// <summary>
        /// The same gate, for a document that arrived as <b>text</b>.
        /// <para>
        /// A submission is multipart, because the bytes travel with it, and every
        /// part of a multipart form is a string — including the one carrying a
        /// JSON document. So this one may also fail to parse, which a value bound
        /// from a JSON body never can.
        /// </para>
        /// </summary>
        public static string? StoreText(string? json, string field, int ceiling = OpaqueLimits.Document)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return Checked(System.Text.Encoding.UTF8.GetBytes(json), field, ceiling);
        }

        /// <summary>The two rules, in one place, whichever door the value came through.</summary>
        private static string Checked(byte[] bytes, string field, int ceiling)
        {
            // **Over the ceiling is refused, never truncated.** Half a JSON
            // document is not a document, and a Server that trimmed one would be
            // storing something no renderer can read while reporting success.
            if (bytes.Length > ceiling)
            {
                throw new ValidationException(
                    $"`{field}` is {bytes.Length} bytes, over the {ceiling / 1024} kB ceiling",
                    "opaque.tooLarge");
            }

            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(bytes);
            }
            catch (JsonException e)
            {
                throw new ValidationException($"`{field}` is not JSON: {e.Message}", "opaque.notJson");
            }

            // An object, or absent. Not a scalar, not an array — every reader
            // guards the shape before using it, and the guard should match what
            // can actually arrive.
            using (parsed)
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ValidationException(
                        $"`{field}` must be an object or absent, not {Named(parsed.RootElement.ValueKind)}",
                        "opaque.notAnObject");
                }

                return JsonSerializer.Serialize(parsed.RootElement);
            }
        }

        /// <summary>What a caller called it, rather than what .NET calls it.</summary>
        private static string Named(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Array => "an array",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }
}
