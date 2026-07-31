using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Tender.Domain;

namespace Tender.Infrastructure;

/// <summary>A column in a printed tender report.</summary>
public record ReportColumn(string Header, float Width, bool Right = false);

/// <summary>
/// One printable report: a title, some columns, the rows, and optional totals — the
/// same shape Inventory uses, so the screen and the printout can't disagree.
/// </summary>
public record TenderReport(
    string Title,
    string Subtitle,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string[]> Rows,
    IReadOnlyList<string>? TotalsRow = null,
    string? Note = null);

public interface ITenderPrintService
{
    /// <summary>Renders any built report as an A4 document.</summary>
    byte[] Report(TenderReport report, CompanyBranding company, bool landscape = false);

    /// <summary>
    /// The full tender file note: identification, commercials, timeline, the
    /// comparative statement of bidders, every security lodged and every document
    /// logged — the single sheet a file inspection or an audit would ask for first.
    /// </summary>
    byte[] TenderSummarySheet(TenderRecord tender, CompanyBranding company);

    /// <summary>
    /// The EMD/bank-guarantee register for one tender — what's handed to whoever
    /// signs off releasing or renewing a security.
    /// </summary>
    byte[] SecurityRegister(TenderRecord tender, CompanyBranding company);

    /// <summary>
    /// The spine sticker for a physical file. Falls back to a built-in 62mm layout
    /// when nobody has configured a template, so stickers print on a fresh install.
    /// </summary>
    byte[] FileStickers(IReadOnlyList<PhysicalFile> files, CompanyBranding company,
        LabelTemplateSpec? template = null);

    /// <summary>The movement history of one file — the sheet an audit asks for.</summary>
    byte[] FileMovementRegister(PhysicalFile file, CompanyBranding company);
}

