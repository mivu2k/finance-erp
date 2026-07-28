namespace ErpPlatform.Shared.Identity;

/// <summary>A role a module ships with, and the permissions it starts out holding.</summary>
public record RoleTemplate(string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>
/// What a module tells the platform about itself at startup: its permissions and
/// its default roles. The identity seeder reads this to create roles and grant
/// claims, and the role editor reads it to render the permission matrix.
/// </summary>
public record ModuleRegistration(
    string ModuleKey,
    IReadOnlyList<PermissionDescriptor> Permissions,
    IReadOnlyList<RoleTemplate> Roles);

/// <summary>
/// Startup-time registry of every module linked into the host. Modules call
/// <see cref="Register"/> from their <c>AddXxxModule</c> extension method.
/// </summary>
public static class ModuleRegistry
{
    private static readonly List<ModuleRegistration> Items = [];
    private static readonly Lock Gate = new();

    public static void Register(ModuleRegistration registration)
    {
        lock (Gate)
        {
            if (Items.Any(i => i.ModuleKey == registration.ModuleKey)) return;
            Items.Add(registration);
        }
        PermissionCatalog.Register(registration.Permissions);
    }

    public static IReadOnlyList<ModuleRegistration> All
    {
        get { lock (Gate) return Items.ToList(); }
    }

    public static ModuleRegistration? Find(string moduleKey)
    {
        lock (Gate)
            return Items.FirstOrDefault(i =>
                i.ModuleKey.Equals(moduleKey, StringComparison.OrdinalIgnoreCase));
    }
}
