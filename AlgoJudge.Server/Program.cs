using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration.AddEnvironmentVariables(prefix: "AJ_");

            {
                var dbConnectionString = builder.Configuration.GetConnectionString("DbConnectionString");
                builder.Services.AddDbContext<ApplicationDbContext>(
                    options => options.UseNpgsql(dbConnectionString));
            }

            builder.Services.AddHttpContextAccessor();

            builder.Services.AddAuthorization();
            builder.Services.AddIdentityApiEndpoints<User>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
            builder.Services.AddScoped<IPermissionService, PermissionService>();

            // One shape for every failure, including the ones raised outside MVC.
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.AddCors(options =>
            {
                var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<List<string>>() ?? [];

                if (corsOrigins.Count > 0)
                {
                    options.AddDefaultPolicy(
                        policy =>
                        {
                            policy.WithOrigins(corsOrigins.ToArray()).AllowAnyMethod().AllowAnyHeader()
                                .AllowCredentials();
                        });
                }
            });

            var app = builder.Build();

            app.UseExceptionHandler();

            app.MapGroup("/identity").MapIdentityApi<User>();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();

                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.Migrate();
            }

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (db.Database.GetPendingMigrations().Any())
                {
                    throw new Exception("Database has pending migrations");
                }
            }

            app.UseHttpsRedirection();

            app.UseCors();

            // Both, in this order. Only UseAuthorization was here before, which
            // meant the identity cookie was never turned into a ClaimsPrincipal
            // and every [Authorize] endpoint answered 401 to a signed-in caller.
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
