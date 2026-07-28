using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

public interface ISalesOrderService
{
    Task<List<SalesOrder>> ListAsync(string? search = null, PaymentStatus? status = null,
        CancellationToken ct = default);
    Task<SalesOrder?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>Turns an approved quotation into an order, snapshotting the amounts.</summary>
    Task<SalesOrder> CreateFromQuotationAsync(int quotationId, string userId, string userName,
        CancellationToken ct = default);
    Task<Payment> RecordPaymentAsync(Payment payment, CancellationToken ct = default);
    Task DeletePaymentAsync(int paymentId, CancellationToken ct = default);
}

public class SalesOrderService(RepairDbContext db) : ISalesOrderService
{
    public async Task<List<SalesOrder>> ListAsync(
        string? search = null, PaymentStatus? status = null, CancellationToken ct = default)
    {
        var q = db.SalesOrders.Include(o => o.Customer).AsNoTracking().AsQueryable();

        if (status is { } s) q = q.Where(o => o.PaymentStatus == s);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(o => o.OrderNumber.Contains(t) || o.Customer.Name.Contains(t));
        }

        return await q.OrderByDescending(o => o.Id).Take(300).ToListAsync(ct);
    }

    public Task<SalesOrder?> GetAsync(int id, CancellationToken ct = default) =>
        db.SalesOrders
            .Include(o => o.Customer)
            .Include(o => o.Payments)
            .Include(o => o.Quotation).ThenInclude(q => q.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<SalesOrder> CreateFromQuotationAsync(
        int quotationId, string userId, string userName, CancellationToken ct = default)
    {
        var quotation = await db.Quotations.Include(q => q.Items)
                            .FirstOrDefaultAsync(q => q.Id == quotationId, ct)
                        ?? throw new InvalidOperationException("Quotation not found.");

        if (quotation.Status != QuotationStatus.Approved)
            throw new InvalidOperationException("Only an approved quotation can become an order.");

        if (await db.SalesOrders.AnyAsync(o => o.QuotationId == quotationId, ct))
            throw new InvalidOperationException("This quotation has already been ordered.");

        var customerId = quotation.CustomerId
                         ?? (quotation.RepairJobId is { } jobId
                             ? await db.RepairJobs.Where(j => j.Id == jobId)
                                 .Select(j => (int?)j.CustomerId).FirstOrDefaultAsync(ct)
                             : null)
                         ?? (quotation.IntakeId is { } intakeId
                             ? await db.Intakes.Where(i => i.Id == intakeId)
                                 .Select(i => (int?)i.CustomerId).FirstOrDefaultAsync(ct)
                             : null)
                         ?? throw new InvalidOperationException(
                             "The quotation isn't attached to a customer.");

        // Amounts are copied, not referenced: an order is a bill, and a bill can't
        // move because someone edited the estimate it came from.
        var order = new SalesOrder
        {
            OrderNumber = await new DocumentNumberService(db).NextAsync("SalesOrder", "SO", ct),
            QuotationId = quotation.Id,
            RepairJobId = quotation.RepairJobId,
            IntakeId = quotation.IntakeId,
            CustomerId = customerId,
            FinalizedById = userId,
            FinalizedByName = userName,
            LaborAmount = quotation.LaborAmount,
            PartsAmount = quotation.PartsAmount,
            TaxAmount = quotation.TaxAmount,
            DiscountAmount = quotation.DiscountAmount,
            TotalAmount = quotation.TotalAmount,
            PaymentStatus = quotation.TotalAmount <= 0 ? PaymentStatus.Paid : PaymentStatus.Unpaid
        };

        db.SalesOrders.Add(order);
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task<Payment> RecordPaymentAsync(Payment payment, CancellationToken ct = default)
    {
        if (payment.Amount <= 0)
            throw new InvalidOperationException("Payment amount must be positive.");

        var order = await db.SalesOrders.Include(o => o.Payments)
                        .FirstOrDefaultAsync(o => o.Id == payment.SalesOrderId, ct)
                    ?? throw new InvalidOperationException("Sales order not found.");

        var alreadyPaid = order.Payments.Sum(p => p.Amount);
        if (alreadyPaid + payment.Amount > order.TotalAmount)
            throw new InvalidOperationException(
                $"That would overpay the order by {alreadyPaid + payment.Amount - order.TotalAmount:N2}.");

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        await RefreshPaymentStatusAsync(order.Id, ct);
        return payment;
    }

    public async Task DeletePaymentAsync(int paymentId, CancellationToken ct = default)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return;

        var orderId = payment.SalesOrderId;
        db.Payments.Remove(payment);
        await db.SaveChangesAsync(ct);
        await RefreshPaymentStatusAsync(orderId, ct);
    }

    /// <summary>Recomputes paid-to-date from the payments rather than accumulating.</summary>
    private async Task RefreshPaymentStatusAsync(int orderId, CancellationToken ct)
    {
        var order = await db.SalesOrders.Include(o => o.Payments)
                        .FirstOrDefaultAsync(o => o.Id == orderId, ct)
                    ?? throw new InvalidOperationException("Sales order not found.");

        order.AmountPaid = order.Payments.Sum(p => p.Amount);
        order.PaymentStatus = order.AmountPaid <= 0
            ? PaymentStatus.Unpaid
            : order.AmountPaid >= order.TotalAmount
                ? PaymentStatus.Paid
                : PaymentStatus.Partial;

        await db.SaveChangesAsync(ct);
    }
}
