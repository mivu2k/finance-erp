using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Repair.Domain;

namespace Repair.Infrastructure;

/// <summary>
/// The printable documents carried over from the Laravel Blade templates: the job
/// card that follows the device around the workshop, the customer-facing
/// quotation, and the invoice raised off a sales order.
/// </summary>
public interface IRepairPrintService
{
    byte[] JobCard(RepairJob job, string companyName);
    byte[] Quotation(Quotation quotation, string companyName);
    byte[] Invoice(SalesOrder order, string companyName);
    /// <summary>80mm thermal receipt handed over at the counter.</summary>
    byte[] IntakeReceipt(Intake intake, string companyName);
}

public class RepairPrintService : IRepairPrintService
{
    static RepairPrintService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] JobCard(RepairJob job, string companyName) =>
        Document.Create(doc => doc.Page(page =>
        {
            Frame(page, companyName, "JOB CARD", job.JobNumber);

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Element(c => Fields(c,
                [
                    ("Customer", job.Customer.Name),
                    ("Phone", job.Customer.Phone),
                    ("Intake", job.Intake.IntakeNumber),
                    ("Received", job.Intake.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                    ("Device", job.DeviceName),
                    ("Brand / model", $"{job.Brand} {job.Model}".Trim()),
                    ("Serial", job.SerialNumber ?? "-"),
                    ("Condition", job.ConditionOnArrival.ToString()),
                    ("Priority", job.Priority.ToString()),
                    ("Expected", job.ExpectedDeliveryDate?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Technician", job.AssignedTechnicianName ?? "unassigned"),
                    ("Status", JobWorkflow.Describe(job.Status))
                ]));

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
                    col.Item().PaddingTop(10).Text($"Diagnosis — {d.TechnicianName} " +
                        $"({d.CreatedAtUtc:yyyy-MM-dd})").SemiBold();
                    col.Item().Text(d.Findings);
                    if (!string.IsNullOrWhiteSpace(d.RequiredParts))
                        col.Item().Text($"Parts required: {d.RequiredParts}").FontSize(9);
                    if (!string.IsNullOrWhiteSpace(d.WorkPerformed))
                        col.Item().Text($"Work performed: {d.WorkPerformed}").FontSize(9);
                }

                col.Item().PaddingTop(45).Element(c =>
                    Signatures(c, ["Technician", "Supervisor", "Received by (customer)"]));
            });

            Footer(page);
        })).GeneratePdf();

    public byte[] Quotation(Quotation q, string companyName) =>
        Document.Create(doc => doc.Page(page =>
        {
            Frame(page, companyName, "QUOTATION", q.QuotationNumber);

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Element(c => Fields(c,
                [
                    ("Customer", q.Customer?.Name ?? "-"),
                    ("Date", q.Date.ToString("yyyy-MM-dd")),
                    ("Subject", q.Subject ?? "-"),
                    ("Reference", q.Reference ?? "-"),
                    ("Job", q.RepairJob?.JobNumber ?? "-"),
                    ("Valid until", q.ValidUntil?.ToString("yyyy-MM-dd") ?? "-"),
                    ("Project", q.Project ?? "-"),
                    ("Prepared by", q.PreparedByName)
                ]));

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

                    t.Header(h =>
                    {
                        foreach (var (name, right) in new[]
                                 {
                                     ("#", false), ("Description", false), ("Qty", true),
                                     ("Unit price", true), ("Discount", true), ("Amount", true)
                                 })
                        {
                            var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                            (right ? cell.AlignRight() : cell).Text(name).SemiBold();
                        }
                    });

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
                    if (q.TaxPercent > 0) Total(totals, $"Tax ({q.TaxPercent:0.##}%)", q.TaxAmount, q.Currency);
                    totals.Item().PaddingTop(3).LineHorizontal(1);
                    Total(totals, "Total", q.TotalAmount, q.Currency, bold: true);
                });

                if (!string.IsNullOrWhiteSpace(q.Notes))
                    col.Item().PaddingTop(10).Text($"Notes: {q.Notes}").FontSize(9).Italic();

                col.Item().PaddingTop(45).Element(c =>
                    Signatures(c, ["Prepared by", "Approved by (manager)", "Accepted by (customer)"]));
            });

            Footer(page);
        })).GeneratePdf();

    public byte[] Invoice(SalesOrder order, string companyName) =>
        Document.Create(doc => doc.Page(page =>
        {
            Frame(page, companyName, "INVOICE", order.OrderNumber);

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Item().Element(c => Fields(c,
                [
                    ("Customer", order.Customer.Name),
                    ("Phone", order.Customer.Phone),
                    ("Date", order.CreatedAtUtc.ToString("yyyy-MM-dd")),
                    ("Quotation", order.Quotation.QuotationNumber),
                    ("Finalised by", order.FinalizedByName),
                    ("Payment status", order.PaymentStatus.ToString())
                ]));

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

                    t.Header(h =>
                    {
                        foreach (var (name, right) in new[]
                                 {
                                     ("#", false), ("Description", false),
                                     ("Qty", true), ("Unit price", true), ("Amount", true)
                                 })
                        {
                            var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(4);
                            (right ? cell.AlignRight() : cell).Text(name).SemiBold();
                        }
                    });

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
                    if (order.DiscountAmount > 0) Total(totals, "Discount", -order.DiscountAmount, null);
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
                        col.Item().Text($"{p.CreatedAtUtc:yyyy-MM-dd}  {p.Method}  " +
                                        $"{p.Amount:N2}  {p.ReferenceNumber}").FontSize(9);
                }

                col.Item().PaddingTop(45).Element(c => Signatures(c, ["Received by", "For " + companyName]));
            });

            Footer(page);
        })).GeneratePdf();

    public byte[] IntakeReceipt(Intake intake, string companyName) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.ContinuousSize(80, Unit.Millimetre);
            page.Margin(4, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(8));

            page.Content().Column(col =>
            {
                col.Item().AlignCenter().Text(companyName).Bold().FontSize(10);
                col.Item().AlignCenter().Text("INTAKE RECEIPT").Bold().FontSize(9);
                col.Item().AlignCenter().Text(intake.IntakeNumber).FontSize(9);
                col.Item().PaddingVertical(3).LineHorizontal(0.5f);

                foreach (var (label, value) in new[]
                         {
                             ("Customer", intake.Customer.Name),
                             ("Phone", intake.Customer.Phone),
                             ("Received", intake.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm")),
                             ("By", intake.ReceivedByName),
                             ("Payment", intake.PaymentMethod.ToString())
                         })
                    col.Item().Row(r =>
                    {
                        r.ConstantItem(52).Text(label).SemiBold();
                        r.RelativeItem().Text(value);
                    });

                col.Item().PaddingVertical(3).LineHorizontal(0.5f);
                col.Item().Text("DEVICES").SemiBold();

                foreach (var job in intake.Jobs)
                {
                    col.Item().PaddingTop(2).Text($"{job.JobNumber}  {job.DeviceName}").SemiBold();
                    col.Item().Text($"{job.Brand} {job.Model} {job.SerialNumber}".Trim()).FontSize(7);
                    col.Item().Text(job.IssueDescription).FontSize(7);
                }

                col.Item().PaddingVertical(3).LineHorizontal(0.5f);
                col.Item().Text("Please bring this receipt when collecting.").FontSize(7).Italic();
                col.Item().PaddingTop(16).Text("Signature: ______________").FontSize(8);
            });
        })).GeneratePdf();

    // --- shared layout pieces ---

    private static void Frame(PageDescriptor page, string company, string title, string number)
    {
        page.Size(PageSizes.A4);
        page.Margin(1.5f, Unit.Centimetre);
        page.DefaultTextStyle(t => t.FontSize(10));

        page.Header().Column(col =>
        {
            col.Item().Row(r =>
            {
                r.RelativeItem().Text(company).FontSize(15).Bold();
                r.ConstantItem(200).AlignRight().Column(c =>
                {
                    c.Item().Text(title).FontSize(13).Bold();
                    c.Item().Text(number).FontSize(11);
                });
            });
            col.Item().PaddingTop(6).LineHorizontal(1);
        });
    }

    private static void Footer(PageDescriptor page) =>
        page.Footer().AlignCenter().Text(t =>
        {
            t.Span("Page ").FontSize(8);
            t.CurrentPageNumber().FontSize(8);
            t.Span(" of ").FontSize(8);
            t.TotalPages().FontSize(8);
        });

    private static void Fields(IContainer container, List<(string Label, string Value)> fields) =>
        container.Table(t =>
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
            var right = r.ConstantItem(110).AlignRight()
                .Text($"{currency} {amount:N2}".Trim());
            if (bold) { left.SemiBold(); right.SemiBold(); }
        });
}
