using ErpPlatform.Shared.Identity;
using FinanceERP.Domain.Security;

namespace FinanceERP.Infrastructure;

/// <summary>
/// What the Finance app tells the platform about itself: its permission catalog
/// and the roles it ships with. The identity seeder turns this into rows in the
/// shared identity database, and the role editor renders its permission matrix
/// from it.
/// </summary>
public static class FinanceModule
{
    public static ModuleRegistration Registration => new(
        AppModules.Finance,
        Permissions_,
        Roles_);

    private static PermissionDescriptor P(string name, string group, string description) =>
        new(name, AppModules.Finance, group, description);

    private static readonly IReadOnlyList<PermissionDescriptor> Permissions_ =
    [
        P(Permissions.AccountsView, "Chart of Accounts", "View the chart of accounts"),
        P(Permissions.AccountsManage, "Chart of Accounts", "Create and edit accounts"),

        P(Permissions.VouchersView, "Vouchers", "View vouchers"),
        P(Permissions.VouchersCreate, "Vouchers", "Create draft vouchers"),
        P(Permissions.VouchersEdit, "Vouchers", "Edit draft vouchers"),
        P(Permissions.VouchersDelete, "Vouchers", "Delete draft vouchers"),
        P(Permissions.VouchersPost, "Vouchers", "Post and void vouchers"),
        P(Permissions.LedgerView, "Vouchers", "View the ledger and day book"),

        P(Permissions.PettyCashView, "Petty Cash", "View petty cash balances"),
        P(Permissions.PettyCashManage, "Petty Cash", "Record petty cash movements"),
        P(Permissions.PettyCashAssign, "Petty Cash", "Assign petty cash holders"),

        P(Permissions.RequestsCreate, "Payment Requests", "Raise a payment request"),
        P(Permissions.RequestsViewOwn, "Payment Requests", "View own requests"),
        P(Permissions.RequestsViewAll, "Payment Requests", "View everyone's requests"),
        P(Permissions.RequestsApproveManager, "Payment Requests", "Approve as line manager"),
        P(Permissions.RequestsApproveAdmin, "Payment Requests", "Approve as administrator"),
        P(Permissions.RequestsPay, "Payment Requests", "Release payment and post the voucher"),

        P(Permissions.AdvancesCreate, "Advances", "Request an advance"),
        P(Permissions.AdvancesViewOwn, "Advances", "View own advances"),
        P(Permissions.AdvancesViewAll, "Advances", "View everyone's advances"),
        P(Permissions.AdvancesApprove, "Advances", "Approve advances"),
        P(Permissions.AdvancesManage, "Advances", "Disburse, settle and recover advances"),

        P(Permissions.PayrollView, "Payroll", "View payroll runs and payslips"),
        P(Permissions.PayrollManage, "Payroll", "Manage components, structures and runs"),
        P(Permissions.PayrollApprove, "Payroll", "Approve a payroll run"),
        P(Permissions.PayrollPay, "Payroll", "Pay a payroll run and post the voucher"),
        P(Permissions.PayrollViewOwn, "Payroll", "View own payslips"),

        P(Permissions.DirectorFundsRequest, "Director Funds", "Raise a director fund request"),
        P(Permissions.DirectorFundsView, "Director Funds", "View director fund activity"),

        P(Permissions.ThirdPartiesView, "Third Parties", "View third parties"),
        P(Permissions.ThirdPartiesManage, "Third Parties", "Create and edit third parties"),

        P(Permissions.LoansView, "Loans & Investments", "View loans"),
        P(Permissions.LoansManage, "Loans & Investments", "Manage loans and instalments"),
        P(Permissions.InvestmentsView, "Loans & Investments", "View investments"),
        P(Permissions.InvestmentsManage, "Loans & Investments", "Manage investments"),

        P(Permissions.UtilitiesView, "Utilities", "View utility connections and bills"),
        P(Permissions.UtilitiesManage, "Utilities", "Manage connections and record bills"),
        P(Permissions.UtilitiesPay, "Utilities", "Pay utility bills"),

        P(Permissions.ReportsView, "Reports", "View financial reports"),
        P(Permissions.ReportsExport, "Reports", "Export reports to PDF and Excel"),

        P(Permissions.UsersManage, "Administration", "Manage finance employee profiles"),
        P(Permissions.RolesManage, "Administration", "Edit finance roles"),
        P(Permissions.AuditView, "Administration", "View the finance audit trail"),
        P(Permissions.SettingsManage, "Administration", "Change finance settings and close the year")
    ];

