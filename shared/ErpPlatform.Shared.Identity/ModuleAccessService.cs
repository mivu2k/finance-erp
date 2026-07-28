using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Identity;

/// <summary>Resolves which modules a user may enter.</summary>
public interface IModuleAccessService
{
    Task<IReadOnlyList<string>> GetAccessibleModuleKeysAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<ModuleRecord>> GetAccessibleModulesAsync(string userId, CancellationToken ct = default);
    Task SetAccessAsync(string userId, string moduleKey, bool? granted, string? actor, CancellationToken ct = default);
}

/// <summary>
/// Access is the union of the modules the user's roles are scoped to, plus any
/// explicit grant, minus any explicit deny. A user in a platform-wide role (one
/// with a null ModuleKey) that carries the platform admin permission sees
/// everything. Denies always win.
/// </summary>
public class ModuleAccessService(PlatformIdentityDbContext db) : IModuleAccessService
{
    public async Task<IReadOnlyList<string>> GetAccessibleModuleKeysAsync(string userId, CancellationToken ct = default)
    {
        var roleIds = await db.UserRoles.Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId).ToListAsync(ct);

        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);

        var enabled = await db.Modules.Where(m => m.IsEnabled)
            .OrderBy(m => m.SortOrder).Select(m => m.Key).ToListAsync(ct);

        // A platform-wide role holding the platform-admin permission opens every module.
        var platformRoleIds = roles.Where(r => r.ModuleKey == null).Select(r => r.Id).ToList();
        var isPlatformAdmin = platformRoleIds.Count > 0 && await db.RoleClaims.AnyAsync(
            c => platformRoleIds.Contains(c.RoleId)
                 && c.ClaimType == PermissionCatalog.ClaimType
                 && c.ClaimValue == PlatformPermissions.ModulesManageAll, ct);

        var overrides = await db.UserModuleAccess.Where(a => a.UserId == userId).ToListAsync(ct);
        var denied = overrides.Where(a => !a.IsGranted).Select(a => a.ModuleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keys = isPlatformAdmin
            ? new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase)
            : roles.Where(r => r.ModuleKey != null).Select(r => r.ModuleKey!)
                   .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var g in overrides.Where(a => a.IsGranted)) keys.Add(g.ModuleKey);
        keys.RemoveWhere(k => denied.Contains(k) || !enabled.Contains(k, StringComparer.OrdinalIgnoreCase));

        return enabled.Where(keys.Contains).ToList();
    }

    public async Task<IReadOnlyList<ModuleRecord>> GetAccessibleModulesAsync(string userId, CancellationToken ct = default)
    {
        var keys = await GetAccessibleModuleKeysAsync(userId, ct);
        return await db.Modules.Where(m => keys.Contains(m.Key))
            .OrderBy(m => m.SortOrder).ToListAsync(ct);
    }

    /// <summary><paramref name="granted"/> null clears the override and falls back to role-implied access.</summary>
    public async Task SetAccessAsync(string userId, string moduleKey, bool? granted, string? actor, CancellationToken ct = default)
    {
        var existing = await db.UserModuleAccess
            .FirstOrDefaultAsync(a => a.UserId == userId && a.ModuleKey == moduleKey, ct);

        if (granted is null)
        {
            if (existing is not null) db.UserModuleAccess.Remove(existing);
        }
        else if (existing is not null)
        {
            existing.IsGranted = granted.Value;
        }
        else
        {
            db.UserModuleAccess.Add(new UserModuleAccess
            {
                UserId = userId,
                ModuleKey = moduleKey,
                IsGranted = granted.Value,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = actor
            });
        }

        await db.SaveChangesAsync(ct);
    }
}

public static class ClaimsPrincipalModuleExtensions
{
    /// <summary>True if the signed-in principal carries access to <paramref name="moduleKey"/>.</summary>
    public static bool CanAccessModule(this ClaimsPrincipal user, string moduleKey) =>
        user.HasClaim(PermissionCatalog.ModuleClaimType, moduleKey);

    public static IReadOnlyList<string> AccessibleModules(this ClaimsPrincipal user) =>
        user.FindAll(PermissionCatalog.ModuleClaimType).Select(c => c.Value).ToList();

    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(PermissionCatalog.ClaimType, permission);
}
