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
    /// <summary>Identity user id — narrows to spend traceable to one person.</summary>
    public string? PersonId { get; set; }
    public FinanceERP.Domain.Enums.VoucherType? VoucherType { get; set; }
    public FinanceERP.Domain.Enums.VoucherStatus? VoucherStatus { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public record LedgerRowDto(
    DateOnly Date, string VoucherNo, int VoucherId, string AccountCode, string AccountName,
    string? Description, decimal Debit, decimal Credit, decimal RunningBalance,
    string? CostCenter, string? Department, string? Project, string? Person = null);

public record TrialBalanceRowDto(string Code, string Name, string Type, decimal Debit, decimal Credit);

public record AccountBalanceDto(int AccountId, string Code, string Name, string Type, decimal Balance);

public record DailySummaryDto(decimal TodayDebit, decimal TodayCredit, decimal CashInHand,
    decimal PettyCash, decimal BankBalance, int PendingRequests, int PendingApprovals,
    decimal OutstandingAdvances, decimal LoansReceivable, decimal LoansPayable, decimal Investments);

public record CashFlowPointDto(DateOnly Date, decimal Inflow, decimal Outflow);

public record ExpenseBreakdownDto(string Category, decimal Amount);

/// <summary>Per-employee inputs the accountant supplies when generating/recalculating a run.</summary>
public class PayslipInputDto
{
    public string EmployeeId { get; set; } = string.Empty;
    /// <summary>Unpaid absence days; pro-rates basic and allowances over WorkingDays.</summary>
    public decimal AbsentDays { get; set; }
    public int WorkingDays { get; set; } = 30;
    /// <summary>Recover due advance instalments from this month's salary.</summary>
    public bool DeductAdvances { get; set; } = true;
    /// <summary>Ad-hoc one-off deductions for this run only (label → amount).</summary>
    public List<ManualDeductionDto> ManualDeductions { get; set; } = [];
    public string? Notes { get; set; }
}

public record ManualDeductionDto(string Label, decimal Amount, int? AccountId = null);

