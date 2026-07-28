namespace FinanceERP.Domain.Security;

/// <summary>
/// Central catalog of permissions. Persisted as role claims (AspNetRoleClaims table)
/// with claim type "permission". Policies are generated dynamically from these values.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "permission";

    // Chart of Accounts
    public const string AccountsView = "finance.accounts.view";
    public const string AccountsManage = "finance.accounts.manage";

    // Vouchers / Ledger
    public const string VouchersView = "finance.vouchers.view";
    public const string VouchersCreate = "finance.vouchers.create";
    public const string VouchersEdit = "finance.vouchers.edit";
    public const string VouchersDelete = "finance.vouchers.delete";
    public const string VouchersPost = "finance.vouchers.post";
    public const string LedgerView = "finance.ledger.view";

    // Petty cash
    public const string PettyCashView = "finance.pettycash.view";
    public const string PettyCashManage = "finance.pettycash.manage";
    public const string PettyCashAssign = "finance.pettycash.assign";

    // Payment requests
    public const string RequestsCreate = "finance.requests.create";
    public const string RequestsViewOwn = "finance.requests.viewown";
    public const string RequestsViewAll = "finance.requests.viewall";
    public const string RequestsApproveManager = "finance.requests.approvemanager";
    public const string RequestsApproveAdmin = "finance.requests.approveadmin";
    public const string RequestsPay = "finance.requests.pay";

    // Advances
    public const string AdvancesCreate = "finance.advances.create";
    public const string AdvancesViewOwn = "finance.advances.viewown";
    public const string AdvancesViewAll = "finance.advances.viewall";
    public const string AdvancesApprove = "finance.advances.approve";
    public const string AdvancesManage = "finance.advances.manage";

    // Payroll
    public const string PayrollView = "finance.payroll.view";
    /// <summary>Salary structures, component catalog, creating and generating runs.</summary>
    public const string PayrollManage = "finance.payroll.manage";
    public const string PayrollApprove = "finance.payroll.approve";
    public const string PayrollPay = "finance.payroll.pay";
    /// <summary>An employee seeing their own payslips.</summary>
    public const string PayrollViewOwn = "finance.payroll.viewown";

    // Director funds
    public const string DirectorFundsRequest = "finance.directorfunds.request";
    public const string DirectorFundsView = "finance.directorfunds.view";

    // Third parties
    public const string ThirdPartiesView = "finance.thirdparties.view";
    public const string ThirdPartiesManage = "finance.thirdparties.manage";

    // Loans & investments
    public const string LoansView = "finance.loans.view";
    public const string LoansManage = "finance.loans.manage";
    public const string InvestmentsView = "finance.investments.view";
    public const string InvestmentsManage = "finance.investments.manage";

    // Utilities
    public const string UtilitiesView = "finance.utilities.view";
    public const string UtilitiesManage = "finance.utilities.manage";
    public const string UtilitiesPay = "finance.utilities.pay";

    // Reports
    public const string ReportsView = "finance.reports.view";
    public const string ReportsExport = "finance.reports.export";

    // Administration
    public const string UsersManage = "finance.users.manage";
    public const string RolesManage = "finance.roles.manage";
    public const string AuditView = "finance.audit.view";
    public const string SettingsManage = "finance.settings.manage";

    public static IReadOnlyList<string> All { get; } = typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(ClaimType))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();
}

/// <summary>
/// Roles that live inside the Finance app. Every one of these is scoped to the
/// "finance" module in the identity database, so holding one both admits the user
/// to the Finance tile and decides what they can do once inside.
/// </summary>
public static class AppRoles
{
    /// <summary>
    /// The platform-wide administrator role. Not owned by Finance — it lives in
    /// <c>ErpPlatform.Shared.Identity.PlatformRoles</c> and is named here so
    /// accounts code can refer to it without taking a dependency on the identity
    /// layer. Keep the two in step.
    /// </summary>
    public const string SuperAdmin = "Super Admin";

    public const string Admin = "Finance Admin";
    public const string Director = "Director";
    public const string FinanceManager = "Finance Manager";
    public const string Accountant = "Accountant";
    public const string Manager = "Finance Approver";
    public const string Employee = "Finance Employee";
    public const string Auditor = "Auditor";
    public const string Viewer = "Finance Viewer";

    public static readonly string[] All =
    [
        Admin, Director, FinanceManager, Accountant, Manager, Employee, Auditor, Viewer
    ];
}
