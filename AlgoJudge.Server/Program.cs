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

            builder.Services.AddAuthorization();
            builder.Services.AddIdentityApiEndpoints<User>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
            builder.Services.AddScoped<IPermissionService, PermissionService>();

            builder.Services.AddControllers(options =>
                options.Filters.Add<HttpResponseExceptionFilter>());
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

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
