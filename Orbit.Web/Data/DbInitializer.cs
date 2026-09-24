using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Data;

/// <summary>Startup: apply migrations, seed the roles and the synthetic Claude user, and run the one-time admin bootstrap.</summary>
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

        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var builtIn = await EnsureBuiltInRoleAsync(db, roleManager, logger, ct);
        // The two shipped roles are created once, with their default grants, and never re-applied: an admin's edits stick.
        await EnsureRoleAsync(db, roleManager, DefaultRoles.Member, DefaultRoles.MemberDescription, DefaultRoles.MemberGrants, logger, ct);
        await EnsureRoleAsync(db, roleManager, DefaultRoles.DepartmentAdmin, DefaultRoles.DepartmentAdminDescription, DefaultRoles.DepartmentAdminGrants, logger, ct);

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

        // First-run System Administrator bootstrap: only when nobody holds the built-in role yet.
        var seed = sp.GetRequiredService<IOptions<SeedOptions>>().Value.Admin;
        if (!string.IsNullOrWhiteSpace(seed.Email) && !string.IsNullOrWhiteSpace(seed.Password))
        {
            var admins = await userManager.GetUsersInRoleAsync(builtIn.Name!);
            if (admins.Count == 0)
            {
                var admin = new ApplicationUser
                {
                    UserName = seed.Email.Trim(),
                    Email = seed.Email.Trim(),
                    EmailConfirmed = true,
                    DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? "System Administrator" : seed.DisplayName.Trim(),
                    IsActive = true,
                    LockoutEnabled = true
                };
                var result = await userManager.CreateAsync(admin, seed.Password);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(admin, builtIn.Name!);
                    logger.LogWarning("Seeded the initial System Administrator account {Email}. Sign in, change the password, then remove the Seed:Admin settings.", admin.Email);
                }
                else
                {
                    logger.LogError("Could not seed the System Administrator account: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                }
            }
        }
    }

    /// <summary>
    /// The built-in role always exists: the one flagged in the database, else the role named System Administrator
    /// (flagged now), else a new one. Its grants are never stored - it resolves to every permission at All.
    /// </summary>
    private static async Task<ApplicationRole> EnsureBuiltInRoleAsync(ApplicationDbContext db, RoleManager<ApplicationRole> roleManager, ILogger logger, CancellationToken ct)
    {
        var existing = await db.Roles.FirstOrDefaultAsync(r => r.IsBuiltIn, ct);
        if (existing is not null) return existing;

        var named = await roleManager.FindByNameAsync(DefaultRoles.SystemAdministrator);
        if (named is not null)
        {
            named.IsBuiltIn = true;
            named.Description ??= DefaultRoles.SystemAdministratorDescription;
            named.UpdatedAt = DateTime.UtcNow;
            Check(await roleManager.UpdateAsync(named), logger, named.Name!);
            return named;
        }

        var role = new ApplicationRole(DefaultRoles.SystemAdministrator)
        {
            Description = DefaultRoles.SystemAdministratorDescription,
            IsBuiltIn = true
        };
        Check(await roleManager.CreateAsync(role), logger, role.Name!);
        return role;
    }

    private static async Task EnsureRoleAsync(ApplicationDbContext db, RoleManager<ApplicationRole> roleManager, string name, string description,
        IReadOnlyDictionary<string, PermissionScope> grants, ILogger logger, CancellationToken ct)
    {
        if (await roleManager.RoleExistsAsync(name)) return;
        var role = new ApplicationRole(name) { Description = description };
        if (!Check(await roleManager.CreateAsync(role), logger, name)) return;
        foreach (var (permission, scope) in grants)
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = permission, Scope = scope });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Created the {Role} role with its default permissions.", name);
    }

    private static bool Check(IdentityResult result, ILogger logger, string role)
    {
        if (result.Succeeded) return true;
        logger.LogError("Could not save the {Role} role: {Errors}", role, string.Join("; ", result.Errors.Select(e => e.Description)));
        return false;
    }
}
