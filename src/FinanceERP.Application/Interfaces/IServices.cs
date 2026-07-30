using FinanceERP.Application.DTOs;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Application.Interfaces;

public interface IVoucherService
{
    Task<PagedResult<VoucherListItemDto>> ListAsync(ReportFilter filter);
    Task<Voucher?> GetAsync(int id);
    Task<Voucher> SaveAsync(VoucherEditDto dto, bool post);
    Task PostAsync(int id);
    Task VoidAsync(int id, string reason);
    /// <summary>Soft-deletes a draft voucher entered by mistake (posted vouchers must be voided).</summary>
    Task DeleteDraftAsync(int id);
    /// <summary>Copies a voucher into a fresh draft (dated today) — the fix-and-repost path.</summary>
    Task<Voucher> DuplicateAsDraftAsync(int id);
    /// <summary>Creates and posts a system-generated voucher from a source module.</summary>
    /// <remarks>
    /// Pass <paramref name="personId"/> when the whole voucher is traceable to one person
    /// (a payment request, an advance) — every line is stamped with it so the ledger and
    /// reports can be filtered per user.
    /// </remarks>
    Task<Voucher> PostSystemVoucherAsync(VoucherType type, DateOnly date, string narration,
        string source, int? sourceId, IEnumerable<(int AccountId, decimal Debit, decimal Credit, string? Description)> lines,
        string? personId = null, string? personName = null);
    /// <summary>
    /// Year-end close: posts a journal moving all income/expense balances up to
    /// <paramref name="closeDate"/> into Retained Earnings, then locks the books
    /// through that date.
    /// </summary>
    Task<Voucher> CloseFiscalYearAsync(DateOnly closeDate);
}

public interface IReconciliationService
{
    Task<List<VoucherLine>> GetLinesAsync(int accountId, DateOnly from, DateOnly to, bool? reconciled = null);
    Task SetReconciledAsync(IEnumerable<int> lineIds, bool reconciled);
    Task<(decimal Book, decimal Reconciled)> BalancesAsync(int accountId, DateOnly asOf);
}

public interface IAccountService
{
    Task<List<Account>> GetTreeAsync();
    Task<List<Account>> GetPostableAsync();
    Task<Account> SaveAsync(Account account);
    Task DeleteAsync(int id);
    Task<decimal> GetBalanceAsync(int accountId, DateOnly? asOf = null);
    Task<Account> EnsureChildAccountAsync(string parentCode, string name, bool isSystem = false);
}

public interface IPaymentRequestService
{
    Task<PagedResult<PaymentRequest>> ListAsync(ReportFilter filter, string? requesterId = null, RequestStatus? status = null);
    Task<PaymentRequest?> GetAsync(int id);
    Task<PaymentRequest> SaveDraftAsync(PaymentRequest request);
    Task SubmitAsync(int id);
    Task ApproveAsync(int id, string level, string? comment);
    Task RejectAsync(int id, string level, string? comment);
    /// <param name="lineAccounts">Accountant's classification: request line id → ledger account id.</param>
    Task<Voucher> PayAsync(int id, int payFromAccountId, string? comment,
        IReadOnlyDictionary<int, int>? lineAccounts = null);
    Task CancelAsync(int id);
    /// <summary>Admin-only: soft-delete a draft/rejected/cancelled request entered by mistake.</summary>
    Task DeleteAsync(int id);

    // Advance-kind lifecycle: disburse → justify → approve justification → settle.
    Task<Voucher> DisburseAsync(int id, int payFromAccountId, string? comment);
    Task SubmitJustificationAsync(int id, List<PaymentRequestLine> lines);
    Task ApproveJustificationAsync(int id, string? comment);
    Task RejectJustificationAsync(int id, string? comment);
    /// <summary>
    /// Posts actuals and clears the advance. The gap between the amount disbursed and the
    /// amount justified (e.g. 20,000 taken, 17,000 spent) is handled per
    /// <paramref name="handling"/>: settled through <paramref name="cashAccountId"/> now,
    /// left outstanding for manual clearing, or — underspend only — converted into a
    /// salary-deductible advance the next payroll run recovers.
    /// </summary>
    Task<Voucher> SettleAsync(int id, int? cashAccountId, string? comment,
        IReadOnlyDictionary<int, int> lineAccounts,
        AdvanceDifferenceHandling handling = AdvanceDifferenceHandling.SettleNow,
        int recoveryInstallments = 1);

