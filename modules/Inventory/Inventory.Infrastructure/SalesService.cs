using ErpPlatform.Shared.Persistence;
using Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure;

public interface ICustomerService
{
    Task<List<Customer>> ListAsync(string? search = null, bool activeOnly = false,
        CancellationToken ct = default);
    Task<Customer?> GetAsync(int id, CancellationToken ct = default);
    Task<Customer> SaveAsync(Customer customer, CancellationToken ct = default);
    /// <summary>Soft-deletes a customer. Refused once orders or deliveries reference it.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class CustomerService(InventoryDbContext db) : ICustomerService
{
    public async Task<List<Customer>> ListAsync(
        string? search = null, bool activeOnly = false, CancellationToken ct = default)
    {
        var q = db.Customers.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(c => c.Name.Contains(t)
                          || (c.Code != null && c.Code.Contains(t))
                          || (c.Phone != null && c.Phone.Contains(t)));
        }
        return await q.OrderBy(c => c.Name).ToListAsync(ct);
    }

    public Task<Customer?> GetAsync(int id, CancellationToken ct = default) =>
        db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<Customer> SaveAsync(Customer customer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customer.Name))
            throw new InvalidOperationException("Customer name is required.");

        if (customer.Id == 0)
        {
            db.Customers.Add(customer);
        }
        else
        {
            var existing = await db.Customers.FirstOrDefaultAsync(c => c.Id == customer.Id, ct)
                ?? throw new InvalidOperationException("Customer not found.");

            existing.Name = customer.Name;
            existing.Code = customer.Code;
            existing.ContactPerson = customer.ContactPerson;
            existing.Phone = customer.Phone;
            existing.Email = customer.Email;
            existing.Address = customer.Address;
            existing.TaxNumber = customer.TaxNumber;
            existing.PaymentTermDays = customer.PaymentTermDays;
            existing.Notes = customer.Notes;
            existing.IsActive = customer.IsActive;
        }

        await db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return;

        // History has to keep resolving, so a customer with paperwork against them is
        // deactivated rather than removed.
        if (await db.SalesOrders.AnyAsync(o => o.CustomerId == id, ct)
            || await db.Deliveries.AnyAsync(d => d.CustomerId == id, ct))
            throw new InvalidOperationException(
                "This customer has orders or deliveries against them. Mark them inactive instead.");

        db.Customers.Remove(customer);
        await db.SaveChangesAsync(ct);
    }
}

public interface ISalesOrderService
{
    Task<List<SalesOrder>> ListAsync(string? search = null, SalesOrderStatus? status = null,
        CancellationToken ct = default);
    Task<SalesOrder?> GetAsync(int id, CancellationToken ct = default);
    Task<SalesOrder> SaveAsync(SalesOrder order, CancellationToken ct = default);

    /// <summary>Moves a draft to Confirmed. Reserves nothing — see <see cref="SalesOrder"/>.</summary>
    Task<SalesOrder> ConfirmAsync(int id, CancellationToken ct = default);
    Task<SalesOrder> CancelAsync(int id, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Orders with something still owed to the customer.</summary>
    Task<List<SalesOrder>> ListOutstandingAsync(CancellationToken ct = default);
}

public class SalesOrderService(InventoryDbContext db) : ISalesOrderService
{
    private static readonly List<SalesOrderStatus> OpenStatuses =
    [
        SalesOrderStatus.Confirmed, SalesOrderStatus.PartlyDelivered
    ];

    public async Task<List<SalesOrder>> ListAsync(
        string? search = null, SalesOrderStatus? status = null, CancellationToken ct = default)
    {
        var q = db.SalesOrders.Include(o => o.Customer).Include(o => o.Lines)
            .AsNoTracking().AsSplitQuery().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(o => o.OrderNumber.Contains(t) || o.Customer.Name.Contains(t)
                          || (o.CustomerReference != null && o.CustomerReference.Contains(t)));
        }

