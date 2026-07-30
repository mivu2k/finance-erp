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
public class ProductModel : AuditableEntity
{
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? ModelNumber { get; set; }
    public string? Sku { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal ReorderThreshold { get; set; }
    public decimal CurrentQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    public List<Accessory> Accessories { get; set; } = [];
}

/// <summary>
/// An accessory that goes with a model (charger, bag, case, ...). Also a real
/// stock-keeping unit with its own quantity, tracked independently of the model.
/// </summary>
public class Accessory : AuditableEntity
{
    public int ProductModelId { get; set; }
    public ProductModel ProductModel { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal ReorderThreshold { get; set; }
    public decimal CurrentQuantity { get; set; }
    public bool IsActive { get; set; } = true;
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

    public string PerformedById { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
}
