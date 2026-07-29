namespace Repair.Domain;

/// <summary>
/// A person or organisation whose equipment we repair. Ported from the Laravel
/// app's customers table.
/// </summary>
public class Customer : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Organization { get; set; }
    public string? Address { get; set; }
    public CommunicationPreference CommunicationPreference { get; set; } = CommunicationPreference.Phone;
    public string? Notes { get; set; }

    public List<Intake> Intakes { get; set; } = [];
}

/// <summary>
/// One drop-off at the counter. A customer can hand over several devices at once;
/// each becomes its own <see cref="RepairJob"/> under the same intake.
/// </summary>
public class Intake : AuditableEntity
{
    public string IntakeNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string ReceivedById { get; set; } = string.Empty;
    public string ReceivedByName { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public string? Notes { get; set; }

    public List<RepairJob> Jobs { get; set; } = [];
}

/// <summary>One device, tracked from the counter through diagnosis, repair and delivery.</summary>
public class RepairJob : AuditableEntity
{
    public string JobNumber { get; set; } = string.Empty;
    public int IntakeId { get; set; }
    public Intake Intake { get; set; } = null!;
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Identity user id of the technician. Null until assigned.</summary>
    public string? AssignedTechnicianId { get; set; }
    public string? AssignedTechnicianName { get; set; }

