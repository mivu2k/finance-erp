using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

public interface ISupplierService
{
    Task<List<Supplier>> ListAsync(string? search = null, bool activeOnly = false,
        CancellationToken ct = default);
    Task<Supplier?> GetAsync(int id, CancellationToken ct = default);
    Task<Supplier> SaveAsync(Supplier supplier, CancellationToken ct = default);
    /// <summary>Soft-deletes a supplier. Refused once orders or receipts reference it.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class SupplierService(InventoryDbContext db) : ISupplierService
{
    public async Task<List<Supplier>> ListAsync(
        string? search = null, bool activeOnly = false, CancellationToken ct = default)
    {
        var q = db.Suppliers.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(s => s.Name.Contains(t)
                          || (s.Code != null && s.Code.Contains(t))
                          || (s.Phone != null && s.Phone.Contains(t)));
        }
        return await q.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public Task<Supplier?> GetAsync(int id, CancellationToken ct = default) =>
        db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Supplier> SaveAsync(Supplier supplier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
            throw new InvalidOperationException("Supplier name is required.");

        if (supplier.Id == 0)
        {
            db.Suppliers.Add(supplier);
        }
        else
        {
            var existing = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplier.Id, ct)
                ?? throw new InvalidOperationException("Supplier not found.");

            existing.Name = supplier.Name;
            existing.Code = supplier.Code;
            existing.ContactPerson = supplier.ContactPerson;
            existing.Phone = supplier.Phone;
            existing.Email = supplier.Email;
            existing.Address = supplier.Address;
            existing.TaxNumber = supplier.TaxNumber;
            existing.PaymentTermDays = supplier.PaymentTermDays;
            existing.Notes = supplier.Notes;
            existing.IsActive = supplier.IsActive;
            supplier = existing;
        }

        await db.SaveChangesAsync(ct);
        return supplier;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (supplier is null) return;

        // Purchase history has to keep naming who it was bought from.
        if (await db.PurchaseOrders.AnyAsync(o => o.SupplierId == id, ct)
            || await db.GoodsReceipts.AnyAsync(r => r.SupplierId == id, ct))
            throw new InvalidOperationException(
                $"{supplier.Name} has purchase history. Mark it inactive instead.");

        db.Suppliers.Remove(supplier);
        await db.SaveChangesAsync(ct);
    }
}

public interface IPurchaseOrderService
{
    Task<List<PurchaseOrder>> ListAsync(PurchaseOrderStatus? status = null, int? supplierId = null,
        CancellationToken ct = default);
    Task<PurchaseOrder?> GetAsync(int id, CancellationToken ct = default);
    Task<PurchaseOrder> SaveAsync(PurchaseOrder order, string userId, string userName,
        CancellationToken ct = default);
    /// <summary>Commits the order and sends it. Lines are fixed from here on.</summary>
    Task<PurchaseOrder> PlaceAsync(int id, CancellationToken ct = default);
    Task<PurchaseOrder> CancelAsync(int id, CancellationToken ct = default);

    /// <summary>Orders still owing goods — what a buyer chases.</summary>
    Task<List<PurchaseOrder>> OutstandingAsync(CancellationToken ct = default);

    /// <summary>Recomputes line totals and the header from the lines.</summary>
    static void Recalculate(PurchaseOrder o)
    {
        foreach (var line in o.Lines)
            line.LineTotal = Math.Round(line.Quantity * line.UnitCost, 2);

        o.Subtotal = o.Lines.Sum(l => l.LineTotal);
        o.TotalAmount = Math.Round(o.Subtotal - o.DiscountAmount + o.TaxAmount + o.OtherCharges, 2);
    }
}

