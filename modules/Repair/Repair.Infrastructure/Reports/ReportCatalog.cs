using Repair.Domain;

namespace Repair.Infrastructure.Reports;

/// <summary>Every report the module offers, in the order the reports page lists them.</summary>
public enum ReportKind
{
    Summary,
    Pipeline,
    Ageing,
    Turnaround,
    Technicians,
    QuotationOutcomes,
    Customers,
    FailureAnalysis,
    Symptoms,
    PartUsage,
    SupplierSpend,
    Receivables,
    Collections,
    DailyActivity,
    WarrantyMix
}

public record ReportDefinition(
    ReportKind Kind,
    string Title,
    string Description,
    string Icon,
    /// <summary>False for reports that are a snapshot of now, not of a period.</summary>
    bool UsesDateRange = true);

public static class ReportCatalog
{
    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(ReportKind.Summary, "Workshop Summary",
            "Headline numbers for the period: intake, throughput, turnaround, money.",
            "Dashboard"),
        new(ReportKind.Pipeline, "Pipeline",
            "Where every job in the workshop stands right now.",
            "AccountTree", UsesDateRange: false),
        new(ReportKind.Ageing, "Job Ageing",
            "Open jobs by how long they've been sitting — catches forgotten devices.",
            "HourglassBottom", UsesDateRange: false),
        new(ReportKind.Turnaround, "Turnaround",
            "Every job with days taken against the date promised, late ones first.",
            "Timer"),
        new(ReportKind.Technicians, "Technician Performance",
            "Jobs handled, average turnaround, on-time rate and revenue per technician.",
            "Engineering"),
        new(ReportKind.QuotationOutcomes, "Quotation Outcomes",
            "Every quotation with both approvals, days to decision and whether it converted.",
            "RequestQuote"),
        new(ReportKind.Customers, "Customer Activity",
            "Jobs, spend and outstanding balance per customer, with repeat business flagged.",
            "People"),
        new(ReportKind.FailureAnalysis, "Brand & Model Failures",
            "Which equipment comes back, how often it repeats, and the top faults for each.",
            "Warning"),
        new(ReportKind.Symptoms, "Symptom Frequency",
            "The faults reported most often across the period.",
            "BugReport"),
        new(ReportKind.PartUsage, "Parts Usage & Margin",
            "What was quoted against what it cost, with estimated margin per part.",
            "Inventory2"),
        new(ReportKind.SupplierSpend, "Supplier Spend",
            "Purchases and spend per supplier.",
            "LocalShipping"),
        new(ReportKind.Receivables, "Aged Receivables",
            "Unpaid orders bucketed by age. A snapshot of now, not of a period.",
            "MoneyOff", UsesDateRange: false),
        new(ReportKind.Collections, "Collections by Method",
            "What was taken in, split by cash, card and transfer.",
            "Payments"),
        new(ReportKind.DailyActivity, "Daily Activity Log",
            "Day by day: received, delivered, quoted, invoiced and collected.",
            "CalendarMonth"),
        new(ReportKind.WarrantyMix, "Warranty vs Paid",
            "How much of the workload carries no revenue.",
            "Shield")
    ];

    public static ReportDefinition Find(ReportKind kind) => All.First(r => r.Kind == kind);
}

/// <summary>
/// Turns a report's typed rows into the flat table shape the exporters and the
/// screen both render. One place decides column order and formatting, so the
/// Excel, the PDF and the page can never disagree.
/// </summary>
public class ReportTableBuilder(IRepairReportService reports)
{
    private static string N(decimal value) => value.ToString("0.##");
    private static string M(decimal value) => value.ToString("N2");
    private static string D(double value) => value.ToString("0.#");

    public async Task<IReadOnlyList<ReportTable>> BuildAsync(
        ReportKind kind, ReportRange range, CancellationToken ct = default) => kind switch
    {
        ReportKind.Summary => await SummaryAsync(range, ct),
        ReportKind.Pipeline => [await PipelineAsync(ct)],
        ReportKind.Ageing => [await AgeingAsync(ct)],
        ReportKind.Turnaround => [await TurnaroundAsync(range, ct)],
        ReportKind.Technicians => [await TechniciansAsync(range, ct)],
        ReportKind.QuotationOutcomes => [await QuotationsAsync(range, ct)],
        ReportKind.Customers => [await CustomersAsync(range, ct)],
        ReportKind.FailureAnalysis => [await FailuresAsync(range, ct)],
        ReportKind.Symptoms => [await SymptomsAsync(range, ct)],
        ReportKind.PartUsage => [await PartsAsync(range, ct)],
        ReportKind.SupplierSpend => [await SuppliersAsync(range, ct)],
        ReportKind.Receivables => [await ReceivablesAsync(ct)],
        ReportKind.Collections => [await CollectionsAsync(range, ct)],
        ReportKind.DailyActivity => [await DailyAsync(range, ct)],
        ReportKind.WarrantyMix => [await WarrantyAsync(range, ct)],
        _ => []
    };