    public string DeviceName { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public DeviceCondition ConditionOnArrival { get; set; } = DeviceCondition.Good;
    public string IssueDescription { get; set; } = string.Empty;
    public JobPriority Priority { get; set; } = JobPriority.Normal;
    public DateOnly? ExpectedDeliveryDate { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Received;
    public DateTime StatusUpdatedAtUtc { get; set; }

    // --- handover, captured when the device goes back to the customer ---
    public DateTime? DeliveredAtUtc { get; set; }
    /// <summary>Who physically collected it — often not the customer themselves.</summary>
    public string? DeliveredToName { get; set; }
    public string? DeliveredToPhone { get; set; }
    public string? DeliveredToCnic { get; set; }
    public string? DeliveredByName { get; set; }
    public string? DeliveryNote { get; set; }

    public List<JobStatusHistory> StatusHistory { get; set; } = [];
    public List<JobSymptom> Symptoms { get; set; } = [];
    public List<JobAccessory> Accessories { get; set; } = [];
    public List<Diagnosis> Diagnoses { get; set; } = [];
    public List<JobPhoto> Photos { get; set; } = [];
    public List<JobWorkItem> WorkItems { get; set; } = [];
}

/// <summary>
/// What was actually fitted or done on one device — a part replaced, an hour of
/// bench work. This is the priced record the workshop keeps per job, and it is what
/// a quotation is built from, whether that quotation covers the single job or every
/// job on a collective intake.
/// </summary>
/// <remarks>
/// Parts and labour share one entity because they only differ by
/// <see cref="Kind"/> and both become a quotation line unchanged. Non-billable
/// items (goodwill, warranty rework) stay on the job card but never reach a price.
/// </remarks>
public class JobWorkItem : AuditableEntity
{
    public int RepairJobId { get; set; }
    public RepairJob RepairJob { get; set; } = null!;

    public JobWorkItemKind Kind { get; set; } = JobWorkItemKind.Part;
    /// <summary>Set when the line came off the parts catalog; free-text lines leave it null.</summary>
    public int? PartId { get; set; }
    public Part? Part { get; set; }

    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public bool Billable { get; set; } = true;
    public string? Notes { get; set; }
}

public class JobStatusHistory : BaseEntity
{
    public int RepairJobId { get; set; }
    public RepairJob RepairJob { get; set; } = null!;
    public string ChangedById { get; set; } = string.Empty;
    public string ChangedByName { get; set; } = string.Empty;
    public JobStatus FromStatus { get; set; }
    public JobStatus ToStatus { get; set; }
    public string? Note { get; set; }
}

/// <summary>Catalog of reported faults, ticked off against a job.</summary>
public class Symptom : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
}

public class JobSymptom : BaseEntity
{
    public int RepairJobId { get; set; }
    public RepairJob RepairJob { get; set; } = null!;
    public int SymptomId { get; set; }
    public Symptom Symptom { get; set; } = null!;
}

/// <summary>Catalog of things that can arrive with a device — cables, cases, batteries.</summary>
public class Accessory : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
}

public class JobAccessory : BaseEntity
{
    public int RepairJobId { get; set; }
    public RepairJob RepairJob { get; set; } = null!;
    public int AccessoryId { get; set; }
    public Accessory Accessory { get; set; } = null!;
    public string? Note { get; set; }
}

/// <summary>
/// A technician's findings on a job. The Laravel schema started one-per-job and
/// was later relaxed to many, so a job can accumulate findings as work proceeds.
/// </summary>
public class Diagnosis : AuditableEntity
{
    public int RepairJobId { get; set; }
    public RepairJob RepairJob { get; set; } = null!;
    public string TechnicianId { get; set; } = string.Empty;
    public string TechnicianName { get; set; } = string.Empty;
    public string Findings { get; set; } = string.Empty;
    public string? RequiredParts { get; set; }
    public string? RequiredLabor { get; set; }
    public int? EstimatedRepairTimeDays { get; set; }
    public decimal? EstimatedHours { get; set; }
    public string? WorkPerformed { get; set; }
    /// <summary>Optional link to the stocked part this diagnosis calls for.</summary>
    public int? PartId { get; set; }
    public Part? Part { get; set; }
    public string? InternalNotes { get; set; }
}

public class JobPhoto : AuditableEntity
{
    public int RepairJobId { get; set; }
    public RepairJob RepairJob { get; set; } = null!;
    public string UploadedById { get; set; } = string.Empty;
    public PhotoType Type { get; set; } = PhotoType.Before;
    public string Path { get; set; } = string.Empty;
    public string? Caption { get; set; }
}

/// <summary>
/// A spare part in the catalog. Deliberately carries no stock quantity — the
/// workshop buys against a job rather than holding a counted store, so what
/// matters is what the part costs and what it sells for, not how many are on a
/// shelf. Cost comes from the purchase history.
/// </summary>
public class Part : AuditableEntity
{
    public string? Sku { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Model { get; set; }

    /// <summary>What it is quoted at. Seeded from cost plus margin, then editable.</summary>
    public decimal Price { get; set; }

    // --- derived from PartPurchase, refreshed on every purchase ---

    /// <summary>Unit cost on the most recent purchase.</summary>
    public decimal? LastPurchaseCost { get; set; }
    public DateOnly? LastPurchasedOn { get; set; }
    public int? LastSupplierId { get; set; }
    public Supplier? LastSupplier { get; set; }
    /// <summary>Quantity-weighted mean cost across every purchase.</summary>
    public decimal? AverageCost { get; set; }
    /// <summary>Total quantity ever bought — the denominator behind the average.</summary>
    public decimal PurchasedQuantity { get; set; }

    /// <summary>Margin on the last purchase, as a percentage of cost.</summary>
    public decimal? MarginPercent => LastPurchaseCost is > 0
        ? Math.Round((Price - LastPurchaseCost.Value) / LastPurchaseCost.Value * 100m, 2)
        : null;
}

/// <summary>Where parts are bought from.</summary>
public class Supplier : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// A purchase of parts from a supplier. This is the price-tracking mechanism:
/// every receipt updates the part's last and average cost, so quotations can be
/// priced against what the part actually costs today.
/// </summary>
public class PartPurchase : AuditableEntity
{
    public string PurchaseNumber { get; set; } = string.Empty;
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>The supplier's own invoice or bill number.</summary>
    public string? SupplierInvoiceNumber { get; set; }
    public DateOnly PurchasedOn { get; set; }

    public string ReceivedById { get; set; } = string.Empty;
    public string ReceivedByName { get; set; } = string.Empty;

    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal OtherCharges { get; set; }
    public decimal TotalAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public string? Notes { get; set; }

    public List<PartPurchaseItem> Items { get; set; } = [];
}

public class PartPurchaseItem : BaseEntity
{
    public int PartPurchaseId { get; set; }
    public PartPurchase PartPurchase { get; set; } = null!;
    public int PartId { get; set; }
    public Part Part { get; set; } = null!;

