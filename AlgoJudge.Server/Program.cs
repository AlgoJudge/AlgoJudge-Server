using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoJudge.Server
{
    public class Program
    {
        /// <summary>
        /// Every installation serves the API here, whatever host it is on.
        /// <para>
        /// Fixed rather than configurable (2026-08-06). Where the Client and the
        /// Server share a domain the API cannot live at the root — the root is
        /// the application — so it must live under a path, and a path that is
        /// only sometimes there is a path every deployment has to get right
        /// separately.
        /// </para>
        /// </summary>
        public const string ApiPathBase = "/api/v1";

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration.AddEnvironmentVariables(prefix: "AJ_");

            var limits = builder.Configuration.GetSection("Limits");
            // The package ceiling is the largest thing the Server ever accepts,
            // so it is what the request body limit is set from. Kestrel defaults
            // to 30 MB, which would reject a 128 MB package; nginx defaults to
            // 1 MB, which would reject almost everything — both have to be set,
            // and only one of them is ours.
            var maxRequestBytes = limits.GetValue("MaxRequestBytes", 128L * 1024 * 1024);

            builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
                options => options.Limits.MaxRequestBodySize = maxRequestBytes);
            builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = maxRequestBytes;
                options.ValueLengthLimit = int.MaxValue;
            });

            {
                var dbConnectionString = builder.Configuration.GetConnectionString("DbConnectionString");
                builder.Services.AddDbContext<ApplicationDbContext>(
                    options => options.UseNpgsql(dbConnectionString));
            }

            // The LTI module. One of the two lines it is allowed outside `Lti/`;
            // the other is `app.MapLti()` below. See `Lti/LtiModule.cs`.
            AlgoJudge.Server.Lti.LtiModule.AddLti(builder.Services, builder.Configuration);

            // The Server sits behind a reverse proxy in every real deployment.
            // Without this the scheme is http, redirects point at the wrong
            // place, and — the one that matters here — every Runner is recorded
            // at the proxy's address instead of its own.
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // Cleared because the defaults are loopback only, which is never
                // where the proxy is in a container network. An installation that
                // wants to pin them does it in configuration.
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            builder.Services.AddHttpContextAccessor();

            builder.Services.AddAuthorization();
            builder.Services.AddIdentityApiEndpoints<User>(options =>
            {
                // Twelve characters of anything, rather than a character-class
                // rule. Length is what makes a password hard to guess; classes
                // mostly make it hard to remember and easy to write down.
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                // Blocking is LockoutEnd and nothing else — ten attempts, an hour.
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(60);
                options.Lockout.AllowedForNewUsers = true;

                // Enforced by OptionalEmailValidator, not by this: the framework
                // reads it as "every account has an address AND it is unique",
                // and the first half makes a temporary account impossible.
                options.User.RequireUniqueEmail = false;
                // No mail sender in v1, so requiring confirmation here would lock
                // everybody out. Whether an instance demands it is a setting on
                // Instance, applied at sign-in.
                options.SignIn.RequireConfirmedEmail = false;
            })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                // Replaces the built-in one, which would demand an address of
                // every account. An address stays unique when there is one.
                .AddUserValidator<OptionalEmailValidator>()
                // The one login this product reserves. A validator because it is
                // the only place that catches `MapIdentityApi`'s own /register,
                // which is framework code and takes the login it is given.
                .AddUserValidator<ReservedLoginValidator>();

            // **A session established inside somebody else's page keeps a wider
            // cookie, and only that session.** `SameSite=Lax` is right for every
            // ordinary sign-in and is what stays; a response written into a
            // frame on another site, though, has its cookie dropped by the
            // browser before anything can go wrong with it, and the sign-in then
            // looks like it worked. `Authorization/EmbeddedSessions.cs` says why
            // in full — including why it names no integration.
            builder.Services.ConfigureApplicationCookie(options =>
            {
                var signingIn = options.Events.OnSigningIn;
                options.Events.OnSigningIn = async context =>
                {
                    if (signingIn is not null) await signingIn(context);
                    if (Authorization.EmbeddedSessions.IsEmbedded(context.Properties))
                    {
                        Authorization.EmbeddedSessions.Widen(context.CookieOptions);
                        // So anything else set in this same response knows too.
                        Authorization.EmbeddedSessions.Mark(context.HttpContext);
                    }
                };
            });

            // The external cookie — where a provider's ticket waits between the
            // callback and the decision to admit — is already registered:
            // `AddIdentityApiEndpoints` ends in `AddIdentityCookies()`, which
            // adds all four of Identity's schemes whether or not anything used
            // them. Adding it again here would be a second registration of the
            // same name.

            // **One scheme per registered provider, resolved at run time.**
            // Providers are rows an operator adds from the panel, so the set is
            // not known at startup and `AddOpenIdConnect("name", …)` cannot
            // express it. The protocol itself is still the framework's handler:
            // state, nonce, PKCE, the code exchange and `id_token` validation are
            // not reimplemented here.
            builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            builder.Services.AddSingleton<IAuthenticationSchemeProvider, FederatedSchemeProvider>();
            builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, FederatedOidcConfigure>();

            // **Without this the handler throws on the first challenge**, in
            // `WriteNonceCookie`, with a `NullReferenceException` that names
            // nothing useful.
            //
            // `AddOpenIdConnect(...)` normally registers it, and this product
            // never calls that method — there is no scheme to name at startup,
            // which is the whole reason the schemes are resolved from the
            // database. What that method also does, and what is easy to miss, is
            // register the post-configuration that gives every
            // `OpenIdConnectOptions` its state format, its nonce cookie name and
            // its data protector. Options built without it look complete and are
            // not.
            builder.Services.AddSingleton<
                IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>();

            // Injected rather than DateTime.UtcNow, so the scheduler and the
            // file collector can be tested against a clock somebody turns.
            builder.Services.AddSingleton(TimeProvider.System);

            builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
            builder.Services.AddScoped<IPermissionService, PermissionService>();
            // A singleton because a store is configuration, not state: it is read
            // once from the environment and never changes while the process
            // lives. Building it here also means an unusable configuration takes
            // the container down at startup rather than at the first upload.
            builder.Services.AddSingleton<Storage.IBlobStoreRegistry>(
                services => new Storage.BlobStoreRegistry(
                    services.GetRequiredService<IConfiguration>()));

            builder.Services.AddSingleton<Storage.IStorageHealth, Storage.StorageHealth>();
            builder.Services.AddScoped<Storage.IStorageMigrations, Storage.StorageMigrations>();

            builder.Services.AddScoped<IFileService, FileService>();
            builder.Services.AddScoped<IInstanceService, InstanceService>();
            builder.Services.AddScoped<IActivityService, ActivityService>();
            builder.Services.AddScoped<ISeriesService, SeriesService>();
            builder.Services.AddScoped<IProblemService, ProblemService>();
            builder.Services.AddScoped<IResultsService, ResultsService>();
            builder.Services.AddScoped<ITrialService, TrialService>();
            builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
            builder.Services.AddScoped<IQuestionService, QuestionService>();
            builder.Services.AddScoped<IAccountService, AccountService>();
            builder.Services.AddScoped<IDocumentService, DocumentService>();
            builder.Services.AddScoped<IGrantService, GrantService>();
            builder.Services.AddScoped<IIdentityProviderService, IdentityProviderService>();
            builder.Services.AddScoped<IClaimMappingService, ClaimMappingService>();
            builder.Services.AddScoped<IFederatedSignInService, FederatedSignInService>();
            builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddScoped<IManagerWriteService, ManagerWriteService>();
            builder.Services.AddScoped<IManagerReadService, ManagerReadService>();
            builder.Services.AddScoped<ISubmissionService, SubmissionService>();
            builder.Services.AddScoped<IRunnerService, RunnerService>();
            builder.Services.AddSingleton<ISeriesGate, SeriesGate>();

            // The connection registry outlives any request: it is what the
            // users screen counts, and what an event is fanned out over.
            builder.Services.AddSingleton<IEventHub, EventHub>();
            // Who hears about a thing, resolved by the same rule that answers a
            // fetch for it. Scoped, because it reads the grants.
            builder.Services.AddScoped<Realtime.IEventAudience, Realtime.EventAudience>();

            builder.Services.AddScoped<Seeder>();

            // Recovery for a Runner that died holding a job. Registered as a
            // singleton so it can be resolved in tests and swept on demand.
            builder.Services.AddSingleton<Workers.MaintenanceDrainer>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Workers.MaintenanceDrainer>());

            builder.Services.AddSingleton<Workers.LeaseReaper>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Workers.LeaseReaper>());
            builder.Services.AddSingleton<Workers.DeletionSweeper>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Workers.DeletionSweeper>());

            // Owns every open/close transition, because openness is stored.
            builder.Services.AddSingleton<Workers.SeriesScheduler>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Workers.SeriesScheduler>());

            // Retention is a property of what references a file, not of the file.
            builder.Services.AddSingleton<Workers.FileCollector>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Workers.FileCollector>());

            // Separate from the collector: one copies, the other deletes.
            builder.Services.AddSingleton<Workers.StorageMigrator>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Workers.StorageMigrator>());

            // One shape for every failure, including the ones raised outside MVC.
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

            // An absent value is **absent**, not `null`.
            //
            // The contract says optional throughout, and the Client guards with
            // `!== undefined` — so a field written as `"finalScore": null`
            // passes every one of those guards and reaches the screen, where
            // `Wynik: ${finalScore} / ${maxScore}` renders the word "null" at a
            // competitor. Found on the activities list on 2026-08-08, the first
            // time a screen was pointed at this Server.
            //
            // Set in both places because the surface has two halves: the
            // controllers, and the minimal-API endpoints `MapIdentityApi` adds.
            static void OmitNulls(System.Text.Json.JsonSerializerOptions options) =>
                options.DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;

            builder.Services.AddControllers()
                .AddJsonOptions(options => OmitNulls(options.JsonSerializerOptions));
            builder.Services.ConfigureHttpJsonOptions(options => OmitNulls(options.SerializerOptions));

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                // The three streamed uploads read their own bodies, so they
                // declare no parameters for Swashbuckle to infer a form from.
                options.OperationFilter<Api.MultipartFormOperationFilter>();
            });

            builder.Services.AddCors(options =>
            {
                var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<List<string>>() ?? [];

                if (corsOrigins.Count > 0)
                {
                    options.AddDefaultPolicy(policy => policy
                        .WithOrigins([.. corsOrigins])
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials());
                }
            });

            var app = builder.Build();

            // **Before the rewrite**, and that ordering is the whole point: the
            // next line replaces the remote address with what the proxy says,
            // which is right for logs and wrong for "is this call coming from
            // inside the container". See `Authorization/Peer.cs`.
            app.UseTruePeerAddress();

            // First after that, so everything else sees the caller's real scheme
            // and address rather than the proxy's.
            app.UseForwardedHeaders();

            // Everything the Server serves lives under /api/v1. UsePathBase
            // rather than a prefix on every route: it also covers the Identity
            // endpoints and the WebSocket, and it keeps the generated links
            // right instead of leaving each place to remember the prefix.
            //
            // UsePathBase only STRIPS the prefix when it is there; it does not
            // require it. Left alone, every endpoint answers at both `/health`
            // and `/api/v1/health`, which is the misconfiguration the fixed
            // prefix exists to prevent — a Client asking a correct host for the
            // wrong path would be answered instead of corrected. So the guard is
            // explicit, and it runs before the base is stripped.
            app.Use(async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments(ApiPathBase, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
            });

            app.UsePathBase(ApiPathBase);

            app.UseExceptionHandler();

            // A 404 from routing is never thrown, so the exception handler never
            // sees it and the body would be empty. The contract says every
            // failure is problem+json with a code; this is what makes that true
            // of the ones nobody raised.
            app.UseStatusCodePages(async (StatusCodeContext context) =>
            {
                var response = context.HttpContext.Response;
                if (response.HasStarted) return;

                var code = response.StatusCode == StatusCodes.Status404NotFound ? "not_found" : "error";
                var body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = response.StatusCode,
                    title = ReasonPhrases.GetReasonPhrase(response.StatusCode),
                    code,
                });

                // Written by hand rather than through WriteAsJsonAsync, which
                // sets `application/json` and would undo the content type the
                // contract asks for.
                response.ContentType = "application/problem+json; charset=utf-8";
                await response.WriteAsync(body);
            });

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
                    throw new InvalidOperationException("Database has pending migrations");
                }
            }

            // **Every store a file names has to be one this Server has.** A
            // configuration that dropped a store id still holds somebody's
            // submissions, and the failure it produces otherwise is a 503 at a
            // download nobody connected to a deployment change. Logged loudly and
            // reported by the health endpoint; not fatal, because the rest of the
            // installation works and refusing to start would take that down too.
            app.Services.GetRequiredService<Storage.IStorageHealth>()
                .ValidateAsync(CancellationToken.None).GetAwaiter().GetResult();

            app.UseHttpsRedirection();
            app.UseCors();

            // Both, in this order. Only UseAuthorization was here before, which
            // meant the identity cookie was never turned into a ClaimsPrincipal
            // and every [Authorize] endpoint answered 401 to a signed-in caller.
            app.UseAuthentication();
            app.UseAuthorization();

            // In front of the endpoints rather than around them: MapIdentityApi
            // maps a surface this product has decided not to have in full, and a
            // refusal has to land before the framework binds a body it is going
            // to reject for its own reasons.
            app.UseIdentitySurfaceRules();

            // The operator's own surface: the loopback interface **and** a
            // token, and one 404 for everything that is neither. Before the
            // maintenance gate, which exempts `/admin` so an installation that
            // has closed itself can still be reopened.
            app.UseAdminSurfaceRules();

            // After authorisation, so the refusal is the last word rather than
            // an anonymous 503 hiding a 401 — and after the exception handler,
            // so throwing gets `application/problem+json` for free.
            app.UseMaintenanceGate();

            // After authentication, so it knows who is asking, and last so it
            // records only requests that actually got somewhere.
            app.UseMiddleware<SessionTrackingMiddleware>();

            app.UseWebSockets();

            app.MapGroup("/identity").MapIdentityApi<User>();
            app.MapControllers();

            AlgoJudge.Server.Lti.LtiModule.MapLti(app);

            // One socket per tab, carrying core, participant and manager events
            // together. Authenticated by the same cookie as everything else — no
            // token in the query string, which would end up in proxy logs.
            app.MapEventSocket("/ws");

            using (var scope = app.Services.CreateScope())
            {
                var seed = scope.ServiceProvider.GetRequiredService<Seeder>();
                seed.EnsureAsync(app.Environment.IsDevelopment()).GetAwaiter().GetResult();
            }

            // Said once, at start, so an operator learns the state of the door
            // before the night they need it rather than by getting a 404 from
            // something they were told would work. **Never the token itself.**
            var adminToken = app.Configuration[AdminSurface.TokenSetting];
            var startup = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AlgoJudge.Admin");
            if (string.IsNullOrWhiteSpace(adminToken))
            {
                startup.LogWarning(
                    "AJ_Admin__Token is not set, so /admin is closed — including the maintenance "
                    + "switch and the only way to set the administrator's password.");
            }
            else if (adminToken == AdminSurface.DevelopmentToken && !app.Environment.IsDevelopment())
            {
                // Not merely untidy: this value is in a compose file in a public
                // repository, so an installation running it has an admin surface
                // whose token anybody can read.
                startup.LogWarning(
                    "AJ_Admin__Token is the well-known development token outside Development. "
                    + "Anybody who can read this product's repository can throw the switch.");
            }

            app.Run();
        }
    }
}