    /// <summary>Everything at once — the "full pack" export a manager takes to a meeting.</summary>
    public async Task<IReadOnlyList<ReportTable>> BuildAllAsync(
        ReportRange range, CancellationToken ct = default)
    {
        var tables = new List<ReportTable>();
        foreach (var definition in ReportCatalog.All)
            tables.AddRange(await BuildAsync(definition.Kind, range, ct));
        return tables;
    }

    private async Task<List<ReportTable>> SummaryAsync(ReportRange range, CancellationToken ct)
    {
        var s = await reports.SummaryAsync(range, ct);

        List<IReadOnlyList<string>> Rows(params (string Label, string Value)[] pairs) =>
            pairs.Select(p => (IReadOnlyList<string>)new[] { p.Label, p.Value }).ToList();

        return
        [
            new ReportTable("Workshop Summary", ["Measure", "Value"],
                Rows(
                    ("Intakes received", s.IntakesReceived.ToString()),
                    ("Jobs opened", s.JobsOpened.ToString()),
                    ("Jobs delivered", s.JobsDelivered.ToString()),
                    ("Jobs cancelled", s.JobsCancelled.ToString()),
                    ("Open in the workshop now", s.OpenAtPeriodEnd.ToString()),
                    ("Overdue now", s.OverdueNow.ToString()),
                    ("Open and unassigned", s.Unassigned.ToString()),
                    ("Average turnaround (days)", D(s.AverageTurnaroundDays)),
                    ("Median turnaround (days)", D(s.MedianTurnaroundDays)),
                    ("Quotations raised", s.QuotationsRaised.ToString()),
                    ("Quotations approved", s.QuotationsApproved.ToString()),
                    ("Conversion rate (%)", N(s.ConversionRate)),
                    ("Quotation value", M(s.QuotationValue)),
                    ("Invoiced", M(s.OrderValue)),
                    ("Collected", M(s.Collected)),
                    ("Outstanding (all time)", M(s.Outstanding)),
                    ("Parts purchased", M(s.PartsSpend))),
                [false, true])
        ];
    }

    private async Task<ReportTable> PipelineAsync(CancellationToken ct)
    {
        var rows = await reports.PipelineAsync(ct);
        return new ReportTable("Pipeline", ["Status", "Jobs", "Share %"],
            rows.Select(r => (IReadOnlyList<string>)
                new[] { r.Status, r.Count.ToString(), N(r.Share) }).ToList(),
            [false, true, true]);
    }