    public decimal Quantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Optionally re-price the part's selling price from this receipt.</summary>
    public decimal? NewSellingPrice { get; set; }
    public string? Remarks { get; set; }
}

/// <summary>
/// A priced estimate. Attaches to a job or, for work not yet split into jobs, to
/// the intake. Carries two independent approvals — the customer's and a manager's.
/// </summary>
public class Quotation : AuditableEntity
{
    public string QuotationNumber { get; set; } = string.Empty;
    public int? RepairJobId { get; set; }
    public RepairJob? RepairJob { get; set; }
    public int? IntakeId { get; set; }
    public Intake? Intake { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public string? Subject { get; set; }
    public string? Reference { get; set; }
    public DateOnly Date { get; set; }
    public string? Currency { get; set; }
    public string? Project { get; set; }

    public string PreparedById { get; set; } = string.Empty;
    public string PreparedByName { get; set; } = string.Empty;

    public string? LaborDescription { get; set; }
    public decimal LaborAmount { get; set; }
    public decimal PartsAmount { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public ApprovalState CustomerApproval { get; set; } = ApprovalState.Pending;
    public DateTime? CustomerApprovedAtUtc { get; set; }
    public ApprovalState ManagerApproval { get; set; } = ApprovalState.Pending;
    public string? ManagerId { get; set; }
    public DateTime? ManagerApprovedAtUtc { get; set; }

    public QuotationStatus Status { get; set; } = QuotationStatus.Draft;
    public DateOnly? ValidUntil { get; set; }
    public string? Notes { get; set; }

    public List<QuotationItem> Items { get; set; } = [];
}

public class QuotationItem : BaseEntity
{
    public int QuotationId { get; set; }
    public Quotation Quotation { get; set; } = null!;
    public int? RepairJobId { get; set; }
    public int? PartId { get; set; }
    public Part? Part { get; set; }
    public QuotationItemType ItemType { get; set; } = QuotationItemType.Misc;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal LineTotal { get; set; }
}

/// <summary>An approved quotation turned into a billable order.</summary>
public class SalesOrder : AuditableEntity
{
    public string OrderNumber { get; set; } = string.Empty;
    public int QuotationId { get; set; }
    public Quotation Quotation { get; set; } = null!;
    public int? RepairJobId { get; set; }
    public int? IntakeId { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string FinalizedById { get; set; } = string.Empty;
    public string FinalizedByName { get; set; } = string.Empty;

    // Amounts are snapshotted off the quotation so a later edit can't move a bill.
    public decimal LaborAmount { get; set; }
    public decimal PartsAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;
    public decimal AmountPaid { get; set; }
    public string? Notes { get; set; }

    public List<Payment> Payments { get; set; } = [];

    public decimal Balance => TotalAmount - AmountPaid;
}

public class Payment : AuditableEntity
{
    public int SalesOrderId { get; set; }
    public SalesOrder SalesOrder { get; set; } = null!;
    public string RecordedById { get; set; } = string.Empty;
    public string RecordedByName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;
    public string? ReferenceNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Dropdown catalogs, kept editable rather than hardcoded.</summary>
public class Brand : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
}

public class DeviceType : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
}

public enum CommunicationPreference { Phone = 0, Email = 1, WhatsApp = 2 }
public enum PaymentMethod { Cash = 0, Credit = 1, BankTransfer = 2, Warranty = 3, Card = 4 }
public enum DeviceCondition { Good = 0, Fair = 1, Damaged = 2, Broken = 3 }
public enum JobPriority { Normal = 0, Urgent = 1 }

/// <summary>
/// The repair pipeline. Received → Diagnosing → WaitingApproval → InProgress →
/// Completed → Delivered, with Cancelled available until the job is delivered.
/// </summary>
public enum JobStatus
{
    Received = 0,
    Diagnosing = 1,
    WaitingApproval = 2,
    InProgress = 3,
    Completed = 4,
    Delivered = 5,
    Cancelled = 6
}

public enum PhotoType { Before = 0, After = 1, Damage = 2 }
public enum ApprovalState { Pending = 0, Approved = 1, Rejected = 2 }
public enum QuotationStatus { Draft = 0, Sent = 1, Pending = 2, Approved = 3, Rejected = 4, Expired = 5 }
public enum QuotationItemType { Misc = 0, Part = 1, Labor = 2, Service = 3 }

/// <summary>
/// Deliberately mirrors <see cref="QuotationItemType"/> value for value — a job
/// work item becomes a quotation line without a lookup table.
/// </summary>
public enum JobWorkItemKind { Misc = 0, Part = 1, Labor = 2, Service = 3 }
public enum PaymentStatus { Unpaid = 0, Partial = 1, Paid = 2 }