public class TenderPrintService : ITenderPrintService
{
    static TenderPrintService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] Report(TenderReport report, CompanyBranding company, bool landscape = false) =>
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

    public byte[] TenderSummarySheet(TenderRecord tender, CompanyBranding company) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.4f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(9.5f));

            page.Header().CompanyHeader(company, "TENDER FILE NOTE", right => right.Width(150).Column(c =>
            {
                c.Item().AlignRight().Text(tender.TenderNumber).FontSize(12).Bold();
                c.Item().PaddingTop(4).AlignRight().Element(e => e.QrCode(tender.TenderNumber, 40));
            }));

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Text(tender.Title).FontSize(13).SemiBold();
                col.Item().PaddingBottom(6).Text($"Status: {tender.Status}").FontSize(9)
                    .FontColor(Colors.Grey.Darken1);

                Section(col, "Identification", new()
                {
                    ("Issuing authority", tender.IssuingAuthority),
                    ("Department", tender.Department ?? "-"),
                    ("Portal reference", tender.PortalReference ?? "-"),
                    ("Submission mode", tender.SubmissionMode.ToString())
                });

                Section(col, "Commercials", new()
                {
                    ("Estimated value", tender.EstimatedValue.ToString("N2")),
                    ("Tender fee", tender.TenderFee?.ToString("N2") ?? "-"),
                    ("EMD amount", tender.IsEmdExempted ? "Exempted" : tender.EmdAmount?.ToString("N2") ?? "-"),
                    ("Performance guarantee %", tender.PerformanceGuaranteePercentage?.ToString("N2") ?? "-"),
                    ("Retention money %", tender.RetentionMoneyPercentage?.ToString("N2") ?? "-"),
                    ("Bid validity (days)", tender.BidValidityDays?.ToString() ?? "-")
                });

                Section(col, "Timeline", new()
                {
                    ("Publish date", tender.PublishDate?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Submission deadline", tender.SubmissionDeadline?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Technical opening", tender.TechnicalOpeningDate?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Financial opening", tender.FinancialOpeningDate?.ToString("yyyy-MM-dd") ?? "-")
                });

                if (tender.Status is TenderStatus.Won or TenderStatus.Lost)
                {
                    Section(col, "Outcome", new()
                    {
                        ("Our rank", tender.OurRank?.ToString() ?? "-"),
                        ("L1 amount", tender.L1Amount?.ToString("N2") ?? "-"),
                        ("Awarded value", tender.AwardedValue?.ToString("N2") ?? "-"),
                        ("Award date", tender.AwardDate?.ToString("yyyy-MM-dd") ?? "-"),
                        ("Work order number", tender.WorkOrderNumber ?? "-"),
                        ("Contract period", tender.ContractStartDate is null ? "-"
                            : $"{tender.ContractStartDate:yyyy-MM-dd} to {tender.ContractEndDate:yyyy-MM-dd}")
                    });
                }

                if (tender.Competitors.Count > 0)
                {
                    col.Item().PaddingTop(8).Text("Comparative statement of bidders").FontSize(10).SemiBold();
                    col.Item().PaddingTop(3).Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.ConstantColumn(26); c.RelativeColumn(4); c.ConstantColumn(90); c.ConstantColumn(50); c.RelativeColumn(3); });
                        foreach (var (h, r) in new[] { ("Rank", false), ("Bidder", false), ("Quoted amount", true), ("Own", false), ("Remarks", false) })
                        {
                            var cell = t.Cell().Background(Colors.Grey.Lighten3).Padding(3);
                            (r ? cell.AlignRight() : cell.AlignLeft()).Text(h).SemiBold();
                        }
                        foreach (var c in tender.Competitors.OrderBy(x => x.Rank ?? int.MaxValue))
                        {
                            Cell2(t).Text(c.Rank?.ToString() ?? "-");
                            Cell2(t).Text(c.BidderName);
                            Cell2(t).AlignRight().Text(c.QuotedAmount.ToString("N2"));
                            Cell2(t).Text(c.IsOwnBid ? "Yes" : "");
                            Cell2(t).Text(c.Remarks ?? "");
                        }
                    });
                }

                if (tender.Guarantees.Count > 0)
                {
                    col.Item().PaddingTop(8).Text("Securities lodged").FontSize(10).SemiBold();
                    col.Item().PaddingTop(3).Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(2); c.ConstantColumn(70); c.ConstantColumn(60); c.ConstantColumn(60); c.ConstantColumn(55); });
                        foreach (var (h, r) in new[] { ("Type", false), ("Bank / number", false), ("Instrument", false), ("Amount", true), ("Issue", false), ("Expiry", false), ("Status", false) })
                        {
                            var cell = t.Cell().Background(Colors.Grey.Lighten3).Padding(3);
                            (r ? cell.AlignRight() : cell.AlignLeft()).Text(h).SemiBold();
                        }
                        foreach (var g in tender.Guarantees)
                        {
                            Cell2(t).Text(g.Type.ToString());
                            Cell2(t).Text($"{g.BankName} {g.GuaranteeNumber}");
                            Cell2(t).Text(g.InstrumentType.ToString());
                            Cell2(t).AlignRight().Text(g.Amount.ToString("N2"));
                            Cell2(t).Text(g.IssueDate.ToString("yyyy-MM-dd"));
                            Cell2(t).Text(g.ExpiryDate.ToString("yyyy-MM-dd"));
                            Cell2(t).Text(g.Status.ToString());
                        }
                    });
                }

                if (tender.Documents.Count > 0)
                {
                    col.Item().PaddingTop(8).Text("Related documents").FontSize(10).SemiBold();
                    col.Item().PaddingTop(3).Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(4); c.RelativeColumn(2); c.ConstantColumn(70); });
                        foreach (var h in new[] { "Category", "Name", "Reference", "Date" })
                            t.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(h).SemiBold();
                        foreach (var d in tender.Documents)
                        {
                            Cell2(t).Text(d.Category.ToString());
                            Cell2(t).Text(d.Name);
                            Cell2(t).Text(d.ReferenceNumber ?? "-");
                            Cell2(t).Text(d.DocumentDate?.ToString("yyyy-MM-dd") ?? "-");
                        }
                    });
                }

                if (!string.IsNullOrWhiteSpace(tender.Notes))
                    col.Item().PaddingTop(10).Text($"Notes: {tender.Notes}").FontSize(8).Italic();

                col.Item().PaddingTop(30).Row(r =>
                {
                    foreach (var sig in new[] { "Prepared by", "Reviewed by", "Approved by" })
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().LineHorizontal(0.8f);
                            c.Item().PaddingTop(3).Text(sig).FontSize(9);
                        });
                        r.ConstantItem(16);
                    }
                });
            });

            page.Footer().Column(c =>
            {
                c.Item().AlignCenter().Barcode(tender.TenderNumber, height: 22);
                c.Item().CompanyFooter(company, tender.TenderNumber);
            });
        })).GeneratePdf();

    public byte[] SecurityRegister(TenderRecord tender, CompanyBranding company) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(1.2f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(9));

            page.Header().CompanyHeader(company, "EMD / BANK GUARANTEE REGISTER", right => right.Width(180)
                .AlignRight().AlignMiddle().Text(tender.TenderNumber).FontSize(12).Bold());

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Text($"{tender.TenderNumber} — {tender.Title}").FontSize(10).SemiBold();
                col.Item().PaddingBottom(6).Text(tender.IssuingAuthority).FontSize(9)
                    .FontColor(Colors.Grey.Darken1);

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(2);
                        c.ConstantColumn(65); c.ConstantColumn(65); c.ConstantColumn(65); c.ConstantColumn(65);
                        c.ConstantColumn(60); c.RelativeColumn(3);
                    });

                    foreach (var (h, r) in new[]
                             {
                                 ("Type", false), ("Instrument", false), ("Bank / branch", false), ("Number", false),
                                 ("Amount", true), ("Issue", false), ("Expiry", false), ("Claim end", false),
                                 ("Status", false), ("Remarks", false)
                             })
                    {
                        var cell = t.Cell().Background(Colors.Grey.Lighten3)
                            .BorderBottom(1).BorderColor(Colors.Grey.Darken1).Padding(4);
                        (r ? cell.AlignRight() : cell.AlignLeft()).Text(h).SemiBold();
                    }

                    foreach (var g in tender.Guarantees)
                    {
                        Cell2(t).Text(g.Type.ToString());
                        Cell2(t).Text(g.InstrumentType.ToString());
                        Cell2(t).Text($"{g.BankName} {g.BranchName}");
                        Cell2(t).Text(g.GuaranteeNumber);
                        Cell2(t).AlignRight().Text(g.Amount.ToString("N2"));
                        Cell2(t).Text(g.IssueDate.ToString("yyyy-MM-dd"));
                        Cell2(t).Text(g.ExpiryDate.ToString("yyyy-MM-dd"));
                        Cell2(t).Text(g.ClaimPeriodEndDate?.ToString("yyyy-MM-dd") ?? "-");
                        Cell2(t).Text(g.Status.ToString());
                        Cell2(t).Text(g.Remarks ?? "");
                    }

                    t.Cell().ColumnSpan(4).BorderTop(1).PaddingTop(4).AlignRight().Text("Total").SemiBold();
                    t.Cell().BorderTop(1).PaddingTop(4).AlignRight()
                        .Text(tender.Guarantees.Sum(g => g.Amount).ToString("N2")).SemiBold();
                    t.Cell().ColumnSpan(5).BorderTop(1);
                });

                if (tender.Guarantees.Count == 0)
                    col.Item().PaddingTop(12).AlignCenter()
                        .Text("No securities recorded for this tender.").Italic().FontColor(Colors.Grey.Darken1);

                col.Item().PaddingTop(35).Row(r =>
                {
                    foreach (var sig in new[] { "Prepared by", "Verified by" })
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

            page.Footer().CompanyFooter(company, tender.TenderNumber);
        })).GeneratePdf();

    public byte[] FileStickers(IReadOnlyList<PhysicalFile> files, CompanyBranding company,
        LabelTemplateSpec? template = null) =>
        template is null
            ? Document.Create(doc =>
            {
                foreach (var file in files)
                    doc.Page(page => FileSticker(page, file, company));
            }).GeneratePdf()
            : LabelRenderer.Render(template, files.Select(LabelDataFor).ToList(), company);

    /// <summary>
    /// Every field a file sticker could show, keyed as TenderModule advertises them.
    /// The template picks which of these print, so offering all of them is free.
    /// </summary>
    private static LabelData LabelDataFor(PhysicalFile file) => new(
        file.OwnerTitle,
        file.FileNumber,
        new Dictionary<string, string?>
        {
            ["file.number"] = file.FileNumber,
            ["file.kind"] = file.OwnerType == FileOwnerType.Tender ? "TENDER" : "PROJECT",
            ["owner.reference"] = file.OwnerReference,
            ["owner.title"] = file.OwnerTitle,
            ["file.opened"] = file.OpenedOn.ToString("yyyy-MM-dd"),
            ["file.location"] = file.Location,
            ["file.volume"] = string.IsNullOrWhiteSpace(file.VolumeNumber)
                ? null : $"Vol. {file.VolumeNumber}",
            ["file.status"] = file.Status.ToString(),
            ["file.holder"] = file.HolderName
        });

    /// <summary>
    /// The built-in 62mm spine sticker, used when no template is configured.
    /// </summary>
    /// <remarks>
    /// The stretching <c>Barcode()</c> renderer inside a fixed-width container, not
    /// <c>BarcodeFixed()</c>: a 62mm roll leaves roughly 159pt of usable width, and a
    /// fixed-width Code 128 plus a QR does not fit in that.
    /// </remarks>
    private static void FileSticker(PageDescriptor page, PhysicalFile file, CompanyBranding company)
    {
        page.ContinuousSize(62, Unit.Millimetre);
        page.Margin(3, Unit.Millimetre);
        page.DefaultTextStyle(t => Letterhead.Thermal(t).FontSize(8));

        page.Content().Column(col =>
        {
            col.Item().Row(r =>
            {
                r.RelativeItem().Row(brand =>
                {
                    if (company.HasLogo)
                        brand.ConstantItem(34).PaddingRight(3).AlignMiddle()
                            .MaxHeight(12).Image(company.Logo!).FitArea();
                    brand.RelativeItem().AlignMiddle().Text(company.Name).Bold().FontSize(8);
                });
                r.ConstantItem(52).AlignRight().AlignMiddle()
                    .Text(file.OwnerType == FileOwnerType.Tender ? "TENDER" : "PROJECT")
                    .Bold().FontSize(7.5f).FontColor(Colors.Grey.Darken2);
            });

            col.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().AlignMiddle()
                    .Element(c => c.Barcode(file.FileNumber, 26, showText: false));
                r.ConstantItem(44).PaddingLeft(4).AlignMiddle()
                    .Element(c => c.QrCode(file.FileNumber, 40));
            });

            col.Item().AlignCenter().Text(file.FileNumber)
                .FontSize(10).Bold().FontFamily(Fonts.Consolas);

            col.Item().PaddingTop(2).Text(file.OwnerReference).SemiBold().FontSize(8);
            col.Item().Text(file.OwnerTitle).FontSize(7.5f);

            if (!string.IsNullOrWhiteSpace(file.VolumeNumber))
                col.Item().Text($"Vol. {file.VolumeNumber}").SemiBold();
            if (!string.IsNullOrWhiteSpace(file.Location))
                col.Item().Text($"Loc: {file.Location}");

            col.Item().PaddingTop(1).Text($"Opened {file.OpenedOn:yyyy-MM-dd}")
                .FontSize(7).FontColor(Colors.Grey.Darken1);
        });
    }

    public byte[] FileMovementRegister(PhysicalFile file, CompanyBranding company) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.2f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(9));

            page.Header().CompanyHeader(company, "FILE MOVEMENT REGISTER", right => right.Width(180)
                .AlignRight().AlignMiddle().Text(file.FileNumber).FontSize(12).Bold());

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Text($"{file.OwnerReference} — {file.OwnerTitle}").FontSize(10).SemiBold();
                col.Item().Text(file.OwnerType == FileOwnerType.Tender ? "Tender file" : "Project file")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
                col.Item().PaddingBottom(6).Text(
                        $"Status: {file.Status}"
                        + (string.IsNullOrWhiteSpace(file.HolderName) ? "" : $"  •  Held by {file.HolderName}")
                        + (string.IsNullOrWhiteSpace(file.Location) ? "" : $"  •  {file.Location}"))
                    .FontSize(9);

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(62); c.ConstantColumn(66); c.RelativeColumn(2);
                        c.RelativeColumn(2); c.ConstantColumn(62); c.RelativeColumn(2);
                    });

                    foreach (var h in new[] { "Date", "Action", "From", "To", "Due back", "Purpose / remarks" })
                        t.Cell().Background(Colors.Grey.Lighten3)
                            .BorderBottom(0.8f).BorderColor(Colors.Grey.Medium)
                            .Padding(3).Text(h).SemiBold();

                    foreach (var m in file.Movements.OrderBy(m => m.MovedOn).ThenBy(m => m.Id))
                    {
                        Cell2(t).Text(m.MovedOn.ToString("yyyy-MM-dd"));
                        Cell2(t).Text(m.Action.ToString());
                        Cell2(t).Text(m.FromHolderName ?? m.FromLocation ?? "-");
                        Cell2(t).Text(m.ToHolderName ?? m.ToLocation ?? "-");
                        Cell2(t).Text(m.DueBack?.ToString("yyyy-MM-dd") ?? "-");
                        Cell2(t).Text(string.Join(" — ",
                            new[] { m.Purpose, m.Remarks }.Where(s => !string.IsNullOrWhiteSpace(s))));
                    }
                });

                if (file.Movements.Count == 0)
                    col.Item().PaddingTop(8).Text("No movements recorded.")
                        .FontSize(9).Italic().FontColor(Colors.Grey.Darken1);

                col.Item().PaddingTop(30).Row(r =>
                {
                    foreach (var sig in new[] { "Records clerk", "Verified by" })
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

            page.Footer().CompanyFooter(company, file.FileNumber);
        })).GeneratePdf();

    private static IContainer Cell2(TableDescriptor t) =>
        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);

    private static void Section(ColumnDescriptor col, string title, List<(string Label, string Value)> fields)
    {
        col.Item().PaddingTop(6).Text(title).FontSize(10).SemiBold();
        col.Item().PaddingTop(2).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(130); c.RelativeColumn();
                c.ConstantColumn(130); c.RelativeColumn();
            });
            for (var i = 0; i < fields.Count; i += 2)
            {
                t.Cell().Padding(2).Text(fields[i].Label).SemiBold();
                t.Cell().Padding(2).Text(fields[i].Value);
                if (i + 1 < fields.Count)
                {
                    t.Cell().Padding(2).Text(fields[i + 1].Label).SemiBold();
                    t.Cell().Padding(2).Text(fields[i + 1].Value);
                }
                else { t.Cell(); t.Cell(); }
            }
        });
    }
}