public class PurchaseOrderService(InventoryDbContext db) : IPurchaseOrderService
{
    public async Task<List<PurchaseOrder>> ListAsync(
        PurchaseOrderStatus? status = null, int? supplierId = null, CancellationToken ct = default)
    {
        var q = db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines)
            .AsNoTracking().AsSplitQuery().AsQueryable();
        if (status is { } s) q = q.Where(o => o.Status == s);
        if (supplierId is { } sup) q = q.Where(o => o.SupplierId == sup);
        return await q.OrderByDescending(o => o.Id).Take(300).ToListAsync(ct);
    }

    public Task<PurchaseOrder?> GetAsync(int id, CancellationToken ct = default) =>
        db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<PurchaseOrder> SaveAsync(
        PurchaseOrder order, string userId, string userName, CancellationToken ct = default)
    {
        if (order.SupplierId == 0)
            throw new InvalidOperationException("Pick a supplier.");
        if (order.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one line.");
        if (order.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Line quantities must be positive.");
        if (order.Lines.Any(l => l.UnitCost < 0))
            throw new InvalidOperationException("Unit cost can't be negative.");

        IPurchaseOrderService.Recalculate(order);

        if (order.Id == 0)
        {
            order.OrderNumber = await new DocumentNumberService(db).NextAsync("PurchaseOrder", "PO", ct);
            order.Status = PurchaseOrderStatus.Draft;
            order.RaisedById = userId;
            order.RaisedByName = userName;
            if (order.Date == default) order.Date = DateOnly.FromDateTime(DateTime.UtcNow);
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync(ct);
            return order;
        }

        var existing = await Require(order.Id, ct);
        if (existing.Status != PurchaseOrderStatus.Draft)
            throw new InvalidOperationException(
                "Only a draft order can be edited — goods may already have been booked against it.");

        existing.Date = order.Date;
        existing.ExpectedOn = order.ExpectedOn;
        existing.SupplierId = order.SupplierId;
        existing.WarehouseId = order.WarehouseId;
        existing.Reference = order.Reference;
        existing.Notes = order.Notes;
        existing.TaxAmount = order.TaxAmount;
        existing.OtherCharges = order.OtherCharges;
        existing.DiscountAmount = order.DiscountAmount;

        db.PurchaseOrderLines.RemoveRange(existing.Lines);
        existing.Lines = order.Lines.Select(l => new PurchaseOrderLine
        {
            ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
            Quantity = l.Quantity, UnitCost = l.UnitCost, Note = l.Note
        }).ToList();

        IPurchaseOrderService.Recalculate(existing);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<PurchaseOrder> PlaceAsync(int id, CancellationToken ct = default)
    {
        var order = await Require(id, ct);
        if (order.Status != PurchaseOrderStatus.Draft)
            throw new InvalidOperationException("Only a draft order can be placed.");

        order.Status = PurchaseOrderStatus.Ordered;
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<PurchaseOrder> CancelAsync(int id, CancellationToken ct = default)
    {
        var order = await Require(id, ct);
        if (order.Status == PurchaseOrderStatus.Received)
            throw new InvalidOperationException("A fully received order can't be cancelled.");
        if (order.Lines.Any(l => l.ReceivedQuantity > 0))
            throw new InvalidOperationException(
                "Goods have already been booked against this order. Close it short instead.");

        order.Status = PurchaseOrderStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return order;
    }

    public Task<List<PurchaseOrder>> OutstandingAsync(CancellationToken ct = default) =>
        db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines)
            .AsNoTracking().AsSplitQuery()
            .Where(o => o.Status == PurchaseOrderStatus.Ordered
                        || o.Status == PurchaseOrderStatus.PartlyReceived)
            .OrderBy(o => o.ExpectedOn ?? o.Date)
            .ToListAsync(ct);

    private async Task<PurchaseOrder> Require(int id, CancellationToken ct) =>
        await db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new InvalidOperationException("Purchase order not found.");
}

public interface IGoodsReceiptService
{
    Task<List<GoodsReceipt>> ListAsync(GoodsReceiptStatus? status = null, CancellationToken ct = default);
    Task<GoodsReceipt?> GetAsync(int id, CancellationToken ct = default);
    Task<GoodsReceipt> SaveAsync(GoodsReceipt receipt, string userId, string userName,
        CancellationToken ct = default);

    /// <summary>
    /// Books the goods into stock: every line goes through the stock ledger with its
    /// serials and batch, costs are re-derived, and any order behind it is drawn down.
    /// </summary>
    Task<GoodsReceipt> PostAsync(int id, string userId, string userName, CancellationToken ct = default);

    Task<GoodsReceipt> CancelAsync(int id, CancellationToken ct = default);

    /// <summary>Pre-fills a receipt from what an order is still owed.</summary>
    Task<GoodsReceipt> BuildFromOrderAsync(int purchaseOrderId, CancellationToken ct = default);
}

public class GoodsReceiptService(
    InventoryDbContext db, IStockTrackingService tracking) : IGoodsReceiptService
{
    public async Task<List<GoodsReceipt>> ListAsync(
        GoodsReceiptStatus? status = null, CancellationToken ct = default)
    {
        var q = db.GoodsReceipts.Include(r => r.Supplier).Include(r => r.Lines)
            .AsNoTracking().AsSplitQuery().AsQueryable();
        if (status is { } s) q = q.Where(r => r.Status == s);
        return await q.OrderByDescending(r => r.Id).Take(300).ToListAsync(ct);
    }

    public Task<GoodsReceipt?> GetAsync(int id, CancellationToken ct = default) =>
        db.GoodsReceipts.Include(r => r.Supplier).Include(r => r.PurchaseOrder).Include(r => r.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<GoodsReceipt> SaveAsync(
        GoodsReceipt receipt, string userId, string userName, CancellationToken ct = default)
    {
        if (receipt.SupplierId == 0)
            throw new InvalidOperationException("Pick a supplier.");
        if (receipt.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one line.");
        if (receipt.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Line quantities must be positive.");

        foreach (var line in receipt.Lines)
            line.LineTotal = Math.Round(line.Quantity * line.UnitCost, 2);
        receipt.TotalCost = receipt.Lines.Sum(l => l.LineTotal);

        if (receipt.Id == 0)
        {
            receipt.ReceiptNumber = await new DocumentNumberService(db).NextAsync("GoodsReceipt", "GRN", ct);
            receipt.Status = GoodsReceiptStatus.Draft;
            receipt.ReceivedById = userId;
            receipt.ReceivedByName = userName;
            if (receipt.Date == default) receipt.Date = DateOnly.FromDateTime(DateTime.UtcNow);

            // BuildFromOrderAsync fills these from a loaded order so the caller can
            // read supplier and order details off the draft. Add() cascades through
            // every reachable navigation, so leaving them attached makes EF try to
            // insert a Supplier it is already tracking. Only the scalar ids persist.
            receipt.Supplier = null!;
            receipt.PurchaseOrder = null;

            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            return receipt;
        }

        var existing = await Require(receipt.Id, ct);
        if (existing.Status != GoodsReceiptStatus.Draft)
            throw new InvalidOperationException("A posted receipt can't be edited — its stock has moved.");

        existing.Date = receipt.Date;
        existing.SupplierId = receipt.SupplierId;
        existing.PurchaseOrderId = receipt.PurchaseOrderId;
        existing.WarehouseId = receipt.WarehouseId;
        existing.SupplierDocumentNumber = receipt.SupplierDocumentNumber;
        existing.Notes = receipt.Notes;

        db.GoodsReceiptLines.RemoveRange(existing.Lines);
        existing.Lines = receipt.Lines.Select(l => new GoodsReceiptLine
        {
            PurchaseOrderLineId = l.PurchaseOrderLineId,
            ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
            Quantity = l.Quantity, UnitCost = l.UnitCost, LineTotal = l.LineTotal,
            SerialNumbers = l.SerialNumbers, BatchNumber = l.BatchNumber,
            ExpiresOn = l.ExpiresOn, Note = l.Note
        }).ToList();
        existing.TotalCost = receipt.TotalCost;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<GoodsReceipt> PostAsync(
        int id, string userId, string userName, CancellationToken ct = default)
    {
        var receipt = await Require(id, ct);
        if (receipt.Status != GoodsReceiptStatus.Draft)
            throw new InvalidOperationException("Only a draft receipt can be posted.");

        // Read off the row rather than the navigation: SaveAsync deliberately detaches
        // it before Add, so a receipt loaded straight after saving may not have it.
        var supplierName = receipt.Supplier?.Name
            ?? await db.Suppliers.AsNoTracking()
                .Where(s => s.Id == receipt.SupplierId).Select(s => s.Name).FirstOrDefaultAsync(ct)
            ?? "supplier";

        foreach (var line in receipt.Lines)
        {
            // Routed through the tracking service rather than straight at the ledger,
            // so serial and batch rules are enforced identically however goods arrive.
            await tracking.ReceiveAsync(new StockReceipt(
                line.ItemType, line.ItemId, line.Quantity, line.UnitCost,
                line.BatchNumber, line.ExpiresOn, line.Serials(),
                receipt.ReceiptNumber,
                $"GRN {receipt.ReceiptNumber} from {supplierName}"),
                userId, userName, receipt.WarehouseId, ct);

            // Draw down what the order is still owed, so "outstanding" stays honest.
            if (line.PurchaseOrderLineId is { } poLineId)
            {
                var poLine = await db.PurchaseOrderLines.FirstOrDefaultAsync(l => l.Id == poLineId, ct);
                if (poLine is not null)
                    poLine.ReceivedQuantity += line.Quantity;
            }
        }

        receipt.Status = GoodsReceiptStatus.Posted;
        receipt.PostedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await SyncOrderStatusAsync(receipt.PurchaseOrderId, ct);
        return receipt;
    }

    public async Task<GoodsReceipt> CancelAsync(int id, CancellationToken ct = default)
    {
        var receipt = await Require(id, ct);
        if (receipt.Status == GoodsReceiptStatus.Posted)
            throw new InvalidOperationException(
                "A posted receipt can't be cancelled — the stock is already in. " +
                "Adjust it out instead, so the correction is on the record.");

        receipt.Status = GoodsReceiptStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task<GoodsReceipt> BuildFromOrderAsync(
        int purchaseOrderId, CancellationToken ct = default)
    {
        var order = await db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines)
            .AsNoTracking().AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == purchaseOrderId, ct)
            ?? throw new InvalidOperationException("Purchase order not found.");

        if (order.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Cancelled)
            throw new InvalidOperationException("Place the order before receiving against it.");

        var outstanding = order.Lines.Where(l => l.Outstanding > 0).ToList();
        if (outstanding.Count == 0)
            throw new InvalidOperationException($"{order.OrderNumber} has nothing left to receive.");

        // Unsaved: the receiver adjusts quantities and adds serials before it becomes
        // a document, the same way a quotation is built before being saved.
        return new GoodsReceipt
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            SupplierId = order.SupplierId,
            Supplier = order.Supplier,
            PurchaseOrderId = order.Id,
            WarehouseId = order.WarehouseId,
            Lines = outstanding.Select(l => new GoodsReceiptLine
            {
                PurchaseOrderLineId = l.Id,
                ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
                Quantity = l.Outstanding, UnitCost = l.UnitCost,
                LineTotal = Math.Round(l.Outstanding * l.UnitCost, 2)
            }).ToList()
        };
    }

    /// <summary>Moves an order between Ordered, PartlyReceived and Received.</summary>
    private async Task SyncOrderStatusAsync(int? orderId, CancellationToken ct)
    {
        if (orderId is not { } id) return;

        var order = await db.PurchaseOrders.Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || order.Status == PurchaseOrderStatus.Cancelled) return;

        order.Status = order.IsFullyReceived
            ? PurchaseOrderStatus.Received
            : order.Lines.Any(l => l.ReceivedQuantity > 0)
                ? PurchaseOrderStatus.PartlyReceived
                : PurchaseOrderStatus.Ordered;

        await db.SaveChangesAsync(ct);
    }

    private async Task<GoodsReceipt> Require(int id, CancellationToken ct) =>
        await db.GoodsReceipts.Include(r => r.Supplier).Include(r => r.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new InvalidOperationException("Goods receipt not found.");
}
