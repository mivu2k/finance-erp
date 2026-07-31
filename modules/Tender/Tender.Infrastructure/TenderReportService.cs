using Microsoft.EntityFrameworkCore;
using Tender.Domain;

namespace Tender.Infrastructure;

/// <summary>One entry in the reports menu.</summary>
public record TenderReportDefinition(string Key, string Title, bool Landscape = false);

public interface ITenderReportService
{
    IReadOnlyList<TenderReportDefinition> Catalog { get; }
    TenderReportDefinition? Find(string key);
    Task<TenderReport> BuildAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Builds every tender report into the same flat <see cref="TenderReport"/> shape
/// so the on-screen tables, the PDF and any future export can't drift apart.
/// </summary>
public class TenderReportService(TenderDbContext db) : ITenderReportService
{
    public IReadOnlyList<TenderReportDefinition> Catalog { get; } =
    [
        new("pipeline", "Tender Pipeline by Status"),
        new("win-loss", "Win / Loss Analysis"),
        new("authority", "Value by Issuing Authority"),
        new("security-register", "Security / Guarantee Register", Landscape: true),
        new("expiry", "Guarantees Expiring Soon"),
        new("bank-exposure", "Bank-wise Exposure")
    ];

    public TenderReportDefinition? Find(string key) =>
        Catalog.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public async Task<TenderReport> BuildAsync(string key, CancellationToken ct = default) => key switch
    {
        "pipeline" => await PipelineAsync(ct),
        "win-loss" => await WinLossAsync(ct),
        "authority" => await AuthorityAsync(ct),
        "security-register" => await SecurityRegisterAsync(ct),
        "expiry" => await ExpiryAsync(ct),
        "bank-exposure" => await BankExposureAsync(ct),
        _ => throw new InvalidOperationException($"Unknown report '{key}'.")
    };

    private async Task<TenderReport> PipelineAsync(CancellationToken ct)
    {
        var tenders = await db.Tenders.AsNoTracking().ToListAsync(ct);
        var rows = tenders.GroupBy(t => t.Status)
            .OrderBy(g => g.Key)
            .Select(g => new[] { g.Key.ToString(), g.Count().ToString(), g.Sum(t => t.EstimatedValue).ToString("N2") })
            .ToList();

        return new TenderReport("Tender Pipeline by Status", DateTime.Now.ToString("yyyy-MM-dd"),
            [new("Status", 3), new("Count", 1, Right: true), new("Estimated Value", 2, Right: true)],
            rows,
            [ "Total", tenders.Count.ToString(), tenders.Sum(t => t.EstimatedValue).ToString("N2") ]);
    }

    private async Task<TenderReport> WinLossAsync(CancellationToken ct)
    {
        var decided = await db.Tenders.AsNoTracking()
            .Where(t => t.Status == TenderStatus.Won || t.Status == TenderStatus.Lost)
            .OrderByDescending(t => t.AwardDate ?? t.SubmissionDeadline ?? DateOnly.MinValue)
            .ToListAsync(ct);

        var rows = decided.Select(t => new[]
        {
            t.TenderNumber, t.Title, t.IssuingAuthority, t.Status.ToString(),
            t.EstimatedValue.ToString("N2"), (t.AwardedValue ?? t.L1Amount)?.ToString("N2") ?? "-"
        }).ToList();

        var won = decided.Count(t => t.Status == TenderStatus.Won);
        var winRate = decided.Count == 0 ? 0 : won * 100m / decided.Count;

        return new TenderReport("Win / Loss Analysis", $"Win rate: {winRate:N0}% ({won}/{decided.Count})",
            [new("Reference", 2), new("Title", 3), new("Authority", 3), new("Outcome", 1), new("Estimated", 2, Right: true), new("Final", 2, Right: true)],
            rows);
    }

