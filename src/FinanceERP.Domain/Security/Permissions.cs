namespace FinanceERP.Domain.Security;

/// <summary>
/// Central catalog of permissions. Persisted as role claims (AspNetRoleClaims table)
/// with claim type "permission". Policies are generated dynamically from these values.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "permission";

    // Chart of Accounts
    public const string AccountsView = "Accounts.View";
    public const string AccountsManage = "Accounts.Manage";

    // Vouchers / Ledger
    public const string VouchersView = "Vouchers.View";
    public const string VouchersCreate = "Vouchers.Create";
    public const string VouchersEdit = "Vouchers.Edit";
    public const string VouchersDelete = "Vouchers.Delete";
    public const string VouchersPost = "Vouchers.Post";
    public const string LedgerView = "Ledger.View";

    // Petty cash
    public const string PettyCashView = "PettyCash.View";
    public const string PettyCashManage = "PettyCash.Manage";
    public const string PettyCashAssign = "PettyCash.Assign";

    // Payment requests
    public const string RequestsCreate = "Requests.Create";
    public const string RequestsViewOwn = "Requests.ViewOwn";
    public const string RequestsViewAll = "Requests.ViewAll";
    public const string RequestsApproveManager = "Requests.ApproveManager";
    public const string RequestsApproveAdmin = "Requests.ApproveAdmin";
    public const string RequestsPay = "Requests.Pay";

    // Advances
    public const string AdvancesCreate = "Advances.Create";
    public const string AdvancesViewOwn = "Advances.ViewOwn";
    public const string AdvancesViewAll = "Advances.ViewAll";
    public const string AdvancesApprove = "Advances.Approve";
    public const string AdvancesManage = "Advances.Manage";

    // Director funds
    public const string DirectorFundsRequest = "DirectorFunds.Request";
    public const string DirectorFundsView = "DirectorFunds.View";

    // Third parties
    public const string ThirdPartiesView = "ThirdParties.View";
    public const string ThirdPartiesManage = "ThirdParties.Manage";

    // Loans & investments
    public const string LoansView = "Loans.View";
    public const string LoansManage = "Loans.Manage";
    public const string InvestmentsView = "Investments.View";
    public const string InvestmentsManage = "Investments.Manage";

    // Reports
    public const string ReportsView = "Reports.View";
    public const string ReportsExport = "Reports.Export";

    // Administration
    public const string UsersManage = "Users.Manage";
    public const string RolesManage = "Roles.Manage";
    public const string AuditView = "Audit.View";
    public const string SettingsManage = "Settings.Manage";

    public static IReadOnlyList<string> All { get; } = typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(ClaimType))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();
}

public static class AppRoles
{
    public const string SuperAdmin = "Super Admin";
    public const string Admin = "Admin";
    public const string Director = "Director";
    public const string FinanceManager = "Finance Manager";
    public const string Accountant = "Accountant";
    public const string Manager = "Manager";
    public const string Employee = "Employee";
    public const string Auditor = "Auditor";
    public const string Viewer = "Viewer";

    public static readonly string[] All =
    [
        SuperAdmin, Admin, Director, FinanceManager, Accountant, Manager, Employee, Auditor, Viewer
    ];
}
