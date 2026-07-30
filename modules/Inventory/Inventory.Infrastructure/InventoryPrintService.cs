using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using Inventory.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Inventory.Infrastructure;

/// <summary>A column in a printed inventory report.</summary>
/// <param name="Width">Relative width against the other columns.</param>
/// <param name="Right">Figures are right-aligned so a column reads as a column.</param>
public record ReportColumn(string Header, float Width, bool Right = false);

/// <summary>
/// One printable report: a title, some columns, the rows, and optional totals.
/// </summary>
/// <remarks>
/// Every inventory report goes through this one shape so the screen, the print and
/// any future export can't drift apart — the same reasoning behind Repair's
/// ReportTableBuilder.
/// </remarks>
public record InventoryReport(
    string Title,
    string Subtitle,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string[]> Rows,
    IReadOnlyList<string>? TotalsRow = null,
    string? Note = null);

public interface IInventoryPrintService
{
    /// <summary>Renders any report as a landscape-agnostic A4 document.</summary>
    byte[] Report(InventoryReport report, CompanyBranding company, bool landscape = false);

    /// <summary>A goods received note, for the file and for signing.</summary>
    byte[] GoodsReceiptNote(GoodsReceipt receipt, string supplierName, string? warehouseName,
        CompanyBranding company);

    /// <summary>A purchase order to send to the supplier.</summary>
    byte[] PurchaseOrderDocument(PurchaseOrder order, string? warehouseName, CompanyBranding company);
}

