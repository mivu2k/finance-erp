using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using GatePass.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GatePass.Infrastructure;

/// <summary>Which paper the document is going on.</summary>
public enum PrintVariant
{
    /// <summary>Full-page signable document for the file.</summary>
    A4,
    /// <summary>80mm thermal slip for the gate hut.</summary>
    Pos
}

/// <summary>
/// Renders passes and issuances as printable documents. Both variants from the
/// Laravel app are kept: A4 for the signed file copy, POS for the gate.
/// </summary>
public interface IGatePassPrintService
{
    byte[] GatePass(GatePassRecord pass, PrintVariant variant, CompanyBranding company);
    byte[] DemoIssuance(DemoIssuance issuance, PrintVariant variant, CompanyBranding company);
}

public class GatePassPrintService : IGatePassPrintService
{
    static GatePassPrintService() => QuestPDF.Settings.License = LicenseType.Community;

    private const float PosWidthMm = 80f;
    private const float PosWidthPoints = PosWidthMm * 72f / 25.4f;

    public byte[] GatePass(GatePassRecord pass, PrintVariant variant, CompanyBranding company) =>
        variant == PrintVariant.Pos
            ? RenderPos(company,
                pass.Direction == GatePassDirection.Inward ? "INWARD GATE PASS" : "OUTWARD GATE PASS",
                pass.PassNumber,
                [
                    ("Person", pass.PersonName),
                    ("Company", pass.CompanyName ?? "-"),
                    ("Vehicle", pass.VehicleNumber ?? "-"),
                    ("Purpose", pass.Purpose),
                    ("Issued", pass.IssuedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Authorised", pass.AuthorizedByName)
                ],
                pass.Items.Select(i => ($"{i.Description}{(i.SerialNumber is null ? "" : $" ({i.SerialNumber})")}",
                    $"{i.Quantity:0.##} {i.Unit}")).ToList())
            : RenderA4(company,
                pass.Direction == GatePassDirection.Inward ? "INWARD GATE PASS" : "OUTWARD GATE PASS",
                pass.PassNumber,
                [
                    ("Person carrying", pass.PersonName),
                    ("Phone / CNIC", $"{pass.PersonPhone} {pass.PersonCnic}".Trim()),
                    ("Company", pass.CompanyName ?? "-"),
                    ("Vehicle number", pass.VehicleNumber ?? "-"),
                    ("Department", pass.Department ?? "-"),
                    ("Purpose", pass.Purpose),
                    ("Reference", $"{pass.ReferenceType} {pass.ReferenceNumber}".Trim() is { Length: > 0 } r ? r : "-"),
                    ("Issued at", pass.IssuedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Authorised by", pass.AuthorizedByName),
                    ("Returnable", pass.IsReturnable
                        ? $"Yes — due {pass.ExpectedReturnOn:yyyy-MM-dd}"
                        : "No")
                ],
                // Qty and Unit are separate columns: a single "Qty" heading over
                // "5 pcs" put two different facts under one label, and the figures
                // wouldn't line up down the column.
                [("#", 0.6f, false), ("Description", 4f, false), ("Serial", 2f, false),
                 ("Qty", 1f, true), ("Unit", 1f, false), ("Remarks", 2f, false)],
                pass.Items.Select((i, n) => new[]
                {
                    (n + 1).ToString(), i.Description, i.SerialNumber ?? "-",
                    i.Quantity.ToString("0.##"), i.Unit ?? "-", i.Remarks ?? ""
                }).ToList(),
                pass.Notes,
                ["Carried by", "Authorised by", "Security / Gate"]);

    public byte[] DemoIssuance(DemoIssuance issuance, PrintVariant variant, CompanyBranding company) =>
        variant == PrintVariant.Pos
            ? RenderPos(company, "DEMO ISSUANCE", issuance.IssuanceNumber,
                [
                    ("Customer", issuance.CustomerName),
                    ("Phone", issuance.CustomerPhone ?? "-"),
                    ("Issued", issuance.IssuedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Due back", issuance.ExpectedReturnOn?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Issued by", issuance.IssuedByName)
                ],
                issuance.Items.Select(i => ($"{i.Description}{(i.SerialNumber is null ? "" : $" ({i.SerialNumber})")}",
                    $"{i.Quantity:0.##}")).ToList())
            : RenderA4(company, "DEMO GOODS ISSUANCE", issuance.IssuanceNumber,
                [
                    ("Customer", issuance.CustomerName),
                    ("Phone", issuance.CustomerPhone ?? "-"),
                    ("Customer reference", issuance.CustomerReference ?? "-"),
                    ("Department", issuance.Department ?? "-"),
                    ("Reference letter", issuance.ReferenceLetter ?? "-"),
                    ("Issued at", issuance.IssuedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Issued by", issuance.IssuedByName),
                    ("Expected return", issuance.ExpectedReturnOn?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Status", issuance.Status.ToString())
                ],
                [("#", 0.6f, false), ("Description", 4f, false), ("Serial", 2f, false),
                 ("Qty", 1f, true), ("Accessories", 2f, false)],
                issuance.Items.Select((i, n) => new[]
                {
                    (n + 1).ToString(), i.Description, i.SerialNumber ?? "-",
                    $"{i.Quantity:0.##}", i.Accessories ?? ""
                }).ToList(),
                issuance.Notes,
                ["Received by (customer)", "Issued by", "Returned / checked by"]);

    private static byte[] RenderA4(
        CompanyBranding company, string title, string number,
        List<(string Label, string Value)> fields,
        (string Name, float Width, bool Right)[] columns, List<string[]> rows,
        string? notes, string[] signatures) =>
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10));

                page.Header().CompanyHeader(company, title, right => right.Width(180)
                    .AlignRight().AlignMiddle().Text(number).FontSize(13).Bold());

                page.Content().PaddingVertical(10).Column(col =>
                {
                    // Two-column label/value block, so the form reads like the paper it replaces.
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(110); c.RelativeColumn();
                            c.ConstantColumn(110); c.RelativeColumn();
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
                            else
                            {
                                t.Cell(); t.Cell();
                            }
                        }
                    });

                    col.Item().PaddingTop(12).Table(t =>
                    {
                        // Built from the headers rather than a fixed five: a caller
                        // adding a column used to leave more cells than columns, which
                        // QuestPDF only discovers at render time.
                        t.ColumnsDefinition(c =>
                        {
                            foreach (var col in columns) c.RelativeColumn(col.Width);
                        });

                        t.Header(h =>
                        {
                            foreach (var col in columns)
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                                (col.Right ? cell.AlignRight() : cell.AlignLeft())
                                    .Text(col.Name).SemiBold();
                            }
                        });

                        foreach (var row in rows)
                            for (var i = 0; i < row.Length; i++)
                            {
                                var cell = t.Cell().BorderBottom(0.5f)
                                    .BorderColor(Colors.Grey.Lighten2).Padding(4);
                                // Figures right, text left, so a column reads as a column.
                                (i < columns.Length && columns[i].Right
                                    ? cell.AlignRight() : cell.AlignLeft()).Text(row[i]);
                            }
                    });

