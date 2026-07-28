namespace Repair.Domain;

public static class RepairPermissions
{
    public const string CustomersView = "repair.customers.view";
    public const string CustomersManage = "repair.customers.manage";

    public const string IntakesView = "repair.intakes.view";
    public const string IntakesManage = "repair.intakes.manage";

    public const string JobsView = "repair.jobs.view";
    public const string JobsManage = "repair.jobs.manage";
    public const string JobsAssign = "repair.jobs.assign";
    public const string JobsDiagnose = "repair.jobs.diagnose";
    public const string JobsDeliver = "repair.jobs.deliver";

    public const string PartsView = "repair.parts.view";
    public const string PartsManage = "repair.parts.manage";

    public const string QuotationsView = "repair.quotations.view";
    public const string QuotationsManage = "repair.quotations.manage";
    public const string QuotationsApprove = "repair.quotations.approve";

    public const string OrdersView = "repair.orders.view";
    public const string OrdersManage = "repair.orders.manage";
    public const string PaymentsRecord = "repair.payments.record";

    public const string ReportsView = "repair.reports.view";
    public const string CatalogManage = "repair.catalog.manage";

    public static IReadOnlyList<string> All =>
    [
        CustomersView, CustomersManage,
        IntakesView, IntakesManage,
        JobsView, JobsManage, JobsAssign, JobsDiagnose, JobsDeliver,
        PartsView, PartsManage,
        QuotationsView, QuotationsManage, QuotationsApprove,
        OrdersView, OrdersManage, PaymentsRecord,
        ReportsView, CatalogManage
    ];
}

/// <summary>
/// Roles carried over from the Laravel app's role list, scoped to the repair
/// module so holding one also admits the user to the Repair tile.
/// </summary>
public static class RepairRoles
{
    public const string Manager = "Repair Manager";
    public const string Supervisor = "Repair Supervisor";
    public const string Technician = "Technician";
    public const string Sales = "Repair Sales";
    public const string Store = "Store Keeper";
    public const string Accountant = "Repair Accountant";
    public const string Viewer = "Repair Viewer";
}
