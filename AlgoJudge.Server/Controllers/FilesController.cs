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
    public class FilesController(IFileService files) : ControllerBase
    {
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
        [ProducesResponseType<UploadedFileDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UploadedFileDto>> Upload(
            [FromForm] IFormFile file, [FromForm] string sha256, CancellationToken ct)
        {
            if (file is null || file.Length == 0)
            {
                throw new ValidationException("A file is required", "file.required");
            }

            await using var content = file.OpenReadStream();
            var stored = await files.StoreAsync(content, file.FileName, file.ContentType, sha256, ct);

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
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            if (!await files.CanReadAsync(id, ct)) throw new NotFoundException("File");

            var file = await files.FindAsync(id, ct) ?? throw new NotFoundException("File");

            // What the paragraph above promises, rather than `private` for
            // everything: a shared cache may hold the terms of service, and must
            // never hold a model solution.
            var visibility = await files.IsPublicAsync(id, ct) ? "public" : "private";
            Response.Headers.CacheControl = $"{visibility}, max-age=31536000, immutable";
            Response.Headers.ETag = $"\"{file.Sha256}\"";

            var content = await files.OpenAsync(file, ct);
            return File(content, file.MimeType, file.Name);
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
