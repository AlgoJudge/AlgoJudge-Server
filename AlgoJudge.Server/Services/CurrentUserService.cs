using System.Security.Claims;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public class CurrentUserService(
        ApplicationDbContext context,
        IHttpContextAccessor accessor
    ) : ICurrentUserService
    {
        private User? user;
        private bool loaded;

        public string? UserId =>
            accessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        public async Task<User?> GetAsync(CancellationToken ct = default)
        {
            if (loaded) return user;
            loaded = true;

            var id = UserId;
            if (string.IsNullOrEmpty(id)) return null;

            // No try/catch swallowing everything: a database failure here is a
            // failure of the request, not an anonymous caller. Treating it as
            // "nobody is signed in" is how a transient outage turns every
            // authenticated screen into a silent 403.
            user = await context.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            return user;
        }

        public async Task<User> RequireAsync(CancellationToken ct = default) =>
            await GetAsync(ct) ?? throw new UnauthenticatedException();
    }
}
