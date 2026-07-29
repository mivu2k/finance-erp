using ClosedXML.Excel;
using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Repair.Infrastructure.Reports;

/// <summary>A rendered table: a title, headers, and rows already turned into text.</summary>
public record ReportTable(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<bool>? RightAlign = null);

public interface IReportExportService
{
    byte[] ToExcel(string workbookTitle, IReadOnlyList<ReportTable> tables);
    byte[] ToPdf(string title, string subtitle, CompanyBranding company,
        IReadOnlyList<ReportTable> tables);
}

/// <summary>
/// Renders any report as Excel or PDF. Reports are shaped into
/// <see cref="ReportTable"/> by the caller so this stays one implementation
/// rather than one per report.
/// </summary>
public class ReportExportService : IReportExportService
{
    static ReportExportService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] ToExcel(string workbookTitle, IReadOnlyList<ReportTable> tables)
    {
        using var book = new XLWorkbook();

        foreach (var table in tables)
        {
            var sheet = book.Worksheets.Add(SheetName(table.Title, book));

            sheet.Cell(1, 1).Value = table.Title;
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(1, 1).Style.Font.FontSize = 13;

            for (var c = 0; c < table.Headers.Count; c++)
                sheet.Cell(3, c + 1).Value = table.Headers[c];

            sheet.Row(3).Style.Font.Bold = true;
            sheet.Row(3).Style.Fill.BackgroundColor = XLColor.LightGray;

            for (var r = 0; r < table.Rows.Count; r++)
                for (var c = 0; c < table.Rows[r].Count; c++)
                {
                    var cell = sheet.Cell(r + 4, c + 1);
                    var text = table.Rows[r][c];

                    // Let Excel treat numbers as numbers so totals and charts work.
                    if (decimal.TryParse(text, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var number)
                        && text.Trim().Length > 0)
                        cell.Value = number;
                    else
                        cell.Value = text;
                }

            if (table.Rows.Count > 0)
                sheet.Range(3, 1, 3 + table.Rows.Count, table.Headers.Count).SetAutoFilter();

            sheet.Columns(1, Math.Max(1, table.Headers.Count)).AdjustToContents();
            sheet.SheetView.FreezeRows(3);
        }

        if (!book.Worksheets.Any()) book.Worksheets.Add("Empty");

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] ToPdf(
        string title, string subtitle, CompanyBranding company, IReadOnlyList<ReportTable> tables) =>
        Document.Create(doc => doc.Page(page =>
        {
            // Reports are wide; landscape keeps columns readable.
            page.Size(PageSizes.A4.Landscape());
            page.Margin(1.2f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(8));

            page.Header().CompanyHeader(company, title, right => right.Width(220)
                .AlignRight().Column(c =>
                {
                    c.Item().Text(subtitle).FontSize(9);
                    c.Item().Text($"Printed {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(7)
                        .FontColor(Colors.Grey.Darken1);
                }));

            page.Content().PaddingVertical(8).Column(col =>
            {
                foreach (var table in tables)
                {
                    col.Item().PaddingTop(10).Text(table.Title).FontSize(10).SemiBold();

                    if (table.Rows.Count == 0)
                    {
                        col.Item().PaddingTop(3).Text("No data for this period.")
                            .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
                        continue;
                    }

                    col.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            // First column carries names and gets the extra room.
                            for (var i = 0; i < table.Headers.Count; i++)
                                c.RelativeColumn(i == 0 ? 2.4f : 1);
                        });

                        t.Header(h =>
                        {
                            for (var i = 0; i < table.Headers.Count; i++)
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(3);
                                if (Right(table, i)) cell = cell.AlignRight();
                                cell.Text(table.Headers[i]).SemiBold().FontSize(8);
                            }
                        });

                        foreach (var row in table.Rows)
                            for (var i = 0; i < row.Count; i++)
                            {
                                var cell = t.Cell().BorderBottom(0.4f)
                                    .BorderColor(Colors.Grey.Lighten2).Padding(3);
                                if (Right(table, i)) cell = cell.AlignRight();
                                cell.Text(row[i]).FontSize(8);
                            }
                    });
                }
            });

            page.Footer().CompanyFooter(company, title);
        })).GeneratePdf();

    private static bool Right(ReportTable table, int index) =>
        table.RightAlign is { } flags && index < flags.Count && flags[index];

    /// <summary>Excel sheet names are capped at 31 characters and must be unique.</summary>
    private static string SheetName(string title, XLWorkbook book)
    {
        var clean = new string(title.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        if (clean.Length > 28) clean = clean[..28];
        if (string.IsNullOrWhiteSpace(clean)) clean = "Report";

        var name = clean;
        var n = 2;
        while (book.Worksheets.Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{clean[..Math.Min(clean.Length, 26)]} {n++}";

        return name;
    }
}
