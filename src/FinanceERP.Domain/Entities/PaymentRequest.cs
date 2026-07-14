using FinanceERP.Domain.Common;
using FinanceERP.Domain.Enums;

namespace FinanceERP.Domain.Entities;

public class PaymentRequest : AuditableEntity
{
    public string RequestNo { get; set; } = string.Empty;
    public string RequesterId { get; set; } = string.Empty;
    public string RequesterName { get; set; } = string.Empty;
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Draft;
    public string? Purpose { get; set; }
    public decimal TotalAmount { get; set; }
    /// <summary>True when raised by a Director (Director Fund Request) — posts against the director's capital/advance account.</summary>
    public bool IsDirectorRequest { get; set; }
    public int? VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public List<PaymentRequestLine> Lines { get; set; } = [];
    public List<RequestApproval> Approvals { get; set; } = [];
}

public class PaymentRequestLine : BaseEntity
{
    public int PaymentRequestId { get; set; }
    public PaymentRequest PaymentRequest { get; set; } = null!;
    /// <summary>
    /// Ledger account head. Employees only state what they need (category/amount/reason);
    /// the accountant classifies each line to an account before paying.
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    public string? Category { get; set; }
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public string? Description { get; set; }
    public int LineNo { get; set; }
}

public class RequestApproval : BaseEntity
{
    public int PaymentRequestId { get; set; }
    public PaymentRequest PaymentRequest { get; set; } = null!;
    public string Level { get; set; } = string.Empty; // Manager / Admin / Accountant
    public string ActorId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public ApprovalAction Action { get; set; }
    public string? Comment { get; set; }
}
