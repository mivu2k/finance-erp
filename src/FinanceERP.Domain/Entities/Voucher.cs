using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class Voucher : AuditableEntity
{
    public string VoucherNo { get; set; } = string.Empty;
    public VoucherType Type { get; set; }
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;
    public DateOnly Date { get; set; }
    public string? Narration { get; set; }
    /// <summary>Module that generated this voucher (Manual, PaymentRequest, Advance, Loan, Investment, PettyCash).</summary>
    public string Source { get; set; } = "Manual";
    public int? SourceId { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public List<VoucherLine> Lines { get; set; } = [];
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
}

public class VoucherLine : BaseEntity
{
    public int VoucherId { get; set; }
    public Voucher Voucher { get; set; } = null!;
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string? Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public int? ThirdPartyId { get; set; }
    public ThirdParty? ThirdParty { get; set; }
    public string? AttachmentPath { get; set; }
    public int LineNo { get; set; }
}

/// <summary>Per-type, per-year voucher number sequence (e.g. CPV-2026-00001).</summary>
public class VoucherSequence
{
    public int Id { get; set; }
    public VoucherType Type { get; set; }
    public int Year { get; set; }
    public int NextNumber { get; set; } = 1;
}
