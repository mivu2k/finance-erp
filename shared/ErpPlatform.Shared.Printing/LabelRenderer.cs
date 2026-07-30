using ErpPlatform.Shared.Kernel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ErpPlatform.Shared.Printing;

/// <summary>What a label needs to know about one record, independent of which module owns it.</summary>
/// <param name="Title">Heading line — usually the device or item name.</param>
/// <param name="Code">The number the barcode and QR encode, and what a scan resolves.</param>
/// <param name="Fields">
/// Every field the record can offer, keyed the same way the catalog advertises them.
/// The template decides which of these actually print, so passing extras costs nothing.
/// </param>
public record LabelData(
    string Title,
    string? Code,
    IReadOnlyDictionary<string, string?> Fields);

/// <summary>
/// Draws a sticker from a user-defined template, so size and content are configuration
/// rather than code.
/// </summary>
/// <remarks>
/// Every module prints labels on the same roll, so the drawing lives here once instead
/// of each print service growing its own. The renderer never decides <em>what</em> goes
/// on a label — it takes the template's chosen field keys in order and looks each one up
/// in <see cref="LabelData.Fields"/>, skipping anything blank.
/// </remarks>
public static class LabelRenderer
{
    /// <remarks>
    /// Set here rather than relying on a module's print service having been
    /// constructed first: this is a static class, so nothing else guarantees the
    /// licence is configured before the first label is drawn, and QuestPDF throws
    /// without it.
    /// </remarks>
    static LabelRenderer() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Fields the caller supplies as blank are dropped rather than printed empty.</summary>
    public static byte[] Render(
        LabelTemplateSpec template, IReadOnlyList<LabelData> labels, CompanyBranding company) =>
        Document.Create(doc =>
        {
            foreach (var label in labels)
                doc.Page(page => Draw(page, template, label, company));
        }).GeneratePdf();

    private static void Draw(
        PageDescriptor page, LabelTemplateSpec t, LabelData label, CompanyBranding company)
    {
        // A fixed height is a die-cut sheet; no height means a continuous roll that
        // grows with the content, which is what most label printers feed.
        if (t.HeightMm is { } h)
            page.Size((float)t.WidthMm, (float)h, Unit.Millimetre);
        else
            page.ContinuousSize((float)t.WidthMm, Unit.Millimetre);

        page.Margin((float)t.MarginMm, Unit.Millimetre);

        // Labels come off the same thermal head as the receipts, so the same
        // bold-and-black reasoning applies (see Letterhead.Thermal).
        var scale = (float)t.FontScale;
        page.DefaultTextStyle(s => Letterhead.Thermal(s).FontSize(7.5f * scale));

        // ScaleToFit because both the size and the field list are user input: someone
        // will tick eight fields onto a 38x25mm sticker, and shrinking to fit is a
        // far better answer than throwing a layout exception at the printer. On a
        // label that already fits it changes nothing.
        page.Content().ScaleToFit().Column(col =>
        {
            var drew = false;

            if (t.ShowCompanyName && !string.IsNullOrWhiteSpace(company.Name))
            {
                col.Item().Text(company.Name).FontSize(6.5f * scale);
                drew = true;
            }

            if (t.ShowTitle && !string.IsNullOrWhiteSpace(label.Title))
            {
                col.Item().Text(label.Title).FontSize(9.5f * scale);
                drew = true;
            }

            foreach (var key in t.FieldKeys)
            {
                if (!label.Fields.TryGetValue(key, out var value)) continue;
                if (string.IsNullOrWhiteSpace(value)) continue;
                col.Item().Text(value).FontSize(7.5f * scale);
                drew = true;
            }

            if (!string.IsNullOrWhiteSpace(label.Code))
            {
                // Barcode() stretches to its container; BarcodeFixed() sets its own
                // width and would overflow a narrow label. See the note in CLAUDE.md.
                if (t.ShowBarcode)
                    col.Item().PaddingTop(2 * scale)
                        .Element(c => c.Barcode(label.Code, 22 * scale));

                if (t.ShowQrCode)
                    col.Item().PaddingTop(2 * scale).AlignCenter()
                        .Element(c => c.QrCode(label.Code, 46 * scale));

                col.Item().AlignCenter().Text(label.Code).FontSize(7f * scale);
                drew = true;
            }

            // A template with everything switched off leaves the page with no content
            // at all, and a continuous roll then has zero height — which fails inside
            // Skia rather than surfacing as a layout error. One blank line keeps the
            // page valid so a misconfigured template prints empty instead of crashing.
            if (!drew) col.Item().Text(" ");
        });
    }
}

/// <summary>
/// The rendering-relevant half of a saved template.
/// </summary>
/// <remarks>
/// Shared.Printing deliberately does not reference Shared.Identity — printing is a
/// leaf that draws, not something that reads configuration — so the caller flattens
/// a stored <c>LabelTemplate</c> into this, exactly as <c>CompanyProfile</c> is
/// flattened into <see cref="CompanyBranding"/>.
/// </remarks>
public record LabelTemplateSpec(
    decimal WidthMm,
    decimal? HeightMm,
    decimal MarginMm,
    IReadOnlyList<string> FieldKeys,
    bool ShowTitle,
    bool ShowCompanyName,
    bool ShowBarcode,
    bool ShowQrCode,
    decimal FontScale)
{
    /// <summary>
    /// What a module prints when no template has been set up yet: the 62mm roll the
    /// hardcoded labels used, so behaviour is unchanged until someone configures one.
    /// </summary>
    public static LabelTemplateSpec Fallback(IReadOnlyList<string> fieldKeys) =>
        new(62, null, 3, fieldKeys, true, true, true, false, 1.0m);
}