    /// <summary>
    /// Clears a balance left outstanding by <see cref="SettleAsync"/>: the employee hands
    /// back unspent cash (Dr cash, Cr their advance account) or the company pays the
    /// overspend it owed them (Dr payable, Cr cash).
    /// </summary>
    Task<Voucher> RecordAdvanceReturnAsync(int id, decimal amount, int cashAccountId, string? comment);
}

public interface IAdvanceService
{
    Task<PagedResult<EmployeeAdvance>> ListAsync(ReportFilter filter, string? employeeId = null);
    Task<EmployeeAdvance?> GetAsync(int id);
    Task<EmployeeAdvance> SaveDraftAsync(EmployeeAdvance advance);
    Task SubmitAsync(int id);
    Task ApproveAsync(int id);
    Task RejectAsync(int id, string? reason);
    Task<Voucher> DisburseAsync(int id, int payFromAccountId);
    Task<Voucher> RepayInstallmentAsync(int installmentId, decimal amount, int receiveIntoAccountId, DateOnly date);

    // Employee-initiated repayment: claim → accountant confirms (posts) or rejects.
    Task ClaimInstallmentPaidAsync(int installmentId);
    Task<Voucher> ConfirmInstallmentClaimAsync(int installmentId, int receiveIntoAccountId);
    Task RejectInstallmentClaimAsync(int installmentId, string? reason);

    // --- Payroll integration ---
    /// <summary>Unpaid instalments of disbursed advances falling due on or before <paramref name="asOf"/>.</summary>
    Task<List<AdvanceInstallment>> GetDueInstallmentsAsync(string employeeId, DateOnly asOf);
    /// <summary>The ledger account holding this employee's outstanding advances.</summary>
    Task<Account> GetAdvanceAccountAsync(string employeeName);
    /// <summary>
    /// Marks an instalment recovered by a payroll run. Unlike
    /// <see cref="RepayInstallmentAsync"/> this posts nothing — the payroll voucher
    /// already credits the employee's advance account.
    /// </summary>
    Task ApplyPayrollDeductionAsync(int installmentId, decimal amount, int voucherId, DateOnly date);
    /// <summary>Creates an already-disbursed advance for money the employee is holding
    /// (e.g. the unspent part of a settled cash advance), to be recovered from salary.</summary>
    Task<EmployeeAdvance> CreateRecoverableAdvanceAsync(string employeeId, string employeeName,
        decimal amount, string reason, int installmentCount = 1);
}

public interface IPayrollService
{
    // --- Component catalog ---
    Task<List<PayComponent>> GetComponentsAsync(bool activeOnly = true);
    Task<PayComponent> SaveComponentAsync(PayComponent component);
    Task DeleteComponentAsync(int id);

    // --- Salary structures ---
    Task<List<SalaryStructure>> GetStructuresAsync(bool activeOnly = true);
    Task<SalaryStructure?> GetStructureAsync(int id);
    Task<SalaryStructure?> GetActiveStructureForAsync(string employeeId, DateOnly asOf);
    /// <summary>Saves a structure; a new one supersedes (deactivates) the employee's previous structure.</summary>
    Task<SalaryStructure> SaveStructureAsync(SalaryStructure structure);
    Task DeleteStructureAsync(int id);

