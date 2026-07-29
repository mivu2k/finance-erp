using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using ClosedXML.Excel;
using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FinanceERP.Infrastructure.Services;

public class ExportService : IExportService
{
    static ExportService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] TableToPdf(string title, string subtitle, string[] headers,
        IEnumerable<string[]> rows, CompanyBranding? company = null)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().CompanyHeader(company ?? CompanyBranding.Empty, title,
                    right => right.Width(220).AlignRight().AlignMiddle()
                        .Text(subtitle).FontSize(9).FontColor(Colors.Grey.Darken1));

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

                page.Footer().CompanyFooter(company ?? CompanyBranding.Empty, title);
            });
        });
        return doc.GeneratePdf();
    }

    public byte[] DocumentToPdf(PdfDocument doc) =>
        Document.Create(container => Compose(container, doc)).GeneratePdf();

    /// <summary>Each document contributes its own page set, so slips never share a page.</summary>
    public byte[] DocumentsToPdf(IEnumerable<PdfDocument> documents) =>
        Document.Create(container =>
        {
            foreach (var doc in documents) Compose(container, doc);
        }).GeneratePdf();

    private static void Compose(IDocumentContainer container, PdfDocument doc)
    {
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        // Finance keeps its own blue-ruled header rather than the shared
                        // Letterhead: the document number block and the title colour are
                        // part of how these forms are recognised. The branding inside it
                        // is the same profile every other app prints.
                        if (doc.Company.HasLogo)
                            row.ConstantItem(120).PaddingRight(10).AlignMiddle()
                                .MaxHeight(46).Image(doc.Company.Logo!).FitArea();

                        row.RelativeItem().Column(left =>
                        {
                            if (!string.IsNullOrWhiteSpace(doc.Company.Name))
                                left.Item().Text(doc.Company.Name).SemiBold().FontSize(13);
                            if (!string.IsNullOrWhiteSpace(doc.Company.Address))
                                left.Item().Text(doc.Company.Address!).FontSize(8)
                                    .FontColor(Colors.Grey.Darken2);
                            if (!string.IsNullOrWhiteSpace(doc.Company.Contact))
                                left.Item().Text(doc.Company.Contact!).FontSize(8)
                                    .FontColor(Colors.Grey.Darken2);
                            if (!string.IsNullOrWhiteSpace(doc.Company.TaxNumber))
                                left.Item().Text($"Tax No. {doc.Company.TaxNumber}").FontSize(8)
                                    .FontColor(Colors.Grey.Darken2);
                            left.Item().PaddingTop(2).Text(doc.Title).FontSize(17).Bold()
                                .FontColor(Colors.Blue.Darken2);
                            if (!string.IsNullOrWhiteSpace(doc.Subtitle))
                                left.Item().Text(doc.Subtitle!).FontColor(Colors.Grey.Darken1);
                        });
                        if (!string.IsNullOrWhiteSpace(doc.DocumentNo))
                            row.ConstantItem(180).AlignRight().Column(right =>
                            {
                                right.Item().AlignRight().Text("Document No").FontSize(8).FontColor(Colors.Grey.Darken1);
                                right.Item().AlignRight().Text(doc.DocumentNo!).SemiBold().FontSize(12);
                            });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(Colors.Blue.Darken2);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(12);

                    if (doc.Fields.Count > 0)
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(95); c.RelativeColumn();
                                c.ConstantColumn(95); c.RelativeColumn();
                            });
                            foreach (var f in doc.Fields)
                            {
                                t.Cell().PaddingVertical(2).Text(f.Label).FontColor(Colors.Grey.Darken2);
                                var cell = t.Cell().PaddingVertical(2).PaddingRight(10).Text(f.Value ?? "—");
                                if (f.Emphasise) cell.SemiBold();
                            }
                        });

                    if (doc.TableHeaders is { Length: > 0 })
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                // First column carries descriptions, so give it the slack.
                                for (var i = 0; i < doc.TableHeaders.Length; i++)
                                    c.RelativeColumn(i == 0 ? 2.2f : 1f);
                            });
                            t.Header(h =>
                            {
                                for (var i = 0; i < doc.TableHeaders.Length; i++)
                                {
                                    var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(5);
                                    var text = doc.RightAlignedColumns.Contains(i)
                                        ? cell.AlignRight().Text(doc.TableHeaders[i])
                                        : cell.Text(doc.TableHeaders[i]);
                                    text.SemiBold();
                                }
                            });
                            foreach (var row in doc.TableRows)
                                for (var i = 0; i < row.Length; i++)
                                {
                                    var cell = t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5);
                                    if (doc.RightAlignedColumns.Contains(i)) cell.AlignRight().Text(row[i] ?? "");
                                    else cell.Text(row[i] ?? "");
                                }
                            if (doc.TableFooter is not null)
                                for (var i = 0; i < doc.TableFooter.Length; i++)
                                {
                                    var cell = t.Cell().Background(Colors.Grey.Lighten4).BorderTop(1).Padding(5);
                                    var text = doc.RightAlignedColumns.Contains(i)
                                        ? cell.AlignRight().Text(doc.TableFooter[i] ?? "")
                                        : cell.Text(doc.TableFooter[i] ?? "");
                                    text.Bold();
                                }
                        });

                    if (doc.Totals.Count > 0)
                        col.Item().AlignRight().Width(260).Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(110); });
                            foreach (var total in doc.Totals)
                            {
                                var label = t.Cell().PaddingVertical(3)
                                    .BorderBottom(total.Emphasise ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Text(total.Label);
                                var value = t.Cell().PaddingVertical(3).AlignRight()
                                    .BorderBottom(total.Emphasise ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Text(total.Value ?? "");
                                if (total.Emphasise)
                                {
                                    label.Bold().FontSize(11);
                                    value.Bold().FontSize(11);
                                }
                            }
                        });

                    if (doc.Approvals.Count > 0)
                        col.Item().Column(a =>
                        {
                            a.Item().PaddingBottom(4).Text("Approval Trail").SemiBold();
                            a.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c =>
                                {
                                    c.ConstantColumn(75); c.RelativeColumn(1.3f);
                                    c.ConstantColumn(70); c.RelativeColumn(2f); c.ConstantColumn(85);
                                });
                                t.Header(h =>
                                {
                                    foreach (var head in new[] { "Stage", "By", "Action", "Comment", "When" })
                                        h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(head).SemiBold();
                                });
                                foreach (var row in doc.Approvals)
                                {
                                    void Cell(string? v) => t.Cell().BorderBottom(0.5f)
                                        .BorderColor(Colors.Grey.Lighten2).Padding(4).Text(v ?? "");
                                    Cell(row.Level);
                                    Cell(row.Actor);
                                    Cell(row.Action);
                                    Cell(row.Comment);
                                    Cell(row.When?.ToString("yyyy-MM-dd HH:mm"));
                                }
                            });
                        });

                    if (!string.IsNullOrWhiteSpace(doc.Notes))
                        col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(n =>
                        {
                            n.Item().Text("Notes").SemiBold().FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                            n.Item().Text(doc.Notes!);
                        });

                    if (doc.Signatures.Length > 0)
                        col.Item().PaddingTop(34).Row(row =>
                        {
                            foreach (var caption in doc.Signatures)
                                row.RelativeItem().PaddingRight(18).Column(sig =>
                                {
                                    sig.Item().LineHorizontal(0.8f).LineColor(Colors.Grey.Darken1);
                                    sig.Item().PaddingTop(3).Text(caption)
                                        .FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                                });
                        });
                });

                if (!string.IsNullOrWhiteSpace(doc.Watermark))
                    page.Foreground().AlignCenter().AlignMiddle()
                        .Rotate(-35).Text(doc.Watermark!)
                        .FontSize(90).Bold().FontColor(Colors.Red.Lighten4);

                page.Footer().Column(f =>
                {
                    f.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    f.Item().Row(row =>
                    {
                        row.RelativeItem().Text(doc.FooterNote ?? doc.Company.FooterNote ?? "")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                        row.RelativeItem().AlignRight().Text(t =>
                        {
                            t.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                            t.Span($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}  ·  Page ");
                            t.CurrentPageNumber();
                            t.Span(" / ");
                            t.TotalPages();
                        });
                    });
                });
            });
        }
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