    private async Task<ReportTable> AgeingAsync(CancellationToken ct)
    {
        var rows = await reports.AgeingAsync(ct);
        return new ReportTable("Open Job Ageing", ["Age", "Jobs", "Job numbers"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Label, r.Count.ToString(),
                string.Join(", ", r.JobNumbers.Take(15)) +
                    (r.JobNumbers.Count > 15 ? $" (+{r.JobNumbers.Count - 15} more)" : "")
            }).ToList(),
            [false, true, false]);
    }

    private async Task<ReportTable> TurnaroundAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.TurnaroundAsync(range, ct);
        return new ReportTable("Turnaround",
            ["Job", "Customer", "Device", "Technician", "Received", "Delivered",
             "Promised", "Days", "Late", "Status"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.JobNumber, r.Customer, r.Device, r.Technician ?? "—",
                r.ReceivedAt.ToString("yyyy-MM-dd"),
                r.DeliveredAt?.ToString("yyyy-MM-dd") ?? "",
                r.PromisedOn?.ToString("yyyy-MM-dd") ?? "",
                r.Days?.ToString("0.#") ?? "", r.Late ? "LATE" : "", r.Status
            }).ToList(),
            [false, false, false, false, false, false, false, true, false, false]);
    }

    private async Task<ReportTable> TechniciansAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.TechniciansAsync(range, ct);
        return new ReportTable("Technician Performance",
            ["Technician", "Assigned", "Delivered", "Open", "Diagnoses",
             "Avg days", "Late", "On-time %", "Revenue"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.TechnicianName, r.Assigned.ToString(), r.Delivered.ToString(),
                r.StillOpen.ToString(), r.Diagnoses.ToString(), D(r.AverageTurnaroundDays),
                r.OverdueDelivered.ToString(), N(r.OnTimeRate), M(r.RevenueGenerated)
            }).ToList(),
            [false, true, true, true, true, true, true, true, true]);
    }

    private async Task<ReportTable> QuotationsAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.QuotationOutcomesAsync(range, ct);
        return new ReportTable("Quotation Outcomes",
            ["Quotation", "Customer", "Job", "Date", "Total", "Customer",
             "Manager", "Status", "Days to decide", "Ordered"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.QuotationNumber, r.Customer, r.JobNumber ?? "", r.Date.ToString("yyyy-MM-dd"),
                M(r.Total), r.CustomerDecision, r.ManagerDecision, r.Status,
                r.DaysToDecision?.ToString() ?? "", r.Ordered ? "Yes" : "No"
            }).ToList(),
            [false, false, false, false, true, false, false, false, true, false]);
    }

    private async Task<ReportTable> CustomersAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.CustomersAsync(range, ct);
        return new ReportTable("Customer Activity",
            ["Customer", "Organisation", "Phone", "Jobs", "Devices", "First seen",
             "Last seen", "Billed", "Paid", "Outstanding", "Repeat"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Name, r.Organization ?? "", r.Phone, r.Jobs.ToString(), r.Devices.ToString(),
                r.FirstSeen.ToString("yyyy-MM-dd"), r.LastSeen.ToString("yyyy-MM-dd"),
                M(r.Billed), M(r.Paid), M(r.Outstanding), r.IsRepeat ? "Yes" : ""
            }).ToList(),
            [false, false, false, true, true, false, false, true, true, true, false]);
    }

    private async Task<ReportTable> FailuresAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.FailureAnalysisAsync(range, ct);
        return new ReportTable("Brand & Model Failures",
            ["Brand", "Model", "Device", "Jobs", "Repeat units", "Avg days",
             "Revenue", "Top symptoms"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Brand, r.Model ?? "", r.DeviceName, r.Jobs.ToString(), r.Repeat.ToString(),
                D(r.AverageTurnaroundDays), M(r.Revenue), string.Join("; ", r.TopSymptoms)
            }).ToList(),
            [false, false, false, true, true, true, true, false]);
    }

    private async Task<ReportTable> SymptomsAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.SymptomsAsync(range, ct);
        return new ReportTable("Symptom Frequency",
            ["Symptom", "Category", "Reported", "Share %"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Symptom, r.Category ?? "", r.Count.ToString(), N(r.Share)
            }).ToList(),
            [false, false, true, true]);
    }

    private async Task<ReportTable> PartsAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.PartUsageAsync(range, ct);
        return new ReportTable("Parts Usage & Margin",
            ["Part", "SKU", "Qty quoted", "Quoted value", "Last cost", "Avg cost",
             "Est. margin", "Times quoted", "Qty bought", "Purchase spend"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.PartName, r.Sku ?? "", N(r.QuotedQuantity), M(r.QuotedValue),
                r.LastCost is { } lc ? M(lc) : "", r.AverageCost is { } ac ? M(ac) : "",
                M(r.EstimatedMargin), r.TimesQuoted.ToString(),
                N(r.PurchasedQuantity), M(r.PurchaseSpend)
            }).ToList(),
            [false, false, true, true, true, true, true, true, true, true]);
    }

    private async Task<ReportTable> SuppliersAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.SupplierSpendAsync(range, ct);
        return new ReportTable("Supplier Spend",
            ["Supplier", "Purchases", "Spend", "Distinct parts", "Last purchase"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.SupplierName, r.Purchases.ToString(), M(r.Spend),
                r.DistinctParts.ToString(), r.LastPurchase?.ToString("yyyy-MM-dd") ?? ""
            }).ToList(),
            [false, true, true, true, false]);
    }

    private async Task<ReportTable> ReceivablesAsync(CancellationToken ct)
    {
        var rows = await reports.ReceivablesAsync(ct);
        return new ReportTable("Aged Receivables",
            ["Order", "Customer", "Phone", "Raised", "Age (days)",
             "Total", "Paid", "Balance", "Bucket"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.OrderNumber, r.Customer, r.Phone, r.OrderedAt.ToString("yyyy-MM-dd"),
                r.AgeDays.ToString(), M(r.Total), M(r.Paid), M(r.Balance), r.Bucket
            }).ToList(),
            [false, false, false, false, true, true, true, true, false]);
    }

    private async Task<ReportTable> CollectionsAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.CollectionsAsync(range, ct);
        return new ReportTable("Collections by Method",
            ["Method", "Payments", "Amount", "Share %"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Method, r.Count.ToString(), M(r.Amount), N(r.Share)
            }).ToList(),
            [false, true, true, true]);
    }

    private async Task<ReportTable> DailyAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.DailyActivityAsync(range, ct);
        return new ReportTable("Daily Activity",
            ["Date", "Day", "Received", "Delivered", "Quoted", "Invoiced", "Collected"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Date.ToString("yyyy-MM-dd"), r.Date.DayOfWeek.ToString()[..3],
                r.Received.ToString(), r.Delivered.ToString(), r.QuotationsRaised.ToString(),
                M(r.Invoiced), M(r.Collected)
            }).ToList(),
            [false, false, true, true, true, true, true]);
    }

    private async Task<ReportTable> WarrantyAsync(ReportRange range, CancellationToken ct)
    {
        var rows = await reports.WarrantyMixAsync(range, ct);
        return new ReportTable("Warranty vs Paid",
            ["Payment basis", "Intakes", "Jobs", "Billed", "Share of jobs %"],
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.PaymentBasis, r.Intakes.ToString(), r.Jobs.ToString(), M(r.Billed), N(r.Share)
            }).ToList(),
            [false, true, true, true, true]);
    }
}
