namespace ErpPlatform.Shared.Identity;

/// <summary>
/// Permissions that belong to the platform itself rather than to any business
/// module — managing users, roles and who can enter which app.
/// </summary>
public static class PlatformPermissions
{
    public const string ModuleKey = "platform";

    public const string UsersView = "platform.users.view";
    public const string UsersManage = "platform.users.manage";
    public const string RolesManage = "platform.roles.manage";
    /// <summary>Grants entry to every enabled module and the ability to change module access.</summary>
    public const string ModulesManageAll = "platform.modules.manageall";
    /// <summary>Edit the letterhead every app prints — name, logo, address, footer.</summary>
    public const string CompanyManage = "platform.company.manage";
    /// <summary>Maintain the sticker layouts every module prints labels from.</summary>
    public const string LabelsManage = "platform.labels.manage";

    public static readonly IReadOnlyList<PermissionDescriptor> All =
    [
        new(UsersView, ModuleKey, "Users", "View the user directory"),
        new(UsersManage, ModuleKey, "Users", "Create, edit, deactivate and reset users"),
        new(RolesManage, ModuleKey, "Roles", "Edit roles and their permission matrix"),
        new(ModulesManageAll, ModuleKey, "Modules", "Access every app and assign app access"),
        new(CompanyManage, ModuleKey, "Company", "Edit the company profile and logo used on every printed document"),
        new(LabelsManage, ModuleKey, "Labels", "Define sticker sizes and which fields print on them")
    ];
}

/// <summary>Roles that exist across the whole platform, not inside one app.</summary>
public static class PlatformRoles
{
    public const string SuperAdmin = "Super Admin";
}
