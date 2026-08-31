using Microsoft.Net.Http.Headers;
using DbFile = AlgoJudge.Server.Database.Models.File;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// What the Server says a stored file is, when it hands the bytes over.
    /// <para>
    /// <b>Both download endpoints used to answer with the uploader's own
    /// words.</b> <c>FileService.CommitAsync</c> stores <c>MimeType</c> exactly
    /// as the multipart part declared it — the only normalisation is blank
    /// becoming <c>application/octet-stream</c> — so a file uploaded as
    /// <c>text/html</c> was served as <c>text/html</c>, on the API origin, from
    /// an endpoint that is anonymous for anything an instance document points
    /// at. The edge's <c>nosniff</c> does not help: the type is not being
    /// guessed, it is being declared.
    /// </para>
    /// <para>
    /// <b>And the name could be empty.</b> <c>MultipartUpload</c> treats a part
    /// as a file if either <c>filename</c> or <c>filename*</c> is present but
    /// only ever read the first, so a part carrying just <c>filename*=</c> was
    /// stored with none — and MVC omits <c>Content-Disposition</c> entirely when
    /// the download name is empty, which is what turned the declared type into a
    /// rendered page.
    /// </para>
    /// <para>
    /// So: an allowlist decides what may be <i>said</i>, and everything outside
    /// it is bytes; a disposition is always written; and only PDF is
    /// <c>inline</c>. This is the same shape as
    /// <c>PreconfigurationFile.LogoTypes</c> — a small map with a refusal for
    /// anything absent — rather than a new idea.
    /// </para>
    /// </summary>
    public static class Downloads
    {
        /// <summary>What a file with no usable type is.</summary>
        private const string Bytes = "application/octet-stream";

        /// <summary>
        /// The types served under their own name. Anything else is
        /// <see cref="Bytes"/>, whatever it was stored as.
        /// <para>
        /// <c>image/svg+xml</c> is here <b>and</b> is never inline, and the
        /// combination is deliberate: <c>&lt;img src&gt;</c> ignores
        /// <c>Content-Disposition</c>, so an instance logo still renders, while a
        /// top-level navigation to the same address downloads rather than
        /// executing the script an SVG may carry. Collapsing it to
        /// <see cref="Bytes"/> instead would break the logo, because
        /// <c>nosniff</c> makes a browser refuse an <c>&lt;img&gt;</c> whose type
        /// is not an image.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> Rendered = new(StringComparer.Ordinal)
        {
            "application/pdf",
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp",
            "image/svg+xml",
            "font/woff2",
            "font/woff",
            "text/plain",
            "text/markdown",
        };

        /// <summary>
        /// The one type shown in place rather than offered.
        /// <para>
        /// A statement is read in an <c>&lt;object data&gt;</c>, and a browser
        /// honours <c>attachment</c> even there — so a PDF served as an
        /// attachment falls through to the download link beside it. Everything
        /// else is safer offered.
        /// </para>
        /// </summary>
        private const string Shown = "application/pdf";

        /// <summary>The longest name put on a response.</summary>
        private const int MaxName = 128;

        /// <summary>The media type, with any parameters cut off.</summary>
        internal static string MediaType(string? stored)
        {
            var value = (stored ?? "").Split(';')[0].Trim().ToLowerInvariant();
            return value.Length > 0 ? value : Bytes;
        }

        /// <summary>What to answer with, which is never what the uploader said unless we render it.</summary>
        public static string ContentType(DbFile file) =>
            MediaType(file.MimeType) is { } type && Rendered.Contains(type) ? type : Bytes;

        /// <summary>
        /// A name, never a path, and never nothing — the same doctrine as
        /// <c>ThemeDocument.FontFileNameOrRefuse</c>, except that this cannot
        /// refuse: the rows are already stored.
        /// </summary>
        internal static string NameOrDefault(DbFile file)
        {
            var cleaned = new string([.. (file.Name ?? "").Trim()
                .Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/'))]);

            if (cleaned.Length > MaxName) cleaned = cleaned[..MaxName];

            return cleaned.Length > 0 ? cleaned : $"{file.Id}.bin";
        }

        /// <summary>
        /// The whole header, built by the framework's own RFC 6266 encoder rather
        /// than by pasting a name into a string.
        /// </summary>
        public static string Disposition(DbFile file)
        {
            var header = new ContentDispositionHeaderValue(
                string.Equals(MediaType(file.MimeType), Shown, StringComparison.Ordinal)
                    ? "inline"
                    : "attachment");

            header.SetHttpFileName(NameOrDefault(file));
            return header.ToString();
        }
    }
}