    // --- Runs ---
    Task<PagedResult<PayrollRun>> ListRunsAsync(ReportFilter filter);
    Task<PayrollRun?> GetRunAsync(int id);
    Task<PayrollRun> CreateRunAsync(DateOnly periodMonth, DateOnly payDate, int? departmentId, int? projectId);
    /// <summary>
    /// (Re)builds every payslip in a draft run from the employees' active salary structures:
    /// basic + allowances − component deductions − absence − manual deductions − due advance
    /// instalments. Safe to call repeatedly; existing payslips are replaced.
    /// </summary>
    Task<PayrollRun> GenerateAsync(int runId, IEnumerable<string>? employeeIds = null,
        IReadOnlyDictionary<string, PayslipInputDto>? inputs = null);
    Task SubmitRunAsync(int runId);
    Task ApproveRunAsync(int runId, string? comment);
    Task RejectRunAsync(int runId, string? comment);
    /// <summary>
    /// Posts the salary voucher and disburses net pay:
    /// Dr salary expense per allowance head, Cr each deduction's liability head,
    /// Cr the employee's advance account for recovered instalments, Cr cash/bank for net pay.
    /// Recovered instalments are marked repaid against the same voucher.
    /// </summary>
    Task<Voucher> PayRunAsync(int runId, int payFromAccountId, string? comment);
    Task CancelRunAsync(int runId, string? reason);
    Task DeleteRunAsync(int runId);

    // --- Payslips ---
    Task<Payslip?> GetPayslipAsync(int id);
    Task<List<Payslip>> GetPayslipsForEmployeeAsync(string employeeId, int max = 60);
    /// <summary>Employees on an active structure who have no payslip in the given run yet.</summary>
    Task<List<SalaryStructure>> GetEligibleEmployeesAsync(int runId);
}

public interface ILoanService
{
    Task<PagedResult<Loan>> ListAsync(ReportFilter filter, LoanDirection? direction = null);
    Task<Loan?> GetAsync(int id);
    Task<Loan> CreateAsync(Loan loan, int cashAccountId);
    Task<Voucher> PayInstallmentAsync(int installmentId, decimal amount, int cashAccountId, DateOnly date);
}

public interface IInvestmentService
{
    Task<PagedResult<Investment>> ListAsync(ReportFilter filter);
    Task<Investment?> GetAsync(int id);
    Task<Investment> CreateAsync(Investment investment, int cashAccountId);
    Task<Voucher> AddTransactionAsync(int investmentId, InvestmentTxnType type, decimal amount, DateOnly date, int cashAccountId, string? notes);
}

public interface IPettyCashService
{
    Task<List<PettyCashAssignment>> ListAssignmentsAsync();
    Task<PettyCashAssignment> AssignAsync(PettyCashAssignment assignment, int sourceAccountId);
    Task<(decimal Opening, decimal Received, decimal Paid, decimal Closing)> GetDayBookAsync(int pettyCashAccountId, DateOnly date);
}

/// <summary>Which way money moved between us and the party.</summary>
public enum PartyMovement
{
    /// <summary>Money out to them — debits their account, credits cash/bank.</summary>
    Debit = 0,
    /// <summary>Money in from them — credits their account, debits cash/bank.</summary>
    Credit = 1
}

public interface IThirdPartyService
{
    Task<PagedResult<ThirdParty>> ListAsync(ReportFilter filter);
    Task<ThirdParty?> GetAsync(int id);
    Task<ThirdParty> SaveAsync(ThirdParty thirdParty);
    Task DeleteAsync(int id);

    /// <summary>
    /// Records money moved with a party in one step, posting a real voucher against
    /// their account and the chosen cash/bank account. This is the whole point of the
    /// party screen — no schedules, no instalments, just what was paid or received.
    /// </summary>
    Task<Voucher> RecordAsync(int partyId, PartyMovement movement, decimal amount,
        int cashAccountId, DateOnly date, string? narration = null);

    /// <summary>The party's account statement, with a running balance.</summary>
    Task<List<LedgerRowDto>> GetStatementAsync(int partyId, DateOnly? from = null, DateOnly? to = null);

