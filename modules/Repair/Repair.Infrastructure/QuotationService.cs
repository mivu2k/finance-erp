using ErpPlatform.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

public interface IQuotationService
{
    Task<List<Quotation>> ListAsync(string? search = null, QuotationStatus? status = null,
        CancellationToken ct = default);
    Task<Quotation?> GetAsync(int id, CancellationToken ct = default);
    Task<Quotation> SaveAsync(Quotation quotation, string preparerId, string preparerName,
        CancellationToken ct = default);
    Task<Quotation> SendAsync(int id, CancellationToken ct = default);
    Task<Quotation> SetCustomerApprovalAsync(int id, ApprovalState state, CancellationToken ct = default);
    Task<Quotation> SetManagerApprovalAsync(int id, ApprovalState state, string managerId,
        CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Recomputes line totals and the header from the items. Public because the
    /// editor previews the arithmetic before anything is saved.
    /// </summary>
    static void Recalculate(Quotation q)
    {
        foreach (var item in q.Items)
            item.LineTotal = Math.Round(item.Quantity * item.UnitPrice - item.Discount, 2);

        q.PartsAmount = q.Items.Where(i => i.ItemType == QuotationItemType.Part)
            .Sum(i => i.LineTotal);
        var itemLabor = q.Items.Where(i => i.ItemType != QuotationItemType.Part)
            .Sum(i => i.LineTotal);

        // LaborAmount doubles as a header-level charge and a roll-up of labour
        // lines; the header value wins when there are no labour lines.
        if (itemLabor > 0) q.LaborAmount = itemLabor;

        q.Subtotal = Math.Round(q.PartsAmount + q.LaborAmount, 2);
        q.TaxAmount = Math.Round((q.Subtotal - q.DiscountAmount) * q.TaxPercent / 100m, 2);
        q.TotalAmount = Math.Round(q.Subtotal - q.DiscountAmount + q.TaxAmount, 2);
    }
}

public class QuotationService(RepairDbContext db) : IQuotationService
{
    public async Task<List<Quotation>> ListAsync(
        string? search = null, QuotationStatus? status = null, CancellationToken ct = default)
    {
        var q = db.Quotations.Include(x => x.Customer).Include(x => x.RepairJob)
            .AsNoTracking().AsQueryable();

        if (status is { } s) q = q.Where(x => x.Status == s);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(x => x.QuotationNumber.Contains(t)
                          || (x.Subject != null && x.Subject.Contains(t))
                          || (x.Customer != null && x.Customer.Name.Contains(t))
                          || (x.RepairJob != null && x.RepairJob.JobNumber.Contains(t)));
        }

        return await q.OrderByDescending(x => x.Id).Take(300).ToListAsync(ct);
    }

    public Task<Quotation?> GetAsync(int id, CancellationToken ct = default) =>
        db.Quotations
            .Include(q => q.Items).ThenInclude(i => i.Part)
            .Include(q => q.Customer)
            .Include(q => q.RepairJob)
            .Include(q => q.Intake)
            .FirstOrDefaultAsync(q => q.Id == id, ct);

