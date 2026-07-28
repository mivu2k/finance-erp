namespace Hr.Domain;

/// <summary>
/// HR's permission catalog. Names are prefixed with the module key so they can
/// never collide with another app's — see PermissionCatalog.
/// </summary>
public static class HrPermissions
{
    public const string EmployeesView = "hr.employees.view";
    public const string EmployeesManage = "hr.employees.manage";
    public const string EmployeesViewSensitive = "hr.employees.viewsensitive";
    public const string DocumentsView = "hr.documents.view";
    public const string DocumentsManage = "hr.documents.manage";
    public const string CatalogManage = "hr.catalog.manage";
    public const string ReportsView = "hr.reports.view";

    public static IReadOnlyList<string> All =>
    [
        EmployeesView, EmployeesManage, EmployeesViewSensitive,
        DocumentsView, DocumentsManage, CatalogManage, ReportsView
    ];
}

/// <summary>Roles HR ships with. Each is scoped to the "hr" module in identity.</summary>
public static class HrRoles
{
    public const string Manager = "HR Manager";
    public const string Officer = "HR Officer";
    public const string Viewer = "HR Viewer";
}
