using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure.Reports;

/// <summary>
/// Every report the workshop runs. Deliberately one service: the reports share
/// the same window and the same joins, and splitting them would mean loading the
/// same jobs five times.
/// </summary>
public interface IRepairReportService
{
    Task<WorkshopSummary> SummaryAsync(ReportRange range, CancellationToken ct = default);
    Task<List<StatusCount>> PipelineAsync(CancellationToken ct = default);
    Task<List<AgeingBucket>> AgeingAsync(CancellationToken ct = default);
    Task<List<TurnaroundRow>> TurnaroundAsync(ReportRange range, CancellationToken ct = default);
    Task<List<TechnicianPerformance>> TechniciansAsync(ReportRange range, CancellationToken ct = default);
    Task<List<CustomerActivity>> CustomersAsync(ReportRange range, CancellationToken ct = default);
    Task<List<FailureRow>> FailureAnalysisAsync(ReportRange range, CancellationToken ct = default);
    Task<List<SymptomFrequency>> SymptomsAsync(ReportRange range, CancellationToken ct = default);
    Task<List<PartUsageRow>> PartUsageAsync(ReportRange range, CancellationToken ct = default);
    Task<List<SupplierSpendRow>> SupplierSpendAsync(ReportRange range, CancellationToken ct = default);
    Task<List<ReceivableRow>> ReceivablesAsync(CancellationToken ct = default);
    Task<List<CollectionRow>> CollectionsAsync(ReportRange range, CancellationToken ct = default);
    Task<List<DailyActivityRow>> DailyActivityAsync(ReportRange range, CancellationToken ct = default);
    Task<List<WarrantyRow>> WarrantyMixAsync(ReportRange range, CancellationToken ct = default);
    Task<List<QuotationOutcomeRow>> QuotationOutcomesAsync(ReportRange range, CancellationToken ct = default);
}

public class RepairReportService(RepairDbContext db) : IRepairReportService
{
    public async Task<WorkshopSummary> SummaryAsync(ReportRange range, CancellationToken ct = default)
    {
        var jobs = await JobsInWindow(range).ToListAsync(ct);
        var delivered = jobs.Where(j => j.DeliveredAtUtc is { } d
                                        && d >= range.StartUtc && d < range.EndUtc).ToList();

        var turnarounds = delivered
            .Select(j => (j.DeliveredAtUtc!.Value - j.CreatedAtUtc).TotalDays)
            .Where(d => d >= 0).OrderBy(d => d).ToList();

        var open = JobWorkflow.Open;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var openNow = await db.RepairJobs.CountAsync(j => open.Contains(j.Status), ct);
        var overdue = await db.RepairJobs.CountAsync(
            j => open.Contains(j.Status) && j.ExpectedDeliveryDate != null
                 && j.ExpectedDeliveryDate < today, ct);
        var unassigned = await db.RepairJobs.CountAsync(
            j => open.Contains(j.Status) && j.AssignedTechnicianId == null, ct);

        var quotations = await db.Quotations
            .Where(q => q.Date >= range.From && q.Date <= range.To)
            .Select(q => new { q.Status, q.TotalAmount })
            .ToListAsync(ct);

        var orders = await db.SalesOrders
            .Where(o => o.CreatedAtUtc >= range.StartUtc && o.CreatedAtUtc < range.EndUtc)
            .Select(o => o.TotalAmount)
            .ToListAsync(ct);

        var collected = await db.Payments
            .Where(p => p.CreatedAtUtc >= range.StartUtc && p.CreatedAtUtc < range.EndUtc)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0;

        var outstanding = await db.SalesOrders
            .Where(o => o.PaymentStatus != PaymentStatus.Paid)
            .SumAsync(o => (decimal?)(o.TotalAmount - o.AmountPaid), ct) ?? 0;

        var partsSpend = await db.PartPurchases
            .Where(p => p.PurchasedOn >= range.From && p.PurchasedOn <= range.To)
            .SumAsync(p => (decimal?)p.TotalAmount, ct) ?? 0;

        var intakes = await db.Intakes.CountAsync(
            i => i.ReceivedAtUtc >= range.StartUtc && i.ReceivedAtUtc < range.EndUtc, ct);

        return new WorkshopSummary(
            intakes,
            jobs.Count(j => j.CreatedAtUtc >= range.StartUtc && j.CreatedAtUtc < range.EndUtc),
            delivered.Count,
            jobs.Count(j => j.Status == JobStatus.Cancelled
                            && j.StatusUpdatedAtUtc >= range.StartUtc
                            && j.StatusUpdatedAtUtc < range.EndUtc),
            openNow, overdue, unassigned,
            turnarounds.Count == 0 ? 0 : Math.Round(turnarounds.Average(), 1),
            Median(turnarounds),
            quotations.Count,
            quotations.Count(q => q.Status == QuotationStatus.Approved),
            quotations.Sum(q => q.TotalAmount),
            orders.Sum(),
            collected,
            outstanding,
            partsSpend);
    }

