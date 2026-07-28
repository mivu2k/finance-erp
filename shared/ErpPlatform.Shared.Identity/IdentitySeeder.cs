using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ErpPlatform.Shared.Identity;

/// <summary>
/// Brings the identity database up to date with the code: module catalog, the
/// platform Super Admin role, every module's default roles and their permission
/// grants, and the bootstrap admin account.
/// </summary>
/// <remarks>
/// Re-running is safe. Permissions are only *added* to existing roles for
/// newly-registered permission names — an administrator's edits at
/// <c>/admin/roles</c> are never reverted by a redeploy.
/// </remarks>
public static class IdentitySeeder
{
    public static async Task SeedAsync(
        PlatformIdentityDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<ApplicationRole> roles,
        IConfiguration config,
        ILogger logger)
    {
        await db.Database.MigrateAsync();

        await SeedModulesAsync(db, logger);

        // Super Admin holds every permission the platform knows about, including
        // each module's. Module access is separate — it comes from
        // ModulesManageAll, which opens every enabled app.
        var everything = PermissionCatalog.All.Select(p => p.Name)
            .Concat(ModuleRegistry.All.SelectMany(m => m.Permissions).Select(p => p.Name))
            .Distinct().ToList();

        await SeedRoleAsync(roles, PlatformRoles.SuperAdmin, null,
            "Full access to every app and to user administration.", everything, logger);

        foreach (var module in ModuleRegistry.All)
            foreach (var template in module.Roles)
                await SeedRoleAsync(roles, template.Name, module.ModuleKey,
                    template.Description, template.Permissions, logger);

        await SeedAdminAsync(users, config, logger);
    }

    private static async Task SeedModulesAsync(PlatformIdentityDbContext db, ILogger logger)
    {
        var existing = await db.Modules.ToListAsync();

        foreach (var def in AppModules.All)
        {
            var row = existing.FirstOrDefault(m => m.Key == def.Key);
            if (row is null)
            {
                db.Modules.Add(new ModuleRecord
                {
                    Key = def.Key,
                    Name = def.Name,
                    Description = def.Description,
                    BasePath = def.BasePath,
                    Icon = def.Icon,
                    Color = def.Color,
                    SortOrder = def.SortOrder,
                    IsEnabled = true
                });
                logger.LogInformation("Seeded module {Module}", def.Key);
            }
            else
            {
                // Presentation follows the code; IsEnabled stays an operator decision.
                row.Name = def.Name;
                row.Description = def.Description;
                row.BasePath = def.BasePath;
                row.Icon = def.Icon;
                row.Color = def.Color;
                row.SortOrder = def.SortOrder;
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedRoleAsync(
        RoleManager<ApplicationRole> roles,
        string name,
        string? moduleKey,
        string description,
        IReadOnlyList<string> permissions,
        ILogger logger)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null)
        {
            role = new ApplicationRole(name, moduleKey, description, isSystem: true);
            var created = await roles.CreateAsync(role);
            if (!created.Succeeded)
            {
                logger.LogError("Could not create role {Role}: {Errors}", name,
                    string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogInformation("Seeded role {Role} for module {Module}", name, moduleKey ?? "platform");
        }
        else if (role.ModuleKey != moduleKey || !role.IsSystem)
        {
            role.ModuleKey = moduleKey;
            role.IsSystem = true;
            role.Description ??= description;
            await roles.UpdateAsync(role);
        }

        var held = (await roles.GetClaimsAsync(role))
            .Where(c => c.Type == PermissionCatalog.ClaimType)
            .Select(c => c.Value)
            .ToHashSet();

        foreach (var permission in permissions.Where(p => !held.Contains(p)))
            await roles.AddClaimAsync(role, new Claim(PermissionCatalog.ClaimType, permission));
    }

    private static async Task SeedAdminAsync(
        UserManager<ApplicationUser> users, IConfiguration config, ILogger logger)
    {
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var admin = await users.FindByEmailAsync(email);
        if (admin is not null)
        {
            if (!await users.IsInRoleAsync(admin, PlatformRoles.SuperAdmin))
                await users.AddToRoleAsync(admin, PlatformRoles.SuperAdmin);
            return;
        }

        admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = "System Administrator",
            IsActive = true
        };

        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Could not create the admin user: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        await users.AddToRoleAsync(admin, PlatformRoles.SuperAdmin);
        logger.LogInformation("Seeded platform administrator {Email}", email);
    }
}
