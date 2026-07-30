namespace Inventory.Domain;

/// <summary>A place stock physically sits — a store, a van, a counter, a workshop.</summary>
public class Warehouse : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Where stock lands when nobody names a location. Exactly one warehouse holds
    /// this, so a movement can always be attributed somewhere and older records that
    /// predate warehouses still have a home.
    /// </summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// How much of one item sits in one warehouse.
/// </summary>
/// <remarks>
/// The item's own <c>CurrentQuantity</c> stays the total across every location, so
/// existing reports, low-stock checks and valuation keep working untouched; this row
/// is the breakdown underneath it. Both are caches over the same ledger and both are
/// rebuildable from it.
/// </remarks>
public class StockBalance : BaseEntity
{
    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    public decimal Quantity { get; set; }
}

public enum StockTransferStatus
{
    /// <summary>Being written up. Nothing has moved.</summary>
    Draft = 0,
    /// <summary>Dispatched: stock has left the source but not arrived anywhere yet.</summary>
    InTransit = 1,
    Received = 2,
    Cancelled = 3
}

/// <summary>
/// Stock moving from one warehouse to another.
/// </summary>
/// <remarks>
/// Deliberately two steps rather than one. Between a van leaving one store and
/// arriving at another the goods are real but in neither place, and a single
/// instantaneous move would either show them in two locations at once or in none.
/// Dispatch takes them out of the source; receipt puts them into the destination;
/// in between they sit in <see cref="StockTransferStatus.InTransit"/>, which is
/// where a discrepancy becomes visible instead of silently vanishing.
/// </remarks>
public class StockTransfer : AuditableEntity
{
    public string TransferNumber { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public StockTransferStatus Status { get; set; } = StockTransferStatus.Draft;

    public int FromWarehouseId { get; set; }
    public Warehouse FromWarehouse { get; set; } = null!;
    public int ToWarehouseId { get; set; }
    public Warehouse ToWarehouse { get; set; } = null!;

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public string RaisedById { get; set; } = string.Empty;
    public string RaisedByName { get; set; } = string.Empty;

    public DateTime? DispatchedAtUtc { get; set; }
    public string? DispatchedByName { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public string? ReceivedByName { get; set; }

    public List<StockTransferLine> Lines { get; set; } = [];
}

public class StockTransferLine : BaseEntity
{
    public int StockTransferId { get; set; }
    public StockTransfer StockTransfer { get; set; } = null!;

    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    /// <summary>Snapshot, so an old transfer still reads correctly after a rename.</summary>
    public string ItemName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
    /// <summary>
    /// What actually turned up. Null until received; less than
    /// <see cref="Quantity"/> means something went missing on the way.
    /// </summary>
    public decimal? ReceivedQuantity { get; set; }

    /// <summary>Serials moved, for a serialised item. Comma-separated.</summary>
    public string? SerialNumbers { get; set; }
    public string? Note { get; set; }

    /// <summary>Negative once received short.</summary>
    public decimal Shortfall => ReceivedQuantity is { } r ? r - Quantity : 0;
}
