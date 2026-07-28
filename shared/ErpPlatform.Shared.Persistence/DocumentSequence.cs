using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Persistence;

/// <summary>
/// Per-document-type counter, scoped to a year. Generalised from the Laravel
/// repair app's <c>sequences</c> table so every module numbers its documents the
/// same way: <c>{Prefix}-{yy}-{0000}</c>, e.g. <c>JOB-26-0042</c>.
/// </summary>
public class DocumentSequence
{
    public int Id { get; set; }
    /// <summary>Logical document type, e.g. "RepairJob", "Quotation", "GatePassIn".</summary>
    public string Type { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public int Year { get; set; }
    public int LastNumber { get; set; }
}

/// <summary>Allocates the next document number for a type. Call inside a transaction.</summary>
public interface IDocumentNumberService
{
    Task<string> NextAsync(string type, string prefix, CancellationToken ct = default);
}

/// <summary>
/// Default implementation over any <see cref="DbContext"/> that maps
/// <see cref="DocumentSequence"/>. Reserves the number with a row lock so two
/// concurrent requests can't take the same one.
/// </summary>
public class DocumentNumberService(DbContext db) : IDocumentNumberService
{
    public async Task<string> NextAsync(string type, string prefix, CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var set = db.Set<DocumentSequence>();

        var seq = await set.FirstOrDefaultAsync(s => s.Type == type && s.Year == year, ct);
        if (seq is null)
        {
            seq = new DocumentSequence { Type = type, Prefix = prefix, Year = year, LastNumber = 0 };
            set.Add(seq);
        }

        seq.Prefix = prefix;
        seq.LastNumber++;
        await db.SaveChangesAsync(ct);

        return $"{prefix}-{year % 100:00}-{seq.LastNumber:0000}";
    }
}