                    if (!string.IsNullOrWhiteSpace(notes))
                        col.Item().PaddingTop(10).Text($"Notes: {notes}").FontSize(9).Italic();

                    col.Item().PaddingTop(45).Row(r =>
                    {
                        foreach (var sig in signatures)
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

                page.Footer().CompanyFooter(company, number);
            });
        }).GeneratePdf();

    private static byte[] RenderPos(
        CompanyBranding company, string title, string number,
        List<(string Label, string Value)> fields,
        List<(string Description, string Qty)> items) =>
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                // Continuous roll: fixed width, height grows with content.
                page.ContinuousSize(PosWidthMm, Unit.Millimetre);
                page.Margin(4, Unit.Millimetre);
                // Bold and black throughout — see Letterhead.Thermal for why.
                page.DefaultTextStyle(t => Letterhead.Thermal(t).FontSize(Letterhead.ThermalBodySize));

                page.Content().Column(col =>
                {
                    col.PosCompanyHeader(company, title, PosWidthPoints);
                    col.Item().AlignCenter().Text(number).FontSize(9);
                    col.Item().PaddingVertical(3).LineHorizontal(1f).LineColor(Colors.Black);

                    foreach (var (label, value) in fields)
                        col.Item().Row(r =>
                        {
                            r.ConstantItem(58).Text(label).Bold();
                            r.RelativeItem().Text(value);
                        });

                    col.Item().PaddingVertical(3).LineHorizontal(1f).LineColor(Colors.Black);
                    col.Item().Text("ITEMS").Bold();

                    foreach (var (description, qty) in items)
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text(description);
                            r.ConstantItem(40).AlignRight().Text(qty);
                        });

                    col.Item().PaddingVertical(3).LineHorizontal(1f).LineColor(Colors.Black);
                    col.Item().PaddingTop(18).Text("Signature: ______________");
                    col.PosCompanyFooter(company);
                });
            });
        }).GeneratePdf();
}
