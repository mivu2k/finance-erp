namespace ErpPlatform.Shared.Identity;

/// <summary>
/// One permission, owned by a module. The name is the value stored in
/// AspNetRoleClaims and checked by the authorization policies, and is always
/// namespaced by module key so two modules can both have a "View" without
/// colliding — e.g. <c>finance.vouchers.post</c>, <c>repair.jobs.assign</c>.
/// </summary>
public record PermissionDescriptor(string Name, string ModuleKey, string Group, string Description);

/// <summary>
/// Registry every module fills at startup. The role editor renders from this, and
/// the seeder uses it to grant permissions to built-in roles.
/// </summary>
public static class PermissionCatalog
{
    public const string ClaimType = "permission";
    /// <summary>Claim written onto the principal for each module the user may enter.</summary>
    public const string ModuleClaimType = "module";

    private static readonly List<PermissionDescriptor> Registered = [];
    private static readonly Lock Gate = new();

    public static void Register(IEnumerable<PermissionDescriptor> permissions)
    {
        lock (Gate)
        {
            foreach (var p in permissions)
            {
                if (Registered.Any(x => x.Name == p.Name)) continue;
                Registered.Add(p);
            }
        }
    }

    public static IReadOnlyList<PermissionDescriptor> All
    {
        get { lock (Gate) return Registered.ToList(); }
    }

    public static IReadOnlyList<PermissionDescriptor> ForModule(string moduleKey)
    {
        lock (Gate)
            return Registered
                .Where(p => p.ModuleKey.Equals(moduleKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>Which module a permission name belongs to, from its prefix.</summary>
    public static string ModuleOf(string permission)
    {
        var dot = permission.IndexOf('.');
        return dot > 0 ? permission[..dot] : string.Empty;
    }
}
