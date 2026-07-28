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

    // Attendance
    public const string AttendanceViewOwn = "hr.attendance.viewown";
    public const string AttendanceViewAll = "hr.attendance.viewall";
    /// <summary>Correcting a day by hand when the terminal missed a read.</summary>
    public const string AttendanceEdit = "hr.attendance.edit";
    public const string DevicesManage = "hr.devices.manage";

    // Leave
    public const string LeaveRequest = "hr.leave.request";
    public const string LeaveViewOwn = "hr.leave.viewown";
    public const string LeaveViewAll = "hr.leave.viewall";
    public const string LeaveApprove = "hr.leave.approve";
    public const string LeaveManage = "hr.leave.manage";

    public static IReadOnlyList<string> All =>
    [
        EmployeesView, EmployeesManage, EmployeesViewSensitive,
        DocumentsView, DocumentsManage, CatalogManage, ReportsView,
        AttendanceViewOwn, AttendanceViewAll, AttendanceEdit, DevicesManage,
        LeaveRequest, LeaveViewOwn, LeaveViewAll, LeaveApprove, LeaveManage
    ];
}

/// <summary>Roles HR ships with. Each is scoped to the "hr" module in identity.</summary>
public static class HrRoles
{
    public const string Manager = "HR Manager";
    public const string Officer = "HR Officer";
    public const string Viewer = "HR Viewer";
    /// <summary>Any employee: sees their own attendance and raises leave requests.</summary>
    public const string SelfService = "Employee Self Service";
    /// <summary>Approves their own team's leave without seeing the whole company.</summary>
    public const string LineManager = "Line Manager";
}
