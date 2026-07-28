using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Identity;

/// <summary>A user as a business module sees them: read-only, no Identity types.</summary>
public record PlatformUser(
    string UserId,
    string FullName,
    string? Email,
    string? EmployeeCode,
    string? ManagerId,
    bool IsActive);

/// <summary>
/// The one way a business module is allowed to read the identity database. Modules
/// mirror what they need into their own database rather than joining across —
/// there are no cross-database foreign keys on this platform.
/// </summary>
public interface IPlatformUserDirectory
{
    Task<PlatformUser?> FindAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformUser>> ListAsync(bool activeOnly = true, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformUser>> ListByIdsAsync(IEnumerable<string> userIds, CancellationToken ct = default);
    /// <summary>Users who can enter a given module — the candidate pool for that module's assignments.</summary>
    Task<IReadOnlyList<PlatformUser>> ListForModuleAsync(string moduleKey, CancellationToken ct = default);
    /// <summary>Members of a role, by role name. Used to fan notifications out to a role.</summary>
    Task<IReadOnlyList<PlatformUser>> ListByRoleAsync(string roleName, CancellationToken ct = default);
    /// <summary>Role names held by a user, across every module.</summary>
    Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct = default);
}

public class PlatformUserDirectory(PlatformIdentityDbContext db, IModuleAccessService access)
    : IPlatformUserDirectory
{
    public async Task<PlatformUser?> FindAsync(string userId, CancellationToken ct = default) =>
        await Project(db.Users.Where(u => u.Id == userId)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PlatformUser>> ListAsync(bool activeOnly = true, CancellationToken ct = default) =>
        await Project(db.Users.Where(u => !activeOnly || u.IsActive).OrderBy(u => u.FullName)).ToListAsync(ct);

    public async Task<IReadOnlyList<PlatformUser>> ListByIdsAsync(
        IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        return await Project(db.Users.Where(u => ids.Contains(u.Id))).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformUser>> ListForModuleAsync(
        string moduleKey, CancellationToken ct = default)
    {
        var all = await ListAsync(activeOnly: true, ct);
        var allowed = new List<PlatformUser>();
        foreach (var u in all)
        {
            var keys = await access.GetAccessibleModuleKeysAsync(u.UserId, ct);
            if (keys.Contains(moduleKey, StringComparer.OrdinalIgnoreCase)) allowed.Add(u);
        }
        return allowed;
    }

    public async Task<IReadOnlyList<PlatformUser>> ListByRoleAsync(
        string roleName, CancellationToken ct = default)
    {
        var query =
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            join u in db.Users on ur.UserId equals u.Id
            where r.Name == roleName
            select u;

        return await Project(query).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct = default) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               where ur.UserId == userId && r.Name != null
               select r.Name!).ToListAsync(ct);

    private static IQueryable<PlatformUser> Project(IQueryable<ApplicationUser> q) =>
        q.Select(u => new PlatformUser(u.Id, u.FullName, u.Email, u.EmployeeCode, u.ManagerId, u.IsActive));
}
