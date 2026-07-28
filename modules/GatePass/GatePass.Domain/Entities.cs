namespace GatePass.Domain;

/// <summary>
/// A movement of goods through the gate, in or out. Ported from the Laravel
/// repair app's gate_passes table, with two changes: the items JSON blob became a
/// child table so items are queryable, and the pass now has a life beyond being
/// issued — security marks it completed at the gate, and outward passes for
/// returnable goods track the return.
/// </summary>
public class GatePassRecord : AuditableEntity
{
    public string PassNumber { get; set; } = string.Empty;
    public GatePassDirection Direction { get; set; }
    public GatePassStatus Status { get; set; } = GatePassStatus.Issued;

    // --- who and what ---
    /// <summary>Person carrying the goods through the gate.</summary>
    public string PersonName { get; set; } = string.Empty;
    public string? PersonPhone { get; set; }
    public string? PersonCnic { get; set; }
    public string? CompanyName { get; set; }
    public string? VehicleNumber { get; set; }
    public string? Department { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? Notes { get; set; }

    // --- soft link to a record in another app ---
    // Replaces Laravel's polymorphic reference. There is no foreign key: the
    // referenced record lives in another module's database, so only its type and
    // human-readable number are kept.
    public string? ReferenceType { get; set; }
    public string? ReferenceNumber { get; set; }

    // --- authorisation ---
    public string AuthorizedById { get; set; } = string.Empty;
    public string AuthorizedByName { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; }

    // --- the gate itself ---
    /// <summary>Set when security actually passes the goods through.</summary>
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedByName { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancellationReason { get; set; }

    // --- returnable outward movements ---
    public bool IsReturnable { get; set; }
    public DateOnly? ExpectedReturnOn { get; set; }
    public DateTime? ReturnedAtUtc { get; set; }
    public string? ReturnReceivedByName { get; set; }

    public List<GatePassItem> Items { get; set; } = [];

    /// <summary>Overdue once the expected return date has passed with nothing returned.</summary>
    public bool IsOverdue(DateOnly today) =>
        IsReturnable && ReturnedAtUtc is null && Status != GatePassStatus.Cancelled
        && ExpectedReturnOn is { } due && due < today;
}

public class GatePassItem : BaseEntity
{
    public int GatePassRecordId { get; set; }
    public GatePassRecord GatePassRecord { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public string? SerialNumber { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public string? Remarks { get; set; }
}

/// <summary>
/// Equipment lent to a customer or prospect to try. Ported from demo_issuances.
/// Distinct from a gate pass: a gate pass records a movement at a point in time,
/// a demo issuance is an open loan that stays outstanding until it comes back.
/// </summary>
public class DemoIssuance : AuditableEntity
{
    public string IssuanceNumber { get; set; } = string.Empty;
    public DemoStatus Status { get; set; } = DemoStatus.Issued;

    // --- who it went to ---
    // The customer master lives in the Repair app, so this keeps a snapshot plus
    // an optional soft reference rather than a cross-database foreign key.
    public string CustomerName { get; set; } = string.Empty;
    public string? CustomerPhone { get; set; }
    public string? CustomerReference { get; set; }
    public string? Department { get; set; }
    public string? ReferenceLetter { get; set; }

    public DateTime IssuedAtUtc { get; set; }
    public string IssuedById { get; set; } = string.Empty;
    public string IssuedByName { get; set; } = string.Empty;

    public DateOnly? ExpectedReturnOn { get; set; }
    public DateTime? ReturnedAtUtc { get; set; }
    public string? ReceivedByName { get; set; }
    public string? ReturnCondition { get; set; }

    public string? Notes { get; set; }

    public List<DemoIssuanceItem> Items { get; set; } = [];

    public bool IsOverdue(DateOnly today) =>
        Status == DemoStatus.Issued && ExpectedReturnOn is { } due && due < today;
}

public class DemoIssuanceItem : BaseEntity
{
    public int DemoIssuanceId { get; set; }
    public DemoIssuance DemoIssuance { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public string? SerialNumber { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? Accessories { get; set; }
    public string? Remarks { get; set; }
    /// <summary>Set when this particular item comes back, for partial returns.</summary>
    public DateTime? ReturnedAtUtc { get; set; }
}

public enum GatePassDirection { Inward = 0, Outward = 1 }
public enum GatePassStatus { Issued = 0, Completed = 1, Returned = 2, Cancelled = 3 }
public enum DemoStatus { Issued = 0, PartiallyReturned = 1, Returned = 2, Cancelled = 3 }
