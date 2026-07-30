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
    /// Builds an unsaved quotation for one job from the parts and labour recorded
    /// against it. Nothing is persisted — the editor opens on the result so the
    /// preparer can adjust prices before saving.
    /// </summary>
    Task<Quotation> BuildForJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// The collective case: one quotation covering every device on an intake. Lines
    /// stay tagged with the job they came from, so the printed estimate and the
    /// per-device reports still agree on which device cost what.
    /// </summary>
    Task<Quotation> BuildForIntakeAsync(int intakeId, CancellationToken ct = default);

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

            // BuildForJobAsync/BuildForIntakeAsync populate these navigations from
            // a loaded, graph-connected object (e.g. Customer still carries its
            // Intakes back-reference, which still carries Jobs -> WorkItems ->
            // Part). Only the scalar *Id columns are ever persisted, but Add()
            // cascades through any attached navigation and, when two jobs share
            // the same part, walks two separate untracked Part instances for the
            // same key and throws "already being tracked". Detach the display-only
            // navigations before Add() so nothing is reachable to cascade into.
            quotation.Customer = null;
            quotation.RepairJob = null;
            quotation.Intake = null;
            foreach (var item in quotation.Items) item.Part = null;

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

    public async Task<Quotation> BuildForJobAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.RepairJobs
                      .Include(j => j.Customer).Include(j => j.Intake)
                      .Include(j => j.WorkItems).ThenInclude(w => w.Part)
                      .AsNoTracking()
                      .AsSplitQuery()
                      .FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new InvalidOperationException("Job not found.");

        var q = new Quotation
        {
            RepairJobId = job.Id,
            IntakeId = job.IntakeId,
            CustomerId = job.CustomerId,
            Customer = job.Customer,
            RepairJob = job,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Subject = $"{job.JobNumber} — {DeviceLabel(job)}",
            Reference = job.Intake.IntakeNumber,
            Items = LinesFor(job, prefixDevice: false)
        };

        if (q.Items.Count == 0)
            throw new InvalidOperationException(
                $"{job.JobNumber} has no billable parts or labour recorded yet.");

        IQuotationService.Recalculate(q);
        return q;
    }

    public async Task<Quotation> BuildForIntakeAsync(int intakeId, CancellationToken ct = default)
    {
        // AsSplitQuery: Jobs (collection) -> WorkItems (collection) -> Part is a
        // nested collection-of-collections Include. Without splitting, an intake
        // with several jobs whose work items share the same part hits the same
        // EF Core Cartesian-multiplication bug fixed in RepairJobService.Detailed —
        // "already being tracked" for Part even under AsNoTracking.
        var intake = await db.Intakes
                         .Include(i => i.Customer)
                         .Include(i => i.Jobs).ThenInclude(j => j.WorkItems).ThenInclude(w => w.Part)
                         .AsNoTracking()
                         .AsSplitQuery()
                         .FirstOrDefaultAsync(i => i.Id == intakeId, ct)
                     ?? throw new InvalidOperationException("Intake not found.");

        // Device name is folded into each description because a collective estimate
        // is read by the customer, who knows their devices, not our job numbers.
        var items = intake.Jobs
            .Where(j => j.Status != JobStatus.Cancelled)
            .OrderBy(j => j.JobNumber)
            .SelectMany(j => LinesFor(j, prefixDevice: true))
            .ToList();

        if (items.Count == 0)
            throw new InvalidOperationException(
                $"No job on {intake.IntakeNumber} has billable parts or labour recorded yet.");

        var q = new Quotation
        {
            IntakeId = intake.Id,
            CustomerId = intake.CustomerId,
            Customer = intake.Customer,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Subject = $"{intake.IntakeNumber} — {items.Select(i => i.RepairJobId).Distinct().Count()} device(s)",
            Reference = intake.IntakeNumber,
            Items = items
        };

        IQuotationService.Recalculate(q);
        return q;
    }

    private static string DeviceLabel(RepairJob job) =>
        string.Join(' ', new[] { job.Brand, job.DeviceName, job.Model }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// Non-billable work (goodwill, warranty rework) stays on the job card but is
    /// deliberately left out of the estimate.
    /// </summary>
    private static List<QuotationItem> LinesFor(RepairJob job, bool prefixDevice) =>
        job.WorkItems
            .Where(w => w.Billable)
            .Select(w => new QuotationItem
            {
                RepairJobId = job.Id,
                PartId = w.PartId,
                ItemType = (QuotationItemType)w.Kind,
                Description = prefixDevice
                    ? $"{job.JobNumber} {DeviceLabel(job)} — {w.Description}"
                    : w.Description,
                Quantity = w.Quantity,
                UnitPrice = w.UnitPrice
            })
            .ToList();

    private async Task<Quotation> Require(int id, CancellationToken ct) =>
        await db.Quotations.Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == id, ct)
        ?? throw new InvalidOperationException("Quotation not found.");
}
