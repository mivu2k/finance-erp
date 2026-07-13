using FinanceERP.Domain.Common;

namespace FinanceERP.Domain.Entities;

/// <summary>
/// A petty-cash float assigned by a Director/Admin to an accountant.
/// The transfer itself is recorded as a journal voucher (Petty Cash Dr / Cash Cr).
/// </summary>
public class PettyCashAssignment : AuditableEntity
{
    public string AccountantId { get; set; } = string.Empty;
    public string AccountantName { get; set; } = string.Empty;
    public int PettyCashAccountId { get; set; }
    public Account PettyCashAccount { get; set; } = null!;
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public string? Notes { get; set; }
    public int? VoucherId { get; set; }
}
