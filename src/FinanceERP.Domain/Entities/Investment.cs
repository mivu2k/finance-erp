using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class Investment : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? InvestmentType { get; set; }
    public decimal Amount { get; set; }
    public decimal ExpectedRoiPercent { get; set; }
    public DateOnly StartDate { get; set; }
    public InvestmentStatus Status { get; set; } = InvestmentStatus.Active;
    public decimal TotalProfit { get; set; }
    public decimal TotalWithdrawn { get; set; }
    public string? Notes { get; set; }
    public List<InvestmentTransaction> Transactions { get; set; } = [];
}

public class InvestmentTransaction : BaseEntity
{
    public int InvestmentId { get; set; }
    public Investment Investment { get; set; } = null!;
    public InvestmentTxnType Type { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
    public int? VoucherId { get; set; }
}