    private async Task<TenderReport> AuthorityAsync(CancellationToken ct)
    {
        var tenders = await db.Tenders.AsNoTracking().ToListAsync(ct);
        var rows = tenders.GroupBy(t => t.IssuingAuthority)
            .OrderByDescending(g => g.Sum(t => t.EstimatedValue))
            .Select(g => new[]
            {
                g.Key, g.Count().ToString(),
                g.Count(t => t.Status == TenderStatus.Won).ToString(),
                g.Sum(t => t.EstimatedValue).ToString("N2")
            }).ToList();

        return new TenderReport("Value by Issuing Authority", DateTime.Now.ToString("yyyy-MM-dd"),
            [new("Authority", 4), new("Tenders", 1, Right: true), new("Won", 1, Right: true), new("Estimated Value", 2, Right: true)],
            rows);
    }

    private async Task<TenderReport> SecurityRegisterAsync(CancellationToken ct)
    {
        var guarantees = await db.Guarantees.Include(g => g.TenderRecord).AsNoTracking()
            .OrderByDescending(g => g.IssueDate).ToListAsync(ct);

        var rows = guarantees.Select(g => new[]
        {
            g.TenderRecord.TenderNumber, g.Type.ToString(), g.InstrumentType.ToString(),
            $"{g.BankName} {g.GuaranteeNumber}", g.Amount.ToString("N2"),
            g.IssueDate.ToString("yyyy-MM-dd"), g.ExpiryDate.ToString("yyyy-MM-dd"), g.Status.ToString()
        }).ToList();

        return new TenderReport("Security / Guarantee Register", $"{guarantees.Count} instruments",
            [new("Tender", 2), new("Type", 2), new("Instrument", 2), new("Bank / number", 3),
             new("Amount", 2, Right: true), new("Issue", 1), new("Expiry", 1), new("Status", 1)],
            rows,
            [ "Total", "", "", "", guarantees.Sum(g => g.Amount).ToString("N2"), "", "", "" ]);
    }

    private async Task<TenderReport> ExpiryAsync(CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60);
        var guarantees = await db.Guarantees.Include(g => g.TenderRecord).AsNoTracking()
            .Where(g => g.Status == GuaranteeStatus.Active && g.ExpiryDate <= cutoff)
            .OrderBy(g => g.ExpiryDate).ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = guarantees.Select(g => new[]
        {
            g.TenderRecord.TenderNumber, g.Type.ToString(), $"{g.BankName} {g.GuaranteeNumber}",
            g.Amount.ToString("N2"), g.ExpiryDate.ToString("yyyy-MM-dd"),
            g.ExpiryDate < today ? "OVERDUE" : $"{(g.ExpiryDate.DayNumber - today.DayNumber)}d"
        }).ToList();

        return new TenderReport("Guarantees Expiring Within 60 Days", $"As of {today:yyyy-MM-dd}",
            [new("Tender", 2), new("Type", 2), new("Bank / number", 3), new("Amount", 2, Right: true),
             new("Expiry", 1), new("Remaining", 1, Right: true)],
            rows);
    }

    private async Task<TenderReport> BankExposureAsync(CancellationToken ct)
    {
        var guarantees = await db.Guarantees.AsNoTracking()
            .Where(g => g.Status == GuaranteeStatus.Active).ToListAsync(ct);

        var rows = guarantees.GroupBy(g => string.IsNullOrWhiteSpace(g.BankName) ? "(unspecified)" : g.BankName)
            .OrderByDescending(g => g.Sum(x => x.Amount))
            .Select(g => new[] { g.Key, g.Count().ToString(), g.Sum(x => x.Amount).ToString("N2") })
            .ToList();

        return new TenderReport("Bank-wise Exposure (Active Securities)", DateTime.Now.ToString("yyyy-MM-dd"),
            [new("Bank", 4), new("Instruments", 1, Right: true), new("Amount", 2, Right: true)],
            rows,
            [ "Total", guarantees.Count.ToString(), guarantees.Sum(g => g.Amount).ToString("N2") ]);
    }
}
