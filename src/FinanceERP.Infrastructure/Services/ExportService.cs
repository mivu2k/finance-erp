using ClosedXML.Excel;
using FinanceERP.Application.Interfaces;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FinanceERP.Infrastructure.Services;

public class ExportService : IExportService
{
    static ExportService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] TableToPdf(string title, string subtitle, string[] headers, IEnumerable<string[]> rows)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).SemiBold().FontSize(16);
                    col.Item().Text(subtitle).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                });

                page.Content().PaddingTop(8).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        foreach (var _ in headers) cols.RelativeColumn();
                    });
                    table.Header(h =>
                    {
                        foreach (var header in headers)
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4)
                                .Text(header).SemiBold();
                    });
                    foreach (var row in rows)
                        foreach (var cell in row)
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                .Padding(4).Text(cell ?? "");
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("Generated ").FontColor(Colors.Grey.Darken1);
                    t.Span($"{DateTime.Now:yyyy-MM-dd HH:mm}  ·  Page ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });
        return doc.GeneratePdf();
    }

    public byte[] TableToExcel(string sheetName, string[] headers, IEnumerable<object?[]> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheetName.Length > 31 ? sheetName[..31] : sheetName);
        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
            ws.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                var v = row[c];
                ws.Cell(r, c + 1).Value = v switch
                {
                    null => "",
                    decimal d => d,
                    int i => i,
                    DateOnly dt => dt.ToDateTime(TimeOnly.MinValue),
                    DateTime dtt => dtt,
                    _ => v.ToString()
                };
            }
            r++;
        }
        ws.Columns().AdjustToContents(1, Math.Min(r, 100));
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
