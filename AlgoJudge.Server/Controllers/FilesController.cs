using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// One file endpoint for everything except a submission, which keeps its
    /// upload in one request — a participant sending a file that never became a
    /// submission is a worse thing to explain in the minute before a deadline
    /// than one endpoint behaving differently.
    /// </summary>
    [ApiController]
    [Route("files")]
    [Authorize]
    public class FilesController(IFileService files, IExternalFetchService fetching) : ControllerBase
    {
        /// <summary>
        /// Fetches an address the installation allows, and answers with the file
        /// it became.
        /// <para>
        /// <b>The same file as an upload, by the same route.</b> The bytes are
        /// staged and committed exactly as somebody's browser would have done
        /// it, addressed by a checksum this Server computed. Nothing downstream
        /// can tell how a file arrived, and nothing should be able to.
        /// </para>
        /// <para>
        /// It exists because one thing cannot be done from a browser: a host that
        /// sends no <c>Access-Control-Allow-Origin</c> cannot be read by a
        /// manager's page, however willing the manager. Everything else about
        /// importing happens in the Client.
        /// </para>
        /// </summary>
        [HttpPost("fetch")]
        [ProducesResponseType<UploadedFileDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UploadedFileDto>> Fetch(
            [FromBody] FetchFileInputDto input, CancellationToken ct)
        {
            var stored = await fetching.FetchAsync(input.Url, ct);
            var dto = Projections.Uploaded(stored);
            return Created($"/api/v1/files/{dto.Id}", dto);
        }

        /// <summary>
        /// Stores bytes and answers with what they became.
        /// <para>
        /// The checksum is recomputed here and a mismatch is refused with
        /// <c>422 checksum_mismatch</c>. Nothing is stored on a mismatch, so a
        /// truncated upload fails as a corrupt file rather than becoming a file
        /// whose contents are quietly wrong.
        /// </para>
        /// </summary>
        [HttpPost]
        [Consumes("multipart/form-data")]
        [Api.MultipartForm(File = "file", FileRequired = true, Fields = ["sha256"], RequiredFields = ["sha256"])]
        [RequestSizeLimit(UploadLimits.Package)]
        [DisableFormValueModelBinding]
        [ProducesResponseType<UploadedFileDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UploadedFileDto>> Upload(CancellationToken ct)
        {
            // Straight from the socket into the store, hashed on the way. The
            // checksum to compare it against may still be arriving — the Client's
            // own form sends it after the file — so the comparison happens below.
            var upload = await MultipartUpload.ReadAsync(
                Request, UploadLimits.Package,
                (content, _, _, token) => files.StageAsync(content, token), ct);

            if (upload.File is not { SizeBytes: > 0 } staged)
            {
                if (upload.File is { } empty) await files.DiscardAsync(empty, ct);
                throw new ValidationException("A file is required", "file.required");
            }

            var stored = await files.CommitAsync(
                staged, upload.FileName ?? "", upload.ContentType ?? "", upload.Field("sha256"), ct);

            var dto = Projections.Uploaded(stored);
            return Created($"/api/v1/files/{dto.Id}", dto);
        }

        /// <summary>
        /// The bytes.
        /// <para>
        /// Allowed when <b>at least one reference to this file is readable by the
        /// caller</b>. Answers 404 rather than 403 when it is not: a file id is
        /// opaque, and a 403 would confirm that the bytes exist.
        /// </para>
        /// <para>
        /// Cached for ever, because bytes are immutable — there is no replace,
        /// and a corrected file is a new upload with a new id. `private` unless
        /// the answer does not depend on who is asking, which is only true of an
        /// instance document and the logo.
        /// </para>
        /// <para>
        /// <b>Anonymous by attribute, authorized by the rule.</b> The class
        /// carries <c>[Authorize]</c>, which refused a signed-out caller with 401
        /// <b>before</b> the rule ran — and the rule says an instance document
        /// and the logo are readable by anybody, signed in or not. So the terms
        /// of service, which the registration form asks acceptance of and the
        /// footer links from every signed-out screen, could not be opened without
        /// an account. Found on 2026-08-08 by clicking it. Nothing is loosened:
        /// an anonymous caller now reaches the rule, and the rule answers
        /// <c>false</c> for everything else, which becomes a 404.
        /// </para>
        /// </summary>
        [HttpGet("{id:guid}")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status304NotModified)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            if (!await files.CanReadAsync(id, ct)) throw new NotFoundException("File");

            var file = await files.FindAsync(id, ct) ?? throw new NotFoundException("File");

            // What the paragraph above promises, rather than `private` for
            // everything: a shared cache may hold the terms of service, and must
            // never hold a model solution.
            var visibility = await files.IsPublicAsync(id, ct) ? "public" : "private";
            Response.Headers.CacheControl = $"{visibility}, max-age=31536000, immutable";

            var content = await files.OpenAsync(file, ct);

            // **Through the overload, not as a header.** Writing `ETag` onto the
            // response by hand puts the right string in the right place and buys
            // nothing: the framework compares `If-None-Match` only against an
            // entity tag it was handed here, so a conditional request was answered
            // `200` and the whole file. Range processing is off by default for the
            // same kind of reason — nobody turned it on — so `Range:` was answered
            // `200` with every byte. Measured on the running stack, 2026-08-12.
            // Every argument named: the byte[] and Stream overloads differ in
            // what their third positional parameter means, and picking the wrong
            // one is a compile error only by luck.
            // **What the Server says it is, not what the uploader said.** See
            // `Utils/Downloads.cs`: the stored type is the uploader's own word
            // for it, and this endpoint is anonymous for anything public.
            Response.Headers.ContentDisposition = Downloads.Disposition(file);

            return File(
                fileStream: content,
                contentType: Downloads.ContentType(file),
                // Ours is already on the response; a name here would replace it,
                // and an empty one would remove the header altogether.
                fileDownloadName: null,
                lastModified: null,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Sha256}\""),
                enableRangeProcessing: true);
        }

        /// <summary>The same document <c>POST /files</c> answers with, without the bytes.</summary>
        [HttpGet("{id:guid}/meta")]
        // Same reasoning as the download above: the rule decides, not the attribute.
        [AllowAnonymous]
        [ProducesResponseType<UploadedFileDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<UploadedFileDto> Meta(Guid id, CancellationToken ct)
        {
            if (!await files.CanReadAsync(id, ct)) throw new NotFoundException("File");
            var file = await files.FindAsync(id, ct) ?? throw new NotFoundException("File");
            return Projections.Uploaded(file);
        }
    }
}