        if (status is { } s) q = q.Where(o => o.Status == s);

        return await q.OrderByDescending(o => o.Date).ThenByDescending(o => o.Id).ToListAsync(ct);
    }

    public Task<SalesOrder?> GetAsync(int id, CancellationToken ct = default) =>
        db.SalesOrders.Include(o => o.Customer).Include(o => o.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<SalesOrder> SaveAsync(SalesOrder order, CancellationToken ct = default)
    {
        if (order.CustomerId == 0)
            throw new InvalidOperationException("Pick a customer.");
        if (order.Lines.Count == 0)
            throw new InvalidOperationException("An order needs at least one line.");
        if (order.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Every line needs a positive quantity.");

        Total(order);

        if (order.Id == 0)
        {
            order.OrderNumber = await new DocumentNumberService(db).NextAsync("SalesOrder", "SO", ct);

            // Never hand a loaded entity to a navigation on something being added: query
            // fixup drags its whole graph in and two rows end up sharing one lookup.
            order.Customer = null!;
            db.SalesOrders.Add(order);
        }
        else
        {
            var existing = await db.SalesOrders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.Id == order.Id, ct)
                ?? throw new InvalidOperationException("Order not found.");

            if (existing.Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed))
                throw new InvalidOperationException(
                    "An order that has started shipping can't be re-scoped.");

            existing.Date = order.Date;
            existing.RequiredBy = order.RequiredBy;
            existing.CustomerId = order.CustomerId;
            existing.WarehouseId = order.WarehouseId;
            existing.CustomerReference = order.CustomerReference;
            existing.Notes = order.Notes;

            db.SalesOrderLines.RemoveRange(existing.Lines);
            existing.Lines = order.Lines.Select(l => new SalesOrderLine
            {
                ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
                Quantity = l.Quantity, UnitPrice = l.UnitPrice, LineTotal = l.LineTotal,
                DeliveredQuantity = l.DeliveredQuantity, Note = l.Note
            }).ToList();

            existing.Subtotal = order.Subtotal;
            existing.TaxAmount = order.TaxAmount;
            existing.OtherCharges = order.OtherCharges;
            existing.DiscountAmount = order.DiscountAmount;
            existing.TotalAmount = order.TotalAmount;

            await db.SaveChangesAsync(ct);
            return existing;
        }

        await db.SaveChangesAsync(ct);
        return order;
    }

    private static void Total(SalesOrder order)
    {
        foreach (var line in order.Lines)
            line.LineTotal = Math.Round(line.Quantity * line.UnitPrice, 2);

        order.Subtotal = order.Lines.Sum(l => l.LineTotal);
        order.TotalAmount = Math.Round(
            order.Subtotal + order.TaxAmount + order.OtherCharges - order.DiscountAmount, 2);
    }