    private static readonly IReadOnlyList<RoleTemplate> Roles_ =
    [
        new(AppRoles.Admin, "Everything in Finance except changing company settings.",
            Permissions.All.Where(p => p != Permissions.SettingsManage).ToList()),

        new(AppRoles.Director, "Oversight, approvals and director fund requests.",
        [
            Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView,
            Permissions.ReportsView, Permissions.ReportsExport, Permissions.PettyCashView,
            Permissions.PettyCashAssign, Permissions.RequestsViewAll, Permissions.DirectorFundsRequest,
            Permissions.DirectorFundsView, Permissions.AdvancesViewAll, Permissions.AdvancesApprove,
            Permissions.LoansView, Permissions.InvestmentsView, Permissions.ThirdPartiesView,
            Permissions.UtilitiesView, Permissions.PayrollView, Permissions.PayrollApprove
        ]),

        new(AppRoles.FinanceManager, "Runs the finance function day to day.",
        [
            Permissions.AccountsView, Permissions.AccountsManage, Permissions.LedgerView,
            Permissions.VouchersView, Permissions.VouchersCreate, Permissions.VouchersEdit,
            Permissions.VouchersPost, Permissions.ReportsView, Permissions.ReportsExport,
            Permissions.PettyCashView, Permissions.PettyCashManage, Permissions.RequestsViewAll,
            Permissions.RequestsApproveAdmin, Permissions.AdvancesViewAll, Permissions.AdvancesApprove,
            Permissions.AdvancesManage, Permissions.LoansView, Permissions.LoansManage,
            Permissions.InvestmentsView, Permissions.InvestmentsManage,
            Permissions.ThirdPartiesView, Permissions.ThirdPartiesManage,
            Permissions.UtilitiesView, Permissions.UtilitiesManage, Permissions.UtilitiesPay,
            Permissions.PayrollView, Permissions.PayrollManage, Permissions.PayrollApprove
        ]),

        new(AppRoles.Accountant, "Books the entries and releases payments.",
        [
            Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView,
            Permissions.VouchersCreate, Permissions.VouchersEdit, Permissions.VouchersPost,
            Permissions.ReportsView, Permissions.ReportsExport, Permissions.PettyCashView,
            Permissions.PettyCashManage, Permissions.RequestsViewAll, Permissions.RequestsPay,
            Permissions.AdvancesViewAll, Permissions.AdvancesManage,
            Permissions.LoansView, Permissions.InvestmentsView,
            Permissions.ThirdPartiesView, Permissions.ThirdPartiesManage,
            Permissions.UtilitiesView, Permissions.UtilitiesManage, Permissions.UtilitiesPay,
            Permissions.PayrollView, Permissions.PayrollManage, Permissions.PayrollPay
        ]),

        new(AppRoles.Manager, "Approves their team's requests as line manager.",
        [
            Permissions.RequestsCreate, Permissions.RequestsViewOwn, Permissions.RequestsApproveManager,
            Permissions.AdvancesCreate, Permissions.AdvancesViewOwn, Permissions.ReportsView,
            Permissions.PayrollViewOwn
        ]),

        new(AppRoles.Employee, "Raises requests and advances, sees their own payslips.",
        [
            Permissions.RequestsCreate, Permissions.RequestsViewOwn,
            Permissions.AdvancesCreate, Permissions.AdvancesViewOwn, Permissions.PayrollViewOwn
        ]),

        new(AppRoles.Auditor, "Read-only across the books, plus the audit trail.",
        [
            Permissions.AccountsView, Permissions.LedgerView, Permissions.VouchersView,
            Permissions.ReportsView, Permissions.ReportsExport, Permissions.AuditView,
            Permissions.RequestsViewAll, Permissions.AdvancesViewAll, Permissions.LoansView,
            Permissions.InvestmentsView, Permissions.ThirdPartiesView, Permissions.PettyCashView,
            Permissions.UtilitiesView, Permissions.PayrollView
        ]),

        new(AppRoles.Viewer, "Read-only on accounts, ledger and reports.",
        [
            Permissions.AccountsView, Permissions.LedgerView,
            Permissions.VouchersView, Permissions.ReportsView
        ])
    ];
}