public class InventoryPrintService : IInventoryPrintService
{
    static InventoryPrintService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] Report(InventoryReport report, CompanyBranding company, bool landscape = false) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(1.2f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(9));

            page.Header().CompanyHeader(company, report.Title, right => right.Width(200)
                .AlignRight().AlignMiddle().Text(report.Subtitle).FontSize(9));

            page.Content().PaddingVertical(8).Column(col =>
            {
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        foreach (var column in report.Columns) c.RelativeColumn(column.Width);
                    });

                    // Repeated on every page, so a long stock list stays readable
                    // after the first sheet.
                    t.Header(h =>
                    {
                        foreach (var column in report.Columns)
                        {
                            var cell = h.Cell().Background(Colors.Grey.Lighten3)
                                .BorderBottom(1).BorderColor(Colors.Grey.Darken1).Padding(4);
                            (column.Right ? cell.AlignRight() : cell.AlignLeft())
                                .Text(column.Header).SemiBold();
                        }
                    });

                    foreach (var row in report.Rows)
                        for (var i = 0; i < row.Length; i++)
                        {
                            var cell = t.Cell().BorderBottom(0.5f)
                                .BorderColor(Colors.Grey.Lighten2).Padding(3);
                            (i < report.Columns.Count && report.Columns[i].Right
                                ? cell.AlignRight() : cell.AlignLeft()).Text(row[i]);
                        }

                    if (report.TotalsRow is { } totals)
                        for (var i = 0; i < totals.Count; i++)
                        {
                            var cell = t.Cell().BorderTop(1).BorderColor(Colors.Grey.Darken1)
                                .PaddingVertical(4).PaddingHorizontal(3);
                            (i < report.Columns.Count && report.Columns[i].Right
                                ? cell.AlignRight() : cell.AlignLeft())
                                .Text(totals[i]).SemiBold();
                        }
                });

                if (report.Rows.Count == 0)
                    col.Item().PaddingTop(12).AlignCenter()
                        .Text("Nothing to report for this selection.")
                        .Italic().FontColor(Colors.Grey.Darken1);

                if (!string.IsNullOrWhiteSpace(report.Note))
                    col.Item().PaddingTop(10).Text(report.Note!).FontSize(8).Italic()
                        .FontColor(Colors.Grey.Darken1);
            });

            page.Footer().CompanyFooter(company, report.Title);
        })).GeneratePdf();

    public byte[] GoodsReceiptNote(
        GoodsReceipt receipt, string supplierName, string? warehouseName, CompanyBranding company) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10));

            page.Header().CompanyHeader(company, "GOODS RECEIVED NOTE", right => right.Width(180)
                .AlignRight().AlignMiddle().Text(receipt.ReceiptNumber).FontSize(13).Bold());

            page.Content().PaddingVertical(10).Column(col =>
            {
                Fields(col,
                [
                    ("Supplier", supplierName),
                    ("Date", receipt.Date.ToString("yyyy-MM-dd")),
                    ("Their document", receipt.SupplierDocumentNumber ?? "-"),
                    ("Received into", warehouseName ?? "-"),
                    ("Received by", receipt.ReceivedByName),
                    ("Status", receipt.Status.ToString())
                ]);

                col.Item().PaddingTop(12).Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(26); c.RelativeColumn(4);
                        c.RelativeColumn(3); c.ConstantColumn(60);
                        c.ConstantColumn(70); c.ConstantColumn(80);
                    });

                    foreach (var (header, right) in new[]
                             {
                                 ("#", false), ("Item", false), ("Serials / batch", false),
                                 ("Qty", true), ("Unit cost", true), ("Amount", true)
                             })
                    {
                        var cell = t.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                        (right ? cell.AlignRight() : cell.AlignLeft()).Text(header).SemiBold();
                    }

                    var n = 1;
                    foreach (var line in receipt.Lines)
                    {
                        var trace = string.Join(" ", new[]
                        {
                            line.SerialNumbers,
                            line.BatchNumber is null ? null : $"Batch {line.BatchNumber}",
                            line.ExpiresOn is { } e ? $"Exp {e:yyyy-MM-dd}" : null
                        }.Where(x => !string.IsNullOrWhiteSpace(x)));

                        Cell(t).Text((n++).ToString());
                        Cell(t).Text(line.ItemName);
                        Cell(t).Text(trace);
                        Cell(t).AlignRight().Text(line.Quantity.ToString("0.##"));
                        Cell(t).AlignRight().Text(line.UnitCost.ToString("N2"));
                        Cell(t).AlignRight().Text(line.LineTotal.ToString("N2"));
                    }

                    t.Cell().ColumnSpan(5).BorderTop(1).PaddingTop(4)
                        .AlignRight().Text("Total").SemiBold();
                    t.Cell().BorderTop(1).PaddingTop(4)
                        .AlignRight().Text(receipt.TotalCost.ToString("N2")).SemiBold();
                });

                if (!string.IsNullOrWhiteSpace(receipt.Notes))
                    col.Item().PaddingTop(10).Text($"Notes: {receipt.Notes}").FontSize(9).Italic();

                col.Item().PaddingTop(45).Row(r =>
                {
                    foreach (var sig in new[] { "Received by", "Checked by", "Store in-charge" })
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().LineHorizontal(0.8f);
                            c.Item().PaddingTop(3).Text(sig).FontSize(9);
                        });
                        r.ConstantItem(20);
                    }
                });
            });

            page.Footer().CompanyFooter(company, receipt.ReceiptNumber);
        })).GeneratePdf();

    public byte[] PurchaseOrderDocument(
        PurchaseOrder order, string? warehouseName, CompanyBranding company) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10));

            page.Header().CompanyHeader(company, "PURCHASE ORDER", right => right.Width(180)
                .AlignRight().AlignMiddle().Text(order.OrderNumber).FontSize(13).Bold());

            page.Content().PaddingVertical(10).Column(col =>
            {
                Fields(col,
                [
                    ("Supplier", order.Supplier?.Name ?? "-"),
                    ("Order date", order.Date.ToString("yyyy-MM-dd")),
                    ("Expected", order.ExpectedOn?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Deliver to", warehouseName ?? "-"),
                    ("Reference", order.Reference ?? "-"),
                    ("Raised by", order.RaisedByName)
                ]);

                col.Item().PaddingTop(12).Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(26); c.RelativeColumn(5);
                        c.ConstantColumn(60); c.ConstantColumn(75); c.ConstantColumn(85);
                    });

                    foreach (var (header, right) in new[]
                             {
                                 ("#", false), ("Item", false), ("Qty", true),
                                 ("Unit cost", true), ("Amount", true)
                             })
                    {
                        var cell = t.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                        (right ? cell.AlignRight() : cell.AlignLeft()).Text(header).SemiBold();
                    }

                    var n = 1;
                    foreach (var line in order.Lines)
                    {
                        Cell(t).Text((n++).ToString());
                        Cell(t).Text(line.ItemName);
                        Cell(t).AlignRight().Text(line.Quantity.ToString("0.##"));
                        Cell(t).AlignRight().Text(line.UnitCost.ToString("N2"));
                        Cell(t).AlignRight().Text(line.LineTotal.ToString("N2"));
                    }

                    Totals(t, "Subtotal", order.Subtotal);
                    if (order.DiscountAmount != 0) Totals(t, "Discount", -order.DiscountAmount);
                    if (order.TaxAmount != 0) Totals(t, "Tax", order.TaxAmount);
                    if (order.OtherCharges != 0) Totals(t, "Other charges", order.OtherCharges);
                    Totals(t, "Total", order.TotalAmount, bold: true);
                });

                if (!string.IsNullOrWhiteSpace(order.Notes))
                    col.Item().PaddingTop(10).Text($"Notes: {order.Notes}").FontSize(9).Italic();

                col.Item().PaddingTop(45).Row(r =>
                {
                    foreach (var sig in new[] { "Prepared by", "Approved by" })
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().LineHorizontal(0.8f);
                            c.Item().PaddingTop(3).Text(sig).FontSize(9);
                        });
                        r.ConstantItem(20);
                    }
                });
            });

            page.Footer().CompanyFooter(company, order.OrderNumber);
        })).GeneratePdf();

    // --- helpers ---

    private static IContainer Cell(TableDescriptor t) =>
        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);

    private static void Totals(TableDescriptor t, string label, decimal amount, bool bold = false)
    {
        var labelCell = t.Cell().ColumnSpan(4).PaddingTop(3).AlignRight();
        var valueCell = t.Cell().PaddingTop(3).AlignRight();

        if (bold)
        {
            labelCell.BorderTop(1).Text(label).SemiBold();
            valueCell.BorderTop(1).Text(amount.ToString("N2")).SemiBold();
        }
        else
        {
            labelCell.Text(label);
            valueCell.Text(amount.ToString("N2"));
        }
    }

    private static void Fields(ColumnDescriptor col, List<(string Label, string Value)> fields) =>
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(105); c.RelativeColumn();
                c.ConstantColumn(105); c.RelativeColumn();
            });
            for (var i = 0; i < fields.Count; i += 2)
            {
                t.Cell().Padding(3).Text(fields[i].Label).SemiBold();
                t.Cell().Padding(3).Text(fields[i].Value);
                if (i + 1 < fields.Count)
                {
                    t.Cell().Padding(3).Text(fields[i + 1].Label).SemiBold();
                    t.Cell().Padding(3).Text(fields[i + 1].Value);
                }
                else { t.Cell(); t.Cell(); }
            }
        });
}
