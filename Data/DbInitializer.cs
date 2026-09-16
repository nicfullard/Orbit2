using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Data;

/// <summary>Startup: apply migrations, seed roles and the synthetic Claude user, and run the one-time admin bootstrap.</summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Orbit.Startup");
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        if (dbOptions.ApplyMigrations)
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(ct);
            }
            else if (!(await db.Database.GetAppliedMigrationsAsync(ct)).Any())
            {
                logger.LogWarning("No EF Core migrations are known to this build. Scaffold one (e.g. Add-Migration InitialCreate in the Package Manager Console) so the schema can be created.");
            }
        }

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));
        }

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();

        // The synthetic user every API key acts as.
        if (await userManager.FindByIdAsync(WellKnownIds.ClaudeAgentUserId.ToString()) is null)
        {
            var agent = new ApplicationUser
            {
                Id = WellKnownIds.ClaudeAgentUserId,
                UserName = WellKnownIds.ClaudeAgentEmail,
                Email = WellKnownIds.ClaudeAgentEmail,
                EmailConfirmed = true,
                DisplayName = WellKnownIds.ClaudeAgentDisplayName,
                IsSystemAccount = true,
                IsActive = true,
                LockoutEnabled = true,
                LockoutEnd = DateTimeOffset.MaxValue // can never sign in interactively
            };
            var result = await userManager.CreateAsync(agent);
            if (!result.Succeeded)
                logger.LogError("Could not create the Claude agent user: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        // First-run System Admin bootstrap: only when no System Admin exists yet.
        var seed = sp.GetRequiredService<IOptions<SeedOptions>>().Value.Admin;
        if (!string.IsNullOrWhiteSpace(seed.Email) && !string.IsNullOrWhiteSpace(seed.Password))
        {
            var admins = await userManager.GetUsersInRoleAsync(Roles.SystemAdmin);
            if (admins.Count == 0)
            {
                var admin = new ApplicationUser
                {
                    UserName = seed.Email.Trim(),
                    Email = seed.Email.Trim(),
                    EmailConfirmed = true,
                    DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? "System Admin" : seed.DisplayName.Trim(),
                    IsActive = true,
                    LockoutEnabled = true
                };
                var result = await userManager.CreateAsync(admin, seed.Password);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(admin, Roles.SystemAdmin);
                    logger.LogWarning("Seeded the initial System Admin account {Email}. Sign in, change the password, then remove the Seed:Admin settings.", admin.Email);
                }
                else
                {
                    logger.LogError("Could not seed the System Admin account: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                }
            }
        }
    }
}
