using AlgoJudge.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// The ceilings, in one place so that two endpoints cannot disagree about
    /// what a package is.
    /// </summary>
    public static class UploadLimits
    {
        /// <summary>A problem package: the largest thing this product accepts.</summary>
        public const long Package = 128L * 1024 * 1024;

        /// <summary>
        /// A submission.
        /// <para>
        /// <b>Sixteen times smaller, and it was not enforced before this.</b> The
        /// only ceiling in the code was the global one, so a participant could
        /// send a hundred-megabyte "submission" and the Server would store it.
        /// An activity may set something smaller still; this is the outer wall.
        /// </para>
        /// </summary>
        public const long Submission = 8L * 1024 * 1024;
    }

    /// <summary>
    /// One file part and the fields around it, read straight off the socket.
    /// </summary>
    public sealed record MultipartUpload<TFile>
    {
        /// <summary>Every non-file field, by name. Last one wins, as form semantics have it.</summary>
        public required IReadOnlyDictionary<string, string> Fields { get; init; }

        /// <summary>Whatever the handler made of the file part, or null when there was none.</summary>
        public TFile? File { get; init; }

        public string? FileName { get; init; }
        public string? ContentType { get; init; }

        public string Field(string name) => Fields.TryGetValue(name, out var value) ? value : "";
    }

    /// <summary>
    /// Reads a multipart body without ever holding the file in it.
    /// <para>
    /// <b><c>IFormFile</c> is not an option here</b>, which is the whole reason
    /// this exists: model binding buffers any part over 64 KB into a temporary
    /// file of its own before the action runs, and the handler then writes the
    /// bytes a second time. For a 128 MiB package that is two full copies and a
    /// spool the Server does not control.
    /// </para>
    /// </summary>
    public static class MultipartUpload
    {
        /// <summary>What to do with the file part, while it is going past.</summary>
        public delegate Task<TFile> FileHandler<TFile>(
            Stream content, string fileName, string contentType, CancellationToken ct);

        /// <summary>
        /// Walks the body once, in the order it arrives.
        /// <para>
        /// <b>Field order is not a contract.</b> The file part is handed to
        /// <paramref name="onFile"/> the moment it appears, and the fields that
        /// describe it may arrive on either side — the Client's own upload form
        /// appends <c>file</c> before <c>sha256</c>. Anything that needs a field
        /// in order to decide about the bytes has to decide after this returns.
        /// </para>
        /// </summary>
        public static async Task<MultipartUpload<TFile>> ReadAsync<TFile>(
            HttpRequest request,
            long maxFileBytes,
            FileHandler<TFile> onFile,
            CancellationToken ct)
        {
            if (!IsMultipart(request.ContentType))
            {
                throw new ValidationException(
                    "This endpoint takes a multipart form", "request.notMultipart");
            }

            var boundary = HeaderUtilities.RemoveQuotes(
                MediaTypeHeaderValue.Parse(request.ContentType).Boundary).Value;

            if (string.IsNullOrEmpty(boundary))
            {
                throw new ValidationException("The multipart body has no boundary", "request.malformed");
            }

            var reader = new MultipartReader(boundary, request.Body)
            {
                // Ours to set, because nothing else bounds it now that model
                // binding is out of the way.
                BodyLengthLimit = maxFileBytes + FieldsHeadroom,
            };

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var file = default(TFile);
            string? fileName = null;
            string? contentType = null;

            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                if (HasFile(disposition))
                {
                    if (file is not null)
                    {
                        throw new ValidationException(
                            "Only one file may be sent at a time", "file.tooMany");
                    }

                    fileName = HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "";
                    contentType = section.ContentType ?? "application/octet-stream";

                    // The ceiling rides on the stream, so it is enforced against
                    // bytes that actually arrived rather than against a declared
                    // length — and it stops the read rather than the store.
                    await using var limited = new LimitedStream(section.Body, maxFileBytes);
                    file = await onFile(limited, fileName, contentType, ct);
                }
                else if (HasFormData(disposition))
                {
                    var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                    if (name.Length == 0) continue;

                    // Bounded: a field is a word, not a payload. Without this a
                    // body made entirely of fields would have no ceiling at all.
                    using var text = new StreamReader(
                        new LimitedStream(section.Body, MaxFieldBytes), System.Text.Encoding.UTF8);
                    fields[name] = await text.ReadToEndAsync(ct);
                }
            }

            return new MultipartUpload<TFile>
            {
                Fields = fields, File = file, FileName = fileName, ContentType = contentType,
            };
        }

        /// <summary>
        /// A single form field. Generous for pasted source, which travels as one
        /// on the submission endpoint, and far below anything that is a file.
        /// </summary>
        private const long MaxFieldBytes = 1L * 1024 * 1024;

        /// <summary>
        /// Room for the fields and the multipart framing beside the file itself,
        /// so that a body exactly at the file ceiling is not refused for its own
        /// boundaries.
        /// </summary>
        private const long FieldsHeadroom = 8L * 1024 * 1024;

        private static bool IsMultipart(string? contentType) =>
            !string.IsNullOrEmpty(contentType)
            && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

        private static bool HasFile(ContentDispositionHeaderValue disposition) =>
            disposition.DispositionType.Equals("form-data")
            && (!string.IsNullOrEmpty(disposition.FileName.Value)
                || !string.IsNullOrEmpty(disposition.FileNameStar.Value));

        private static bool HasFormData(ContentDispositionHeaderValue disposition) =>
            disposition.DispositionType.Equals("form-data")
            && string.IsNullOrEmpty(disposition.FileName.Value)
            && string.IsNullOrEmpty(disposition.FileNameStar.Value);
    }

    /// <summary>
    /// Stops MVC from reading the form before the action does.
    /// <para>
    /// Without this the framework binds the body eagerly whenever an action's
    /// parameters look like a form — and once it has, the request body is
    /// consumed and <c>MultipartReader</c> finds nothing. The standard recipe for
    /// streamed uploads, and it has to be on every endpoint that streams one.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var providers = context.ValueProviderFactories;
            providers.RemoveType<FormValueProviderFactory>();
            providers.RemoveType<FormFileValueProviderFactory>();
            providers.RemoveType<JQueryFormValueProviderFactory>();
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
