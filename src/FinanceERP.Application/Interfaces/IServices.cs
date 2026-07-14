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
    /// <summary>Creates and posts a system-generated voucher from a source module.</summary>
    Task<Voucher> PostSystemVoucherAsync(VoucherType type, DateOnly date, string narration,
        string source, int? sourceId, IEnumerable<(int AccountId, decimal Debit, decimal Credit, string? Description)> lines);
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

    // Advance-kind lifecycle: disburse → justify → approve justification → settle.
    Task<Voucher> DisburseAsync(int id, int payFromAccountId, string? comment);
    Task SubmitJustificationAsync(int id, List<PaymentRequestLine> lines);
    Task ApproveJustificationAsync(int id, string? comment);
    Task RejectJustificationAsync(int id, string? comment);
    /// <summary>Posts actuals, clears the advance, and settles the cash difference.</summary>
    Task<Voucher> SettleAsync(int id, int cashAccountId, string? comment, IReadOnlyDictionary<int, int> lineAccounts);
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

public interface IThirdPartyService
{
    Task<PagedResult<ThirdParty>> ListAsync(ReportFilter filter);
    Task<ThirdParty> SaveAsync(ThirdParty thirdParty);
    Task DeleteAsync(int id);
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
    Task<DailySummaryDto> DailySummaryAsync(string? forUserId = null);
}

public interface IExportService
{
    byte[] TableToPdf(string title, string subtitle, string[] headers, IEnumerable<string[]> rows);
    byte[] TableToExcel(string sheetName, string[] headers, IEnumerable<object?[]> rows);
}

public interface INotificationService
{
    Task NotifyAsync(string userId, string title, string? message, NotificationType type, string? link = null);
    Task NotifyRoleAsync(string roleName, string title, string? message, NotificationType type, string? link = null);
    Task<List<Notification>> GetUnreadAsync(string userId, int max = 20);
    Task MarkReadAsync(int id);
    Task MarkAllReadAsync(string userId);
}
