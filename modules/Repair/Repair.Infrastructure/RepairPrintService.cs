using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Repair.Domain;

namespace Repair.Infrastructure;

/// <summary>Which paper a document is going on.</summary>
public enum PrintSize
{
    /// <summary>Full page, for the file and for signing.</summary>
    A4,
    /// <summary>80mm thermal roll, for the counter.</summary>
    Pos
}

/// <summary>
/// Every printable document in the repair flow. Each one carries a Code 128
/// barcode <em>and</em> a QR code of its own number, so any piece of paper in the
/// workshop can be scanned straight back to its record — bars for the bench
/// scanner, the square for a phone. A collective intake gets both for the intake
/// itself and again for every device on it.
/// </summary>
public interface IRepairPrintService
{
    /// <summary>Handed to the customer when the device is booked in.</summary>
    byte[] IntakeReceipt(Intake intake, PrintSize size, CompanyBranding company);
    /// <summary>Small adhesive label for the device itself.</summary>
    byte[] DeviceLabels(Intake intake, CompanyBranding company);
    byte[] JobCard(RepairJob job, CompanyBranding company);
    /// <summary>Single device label, reprinted from the job.</summary>
    byte[] JobLabel(RepairJob job, CompanyBranding company);
    byte[] Quotation(Quotation quotation, CompanyBranding company);
    byte[] Invoice(SalesOrder order, PrintSize size, CompanyBranding company);
    /// <summary>Signed on handover — the workshop's proof the device left.</summary>
    byte[] DeliveryNote(RepairJob job, PrintSize size, CompanyBranding company);
    /// <summary>Goods received note for a parts purchase.</summary>
    byte[] PurchaseNote(PartPurchase purchase, CompanyBranding company);
}

public class RepairPrintService : IRepairPrintService
{
    static RepairPrintService() => QuestPDF.Settings.License = LicenseType.Community;

    private const float PosWidthMm = 80f;
    private const float PosWidthPoints = PosWidthMm * 72f / 25.4f;

    // --- receiving ---