    /// <summary>Current balance on the party's account. Zero when they have none yet.</summary>
    Task<decimal> GetBalanceAsync(int partyId);
}

public class UtilityBillFilter
{
    public int? LocationId { get; set; }
    public int? ConnectionId { get; set; }
    public UtilityType? Type { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>null = all, true = paid only, false = unpaid only.</summary>
    public bool? Paid { get; set; }
}

public interface IUtilityService
{
    Task<List<UtilityLocation>> GetLocationsAsync(bool includeConnections = false);
    Task<UtilityLocation> SaveLocationAsync(UtilityLocation location);
    Task<UtilityConnection> SaveConnectionAsync(UtilityConnection connection);
    Task DeleteConnectionAsync(int id);
    Task<List<UtilityBill>> ListBillsAsync(UtilityBillFilter filter, int max = 500);
    Task<UtilityBill> AddBillAsync(UtilityBill bill);
    Task DeleteBillAsync(int id);
    /// <summary>Pays a bill: Dr connection's expense account, Cr cash/bank; posts and links the voucher.</summary>
    Task<Voucher> PayBillAsync(int billId, int payFromAccountId, DateOnly? paidDate = null);
    Task<List<ExpenseBreakdownDto>> SummaryByTypeAsync(UtilityBillFilter filter);
    Task<List<ExpenseBreakdownDto>> SummaryByLocationAsync(UtilityBillFilter filter);
    /// <summary>
    /// Creates this month's bill for every active connection that has bill history
    /// but no bill for the month yet (amount copied from its latest bill).
    /// Returns the number of bills created.
    /// </summary>
    Task<int> GenerateMonthlyBillsAsync(DateOnly month);
}

public interface IReportService
{
    Task<List<LedgerRowDto>> GeneralLedgerAsync(ReportFilter filter);
    Task<List<TrialBalanceRowDto>> TrialBalanceAsync(DateOnly? asOf);
    Task<List<TrialBalanceRowDto>> IncomeStatementAsync(DateOnly from, DateOnly to);
    Task<List<TrialBalanceRowDto>> BalanceSheetAsync(DateOnly asOf);
    Task<List<LedgerRowDto>> CashBookAsync(ReportFilter filter);
    Task<List<CashFlowPointDto>> CashFlowAsync(DateOnly from, DateOnly to);
    Task<List<ExpenseBreakdownDto>> ExpenseBreakdownAsync(DateOnly from, DateOnly to);
    /// <summary>Total posted spend per project (lines tagged with a project) in the period.</summary>
    Task<List<ExpenseBreakdownDto>> ProjectBreakdownAsync(DateOnly from, DateOnly to);
    Task<DailySummaryDto> DailySummaryAsync(string? forUserId = null);
}

public interface IExportService
{
    byte[] TableToPdf(string title, string subtitle, string[] headers, IEnumerable<string[]> rows,
        ErpPlatform.Shared.Kernel.CompanyBranding? company = null);
    byte[] TableToExcel(string sheetName, string[] headers, IEnumerable<object?[]> rows);
    /// <summary>Renders a single record as a printable, signable document (voucher, request, payslip, ...).</summary>
    byte[] DocumentToPdf(PdfDocument document);
    /// <summary>Renders many documents into one file, each starting on a fresh page (e.g. a run's payslips).</summary>
    byte[] DocumentsToPdf(IEnumerable<PdfDocument> documents);
}

public interface IAppEmailSender
{
    bool Enabled { get; }
    Task SendAsync(string toEmail, string subject, string body);
}

public interface INotificationService
{
    Task NotifyAsync(string userId, string title, string? message, NotificationType type, string? link = null);
    Task NotifyRoleAsync(string roleName, string title, string? message, NotificationType type, string? link = null);
    Task<List<Notification>> GetUnreadAsync(string userId, int max = 20);
    Task MarkReadAsync(int id);
    Task MarkAllReadAsync(string userId);
}
