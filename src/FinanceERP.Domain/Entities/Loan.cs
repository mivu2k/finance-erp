using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class Loan : AuditableEntity
{
    public string LoanNo { get; set; } = string.Empty;
    public LoanDirection Direction { get; set; }
    public int ThirdPartyId { get; set; }
    public ThirdParty ThirdParty { get; set; } = null!;
    public decimal Principal { get; set; }
    public decimal InterestRatePercent { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Active;
    public decimal RepaidAmount { get; set; }
    public decimal RemainingBalance => Principal - RepaidAmount;
    public string? Notes { get; set; }
    public int? DisbursementVoucherId { get; set; }
    public List<LoanInstallment> Installments { get; set; } = [];
}

public class LoanInstallment : BaseEntity
{
    public int LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public int Number { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal InterestPortion { get; set; }
    public decimal PaidAmount { get; set; }
    public DateOnly? PaidDate { get; set; }
    public InstallmentStatus Status { get; set; } = InstallmentStatus.Pending;
    public int? PaymentVoucherId { get; set; }
}
