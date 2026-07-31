namespace Inventory.Domain;

/// <summary>Who stock is sold to.</summary>
public class Customer : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    /// <summary>Agreed credit period, for a receivables ageing view later.</summary>
    public int? PaymentTermDays { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum SalesOrderStatus
{
    Draft = 0,
    /// <summary>Accepted from the customer. Committed, but nothing has left the building.</summary>
    Confirmed = 1,
    /// <summary>Some of it has gone out; the rest is still owed.</summary>
    PartlyDelivered = 2,
    Delivered = 3,
    Cancelled = 4
}

/// <summary>
/// An order taken from a customer.
/// </summary>
/// <remarks>
/// The mirror image of <see cref="PurchaseOrder"/>, and for the same reason: an order
/// is a commitment, not a movement. Nothing leaves stock until a delivery note issues
/// it, which is what makes "sold but not yet shipped" answerable and stops a paper
/// order deflating what is on the shelf.
/// <para>
/// Confirming an order deliberately <em>reserves nothing</em>. A soft reservation that
/// the stock figure doesn't honour is worse than none at all, because two orders can
/// still be promised the same unit while the shelf quantity says everything is fine.
/// </para>
/// </remarks>
public class SalesOrder : AuditableEntity
{
    public string OrderNumber { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateOnly? RequiredBy { get; set; }
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Where the goods are expected to ship from.</summary>
    public int? WarehouseId { get; set; }

    /// <summary>The customer's own purchase-order number.</summary>
    public string? CustomerReference { get; set; }
    public string? Notes { get; set; }

    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal OtherCharges { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public string RaisedById { get; set; } = string.Empty;
    public string RaisedByName { get; set; } = string.Empty;

    public List<SalesOrderLine> Lines { get; set; } = [];

    /// <summary>True once every line has had its full quantity shipped.</summary>
    public bool IsFullyDelivered => Lines.Count > 0 && Lines.All(l => l.Outstanding <= 0);
}

public class SalesOrderLine : BaseEntity
{
    public int SalesOrderId { get; set; }
    public SalesOrder SalesOrder { get; set; } = null!;

    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    /// <summary>Snapshot, so an old order still reads correctly after a rename.</summary>
    public string ItemName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>Shipped so far across every delivery against this order.</summary>
    public decimal DeliveredQuantity { get; set; }
    public string? Note { get; set; }

    /// <summary>Still owed to the customer.</summary>
    public decimal Outstanding => Quantity - DeliveredQuantity;
}

public enum DeliveryStatus
{
    Draft = 0,
    /// <summary>Issued out of stock. Terminal — reversing means a separate return or adjustment.</summary>
    Posted = 1,
    Cancelled = 2
}

/// <summary>
/// Goods physically leaving — the thing that actually moves stock.
/// </summary>
/// <remarks>
/// A delivery can stand alone (a counter sale) or sit against a sales order, in which
/// case posting it also draws down what that order still owes. Posting routes every
/// line through the ordinary stock ledger with <see cref="StockReason.Sale"/>, so a
/// delivery is as traceable as any other movement and serialised units are issued by
/// name rather than just decremented.
/// </remarks>
public class Delivery : AuditableEntity, IConcurrencyChecked
{
    /// <summary>Optimistic lock: posting the same note twice would issue the stock twice.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public string DeliveryNumber { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Draft;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Null for a counter sale with no order behind it.</summary>
    public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }

    public int? WarehouseId { get; set; }

    /// <summary>Who physically took the goods — the delivery note is signed against this.</summary>
    public string? ReceivedByName { get; set; }
    public string? VehicleNumber { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? Notes { get; set; }

    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Cost of what went out, captured from the weighted-average cost at the moment of
    /// posting. Snapshotted because the average moves with the next purchase, and a
    /// margin that silently rewrites itself afterwards is worthless.
    /// </summary>
    public decimal TotalCost { get; set; }

    public string DeliveredById { get; set; } = string.Empty;
    public string DeliveredByName { get; set; } = string.Empty;
    public DateTime? PostedAtUtc { get; set; }

    public List<DeliveryLine> Lines { get; set; } = [];

    public decimal GrossProfit => TotalAmount - TotalCost;

    public decimal MarginPercent =>
        TotalAmount == 0 ? 0 : Math.Round(GrossProfit / TotalAmount * 100, 2);
}

public class DeliveryLine : BaseEntity
{
    public int DeliveryId { get; set; }
    public Delivery Delivery { get; set; } = null!;

    /// <summary>Which order line this satisfies, when there is an order.</summary>
    public int? SalesOrderLineId { get; set; }

    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>Weighted-average cost at posting time. Zero until the delivery is posted.</summary>
    public decimal UnitCost { get; set; }
    public decimal LineCost { get; set; }

    /// <summary>Comma-separated, and required when the item is serialised.</summary>
    public string? SerialNumbers { get; set; }
    public string? Note { get; set; }

    public IReadOnlyList<string> Serials() =>
        SerialNumbers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];
}
