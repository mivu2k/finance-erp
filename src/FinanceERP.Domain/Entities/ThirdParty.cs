using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class ThirdParty : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public ThirdPartyType Type { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Dedicated ledger account auto-created under Receivables/Payables.</summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
}
