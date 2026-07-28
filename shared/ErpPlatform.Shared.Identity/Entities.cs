using Microsoft.AspNetCore.Identity;

namespace ErpPlatform.Shared.Identity;

/// <summary>
/// The one user record for the whole platform. Deliberately free of any module's
/// business fields — a module that needs more about a person keys its own profile
/// row off <see cref="IdentityUser.Id"/> (Finance does this with UserProfile, HR
/// with Employee).
/// </summary>
public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    /// <summary>Shared handle used to line a user up with HR/attendance records.</summary>
    public string? EmployeeCode { get; set; }
    public string? ManagerId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginUtc { get; set; }
}

/// <summary>
/// A role, optionally scoped to one module. <see cref="ModuleKey"/> null means a
/// platform-wide role (Super Admin); otherwise the role both grants entry to that
/// module and defines what the user can do inside it. Permissions themselves stay
/// in AspNetRoleClaims as claims of type "permission".
/// </summary>
public class ApplicationRole : IdentityRole
{
    public string? ModuleKey { get; set; }
    public string? Description { get; set; }
    /// <summary>Built-in roles can't be renamed or deleted from the admin UI.</summary>
    public bool IsSystem { get; set; }

    public ApplicationRole() { }

    public ApplicationRole(string name, string? moduleKey, string? description = null, bool isSystem = false)
        : base(name)
    {
        ModuleKey = moduleKey;
        Description = description;
        IsSystem = isSystem;
    }
}

/// <summary>Seeded mirror of <see cref="AppModules.All"/> so modules can be disabled without a deploy.</summary>
public class ModuleRecord
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Per-user override of the access their roles imply. A grant lets someone into a
/// module they hold no role in (read-only visitor); a deny locks someone out
/// without stripping their role. Denies win.
/// </summary>
public class UserModuleAccess
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ModuleKey { get; set; } = string.Empty;
    public bool IsGranted { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
}
