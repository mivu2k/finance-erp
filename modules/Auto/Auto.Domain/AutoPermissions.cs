namespace Auto.Domain;

public static class AutoPermissions
{
    public const string VehiclesView = "auto.vehicles.view";
    public const string VehiclesManage = "auto.vehicles.manage";
    public const string MaintenanceRecord = "auto.maintenance.record";
    public const string ReportsView = "auto.reports.view";

    public static IReadOnlyList<string> All =>
    [
        VehiclesView, VehiclesManage, MaintenanceRecord, ReportsView
    ];
}

public static class AutoRoles
{
    public const string Manager = "Fleet Manager";
    public const string Mechanic = "Fleet Mechanic";
    public const string Viewer = "Fleet Viewer";
}
