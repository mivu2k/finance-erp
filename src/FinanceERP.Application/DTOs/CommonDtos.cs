namespace FinanceERP.Application.DTOs;

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public class ReportFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? AccountId { get; set; }
    public int? DepartmentId { get; set; }
    public int? CostCenterId { get; set; }
    public int? ProjectId { get; set; }
    public int? ThirdPartyId { get; set; }
    public FinanceERP.Domain.Enums.VoucherType? VoucherType { get; set; }
    public FinanceERP.Domain.Enums.VoucherStatus? VoucherStatus { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public record LedgerRowDto(
    DateOnly Date, string VoucherNo, int VoucherId, string AccountCode, string AccountName,
    string? Description, decimal Debit, decimal Credit, decimal RunningBalance,
    string? CostCenter, string? Department, string? Project);

public record TrialBalanceRowDto(string Code, string Name, string Type, decimal Debit, decimal Credit);

public record AccountBalanceDto(int AccountId, string Code, string Name, string Type, decimal Balance);

public record DailySummaryDto(decimal TodayDebit, decimal TodayCredit, decimal CashInHand,
    decimal PettyCash, decimal BankBalance, int PendingRequests, int PendingApprovals,
    decimal OutstandingAdvances, decimal LoansReceivable, decimal LoansPayable, decimal Investments);

public record CashFlowPointDto(DateOnly Date, decimal Inflow, decimal Outflow);

public record ExpenseBreakdownDto(string Category, decimal Amount);