    public async Task<SalesOrder> ConfirmAsync(int id, CancellationToken ct = default)
    {
        var order = await Require(id, ct);
        if (order.Status != SalesOrderStatus.Draft)
            throw new InvalidOperationException("Only a draft order can be confirmed.");

        order.Status = SalesOrderStatus.Confirmed;
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<SalesOrder> CancelAsync(int id, CancellationToken ct = default)
    {
        var order = await Require(id, ct);
        if (order.Lines.Any(l => l.DeliveredQuantity > 0))
            throw new InvalidOperationException(
                "Part of this order has already shipped. Cancel is not the correction — " +
                "record a return instead.");

        order.Status = SalesOrderStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var order = await db.SalesOrders.Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return;

        if (order.Status != SalesOrderStatus.Draft)
            throw new InvalidOperationException("Only a draft order can be deleted.");

        db.SalesOrderLines.RemoveRange(order.Lines);
        db.SalesOrders.Remove(order);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SalesOrder>> ListOutstandingAsync(CancellationToken ct = default)
    {
        var orders = await db.SalesOrders.Include(o => o.Customer).Include(o => o.Lines)
            .AsNoTracking().AsSplitQuery()
            .Where(o => OpenStatuses.Contains(o.Status))
            .OrderBy(o => o.RequiredBy ?? DateOnly.MaxValue)
            .ToListAsync(ct);

        return orders.Where(o => o.Lines.Any(l => l.Outstanding > 0)).ToList();
    }

    private async Task<SalesOrder> Require(int id, CancellationToken ct) =>
        await db.SalesOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new InvalidOperationException("Order not found.");
}

public interface IDeliveryService
{
    Task<List<Delivery>> ListAsync(string? search = null, DeliveryStatus? status = null,
        CancellationToken ct = default);
    Task<Delivery?> GetAsync(int id, CancellationToken ct = default);
    Task<Delivery> SaveAsync(Delivery delivery, CancellationToken ct = default);

    /// <summary>
    /// Issues every line out of stock, snapshots the cost of what went out, and draws
    /// down the order behind it. Terminal.
    /// </summary>
    Task<Delivery> PostAsync(int id, string userId, string userName, CancellationToken ct = default);

    Task<Delivery> CancelAsync(int id, CancellationToken ct = default);

    /// <summary>An unsaved delivery covering whatever the order still owes.</summary>
    Task<Delivery> BuildFromOrderAsync(int salesOrderId, CancellationToken ct = default);
}

public class DeliveryService(
    InventoryDbContext db, IStockService stock, IStockTrackingService tracking) : IDeliveryService
{
    public async Task<List<Delivery>> ListAsync(
        string? search = null, DeliveryStatus? status = null, CancellationToken ct = default)
    {
        var q = db.Deliveries.Include(d => d.Customer).Include(d => d.Lines)
            .AsNoTracking().AsSplitQuery().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(d => d.DeliveryNumber.Contains(t) || d.Customer.Name.Contains(t)
                          || (d.ReceivedByName != null && d.ReceivedByName.Contains(t)));
        }

        if (status is { } s) q = q.Where(d => d.Status == s);

        return await q.OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).ToListAsync(ct);
    }

    public Task<Delivery?> GetAsync(int id, CancellationToken ct = default) =>
        db.Deliveries.Include(d => d.Customer).Include(d => d.SalesOrder).Include(d => d.Lines)
            .AsSplitQuery().FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<Delivery> SaveAsync(Delivery delivery, CancellationToken ct = default)
    {
        if (delivery.CustomerId == 0)
            throw new InvalidOperationException("Pick a customer.");
        if (delivery.Lines.Count == 0)
            throw new InvalidOperationException("A delivery needs at least one line.");
        if (delivery.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Every line needs a positive quantity.");

        foreach (var line in delivery.Lines)
            line.LineTotal = Math.Round(line.Quantity * line.UnitPrice, 2);
        delivery.TotalAmount = delivery.Lines.Sum(l => l.LineTotal);

        if (delivery.Id == 0)
        {
            delivery.DeliveryNumber = await new DocumentNumberService(db).NextAsync("Delivery", "DN", ct);

            // Same fixup trap as the order: only the scalar FK is ever persisted.
            delivery.Customer = null!;
            delivery.SalesOrder = null;
            db.Deliveries.Add(delivery);
        }
        else
        {
            var existing = await db.Deliveries.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == delivery.Id, ct)
                ?? throw new InvalidOperationException("Delivery not found.");

            if (existing.Status != DeliveryStatus.Draft)
                throw new InvalidOperationException("Only a draft delivery can be edited.");

            existing.Date = delivery.Date;
            existing.CustomerId = delivery.CustomerId;
            existing.SalesOrderId = delivery.SalesOrderId;
            existing.WarehouseId = delivery.WarehouseId;
            existing.ReceivedByName = delivery.ReceivedByName;
            existing.VehicleNumber = delivery.VehicleNumber;
            existing.DeliveryAddress = delivery.DeliveryAddress;
            existing.Notes = delivery.Notes;

            db.DeliveryLines.RemoveRange(existing.Lines);
            existing.Lines = delivery.Lines.Select(l => new DeliveryLine
            {
                SalesOrderLineId = l.SalesOrderLineId,
                ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
                Quantity = l.Quantity, UnitPrice = l.UnitPrice, LineTotal = l.LineTotal,
                SerialNumbers = l.SerialNumbers, Note = l.Note
            }).ToList();
            existing.TotalAmount = delivery.TotalAmount;

            await db.SaveChangesAsync(ct);
            return existing;
        }

        await db.SaveChangesAsync(ct);
        return delivery;
    }

