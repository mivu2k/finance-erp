namespace Inventory.Domain;

/// <summary>Where one physical serialised unit currently is.</summary>
public enum StockUnitStatus
{
    InStock = 0,
    /// <summary>Handed out but still ours — on loan, with a technician, on demo.</summary>
    Issued = 1,
    Sold = 2,
    /// <summary>Came back in; needs checking before it counts as stock again.</summary>
    Returned = 3,
    Damaged = 4,
    /// <summary>Written off. Never counts toward stock again.</summary>
    Scrapped = 5
}

/// <summary>
/// One physical unit of a serialised item, followed individually by its serial.
/// </summary>
/// <remarks>
/// Only exists for items flagged <c>IsSerialised</c>. Everything else is a plain
/// quantity, because forcing a row per unit onto consumables is how inventory
/// systems become unusable.
/// <para>
/// A unit's <see cref="Status"/> is what decides whether it counts as stock, and the
/// cached quantity on the item stays the sum of its in-stock units — so the two can
/// be reconciled, and the ledger still explains how it got there.
/// </para>
/// </remarks>
public class StockUnit : AuditableEntity
{
    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }

    /// <summary>Unique per item — two units of the same model can't share a serial.</summary>
    public string SerialNumber { get; set; } = string.Empty;
    public StockUnitStatus Status { get; set; } = StockUnitStatus.InStock;

    public int? StockBatchId { get; set; }
    public StockBatch? StockBatch { get; set; }

    /// <summary>What this particular unit cost, which may differ from the average.</summary>
    public decimal? UnitCost { get; set; }

    public DateOnly ReceivedOn { get; set; }
    public DateOnly? IssuedOn { get; set; }
    /// <summary>Who or what it went to — a job number, a customer, a person.</summary>
    public string? IssuedTo { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    /// <summary>Only these count toward stock on hand.</summary>
    public bool CountsAsStock => Status is StockUnitStatus.InStock or StockUnitStatus.Returned;
}

/// <summary>
/// A batch or lot received together, for goods with an expiry or a recall trail.
/// </summary>
public class StockBatch : AuditableEntity
{
    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }

    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly ReceivedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>What arrived in this batch.</summary>
    public decimal Quantity { get; set; }
    /// <summary>What is left of it — driven down as the batch is issued.</summary>
    public decimal RemainingQuantity { get; set; }
    public decimal? UnitCost { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public bool IsExpired(DateOnly today) => ExpiresOn is { } e && e < today;
}

public enum StockCountStatus
{
    /// <summary>Being counted; nothing has moved yet.</summary>
    Draft = 0,
    /// <summary>Counting finished, variances visible, still reversible.</summary>
    Counted = 1,
    /// <summary>Variances written to the ledger. Terminal.</summary>
    Posted = 2,
    Cancelled = 3
}

/// <summary>
/// A stock take: what the system thinks is there against what was actually found.
/// </summary>
/// <remarks>
/// Posting is the only thing that moves stock, and it does so through the ordinary
/// ledger rather than by writing quantities directly — so a correction after a count
/// is as traceable as any other movement, and the "everything is rebuildable from
/// the ledger" property still holds.
/// </remarks>
public class StockCount : AuditableEntity
{
    public string CountNumber { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public StockCountStatus Status { get; set; } = StockCountStatus.Draft;

    public string? Notes { get; set; }
    public string CountedById { get; set; } = string.Empty;
    public string CountedByName { get; set; } = string.Empty;
    public DateTime? PostedAtUtc { get; set; }

    public List<StockCountLine> Lines { get; set; } = [];

    /// <summary>Lines where the count disagreed with the system.</summary>
    public int VarianceCount => Lines.Count(l => l.Variance != 0);
}

public class StockCountLine : BaseEntity
{
    public int StockCountId { get; set; }
    public StockCount StockCount { get; set; } = null!;

    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    /// <summary>Name as it was when counted, so an old sheet still reads correctly.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>What the system held when the sheet was drawn up.</summary>
    public decimal SystemQuantity { get; set; }
    /// <summary>What was actually found. Null until someone counts it.</summary>
    public decimal? CountedQuantity { get; set; }
    public string? Note { get; set; }

    /// <summary>Positive means more was found than expected. Zero until counted.</summary>
    public decimal Variance => CountedQuantity is { } c ? c - SystemQuantity : 0;
}