    public async Task<Quotation> SaveAsync(
        Quotation quotation, string preparerId, string preparerName, CancellationToken ct = default)
    {
        if (quotation.Items.Count == 0)
            throw new InvalidOperationException("Add at least one line.");
        if (quotation.Items.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            throw new InvalidOperationException("Every line needs a description.");
        if (quotation.Items.Any(i => i.Quantity <= 0))
            throw new InvalidOperationException("Line quantities must be positive.");
        if (quotation.RepairJobId is null && quotation.IntakeId is null && quotation.CustomerId is null)
            throw new InvalidOperationException("Attach the quotation to a job, an intake or a customer.");

        IQuotationService.Recalculate(quotation);

        if (quotation.Id == 0)
        {
            quotation.QuotationNumber = await new DocumentNumberService(db)
                .NextAsync("Quotation", "QTN", ct);
            quotation.PreparedById = preparerId;
            quotation.PreparedByName = preparerName;
            if (quotation.Date == default) quotation.Date = DateOnly.FromDateTime(DateTime.UtcNow);
            db.Quotations.Add(quotation);
            await db.SaveChangesAsync(ct);
            return quotation;
        }

        var existing = await db.Quotations.Include(q => q.Items)
                           .FirstOrDefaultAsync(q => q.Id == quotation.Id, ct)
                       ?? throw new InvalidOperationException("Quotation not found.");

        // Once a customer has agreed a price, the document is settled.
        if (existing.Status is QuotationStatus.Approved)
            throw new InvalidOperationException("An approved quotation can't be edited.");

        existing.Subject = quotation.Subject;
        existing.Reference = quotation.Reference;
        existing.Date = quotation.Date;
        existing.Currency = quotation.Currency;
        existing.Project = quotation.Project;
        existing.LaborDescription = quotation.LaborDescription;
        existing.LaborAmount = quotation.LaborAmount;
        existing.TaxPercent = quotation.TaxPercent;
        existing.DiscountAmount = quotation.DiscountAmount;
        existing.ValidUntil = quotation.ValidUntil;
        existing.Notes = quotation.Notes;

        db.QuotationItems.RemoveRange(existing.Items);
        existing.Items = quotation.Items.Select(i => new QuotationItem
        {
            RepairJobId = i.RepairJobId,
            PartId = i.PartId,
            ItemType = i.ItemType,
            Description = i.Description,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            Discount = i.Discount
        }).ToList();

        IQuotationService.Recalculate(existing);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<Quotation> SendAsync(int id, CancellationToken ct = default)
    {
        var q = await Require(id, ct);
        if (q.Status != QuotationStatus.Draft)
            throw new InvalidOperationException("Only a draft can be sent.");

        q.Status = QuotationStatus.Sent;
        await db.SaveChangesAsync(ct);
        return q;
    }

    public async Task<Quotation> SetCustomerApprovalAsync(
        int id, ApprovalState state, CancellationToken ct = default)
    {
        var q = await Require(id, ct);
        q.CustomerApproval = state;
        q.CustomerApprovedAtUtc = state == ApprovalState.Approved ? DateTime.UtcNow : null;
        Settle(q);
        await db.SaveChangesAsync(ct);
        return q;
    }

    public async Task<Quotation> SetManagerApprovalAsync(
        int id, ApprovalState state, string managerId, CancellationToken ct = default)
    {
        var q = await Require(id, ct);
        q.ManagerApproval = state;
        q.ManagerId = managerId;
        q.ManagerApprovedAtUtc = state == ApprovalState.Approved ? DateTime.UtcNow : null;
        Settle(q);
        await db.SaveChangesAsync(ct);
        return q;
    }

    /// <summary>
    /// The two approvals are independent. A quotation is approved only when both
    /// have said yes; either one rejecting kills it.
    /// </summary>
    private static void Settle(Quotation q)
    {
        if (q.CustomerApproval == ApprovalState.Rejected || q.ManagerApproval == ApprovalState.Rejected)
            q.Status = QuotationStatus.Rejected;
        else if (q.CustomerApproval == ApprovalState.Approved && q.ManagerApproval == ApprovalState.Approved)
            q.Status = QuotationStatus.Approved;
        else if (q.Status == QuotationStatus.Draft)
            q.Status = QuotationStatus.Pending;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var q = await db.Quotations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (q is null) return;
        if (await db.SalesOrders.AnyAsync(o => o.QuotationId == id, ct))
            throw new InvalidOperationException("This quotation has been ordered and can't be removed.");

        db.Quotations.Remove(q);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Quotation> Require(int id, CancellationToken ct) =>
        await db.Quotations.Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == id, ct)
        ?? throw new InvalidOperationException("Quotation not found.");
}
