using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class EmployeeAdvance : AuditableEntity
{
    public string AdvanceNo { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public AdvanceStatus Status { get; set; } = AdvanceStatus.Draft;
    public DateOnly? DueDate { get; set; }
    public int InstallmentCount { get; set; } = 1;
    public decimal MonthlyDeduction { get; set; }
    public decimal RepaidAmount { get; set; }
    public decimal OutstandingBalance => Amount - RepaidAmount;
    public int? DisbursementVoucherId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public List<AdvanceInstallment> Installments { get; set; } = [];
}

public class AdvanceInstallment : BaseEntity
{
    public int EmployeeAdvanceId { get; set; }
    public EmployeeAdvance EmployeeAdvance { get; set; } = null!;
    public int Number { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public DateOnly? PaidDate { get; set; }
    public InstallmentStatus Status { get; set; } = InstallmentStatus.Pending;
    public int? RepaymentVoucherId { get; set; }
}
