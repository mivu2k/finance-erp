namespace Repair.Infrastructure.Reports;

/// <summary>The window and slice every report is run over.</summary>
public record ReportRange(DateOnly From, DateOnly To, int? TechnicianOnly = null)
{
    public static ReportRange ThisMonth()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return new ReportRange(new DateOnly(today.Year, today.Month, 1), today);
    }

    public DateTime StartUtc => From.ToDateTime(TimeOnly.MinValue);
    public DateTime EndUtc => To.AddDays(1).ToDateTime(TimeOnly.MinValue);
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>Headline numbers for the period — the top of the reports page.</summary>
public record WorkshopSummary(
    int IntakesReceived,
    int JobsOpened,
    int JobsDelivered,
    int JobsCancelled,
    int OpenAtPeriodEnd,
    int OverdueNow,
    int Unassigned,
    double AverageTurnaroundDays,
    double MedianTurnaroundDays,
    int QuotationsRaised,
    int QuotationsApproved,
    decimal QuotationValue,
    decimal OrderValue,
    decimal Collected,
    decimal Outstanding,
    decimal PartsSpend)
{
    /// <summary>Approved as a share of quotations raised — the workshop's win rate.</summary>
    public decimal ConversionRate => QuotationsRaised == 0
        ? 0
        : Math.Round(QuotationsApproved * 100m / QuotationsRaised, 1);
}

public record StatusCount(string Status, int Count, decimal Share);

/// <summary>How long jobs sat, bucketed the way a manager reads a backlog.</summary>
public record AgeingBucket(string Label, int Count, IReadOnlyList<string> JobNumbers);

public record TechnicianPerformance(
    string TechnicianId,
    string TechnicianName,
    int Assigned,
    int Delivered,
    int StillOpen,
    int Diagnoses,
    double AverageTurnaroundDays,
    int OverdueDelivered,
    decimal RevenueGenerated)
{
    /// <summary>Delivered on or before the promised date, as a percentage.</summary>
    public decimal OnTimeRate => Delivered == 0
        ? 0
        : Math.Round((Delivered - OverdueDelivered) * 100m / Delivered, 1);
}

public record TurnaroundRow(
    string JobNumber,
    string Customer,
    string Device,
    string? Technician,
    DateTime ReceivedAt,
    DateTime? DeliveredAt,
    DateOnly? PromisedOn,
    double? Days,
    bool Late,
    string Status);

public record CustomerActivity(
    int CustomerId,
    string Name,
    string? Organization,
    string Phone,
    int Jobs,
    int Devices,
    DateTime FirstSeen,
    DateTime LastSeen,
    decimal Billed,
    decimal Paid,
    decimal Outstanding)
{
    public bool IsRepeat => Jobs > 1;
}

/// <summary>Which equipment comes back, and what's wrong with it.</summary>
public record FailureRow(
    string Brand,
    string? Model,
    string DeviceName,
    int Jobs,
    int Repeat,
    double AverageTurnaroundDays,
    decimal Revenue,
    IReadOnlyList<string> TopSymptoms);

public record SymptomFrequency(string Symptom, string? Category, int Count, decimal Share);

public record PartUsageRow(
    int PartId,
    string PartName,
    string? Sku,
    decimal QuotedQuantity,
    decimal QuotedValue,
    decimal? LastCost,
    decimal? AverageCost,
    decimal EstimatedMargin,
    int TimesQuoted,
    decimal PurchasedQuantity,
    decimal PurchaseSpend);

public record SupplierSpendRow(
    int SupplierId,
    string SupplierName,
    int Purchases,
    decimal Spend,
    DateOnly? LastPurchase,
    int DistinctParts);

public record ReceivableRow(
    string OrderNumber,
    string Customer,
    string Phone,
    DateTime OrderedAt,
    int AgeDays,
    decimal Total,
    decimal Paid,
    decimal Balance,
    string Bucket);

public record CollectionRow(string Method, int Count, decimal Amount, decimal Share);

public record DailyActivityRow(
    DateOnly Date,
    int Received,
    int Delivered,
    int QuotationsRaised,
    decimal Invoiced,
    decimal Collected);

/// <summary>Warranty work carries no revenue, so it's tracked separately.</summary>
public record WarrantyRow(
    string PaymentBasis,
    int Intakes,
    int Jobs,
    decimal Billed,
    decimal Share);

public record QuotationOutcomeRow(
    string QuotationNumber,
    string Customer,
    string? JobNumber,
    DateOnly Date,
    decimal Total,
    string CustomerDecision,
    string ManagerDecision,
    string Status,
    int? DaysToDecision,
    bool Ordered);
