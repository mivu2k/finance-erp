using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Identity;

/// <summary>
/// A named sticker layout: how big the label is and which fields go on it.
/// </summary>
/// <remarks>
/// Lives in the shared identity database beside <see cref="CompanyProfile"/> and for
/// the same reason — label stock is a property of the printer on the desk, not of
/// one business module, and the same 38x25mm roll gets used for repair devices and
/// gate-pass items alike. Modules read templates through
/// <see cref="ILabelTemplateService"/> rather than reaching into this database.
/// <para>
/// Which fields are <em>available</em> is decided by the document type (see
/// <see cref="LabelFieldCatalog"/>); which of them are <em>used</em>, and in what
/// order, is this row. That split is what lets a user re-arrange a label without a
/// code change while still preventing them asking for a field the record doesn't have.
/// </para>
/// </remarks>
public class LabelTemplate
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which kind of record this layout is for — a key from
    /// <see cref="LabelDocumentTypes"/>. A template only offers, and only renders,
    /// fields that type actually publishes.
    /// </summary>
    public string DocumentType { get; set; } = string.Empty;

    public decimal WidthMm { get; set; } = 62;
    /// <summary>Null means the roll is continuous — height grows to fit the content.</summary>
    public decimal? HeightMm { get; set; }
    public decimal MarginMm { get; set; } = 3;

    /// <summary>
    /// Selected field keys in print order, comma-separated. Stored flat rather than
    /// as a child table because it is only ever read and written whole, and a plain
    /// column keeps the admin screen a single save.
    /// </summary>
    public string FieldKeys { get; set; } = string.Empty;

    public bool ShowTitle { get; set; } = true;
    public bool ShowCompanyName { get; set; } = true;
    public bool ShowBarcode { get; set; } = true;
    public bool ShowQrCode { get; set; }

    /// <summary>Multiplies every font size, for stock that runs small or large.</summary>
    public decimal FontScale { get; set; } = 1.0m;

    /// <summary>Used when a caller asks to print without naming a template.</summary>
    public bool IsDefault { get; set; }

    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    /// <summary>The selected keys, in order, with blanks dropped.</summary>
    public IReadOnlyList<string> SelectedFields() =>
        FieldKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The record kinds a label can be printed for.</summary>
public static class LabelDocumentTypes
{
    public const string RepairDevice = "repair.device";
    public const string GatePassItem = "gatepass.item";
    public const string InventoryItem = "inventory.item";

    /// <summary>The spine sticker on a tender or project folder.</summary>
    public const string TenderFile = "tender.file";

    public static IReadOnlyList<(string Key, string Name)> All =>
    [
        (RepairDevice, "Repair — device label"),
        (GatePassItem, "Gate Pass — item label"),
        (InventoryItem, "Inventory — item label"),
        (TenderFile, "Tender & Projects — file sticker")
    ];

    public static string Describe(string key) =>
        All.FirstOrDefault(t => t.Key == key).Name ?? key;
}

/// <param name="Key">Stable identifier stored in <see cref="LabelTemplate.FieldKeys"/>.</param>
/// <param name="Label">What the admin screen calls it.</param>
public record LabelField(string Key, string Label);

/// <summary>
/// What each document type can put on a label. Modules register their own set at
/// startup, so the admin screen never offers a field the renderer can't fill.
/// </summary>
public static class LabelFieldCatalog
{
    private static readonly Dictionary<string, IReadOnlyList<LabelField>> Fields = [];
    private static readonly Lock Gate = new();

    public static void Register(string documentType, IReadOnlyList<LabelField> fields)
    {
        lock (Gate) Fields[documentType] = fields;
    }

    public static IReadOnlyList<LabelField> For(string documentType)
    {
        lock (Gate) return Fields.GetValueOrDefault(documentType, []);
    }
}

public interface ILabelTemplateService
{
    Task<List<LabelTemplate>> ListAsync(string? documentType = null, CancellationToken ct = default);
    Task<LabelTemplate?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>The default for a document type, or null when none is set up yet.</summary>
    Task<LabelTemplate?> GetDefaultAsync(string documentType, CancellationToken ct = default);
    Task<LabelTemplate> SaveAsync(LabelTemplate template, string? modifiedBy = null,
        CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class LabelTemplateService(PlatformIdentityDbContext db) : ILabelTemplateService
{
    public async Task<List<LabelTemplate>> ListAsync(
        string? documentType = null, CancellationToken ct = default)
    {
        var q = db.LabelTemplates.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(documentType))
            q = q.Where(t => t.DocumentType == documentType);
        return await q.OrderBy(t => t.DocumentType).ThenBy(t => t.Name).ToListAsync(ct);
    }

    public Task<LabelTemplate?> GetAsync(int id, CancellationToken ct = default) =>
        db.LabelTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<LabelTemplate?> GetDefaultAsync(string documentType, CancellationToken ct = default) =>
        db.LabelTemplates.AsNoTracking()
            .Where(t => t.DocumentType == documentType)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<LabelTemplate> SaveAsync(
        LabelTemplate template, string? modifiedBy = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
            throw new InvalidOperationException("Template name is required.");
        if (string.IsNullOrWhiteSpace(template.DocumentType))
            throw new InvalidOperationException("Pick what kind of record this label is for.");
        if (template.WidthMm <= 0)
            throw new InvalidOperationException("Width must be positive.");
        if (template.HeightMm is <= 0)
            throw new InvalidOperationException("Height must be positive, or empty for a continuous roll.");
        if (template.FontScale is < 0.5m or > 3m)
            throw new InvalidOperationException("Font scale must be between 0.5 and 3.");

        template.ModifiedAtUtc = DateTime.UtcNow;
        template.ModifiedBy = modifiedBy;

        if (template.Id == 0) db.LabelTemplates.Add(template);
        else db.Entry(template).State = EntityState.Modified;

        await db.SaveChangesAsync(ct);

        // Exactly one default per document type, so printing without naming a
        // template is never ambiguous.
        if (template.IsDefault)
        {
            var others = await db.LabelTemplates
                .Where(t => t.DocumentType == template.DocumentType
                            && t.Id != template.Id && t.IsDefault)
                .ToListAsync(ct);
            if (others.Count > 0)
            {
                foreach (var other in others) other.IsDefault = false;
                await db.SaveChangesAsync(ct);
            }
        }

        return template;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var row = await db.LabelTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null) return;
        db.LabelTemplates.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