    public byte[] IntakeReceipt(Intake intake, PrintSize size, CompanyBranding company) =>
        size == PrintSize.Pos
            ? PosDocument(company, "INTAKE RECEIPT", intake.IntakeNumber, col =>
            {
                PosFields(col,
                [
                    ("Customer", intake.Customer.Name),
                    ("Phone", intake.Customer.Phone),
                    ("Received", intake.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("By", intake.ReceivedByName),
                    ("Basis", intake.PaymentMethod.ToString())
                ]);

                PosRule(col);
                col.Item().Text($"DEVICES ({intake.Jobs.Count})").SemiBold();

                foreach (var job in intake.Jobs)
                {
                    col.Item().PaddingTop(3).Text(job.DeviceName).SemiBold().FontSize(8);
                    col.Item().Text($"{job.Brand} {job.Model}".Trim()).FontSize(7);
                    if (!string.IsNullOrWhiteSpace(job.SerialNumber))
                        col.Item().Text($"S/N {job.SerialNumber}").FontSize(7);
                    col.Item().Text(job.IssueDescription).FontSize(7);
                    col.Item().PaddingTop(2).AlignCenter()
                        .Element(c => c.BarcodeFixed(job.JobNumber, 0.9f, 22));
                    col.Item().PaddingTop(2).AlignCenter()
                        .Element(c => c.QrCode(job.JobNumber, 52));
                    col.Item().AlignCenter().Text(job.JobNumber).FontSize(7);
                }

                PosRule(col);
                col.Item().Text("Please bring this receipt when collecting.").FontSize(7).Italic();
                col.Item().PaddingTop(14).Text("Customer signature: ____________").FontSize(7);
            })
            : A4Document(company, "INTAKE RECEIPT", intake.IntakeNumber, page =>
            {
                page.Content().PaddingVertical(10).Column(col =>
                {
                    Fields(col,
                    [
                        ("Customer", intake.Customer.Name),
                        ("Phone", intake.Customer.Phone),
                        ("Organisation", intake.Customer.Organization ?? "-"),
                        ("Address", intake.Customer.Address ?? "-"),
                        ("Received at", intake.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                        ("Received by", intake.ReceivedByName),
                        ("Payment basis", intake.PaymentMethod.ToString()),
                        ("Devices", intake.Jobs.Count.ToString())
                    ]);

                    col.Item().PaddingTop(14).Text("Devices received").SemiBold();

                    foreach (var job in intake.Jobs)
                    {
                        col.Item().PaddingTop(8).Border(0.5f).BorderColor(Colors.Grey.Lighten1)
                            .Padding(8).Row(row =>
                        {
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text(job.DeviceName).SemiBold();
                                c.Item().Text($"{job.Brand} {job.Model}".Trim()).FontSize(9);
                                c.Item().Text($"Serial: {job.SerialNumber ?? "not recorded"}").FontSize(9);
                                c.Item().Text($"Condition: {job.ConditionOnArrival}").FontSize(9);
                                c.Item().PaddingTop(3).Text($"Reported fault: {job.IssueDescription}")
                                    .FontSize(9);
                                if (job.ExpectedDeliveryDate is { } due)
                                    c.Item().Text($"Expected: {due:yyyy-MM-dd}").FontSize(9).SemiBold();
                            });

                            row.ConstantItem(150).AlignMiddle()
                                .Element(c => c.Barcode(job.JobNumber, 32));
                            row.ConstantItem(56).PaddingLeft(6).AlignMiddle()
                                .Element(c => c.QrCode(job.JobNumber, 50));
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(intake.Notes))
                        col.Item().PaddingTop(10).Text($"Notes: {intake.Notes}").FontSize(9).Italic();

                    col.Item().PaddingTop(14).Text(Terms).FontSize(7.5f).Italic()
                        .FontColor(Colors.Grey.Darken1);

                    col.Item().PaddingTop(40).Element(c =>
                        Signatures(c, ["Received by", "Customer signature"]));
                });
            });

    public byte[] DeviceLabels(Intake intake, CompanyBranding company) =>
        Document.Create(doc =>
        {
            // One label per device, on a 62mm roll — the usual label-printer size.
            foreach (var job in intake.Jobs)
                doc.Page(page => Label(page, job, intake, company));
        }).GeneratePdf();

    public byte[] JobLabel(RepairJob job, CompanyBranding company) =>
        Document.Create(doc => doc.Page(page => Label(page, job, job.Intake, company)))
            .GeneratePdf();

    private static void Label(PageDescriptor page, RepairJob job, Intake? intake, CompanyBranding company)
    {
        page.ContinuousSize(62, Unit.Millimetre);
        page.Margin(3, Unit.Millimetre);
        page.DefaultTextStyle(t => t.FontSize(7));

        page.Content().Column(col =>
        {
            col.Item().Row(r =>
            {
                r.RelativeItem().Row(brand =>
                {
                    // A label is only 62mm wide, so the logo gets a strip rather
                    // than the full letterhead block.
                    if (company.HasLogo)
                        brand.ConstantItem(34).PaddingRight(3).AlignMiddle()
                            .MaxHeight(12).Image(company.Logo!).FitArea();
                    brand.RelativeItem().AlignMiddle()
                        .Text(company.Name).Bold().FontSize(8);
                });
                r.ConstantItem(60).AlignRight()
                    .Text(job.Priority == JobPriority.Urgent ? "URGENT" : "")
                    .Bold().FontSize(8).FontColor(Colors.Red.Darken2);
            });

            // The stretching renderer, not BarcodeFixed: a 62mm roll leaves ~159pt,
            // and a fixed-width Code 128 plus a QR does not fit in that.
            col.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().AlignMiddle()
                    .Element(c => c.Barcode(job.JobNumber, 26, showText: false));
                r.ConstantItem(44).PaddingLeft(4).AlignMiddle()
                    .Element(c => c.QrCode(job.JobNumber, 40));
            });
            col.Item().AlignCenter().Text(job.JobNumber).FontSize(7).FontFamily(Fonts.Consolas);

            col.Item().PaddingTop(2).Text(job.DeviceName).SemiBold().FontSize(8);
            col.Item().Text($"{job.Brand} {job.Model}".Trim());
            if (!string.IsNullOrWhiteSpace(job.SerialNumber))
                col.Item().Text($"S/N {job.SerialNumber}");
            col.Item().Text(job.Customer.Name).SemiBold();
            col.Item().Text(job.Customer.Phone);

            if (intake is not null)
                col.Item().Text($"In: {intake.ReceivedAtUtc:yyyy-MM-dd}");
            if (job.ExpectedDeliveryDate is { } due)
                col.Item().Text($"Due: {due:yyyy-MM-dd}").SemiBold();
        });
    }

