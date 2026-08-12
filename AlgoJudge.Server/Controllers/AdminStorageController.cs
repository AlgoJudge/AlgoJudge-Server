using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Storage;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// Where the bytes are, per store, for somebody standing on the machine.
    /// <para>
    /// <b>Everything the public health endpoint may not say.</b> A store id, its
    /// reachability, what went wrong with it, how many files it holds and how
    /// many bytes those are. A65c forbids all of it in a public answer and A65b
    /// requires it here, which is the same sentence read from two ends.
    /// </para>
    /// <para>
    /// Guarded like the rest of <c>/admin</c>: the loopback interface
    /// <b>and</b> the configured token, checked by <see cref="AdminSurface"/> for
    /// the whole group. Not a permission — where the files are is an operator's
    /// question, and a permission is something a compromised administrator
    /// session also has.
    /// </para>
    /// <para>
    /// <b>The zero matters.</b> A store holding no files is the answer to "is it
    /// safe to switch this one off", so a configured store appears here even when
    /// nothing names it.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("admin/storage")]
    [AllowAnonymous]
    public class AdminStorageController(IStorageHealth storage) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<StorageReportDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<StorageReportDto> Get(CancellationToken ct)
        {
            // **No guard here, and that is not an omission.**
            // `UseAdminSurfaceRules` refuses the whole `/admin` group — wrong
            // machine, no token configured, no header, wrong value, all with the
            // same 404 — so a check repeated here would be a second opinion
            // about a decision already taken, and one somebody could later
            // "simplify" away believing it was load-bearing. Found by sabotage
            // on 2026-08-12: removing it changed nothing, because it never did
            // anything. Being in the group is what protects this.
            var report = await storage.ReportAsync(ct);

            return new StorageReportDto
            {
                Stores = report.Stores.Select(store => new StoreStatusDto
                {
                    Id = store.Id,
                    Reachable = store.Reachable,
                    SmokeTestPassed = store.SmokeTestPassed,
                    Detail = store.Detail,
                    Files = store.Files,
                    SizeBytes = store.SizeBytes,
                    IsDefault = store.IsDefault,
                }).ToList(),
                Unconfigured = report.Unconfigured,
            };
        }
    }
}
