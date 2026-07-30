namespace Inventory.Domain;

/// <summary>Who stock is bought from.</summary>
public class Supplier : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    /// <summary>Agreed credit period, for a payables ageing view later.</summary>
    public int? PaymentTermDays { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PurchaseOrderStatus
{
    Draft = 0,
    /// <summary>Sent to the supplier. Committed, but nothing has arrived.</summary>
    Ordered = 1,
    /// <summary>Some of it has come in; the rest is still owed.</summary>
    PartlyReceived = 2,
    Received = 3,
    Cancelled = 4
}

/// <summary>
/// An order placed on a supplier.
/// </summary>
/// <remarks>
/// An order is a commitment, not a movement: nothing reaches stock until a goods
/// received note books it in. Keeping the two apart is what makes "ordered but not
/// yet arrived" answerable, and stops a paper order inflating what is on the shelf.
/// </remarks>
public class PurchaseOrder : AuditableEntity
{
    public string OrderNumber { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateOnly? ExpectedOn { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>Where the goods are expected to land.</summary>
    public int? WarehouseId { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal OtherCharges { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public string RaisedById { get; set; } = string.Empty;
    public string RaisedByName { get; set; } = string.Empty;

    public List<PurchaseOrderLine> Lines { get; set; } = [];

    /// <summary>True once every line has had its full quantity booked in.</summary>
    public bool IsFullyReceived => Lines.Count > 0 && Lines.All(l => l.Outstanding <= 0);
}

public class PurchaseOrderLine : BaseEntity
{
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    /// <summary>Snapshot, so an old order still reads correctly after a rename.</summary>
    public string ItemName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>Booked in so far across every receipt against this order.</summary>
    public decimal ReceivedQuantity { get; set; }
    public string? Note { get; set; }

    /// <summary>Still owed by the supplier.</summary>
    public decimal Outstanding => Quantity - ReceivedQuantity;
}

public enum GoodsReceiptStatus
{
    Draft = 0,
    /// <summary>Booked into stock. Terminal — reversing means a separate adjustment.</summary>
    Posted = 1,
    Cancelled = 2
}

/// <summary>
/// Goods physically arriving — the thing that actually moves stock.
/// </summary>
/// <remarks>
/// A receipt can stand alone (goods bought without paperwork) or sit against a
/// purchase order, in which case posting it also draws down what that order is still
/// owed. Posting routes every line through the ordinary stock ledger, so a receipt
/// is as traceable as any other movement and serials and batches come with it.
/// </remarks>
public class GoodsReceipt : AuditableEntity
{
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public GoodsReceiptStatus Status { get; set; } = GoodsReceiptStatus.Draft;

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>Null for a receipt with no order behind it.</summary>
    public int? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    public int? WarehouseId { get; set; }

    /// <summary>The supplier's own invoice or delivery-note number.</summary>
    public string? SupplierDocumentNumber { get; set; }
    public string? Notes { get; set; }

    public decimal TotalCost { get; set; }

    public string ReceivedById { get; set; } = string.Empty;
    public string ReceivedByName { get; set; } = string.Empty;
    public DateTime? PostedAtUtc { get; set; }

    public List<GoodsReceiptLine> Lines { get; set; } = [];
}

public class GoodsReceiptLine : BaseEntity
{
    public int GoodsReceiptId { get; set; }
    public GoodsReceipt GoodsReceipt { get; set; } = null!;

    /// <summary>Which order line this satisfies, when there is an order.</summary>
    public int? PurchaseOrderLineId { get; set; }

    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>Comma-separated, and required when the item is serialised.</summary>
    public string? SerialNumbers { get; set; }
    public string? BatchNumber { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string? Note { get; set; }

    public IReadOnlyList<string> Serials() =>
        SerialNumbers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];
}