    public async Task<List<StatusCount>> PipelineAsync(CancellationToken ct = default)
    {
        var rows = await db.RepairJobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = rows.Sum(r => r.Count);

        return rows
            .OrderBy(r => r.Status)
            .Select(r => new StatusCount(
                JobWorkflow.Describe(r.Status), r.Count,
                total == 0 ? 0 : Math.Round(r.Count * 100m / total, 1)))
            .ToList();
    }

    /// <summary>
    /// Open jobs bucketed by how long they've been sitting. This is the report that
    /// catches a device quietly forgotten on a bench.
    /// </summary>
    public async Task<List<AgeingBucket>> AgeingAsync(CancellationToken ct = default)
    {
        var open = JobWorkflow.Open;
        var jobs = await db.RepairJobs
            .Where(j => open.Contains(j.Status))
            .Select(j => new { j.JobNumber, j.CreatedAtUtc })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var buckets = new (string Label, int Min, int Max)[]
        {
            ("0–3 days", 0, 3),
            ("4–7 days", 4, 7),
            ("8–14 days", 8, 14),
            ("15–30 days", 15, 30),
            ("Over 30 days", 31, int.MaxValue)
        };

        return buckets.Select(b =>
        {
            var inBucket = jobs
                .Where(j =>
                {
                    var age = (int)(now - j.CreatedAtUtc).TotalDays;
                    return age >= b.Min && age <= b.Max;
                })
                .Select(j => j.JobNumber)
                .OrderBy(n => n)
                .ToList();

            return new AgeingBucket(b.Label, inBucket.Count, inBucket);
        }).ToList();
    }

    public async Task<List<TurnaroundRow>> TurnaroundAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var jobs = await JobsInWindow(range)
            .Include(j => j.Customer)
            .AsNoTracking()
            .ToListAsync(ct);