    // --- workshop ---

    public byte[] JobCard(RepairJob job, CompanyBranding company) =>
        A4Document(company, "JOB CARD", job.JobNumber, page =>
        {
            page.Content().PaddingVertical(10).Column(col =>
            {
                Fields(col,
                [
                    ("Customer", job.Customer.Name),
                    ("Phone", job.Customer.Phone),
                    ("Intake", job.Intake.IntakeNumber),
                    ("Received", job.Intake.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Device", job.DeviceName),
                    ("Brand / model", $"{job.Brand} {job.Model}".Trim()),
                    ("Serial", job.SerialNumber ?? "-"),
                    ("Condition in", job.ConditionOnArrival.ToString()),
                    ("Priority", job.Priority.ToString()),
                    ("Expected", job.ExpectedDeliveryDate?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Technician", job.AssignedTechnicianName ?? "unassigned"),
                    ("Status", JobWorkflow.Describe(job.Status))
                ]);

                col.Item().PaddingTop(10).Text("Reported fault").SemiBold();
                col.Item().Text(job.IssueDescription);

                if (job.Symptoms.Count > 0)
                {
                    col.Item().PaddingTop(8).Text("Symptoms").SemiBold();
                    col.Item().Text(string.Join(", ", job.Symptoms.Select(s => s.Symptom.Name)));
                }

                if (job.Accessories.Count > 0)
                {
                    col.Item().PaddingTop(8).Text("Accessories received").SemiBold();
                    col.Item().Text(string.Join(", ", job.Accessories.Select(a => a.Accessory.Name)));
                }

                foreach (var d in job.Diagnoses)
                {
                    col.Item().PaddingTop(10)
                        .Text($"Diagnosis — {d.TechnicianName} ({d.CreatedAtUtc:yyyy-MM-dd})")
                        .SemiBold();
                    col.Item().Text(d.Findings);
                    if (!string.IsNullOrWhiteSpace(d.RequiredParts))
                        col.Item().Text($"Parts required: {d.RequiredParts}").FontSize(9);
                    if (!string.IsNullOrWhiteSpace(d.WorkPerformed))
                        col.Item().Text($"Work performed: {d.WorkPerformed}").FontSize(9);
                }

                if (job.WorkItems.Count > 0)
                {
                    col.Item().PaddingTop(12).Text("Parts fitted & labour").SemiBold();
                    col.Item().PaddingTop(4).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(52);
                            c.RelativeColumn();
                            c.ConstantColumn(45);
                            c.ConstantColumn(62);
                            c.ConstantColumn(68);
                        });

                        foreach (var header in new[] { "Kind", "Description", "Qty", "Unit", "Total" })
                            table.Cell().BorderBottom(0.8f).PaddingVertical(3)
                                .AlignRight().Text(header).SemiBold().FontSize(9);

                        foreach (var w in job.WorkItems)
                        {
                            table.Cell().PaddingVertical(2).Text(w.Kind.ToString()).FontSize(9);
                            table.Cell().PaddingVertical(2)
                                .Text(w.Billable ? w.Description : $"{w.Description} (not billed)")
                                .FontSize(9);
                            table.Cell().PaddingVertical(2).AlignRight()
                                .Text(w.Quantity.ToString("N2")).FontSize(9);
                            table.Cell().PaddingVertical(2).AlignRight()
                                .Text(w.UnitPrice.ToString("N2")).FontSize(9);
                            table.Cell().PaddingVertical(2).AlignRight()
                                .Text(w.Billable ? w.LineTotal.ToString("N2") : "-").FontSize(9);
                        }

                        table.Cell().ColumnSpan(4).BorderTop(0.8f).PaddingTop(3)
                            .AlignRight().Text("Billable total").SemiBold().FontSize(9);
                        table.Cell().BorderTop(0.8f).PaddingTop(3).AlignRight()
                            .Text(job.WorkItems.Where(w => w.Billable).Sum(w => w.LineTotal).ToString("N2"))
                            .SemiBold().FontSize(9);
                    });
                }

                // Blank ruled space for the bench to write on.
                col.Item().PaddingTop(12).Text("Work log").SemiBold();
                for (var i = 0; i < 6; i++)
                    col.Item().PaddingTop(12).LineHorizontal(0.4f).LineColor(Colors.Grey.Lighten1);

                col.Item().PaddingTop(35).Element(c =>
                    Signatures(c, ["Technician", "Supervisor", "Quality check"]));
            });
        });

    // --- commercial ---

    public byte[] Quotation(Quotation q, CompanyBranding company) =>
        A4Document(company, "QUOTATION", q.QuotationNumber, page =>
        {
            page.Content().PaddingVertical(10).Column(col =>
            {
                Fields(col,
                [
                    ("Customer", q.Customer?.Name ?? "-"),
                    ("Date", q.Date.ToString("yyyy-MM-dd")),
                    ("Subject", q.Subject ?? "-"),
                    ("Reference", q.Reference ?? "-"),
                    ("Job", q.RepairJob?.JobNumber ?? "-"),
                    ("Valid until", q.ValidUntil?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Project", q.Project ?? "-"),
                    ("Prepared by", q.PreparedByName)
                ]);

                col.Item().PaddingTop(12).Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(24);
                        c.RelativeColumn(5);
                        c.ConstantColumn(50);
                        c.ConstantColumn(70);
                        c.ConstantColumn(60);
                        c.ConstantColumn(75);
                    });

                    Head(t, [("#", false), ("Description", false), ("Qty", true),
                             ("Unit price", true), ("Discount", true), ("Amount", true)]);

                    var n = 1;
                    foreach (var item in q.Items)
                    {
                        Cell(t).Text((n++).ToString());
                        Cell(t).Text(item.Description);
                        Cell(t).AlignRight().Text($"{item.Quantity:0.##}");
                        Cell(t).AlignRight().Text($"{item.UnitPrice:N2}");
                        Cell(t).AlignRight().Text($"{item.Discount:N2}");
                        Cell(t).AlignRight().Text($"{item.LineTotal:N2}");
                    }
                });

                col.Item().PaddingTop(10).AlignRight().Width(240).Column(totals =>
                {
                    Total(totals, "Parts", q.PartsAmount, q.Currency);
                    Total(totals, "Labour", q.LaborAmount, q.Currency);
                    Total(totals, "Subtotal", q.Subtotal, q.Currency);
                    if (q.DiscountAmount > 0) Total(totals, "Discount", -q.DiscountAmount, q.Currency);
                    if (q.TaxPercent > 0)
                        Total(totals, $"Tax ({q.TaxPercent:0.##}%)", q.TaxAmount, q.Currency);
                    totals.Item().PaddingTop(3).LineHorizontal(1);
                    Total(totals, "Total", q.TotalAmount, q.Currency, bold: true);
                });

                if (!string.IsNullOrWhiteSpace(q.Notes))
                    col.Item().PaddingTop(10).Text($"Notes: {q.Notes}").FontSize(9).Italic();

                col.Item().PaddingTop(40).Element(c =>
                    Signatures(c, ["Prepared by", "Approved by (manager)", "Accepted by (customer)"]));
            });
        });

    public byte[] Invoice(SalesOrder order, PrintSize size, CompanyBranding company) =>
        size == PrintSize.Pos
            ? PosDocument(company, "INVOICE", order.OrderNumber, col =>
            {
                PosFields(col,
                [
                    ("Customer", order.Customer.Name),
                    ("Phone", order.Customer.Phone),
                    ("Date", order.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Quotation", order.Quotation.QuotationNumber)
                ]);

                PosRule(col);
                foreach (var item in order.Quotation.Items)
                {
                    col.Item().Text(item.Description).FontSize(8);
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Text($"  {item.Quantity:0.##} x {item.UnitPrice:N2}").FontSize(7);
                        r.ConstantItem(60).AlignRight().Text($"{item.LineTotal:N2}").FontSize(8);
                    });
                }

                PosRule(col);
                PosTotal(col, "Total", order.TotalAmount, bold: true);
                PosTotal(col, "Paid", order.AmountPaid);
                PosTotal(col, "Balance", order.Balance, bold: true);

                PosRule(col);
                col.Item().AlignCenter().Text("Thank you for your business").FontSize(7).Italic();
            })
            : A4Document(company, "INVOICE", order.OrderNumber, page =>
            {
                page.Content().PaddingVertical(10).Column(col =>
                {
                    Fields(col,
                    [
                        ("Customer", order.Customer.Name),
                        ("Phone", order.Customer.Phone),
                        ("Address", order.Customer.Address ?? "-"),
                        ("Date", order.CreatedAtUtc.ToString("yyyy-MM-dd")),
                        ("Quotation", order.Quotation.QuotationNumber),
                        ("Finalised by", order.FinalizedByName),
                        ("Payment status", order.PaymentStatus.ToString()),
                        ("Job", order.Quotation.RepairJob?.JobNumber ?? "-")
                    ]);

                    col.Item().PaddingTop(12).Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(24);
                            c.RelativeColumn(5);
                            c.ConstantColumn(50);
                            c.ConstantColumn(80);
                            c.ConstantColumn(85);
                        });

                        Head(t, [("#", false), ("Description", false),
                                 ("Qty", true), ("Unit price", true), ("Amount", true)]);

                        var n = 1;
                        foreach (var item in order.Quotation.Items)
                        {
                            Cell(t).Text((n++).ToString());
                            Cell(t).Text(item.Description);
                            Cell(t).AlignRight().Text($"{item.Quantity:0.##}");
                            Cell(t).AlignRight().Text($"{item.UnitPrice:N2}");
                            Cell(t).AlignRight().Text($"{item.LineTotal:N2}");
                        }
                    });

                    col.Item().PaddingTop(10).AlignRight().Width(240).Column(totals =>
                    {
                        Total(totals, "Parts", order.PartsAmount, null);
                        Total(totals, "Labour", order.LaborAmount, null);
                        if (order.DiscountAmount > 0)
                            Total(totals, "Discount", -order.DiscountAmount, null);
                        if (order.TaxAmount > 0) Total(totals, "Tax", order.TaxAmount, null);
                        totals.Item().PaddingTop(3).LineHorizontal(1);
                        Total(totals, "Total", order.TotalAmount, null, bold: true);
                        Total(totals, "Paid", order.AmountPaid, null);
                        Total(totals, "Balance due", order.Balance, null, bold: true);
                    });

                    if (order.Payments.Count > 0)
                    {
                        col.Item().PaddingTop(12).Text("Payments received").SemiBold();
                        foreach (var p in order.Payments)
                            col.Item().Text(
                                $"{p.CreatedAtUtc:yyyy-MM-dd}  {p.Method}  " +
                                $"{p.Amount:N2}  {p.ReferenceNumber}").FontSize(9);
                    }

                    col.Item().PaddingTop(40).Element(c =>
                        Signatures(c, ["Received by", $"For {company.Name}"]));
                });
            });

    // --- delivering ---

    public byte[] DeliveryNote(RepairJob job, PrintSize size, CompanyBranding company) =>
        size == PrintSize.Pos
            ? PosDocument(company, "DELIVERY NOTE", job.JobNumber, col =>
            {
                PosFields(col,
                [
                    ("Customer", job.Customer.Name),
                    ("Phone", job.Customer.Phone),
                    ("Device", job.DeviceName),
                    ("Serial", job.SerialNumber ?? "-"),
                    ("Delivered", job.DeliveredAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "-"),
                    ("Collected by", job.DeliveredToName ?? "-"),
                    ("Released by", job.DeliveredByName ?? "-")
                ]);

                if (job.Accessories.Count > 0)
                {
                    PosRule(col);
                    col.Item().Text("ACCESSORIES RETURNED").SemiBold();
                    foreach (var a in job.Accessories)
                        col.Item().Text($"- {a.Accessory.Name}").FontSize(7);
                }

                PosRule(col);
                col.Item().Text("Received in working order and complete.").FontSize(7).Italic();
                col.Item().PaddingTop(16).Text("Signature: ______________").FontSize(7);
            })
            : A4Document(company, "DELIVERY NOTE", job.JobNumber, page =>
            {
                page.Content().PaddingVertical(10).Column(col =>
                {
                    Fields(col,
                    [
                        ("Customer", job.Customer.Name),
                        ("Phone", job.Customer.Phone),
                        ("Organisation", job.Customer.Organization ?? "-"),
                        ("Intake", job.Intake.IntakeNumber),
                        ("Device", job.DeviceName),
                        ("Brand / model", $"{job.Brand} {job.Model}".Trim()),
                        ("Serial", job.SerialNumber ?? "-"),
                        ("Received", job.Intake.ReceivedAtUtc.ToString("yyyy-MM-dd")),
                        ("Delivered", job.DeliveredAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "-"),
                        ("Technician", job.AssignedTechnicianName ?? "-"),
                        ("Collected by", job.DeliveredToName ?? "-"),
                        ("Collector phone", job.DeliveredToPhone ?? "-"),
                        ("Collector CNIC", job.DeliveredToCnic ?? "-"),
                        ("Released by", job.DeliveredByName ?? "-")
                    ]);

                    col.Item().PaddingTop(12).Text("Reported fault").SemiBold();
                    col.Item().Text(job.IssueDescription);

                    var work = job.Diagnoses
                        .Where(d => !string.IsNullOrWhiteSpace(d.WorkPerformed))
                        .Select(d => d.WorkPerformed!)
                        .ToList();

                    if (work.Count > 0)
                    {
                        col.Item().PaddingTop(8).Text("Work carried out").SemiBold();
                        foreach (var w in work) col.Item().Text($"• {w}");
                    }

                    if (job.Accessories.Count > 0)
                    {
                        col.Item().PaddingTop(8).Text("Accessories returned").SemiBold();
                        col.Item().Text(string.Join(", ", job.Accessories.Select(a => a.Accessory.Name)));
                    }

                    if (!string.IsNullOrWhiteSpace(job.DeliveryNote))
                        col.Item().PaddingTop(8).Text($"Note: {job.DeliveryNote}").FontSize(9).Italic();

                    col.Item().PaddingTop(14)
                        .Text("I confirm I have received the above device in working order, " +
                              "complete with the accessories listed.")
                        .FontSize(9);

                    col.Item().PaddingTop(40).Element(c =>
                        Signatures(c, ["Released by", "Collected by (name & signature)"]));
                });
            });

    // --- purchasing ---

    public byte[] PurchaseNote(PartPurchase purchase, CompanyBranding company) =>
        A4Document(company, "GOODS RECEIVED NOTE", purchase.PurchaseNumber, page =>
        {
            page.Content().PaddingVertical(10).Column(col =>
            {
                Fields(col,
                [
                    ("Supplier", purchase.Supplier.Name),
                    ("Phone", purchase.Supplier.Phone ?? "-"),
                    ("Supplier invoice", purchase.SupplierInvoiceNumber ?? "-"),
                    ("Purchased on", purchase.PurchasedOn.ToString("yyyy-MM-dd")),
                    ("Received by", purchase.ReceivedByName),
                    ("Payment", purchase.PaymentMethod.ToString())
                ]);

                col.Item().PaddingTop(12).Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(24);
                        c.RelativeColumn(4);
                        c.RelativeColumn(2);
                        c.ConstantColumn(55);
                        c.ConstantColumn(75);
                        c.ConstantColumn(80);
                    });

                    Head(t, [("#", false), ("Part", false), ("SKU", false),
                             ("Qty", true), ("Unit cost", true), ("Amount", true)]);

                    var n = 1;
                    foreach (var item in purchase.Items)
                    {
                        Cell(t).Text((n++).ToString());
                        Cell(t).Text(item.Part.Name);
                        Cell(t).Text(item.Part.Sku ?? "-");
                        Cell(t).AlignRight().Text($"{item.Quantity:0.##}");
                        Cell(t).AlignRight().Text($"{item.UnitCost:N2}");
                        Cell(t).AlignRight().Text($"{item.LineTotal:N2}");
                    }
                });

                col.Item().PaddingTop(10).AlignRight().Width(240).Column(totals =>
                {
                    Total(totals, "Subtotal", purchase.Subtotal, null);
                    if (purchase.DiscountAmount > 0)
                        Total(totals, "Discount", -purchase.DiscountAmount, null);
                    if (purchase.TaxAmount > 0) Total(totals, "Tax", purchase.TaxAmount, null);
                    if (purchase.OtherCharges > 0)
                        Total(totals, "Other charges", purchase.OtherCharges, null);
                    totals.Item().PaddingTop(3).LineHorizontal(1);
                    Total(totals, "Total", purchase.TotalAmount, null, bold: true);
                });

                if (!string.IsNullOrWhiteSpace(purchase.Notes))
                    col.Item().PaddingTop(10).Text($"Notes: {purchase.Notes}").FontSize(9).Italic();

                col.Item().PaddingTop(40).Element(c =>
                    Signatures(c, ["Received by", "Checked by", "Supplier"]));
            });
        });

    // --- shared layout ---

    private const string Terms =
        "Devices not collected within 60 days of notification may be disposed of to recover " +
        "charges. The workshop is not responsible for data held on any device. Estimates are " +
        "subject to change once the unit is opened; any revision will be quoted before work " +
        "proceeds.";

    private static byte[] A4Document(
        CompanyBranding company, string title, string number, Action<PageDescriptor> body) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.4f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10));

            page.Header().CompanyHeader(company, title, right => right.Row(r =>
            {
                // Stretching renderer, not the fixed-width one: a longer number
                // must scale down to fit rather than overflow the header.
                r.ConstantItem(160).AlignRight().AlignMiddle()
                    .Element(c => c.Barcode(number, 32));
                // The QR carries the same number: a bench scanner reads the bars,
                // a phone reads the square, and both land on the same record.
                r.ConstantItem(58).PaddingLeft(8).AlignRight()
                    .Element(c => c.QrCode(number, 50));
            }));

            body(page);

            page.Footer().CompanyFooter(company, number);
        })).GeneratePdf();

    private static byte[] PosDocument(
        CompanyBranding company, string title, string number, Action<ColumnDescriptor> body) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.ContinuousSize(PosWidthMm, Unit.Millimetre);
            page.Margin(4, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(8));

            page.Content().Column(col =>
            {
                col.PosCompanyHeader(company, title, PosWidthPoints);
                col.Item().PaddingTop(2).AlignCenter()
                    .Element(c => c.BarcodeFixed(number, 0.9f, 24));
                col.Item().PaddingTop(3).AlignCenter().Element(c => c.QrCode(number, 62));
                PosRule(col);

                body(col);

                col.PosCompanyFooter(company);
            });
        })).GeneratePdf();

    private static void PosRule(ColumnDescriptor col) =>
        col.Item().PaddingVertical(3).LineHorizontal(0.5f);

    private static void PosFields(ColumnDescriptor col, List<(string Label, string Value)> fields)
    {
        foreach (var (label, value) in fields)
            col.Item().Row(r =>
            {
                r.ConstantItem(54).Text(label).SemiBold();
                r.RelativeItem().Text(value);
            });
    }

    private static void PosTotal(ColumnDescriptor col, string label, decimal amount, bool bold = false) =>
        col.Item().Row(r =>
        {
            var left = r.RelativeItem().Text(label);
            var right = r.ConstantItem(70).AlignRight().Text($"{amount:N2}");
            if (bold) { left.SemiBold(); right.SemiBold(); }
        });

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

    private static void Head(TableDescriptor t, (string Name, bool Right)[] columns) =>
        t.Header(h =>
        {
            foreach (var (name, right) in columns)
            {
                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                (right ? cell.AlignRight() : cell).Text(name).SemiBold();
            }
        });

    private static void Signatures(IContainer container, string[] names) =>
        container.Row(r =>
        {
            foreach (var name in names)
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().LineHorizontal(0.8f);
                    c.Item().PaddingTop(3).Text(name).FontSize(9);
                });
                r.ConstantItem(20);
            }
        });

    private static IContainer Cell(TableDescriptor t) =>
        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);

    private static void Total(ColumnDescriptor col, string label, decimal amount,
        string? currency, bool bold = false) =>
        col.Item().PaddingVertical(1).Row(r =>
        {
            var left = r.RelativeItem().Text(label);
            var right = r.ConstantItem(110).AlignRight().Text($"{currency} {amount:N2}".Trim());
            if (bold) { left.SemiBold(); right.SemiBold(); }
        });
}