    public async Task<Delivery> PostAsync(
        int id, string userId, string userName, CancellationToken ct = default)
    {
        var delivery = await Require(id, ct);
        if (delivery.Status != DeliveryStatus.Draft)
            throw new InvalidOperationException("Only a draft delivery can be posted.");

        var customerName = delivery.Customer?.Name
            ?? await db.Customers.AsNoTracking()
                .Where(c => c.Id == delivery.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            ?? "customer";

        // Check the whole delivery before moving any of it: a half-posted note that
        // ran out of stock on line three is far worse than one that was refused.
        foreach (var line in delivery.Lines)
        {
            var (name, serialised, available) = await DescribeAsync(line.ItemType, line.ItemId, ct);

            if (available < line.Quantity)
                throw new InvalidOperationException(
                    $"Only {available:0.##} of {name} in stock — the note asks for {line.Quantity:0.##}.");

            if (serialised)
            {
                var serials = line.Serials();
                if (serials.Count != line.Quantity)
                    throw new InvalidOperationException(
                        $"{name} is serialised: list exactly {line.Quantity:0.##} serial(s) on that line.");
            }
        }

        // No transaction of our own: every stock movement already runs inside one, via
        // the execution strategy, and EF refuses to nest a second. This mirrors the
        // goods-receipt path — the pre-flight check above is what stops a half-posted
        // note, rather than a rollback after the fact.
        decimal totalCost = 0;

        foreach (var line in delivery.Lines)
        {
            // Snapshot the cost before the movement: the average is unchanged by an
            // issue, but pinning it here is what stops a later purchase rewriting
            // this note's margin.
            line.UnitCost = await AverageCostAsync(line.ItemType, line.ItemId, ct);
            line.LineCost = Math.Round(line.Quantity * line.UnitCost, 2);
            totalCost += line.LineCost;

            var serials = line.Serials();
            if (serials.Count > 0)
            {
                // Issuing named units also moves the quantity and writes the ledger
                // row, so this is the whole movement for a serialised line.
                await tracking.IssueSerialsAsync(line.ItemType, line.ItemId, serials,
                    StockUnitStatus.Sold, customerName, delivery.DeliveryNumber,
                    userId, userName, ct);
            }
            else
            {
                await stock.AdjustAsync(line.ItemType, line.ItemId, StockDirection.Out,
                    line.Quantity, StockReason.Sale, delivery.DeliveryNumber,
                    $"DN {delivery.DeliveryNumber} to {customerName}",
                    userId, userName, delivery.WarehouseId, ct);
            }

            // Draw down what the order still owes, so "outstanding" stays honest.
            if (line.SalesOrderLineId is { } soLineId)
            {
                var soLine = await db.SalesOrderLines.FirstOrDefaultAsync(l => l.Id == soLineId, ct);
                if (soLine is not null) soLine.DeliveredQuantity += line.Quantity;
            }
        }

        delivery.TotalCost = totalCost;
        delivery.Status = DeliveryStatus.Posted;
        delivery.PostedAtUtc = DateTime.UtcNow;
        delivery.DeliveredById = userId;
        delivery.DeliveredByName = userName;
        await db.SaveChangesAsync(ct);

        await SyncOrderStatusAsync(delivery.SalesOrderId, ct);
        return delivery;
    }

    public async Task<Delivery> CancelAsync(int id, CancellationToken ct = default)
    {
        var delivery = await Require(id, ct);
        if (delivery.Status == DeliveryStatus.Posted)
            throw new InvalidOperationException(
                "A posted delivery can't be cancelled — the stock is already out. " +
                "Adjust it back in instead, so the correction is on the record.");

        delivery.Status = DeliveryStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return delivery;
    }

    public async Task<Delivery> BuildFromOrderAsync(int salesOrderId, CancellationToken ct = default)
    {
        var order = await db.SalesOrders.Include(o => o.Customer).Include(o => o.Lines)
            .AsNoTracking().AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == salesOrderId, ct)
            ?? throw new InvalidOperationException("Sales order not found.");

        if (order.Status is SalesOrderStatus.Draft or SalesOrderStatus.Cancelled)
            throw new InvalidOperationException("Confirm the order before delivering against it.");

        var outstanding = order.Lines.Where(l => l.Outstanding > 0).ToList();
        if (outstanding.Count == 0)
            throw new InvalidOperationException($"{order.OrderNumber} has nothing left to deliver.");

        // Unsaved: the storeman adjusts quantities and adds serials before it becomes a
        // document, the same way a goods receipt is built before being saved.
        return new Delivery
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            CustomerId = order.CustomerId,
            Customer = order.Customer,
            SalesOrderId = order.Id,
            WarehouseId = order.WarehouseId,
            DeliveryAddress = order.Customer?.Address,
            Lines = outstanding.Select(l => new DeliveryLine
            {
                SalesOrderLineId = l.Id,
                ItemType = l.ItemType, ItemId = l.ItemId, ItemName = l.ItemName,
                Quantity = l.Outstanding, UnitPrice = l.UnitPrice,
                LineTotal = Math.Round(l.Outstanding * l.UnitPrice, 2)
            }).ToList()
        };
    }

    /// <summary>Moves an order between Confirmed, PartlyDelivered and Delivered.</summary>
    private async Task SyncOrderStatusAsync(int? orderId, CancellationToken ct)
    {
        if (orderId is not { } id) return;

        var order = await db.SalesOrders.Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || order.Status == SalesOrderStatus.Cancelled) return;

        order.Status = order.IsFullyDelivered
            ? SalesOrderStatus.Delivered
            : order.Lines.Any(l => l.DeliveredQuantity > 0)
                ? SalesOrderStatus.PartlyDelivered
                : SalesOrderStatus.Confirmed;

        await db.SaveChangesAsync(ct);
    }

    private async Task<(string Name, bool Serialised, decimal Available)> DescribeAsync(
        StockItemType type, int itemId, CancellationToken ct)
    {
        if (type == StockItemType.Model)
        {
            var m = await db.ProductModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, ct)
                ?? throw new InvalidOperationException("Model not found.");
            return (m.Name, m.IsSerialised, m.CurrentQuantity);
        }

        var a = await db.Accessories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, ct)
            ?? throw new InvalidOperationException("Accessory not found.");
        return (a.Name, a.IsSerialised, a.CurrentQuantity);
    }

    private async Task<decimal> AverageCostAsync(StockItemType type, int itemId, CancellationToken ct)
    {
        if (type == StockItemType.Model)
            return await db.ProductModels.AsNoTracking().Where(x => x.Id == itemId)
                .Select(x => x.AverageCost ?? 0).FirstOrDefaultAsync(ct);

        return await db.Accessories.AsNoTracking().Where(x => x.Id == itemId)
            .Select(x => x.AverageCost ?? 0).FirstOrDefaultAsync(ct);
    }

    private async Task<Delivery> Require(int id, CancellationToken ct) =>
        await db.Deliveries.Include(d => d.Customer).Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
        ?? throw new InvalidOperationException("Delivery not found.");
}