        return jobs
            .Select(j =>
            {
                var days = j.DeliveredAtUtc is { } d
                    ? Math.Round((d - j.CreatedAtUtc).TotalDays, 1)
                    : (double?)null;

                var late = j.ExpectedDeliveryDate is { } promised
                           && (j.DeliveredAtUtc is { } delivered
                               ? DateOnly.FromDateTime(delivered) > promised
                               : DateOnly.FromDateTime(DateTime.Today) > promised
                                 && JobWorkflow.Open.Contains(j.Status));

                return new TurnaroundRow(
                    j.JobNumber, j.Customer.Name, $"{j.Brand} {j.DeviceName} {j.Model}".Trim(),
                    j.AssignedTechnicianName, j.CreatedAtUtc, j.DeliveredAtUtc,
                    j.ExpectedDeliveryDate, days, late, JobWorkflow.Describe(j.Status));
            })
            .OrderByDescending(r => r.Late)
            .ThenByDescending(r => r.Days ?? 0)
            .ToList();
    }

    public async Task<List<TechnicianPerformance>> TechniciansAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var jobs = await JobsInWindow(range)
            .Where(j => j.AssignedTechnicianId != null)
            .Select(j => new
            {
                j.Id, j.AssignedTechnicianId, j.AssignedTechnicianName, j.Status,
                j.CreatedAtUtc, j.DeliveredAtUtc, j.ExpectedDeliveryDate
            })
            .ToListAsync(ct);

        var diagnoses = await db.Diagnoses
            .Where(d => d.CreatedAtUtc >= range.StartUtc && d.CreatedAtUtc < range.EndUtc)
            .GroupBy(d => d.TechnicianId)
            .Select(g => new { TechnicianId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Revenue is credited to whoever held the job when it was ordered.
        var revenue = await db.SalesOrders
            .Where(o => o.RepairJobId != null
                        && o.CreatedAtUtc >= range.StartUtc && o.CreatedAtUtc < range.EndUtc)
            .Select(o => new { JobId = o.RepairJobId!.Value, o.TotalAmount })
            .ToListAsync(ct);

        var revenueByJob = revenue.GroupBy(r => r.JobId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.TotalAmount));

        return jobs
            .GroupBy(j => new { j.AssignedTechnicianId, j.AssignedTechnicianName })
            .Select(g =>
            {
                var done = g.Where(j => j.DeliveredAtUtc is not null).ToList();
                var turnarounds = done
                    .Select(j => (j.DeliveredAtUtc!.Value - j.CreatedAtUtc).TotalDays)
                    .Where(d => d >= 0).ToList();

                var lateDeliveries = done.Count(j =>
                    j.ExpectedDeliveryDate is { } promised
                    && DateOnly.FromDateTime(j.DeliveredAtUtc!.Value) > promised);

                return new TechnicianPerformance(
                    g.Key.AssignedTechnicianId!,
                    g.Key.AssignedTechnicianName ?? "(unnamed)",
                    g.Count(),
                    done.Count,
                    g.Count(j => JobWorkflow.Open.Contains(j.Status)),
                    diagnoses.FirstOrDefault(d => d.TechnicianId == g.Key.AssignedTechnicianId)?.Count ?? 0,
                    turnarounds.Count == 0 ? 0 : Math.Round(turnarounds.Average(), 1),
                    lateDeliveries,
                    g.Sum(j => revenueByJob.GetValueOrDefault(j.Id)));
            })
            .OrderByDescending(t => t.Delivered)
            .ToList();
    }

    public async Task<List<CustomerActivity>> CustomersAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var jobs = await JobsInWindow(range)
            .Include(j => j.Customer)
            .Select(j => new
            {
                j.CustomerId, j.Customer.Name, j.Customer.Organization, j.Customer.Phone,
                j.CreatedAtUtc, j.SerialNumber, j.Id
            })
            .ToListAsync(ct);

        var orders = await db.SalesOrders
            .Where(o => o.CreatedAtUtc >= range.StartUtc && o.CreatedAtUtc < range.EndUtc)
            .Select(o => new { o.CustomerId, o.TotalAmount, o.AmountPaid })
            .ToListAsync(ct);

        var billing = orders.GroupBy(o => o.CustomerId).ToDictionary(
            g => g.Key,
            g => (Billed: g.Sum(o => o.TotalAmount), Paid: g.Sum(o => o.AmountPaid)));

        return jobs
            .GroupBy(j => new { j.CustomerId, j.Name, j.Organization, j.Phone })
            .Select(g =>
            {
                var money = billing.GetValueOrDefault(g.Key.CustomerId);
                return new CustomerActivity(
                    g.Key.CustomerId, g.Key.Name, g.Key.Organization, g.Key.Phone,
                    g.Count(),
                    // Distinct serials, falling back to job count for unserialised kit.
                    g.Select(j => j.SerialNumber).Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct().Count() is var serials && serials > 0 ? serials : g.Count(),
                    g.Min(j => j.CreatedAtUtc), g.Max(j => j.CreatedAtUtc),
                    money.Billed, money.Paid, money.Billed - money.Paid);
            })
            .OrderByDescending(c => c.Billed)
            .ThenByDescending(c => c.Jobs)
            .ToList();
    }

    public async Task<List<FailureRow>> FailureAnalysisAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var jobs = await JobsInWindow(range)
            .Select(j => new
            {
                j.Id, j.Brand, j.Model, j.DeviceName, j.SerialNumber,
                j.CreatedAtUtc, j.DeliveredAtUtc
            })
            .ToListAsync(ct);

        var jobIds = jobs.Select(j => j.Id).ToList();

        var symptoms = await db.JobSymptoms
            .Where(js => jobIds.Contains(js.RepairJobId))
            .Select(js => new { js.RepairJobId, js.Symptom.Name })
            .ToListAsync(ct);

        var revenue = await db.SalesOrders
            .Where(o => o.RepairJobId != null && jobIds.Contains(o.RepairJobId.Value))
            .Select(o => new { JobId = o.RepairJobId!.Value, o.TotalAmount })
            .ToListAsync(ct);

        var revenueByJob = revenue.GroupBy(r => r.JobId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.TotalAmount));

        var symptomsByJob = symptoms.GroupBy(s => s.RepairJobId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToList());

        return jobs
            .GroupBy(j => new { j.Brand, j.Model, j.DeviceName })
            .Select(g =>
            {
                var turnarounds = g.Where(j => j.DeliveredAtUtc is not null)
                    .Select(j => (j.DeliveredAtUtc!.Value - j.CreatedAtUtc).TotalDays)
                    .Where(d => d >= 0).ToList();

                // A serial seen more than once is the same unit back again — the
                // number that tells you whether a repair actually held.
                var repeat = g.Where(j => !string.IsNullOrWhiteSpace(j.SerialNumber))
                    .GroupBy(j => j.SerialNumber)
                    .Count(s => s.Count() > 1);

                var top = g.SelectMany(j => symptomsByJob.GetValueOrDefault(j.Id, []))
                    .GroupBy(s => s)
                    .OrderByDescending(s => s.Count())
                    .Take(3)
                    .Select(s => $"{s.Key} ({s.Count()})")
                    .ToList();

                return new FailureRow(
                    string.IsNullOrWhiteSpace(g.Key.Brand) ? "(unspecified)" : g.Key.Brand,
                    g.Key.Model, g.Key.DeviceName, g.Count(), repeat,
                    turnarounds.Count == 0 ? 0 : Math.Round(turnarounds.Average(), 1),
                    g.Sum(j => revenueByJob.GetValueOrDefault(j.Id)),
                    top);
            })
            .OrderByDescending(f => f.Jobs)
            .ToList();
    }

    public async Task<List<SymptomFrequency>> SymptomsAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var jobIds = await JobsInWindow(range).Select(j => j.Id).ToListAsync(ct);

        var rows = await db.JobSymptoms
            .Where(js => jobIds.Contains(js.RepairJobId))
            .GroupBy(js => new { js.Symptom.Name, js.Symptom.Category })
            .Select(g => new { g.Key.Name, g.Key.Category, Count = g.Count() })
            .ToListAsync(ct);

        var total = rows.Sum(r => r.Count);

        return rows
            .OrderByDescending(r => r.Count)
            .Select(r => new SymptomFrequency(r.Name, r.Category, r.Count,
                total == 0 ? 0 : Math.Round(r.Count * 100m / total, 1)))
            .ToList();
    }

    /// <summary>
    /// What the workshop quotes versus what it pays. Margin is estimated against
    /// the part's average cost, since a quotation line doesn't carry a cost of its
    /// own.
    /// </summary>
    public async Task<List<PartUsageRow>> PartUsageAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var quoted = await db.QuotationItems
            .Include(i => i.Quotation)
            .Where(i => i.PartId != null
                        && i.Quotation.Date >= range.From && i.Quotation.Date <= range.To)
            .Select(i => new { PartId = i.PartId!.Value, i.Quantity, i.LineTotal })
            .ToListAsync(ct);

        var purchased = await db.PartPurchaseItems
            .Include(i => i.PartPurchase)
            .Where(i => i.PartPurchase.PurchasedOn >= range.From
                        && i.PartPurchase.PurchasedOn <= range.To)
            .Select(i => new { i.PartId, i.Quantity, i.LineTotal })
            .ToListAsync(ct);

        var partIds = quoted.Select(q => q.PartId)
            .Concat(purchased.Select(p => p.PartId)).Distinct().ToList();

        var parts = await db.Parts
            .Where(p => partIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Sku, p.LastPurchaseCost, p.AverageCost })
            .ToListAsync(ct);

        return parts.Select(p =>
        {
            var q = quoted.Where(x => x.PartId == p.Id).ToList();
            var b = purchased.Where(x => x.PartId == p.Id).ToList();

            var quantity = q.Sum(x => x.Quantity);
            var value = q.Sum(x => x.LineTotal);
            var cost = (p.AverageCost ?? p.LastPurchaseCost ?? 0) * quantity;

            return new PartUsageRow(
                p.Id, p.Name, p.Sku, quantity, value,
                p.LastPurchaseCost, p.AverageCost,
                Math.Round(value - cost, 2), q.Count,
                b.Sum(x => x.Quantity), b.Sum(x => x.LineTotal));
        })
        .OrderByDescending(r => r.QuotedValue)
        .ThenByDescending(r => r.PurchaseSpend)
        .ToList();
    }

    public async Task<List<SupplierSpendRow>> SupplierSpendAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var purchases = await db.PartPurchases
            .Include(p => p.Supplier)
            .Include(p => p.Items)
            .Where(p => p.PurchasedOn >= range.From && p.PurchasedOn <= range.To)
            .AsNoTracking()
            .ToListAsync(ct);

        return purchases
            .GroupBy(p => new { p.SupplierId, p.Supplier.Name })
            .Select(g => new SupplierSpendRow(
                g.Key.SupplierId, g.Key.Name, g.Count(),
                g.Sum(p => p.TotalAmount),
                g.Max(p => p.PurchasedOn),
                g.SelectMany(p => p.Items).Select(i => i.PartId).Distinct().Count()))
            .OrderByDescending(s => s.Spend)
            .ToList();
    }

    /// <summary>Unpaid orders, aged from the day they were raised.</summary>
    public async Task<List<ReceivableRow>> ReceivablesAsync(CancellationToken ct = default)
    {
        var orders = await db.SalesOrders
            .Include(o => o.Customer)
            .Where(o => o.PaymentStatus != PaymentStatus.Paid)
            .AsNoTracking()
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        return orders
            .Select(o =>
            {
                var age = (int)(now - o.CreatedAtUtc).TotalDays;
                return new ReceivableRow(
                    o.OrderNumber, o.Customer.Name, o.Customer.Phone, o.CreatedAtUtc, age,
                    o.TotalAmount, o.AmountPaid, o.TotalAmount - o.AmountPaid, Bucket(age));
            })
            .OrderByDescending(r => r.AgeDays)
            .ToList();

        static string Bucket(int age) => age switch
        {
            <= 30 => "Current (0–30)",
            <= 60 => "31–60 days",
            <= 90 => "61–90 days",
            _ => "Over 90 days"
        };
    }

    public async Task<List<CollectionRow>> CollectionsAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var rows = await db.Payments
            .Where(p => p.CreatedAtUtc >= range.StartUtc && p.CreatedAtUtc < range.EndUtc)
            .GroupBy(p => p.Method)
            .Select(g => new { Method = g.Key, Count = g.Count(), Amount = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        var total = rows.Sum(r => r.Amount);

        return rows
            .OrderByDescending(r => r.Amount)
            .Select(r => new CollectionRow(r.Method.ToString(), r.Count, r.Amount,
                total == 0 ? 0 : Math.Round(r.Amount * 100m / total, 1)))
            .ToList();
    }

    public async Task<List<DailyActivityRow>> DailyActivityAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var received = await db.RepairJobs
            .Where(j => j.CreatedAtUtc >= range.StartUtc && j.CreatedAtUtc < range.EndUtc)
            .Select(j => j.CreatedAtUtc).ToListAsync(ct);

        var delivered = await db.RepairJobs
            .Where(j => j.DeliveredAtUtc >= range.StartUtc && j.DeliveredAtUtc < range.EndUtc)
            .Select(j => j.DeliveredAtUtc!.Value).ToListAsync(ct);

        var quotations = await db.Quotations
            .Where(q => q.Date >= range.From && q.Date <= range.To)
            .Select(q => q.Date).ToListAsync(ct);

        var invoiced = await db.SalesOrders
            .Where(o => o.CreatedAtUtc >= range.StartUtc && o.CreatedAtUtc < range.EndUtc)
            .Select(o => new { o.CreatedAtUtc, o.TotalAmount }).ToListAsync(ct);

        var collected = await db.Payments
            .Where(p => p.CreatedAtUtc >= range.StartUtc && p.CreatedAtUtc < range.EndUtc)
            .Select(p => new { p.CreatedAtUtc, p.Amount }).ToListAsync(ct);

        var days = new List<DailyActivityRow>();
        for (var date = range.From; date <= range.To; date = date.AddDays(1))
        {
            var d = date;
            days.Add(new DailyActivityRow(
                d,
                received.Count(x => DateOnly.FromDateTime(x) == d),
                delivered.Count(x => DateOnly.FromDateTime(x) == d),
                quotations.Count(x => x == d),
                invoiced.Where(x => DateOnly.FromDateTime(x.CreatedAtUtc) == d).Sum(x => x.TotalAmount),
                collected.Where(x => DateOnly.FromDateTime(x.CreatedAtUtc) == d).Sum(x => x.Amount)));
        }

        return days;
    }

    public async Task<List<WarrantyRow>> WarrantyMixAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var intakes = await db.Intakes
            .Include(i => i.Jobs)
            .Where(i => i.ReceivedAtUtc >= range.StartUtc && i.ReceivedAtUtc < range.EndUtc)
            .AsNoTracking()
            .ToListAsync(ct);

        var jobIds = intakes.SelectMany(i => i.Jobs).Select(j => j.Id).ToList();

        var orders = await db.SalesOrders
            .Where(o => o.RepairJobId != null && jobIds.Contains(o.RepairJobId.Value))
            .Select(o => new { JobId = o.RepairJobId!.Value, o.TotalAmount })
            .ToListAsync(ct);

        var revenueByJob = orders.GroupBy(o => o.JobId)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.TotalAmount));

        var grouped = intakes
            .GroupBy(i => i.PaymentMethod)
            .Select(g => new
            {
                Basis = g.Key.ToString(),
                Intakes = g.Count(),
                Jobs = g.SelectMany(i => i.Jobs).Count(),
                Billed = g.SelectMany(i => i.Jobs).Sum(j => revenueByJob.GetValueOrDefault(j.Id))
            })
            .ToList();

        var totalJobs = grouped.Sum(g => g.Jobs);

        return grouped
            .OrderByDescending(g => g.Jobs)
            .Select(g => new WarrantyRow(g.Basis, g.Intakes, g.Jobs, g.Billed,
                totalJobs == 0 ? 0 : Math.Round(g.Jobs * 100m / totalJobs, 1)))
            .ToList();
    }

    public async Task<List<QuotationOutcomeRow>> QuotationOutcomesAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var quotations = await db.Quotations
            .Include(q => q.Customer)
            .Include(q => q.RepairJob)
            .Where(q => q.Date >= range.From && q.Date <= range.To)
            .AsNoTracking()
            .ToListAsync(ct);

        var ordered = await db.SalesOrders
            .Select(o => o.QuotationId).Distinct().ToListAsync(ct);

        return quotations
            .Select(q =>
            {
                var decidedAt = q.CustomerApprovedAtUtc ?? q.ManagerApprovedAtUtc;
                var toDecision = decidedAt is { } d
                    ? (int)(DateOnly.FromDateTime(d).DayNumber - q.Date.DayNumber)
                    : (int?)null;

                return new QuotationOutcomeRow(
                    q.QuotationNumber,
                    q.Customer?.Name ?? q.RepairJob?.Customer.Name ?? "(none)",
                    q.RepairJob?.JobNumber,
                    q.Date, q.TotalAmount,
                    q.CustomerApproval.ToString(), q.ManagerApproval.ToString(),
                    q.Status.ToString(), toDecision, ordered.Contains(q.Id));
            })
            .OrderByDescending(q => q.Date)
            .ToList();
    }

    // --- shared building blocks ---

    /// <summary>
    /// Jobs that touched the window: opened in it, or delivered in it. A job opened
    /// in March and delivered in April belongs in both months' turnaround figures.
    /// </summary>
    private IQueryable<RepairJob> JobsInWindow(ReportRange range)
    {
        var q = db.RepairJobs.Where(j =>
            (j.CreatedAtUtc >= range.StartUtc && j.CreatedAtUtc < range.EndUtc)
            || (j.DeliveredAtUtc != null
                && j.DeliveredAtUtc >= range.StartUtc && j.DeliveredAtUtc < range.EndUtc));

        return q;
    }

    private static double Median(IReadOnlyList<double> sorted) => sorted.Count switch
    {
        0 => 0,
        var n when n % 2 == 1 => Math.Round(sorted[n / 2], 1),
        var n => Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2, 1)
    };
}
