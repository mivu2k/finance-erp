namespace Inventory.Domain;

/// <summary>
/// A general item family, e.g. "Laptop" or "Printer". Not itself a stock-keeping
/// unit — the models under it are what quantity is tracked against.
/// </summary>
public class Product : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? SkuPrefix { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public List<ProductModel> Models { get; set; } = [];
}

/// <summary>
/// A specific model/variant under a product (e.g. "Laptop — ModelX1"). This is a
/// real stock-keeping unit: <see cref="CurrentQuantity"/> is a cache maintained by
/// <c>IStockService</c>, always rebuildable from <see cref="StockTransaction"/> rows.
/// </summary>
public class ProductModel : AuditableEntity, IConcurrencyChecked
{
    /// <summary>Optimistic lock: two clerks must not both decrement the same quantity.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? ModelNumber { get; set; }
    public string? Sku { get; set; }
    public string Unit { get; set; } = "pcs";
    /// <summary>Manufacturer/supplier part number, distinct from our own SKU.</summary>
    public string? Barcode { get; set; }
    public decimal CurrentQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    // --- stock control ---

    /// <summary>
    /// Track every unit individually with its own serial. Set per item rather than
    /// globally: a laptop is worth following one-by-one, a box of screws is not, and
    /// forcing either rule on everything makes the other unusable.
    /// </summary>
    public bool IsSerialised { get; set; }

    /// <summary>Group receipts into batches/lots, for goods with an expiry or a recall trail.</summary>
    public bool IsBatchTracked { get; set; }

    /// <summary>Reorder level — at or below this the item shows as low.</summary>
    public decimal ReorderThreshold { get; set; }
    /// <summary>How much to buy when reordering. Zero means nobody has decided yet.</summary>
    public decimal ReorderQuantity { get; set; }

    // --- money ---

    /// <summary>What it sells for. Visible only with the cost permission.</summary>
    public decimal SalePrice { get; set; }
    /// <summary>Unit cost on the most recent receipt.</summary>
    public decimal? LastPurchaseCost { get; set; }
    /// <summary>
    /// Quantity-weighted mean cost across every receipt — the figure stock is valued
    /// at. Weighted, so 100 at 50 then 1 at 500 doesn't drag the average to 275.
    /// </summary>
    public decimal? AverageCost { get; set; }
    /// <summary>Total quantity ever received; the denominator behind the average.</summary>
    public decimal PurchasedQuantity { get; set; }

    /// <summary>Stock on hand at what it cost, not what it sells for.</summary>
    public decimal StockValue => CurrentQuantity * (AverageCost ?? 0);

    public List<Accessory> Accessories { get; set; } = [];
}

/// <summary>
/// An accessory that goes with a model (charger, bag, case, ...). Also a real
/// stock-keeping unit with its own quantity, tracked independently of the model.
/// </summary>
public class Accessory : AuditableEntity, IConcurrencyChecked
{
    /// <summary>Optimistic lock: two clerks must not both decrement the same quantity.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public int ProductModelId { get; set; }
    public ProductModel ProductModel { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string Unit { get; set; } = "pcs";
    public string? Barcode { get; set; }
    public decimal CurrentQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    // --- stock control ---

    /// <summary>
    /// Track every unit individually with its own serial. Set per item rather than
    /// globally: a laptop is worth following one-by-one, a box of screws is not, and
    /// forcing either rule on everything makes the other unusable.
    /// </summary>
    public bool IsSerialised { get; set; }

    /// <summary>Group receipts into batches/lots, for goods with an expiry or a recall trail.</summary>
    public bool IsBatchTracked { get; set; }

    /// <summary>Reorder level — at or below this the item shows as low.</summary>
    public decimal ReorderThreshold { get; set; }
    /// <summary>How much to buy when reordering. Zero means nobody has decided yet.</summary>
    public decimal ReorderQuantity { get; set; }

    // --- money ---

    /// <summary>What it sells for. Visible only with the cost permission.</summary>
    public decimal SalePrice { get; set; }
    /// <summary>Unit cost on the most recent receipt.</summary>
    public decimal? LastPurchaseCost { get; set; }
    /// <summary>
    /// Quantity-weighted mean cost across every receipt — the figure stock is valued
    /// at. Weighted, so 100 at 50 then 1 at 500 doesn't drag the average to 275.
    /// </summary>
    public decimal? AverageCost { get; set; }
    /// <summary>Total quantity ever received; the denominator behind the average.</summary>
    public decimal PurchasedQuantity { get; set; }

    /// <summary>Stock on hand at what it cost, not what it sells for.</summary>
    public decimal StockValue => CurrentQuantity * (AverageCost ?? 0);
}

public enum StockItemType { Model = 0, Accessory = 1 }
public enum StockDirection { In = 0, Out = 1 }

public enum StockReason { Purchase = 0, Sale = 1, Adjustment = 2, Return = 3, Damaged = 4, Other = 5 }

/// <summary>
/// The stock ledger. Every +/- against a model or accessory is one row here —
/// never edited afterwards — with <see cref="BalanceAfter"/> as the running
/// quantity at that point, mirroring how Finance treats its voucher ledger.
/// </summary>
public class StockTransaction : BaseEntity
{
    public StockItemType ItemType { get; set; }
    public int ItemId { get; set; }
    public StockDirection Direction { get; set; }
    public decimal Quantity { get; set; }
    public StockReason Reason { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public decimal BalanceAfter { get; set; }

    /// <summary>
    /// Where the movement happened. Nullable because rows written before warehouses
    /// existed have no location to claim, and inventing one would be a lie.
    /// </summary>
    public int? WarehouseId { get; set; }

    public string PerformedById { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
}
