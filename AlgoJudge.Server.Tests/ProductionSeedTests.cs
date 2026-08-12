using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a real deployment is seeded with.
///
/// <para>
/// <b>Its own database, and that is not fussiness.</b> Every other test here
/// runs in Development, where the administrator's password is deliberately
/// replaced with a well-known one — so the shared database can never show what a
/// production installation actually gets. And the account cannot be deleted and
/// re-seeded in place either: half the seeded world references it, so the delete
/// fails on a foreign key and leaves the grants already gone, which is a fine
/// way to break every test that runs afterwards.
/// </para>
///
/// <para>
/// The claim under test is the highest-stakes one in this change: <b>a
/// production administrator's password is not a value anybody can read in this
/// repository.</b> A well-known administrator password is the single most
/// reliable way an installation is taken over, and the only thing standing
/// between this product and one is a branch in the seeder.
/// </para>
/// </summary>
[Collection("storage")]
public class ProductionSeedTests : IAsyncLifetime
{
    private PostgreSqlContainer container = null!;
    private ServiceProvider services = null!;

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder()
            .WithImage("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();
        await container.StartAsync();

        // Only what the seeder needs, wired the way `Program` wires it. The
        // two validators are here because the seeder creates accounts through
        // `UserManager` and one of them guards the very login it is creating —
        // leaving them out would test a seeder nothing else runs.
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(TimeProvider.System);
        // What the reset-token provider is built on. A web host brings it; a
        // bare service collection does not.
        collection.AddDataProtection();
        collection.AddDbContext<ApplicationDbContext>(
            options => options.UseNpgsql(container.GetConnectionString()));
        collection.AddIdentityCore<User>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = false;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddUserValidator<OptionalEmailValidator>()
            .AddUserValidator<ReservedLoginValidator>()
            // `AddIdentityCore` brings none, and the seeder resets a password
            // through a reset token. The application gets these from
            // `AddIdentityApiEndpoints`.
            .AddDefaultTokenProviders();
        collection.AddScoped<IInstanceService, InstanceService>();

        // The seeder writes its documents through a store like everything else,
        // so this collection needs one — and since 2026-08-12 it has to name it,
        // because an installation that configures no storage does not start.
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DbConnectionString"] = container.GetConnectionString(),
                ["Storage:Stores:pg:Kind"] = "postgres",
                ["Storage:Default"] = "pg",
            })
            .Build());
        collection.AddSingleton<AlgoJudge.Server.Storage.IBlobStoreRegistry>(
            services => new AlgoJudge.Server.Storage.BlobStoreRegistry(
                services.GetRequiredService<IConfiguration>()));

        collection.AddScoped<Seeder>();

        services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await services.DisposeAsync();
        await container.DisposeAsync();
    }

    [Fact]
    public async Task It_makes_one_administrator_nobody_knows_the_password_of()
    {
        using var scope = services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<Seeder>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await seeder.EnsureAsync(development: false);

        var admin = await users.FindByNameAsync(Seeder.AdminLogin);
        Assert.NotNull(admin);

        // **Not a person.** It is the account somebody makes people with, so
        // inventing a name for it would put one on a board and an address in a
        // mailbox that belong to nobody.
        Assert.Null(admin.FirstName);
        Assert.Null(admin.LastName);
        Assert.Null(admin.Email);
        Assert.False(admin.EmailConfirmed);
        Assert.NotNull(admin.ApprovedAt);

        // Useless without it — an administrator with no grant is a nameless
        // account that can do nothing at all.
        Assert.True(await context.Grants.AnyAsync(g => g.UserId == admin.Id && g.IsSystem));

        // **The claim.** Not the development password, and not the login, and
        // not empty — the three things somebody would try first.
        Assert.False(await users.CheckPasswordAsync(admin, Seeder.DevAdminPassword));
        Assert.False(await users.CheckPasswordAsync(admin, Seeder.AdminLogin));
        Assert.False(await users.CheckPasswordAsync(admin, ""));
    }

    /// <summary>
    /// Seeding twice does not make a second administrator, and — more to the
    /// point — does not reset the password of the first. An upgrade restarts the
    /// Server, and a seeder that reset the password on every start would undo
    /// whatever the operator had set.
    /// </summary>
    [Fact]
    public async Task Seeding_again_leaves_the_administrator_alone()
    {
        using var scope = services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<Seeder>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await seeder.EnsureAsync(development: false);

        var admin = await users.FindByNameAsync(Seeder.AdminLogin);
        const string chosen = "what-the-operator-set";
        var token = await users.GeneratePasswordResetTokenAsync(admin!);
        Assert.True((await users.ResetPasswordAsync(admin!, token, chosen)).Succeeded);

        await seeder.EnsureAsync(development: false);

        var after = await users.FindByNameAsync(Seeder.AdminLogin);
        Assert.True(await users.CheckPasswordAsync(after!, chosen));
        Assert.Equal(1, await context.Users.CountAsync(u => u.NormalizedUserName == "ADMIN"));
    }
}
