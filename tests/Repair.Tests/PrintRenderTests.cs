using ErpPlatform.Shared.Kernel;
using Repair.Domain;
using Repair.Infrastructure;
using Xunit;

namespace Repair.Tests;

/// <summary>
/// QuestPDF throws at layout time, not at build time, so a barcode or QR that
/// overflows its container only shows up when the document is actually generated.
/// These render every document end to end and assert a real PDF comes out.
/// </summary>
public class PrintRenderTests
{
    private static readonly CompanyBranding Company = new()
    {
        Name = "Middle East Instruments",
        Tagline = "Laboratory equipment sales and service",
        Address = "Plot 12, Industrial Estate, Korangi, Karachi, Pakistan",
        Contact = "+92 21 111 2222  ·  service@mei.example  ·  www.mei.example",
        TaxNumber = "1234567-8",
        FooterNote = "Payment due within 30 days. Bank: Example Bank, IBAN PK00 EXMP 0000 0000 1234 5678."
    };

    /// <summary>
    /// A 2x2 PNG stands in for the real logo — the layout only cares that an image
    /// is there and that it fits its box.
    /// </summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFUlEQVR4nGP8z8Dwn4GBgYEJRsAAAA" +
        "D//wMAAwAB/6xVwQAAAABJRU5ErkJggg==");

    private static Customer ACustomer() => new()
    {
        Id = 1,
        Name = "Gulf Diagnostics Laboratory (Head Office)",
        Phone = "+92 300 1234567",
        Organization = "Gulf Diagnostics",
        Address = "Plot 12, Industrial Estate, Karachi"
    };

    private static RepairJob AJob(int id, Intake intake, Customer customer) => new()
    {
        Id = id,
        JobNumber = $"JOB-26-{id:0000}",
        Intake = intake,
        IntakeId = intake.Id,
        Customer = customer,
        CustomerId = customer.Id,
        DeviceName = "Centrifuge",
        Brand = "Eppendorf",
        Model = "5804 R",
        SerialNumber = "SN-99182736",
        IssueDescription = "Rotor vibrates badly above 6000 rpm and the lid latch sticks.",
        Priority = id % 2 == 0 ? JobPriority.Urgent : JobPriority.Normal,
        ExpectedDeliveryDate = new DateOnly(2026, 8, 12),
        AssignedTechnicianName = "Imran Q.",
        WorkItems =
        [
            new JobWorkItem
            {
                Kind = JobWorkItemKind.Part, Description = "Rotor bearing assembly",
                Quantity = 1, UnitPrice = 8500, LineTotal = 8500
            },
            new JobWorkItem
            {
                Kind = JobWorkItemKind.Labor, Description = "Strip, balance and reassemble drive",
                Quantity = 3, UnitPrice = 1200, LineTotal = 3600
            },
            new JobWorkItem
            {
                Kind = JobWorkItemKind.Service, Description = "Goodwill calibration check",
                Quantity = 1, UnitPrice = 900, LineTotal = 900, Billable = false
            }
        ]
    };

    private static Intake AnIntake(int deviceCount)
    {
        var customer = ACustomer();
        var intake = new Intake
        {
            Id = 1,
            IntakeNumber = "INT-26-0001",
            Customer = customer,
            CustomerId = customer.Id,
            ReceivedAtUtc = new DateTime(2026, 7, 29, 9, 30, 0, DateTimeKind.Utc),
            ReceivedByName = "Counter Staff",
            Notes = "Customer needs all units back before the audit."
        };
        for (var i = 1; i <= deviceCount; i++)
            intake.Jobs.Add(AJob(i, intake, customer));
        return intake;
    }

    [Theory]
    [InlineData(PrintSize.A4)]
    [InlineData(PrintSize.Pos)]
    public void A_collective_intake_receipt_renders(PrintSize size)
    {
        var pdf = new RepairPrintService().IntakeReceipt(AnIntake(4), size, Company);

        AssertIsPdf(pdf);
    }

    [Fact]
    public void Device_labels_render_one_per_device()
    {
        AssertIsPdf(new RepairPrintService().DeviceLabels(AnIntake(3), Company));
    }

    [Fact]
    public void A_job_card_renders_with_its_parts_and_labour()
    {
        var intake = AnIntake(1);

        AssertIsPdf(new RepairPrintService().JobCard(intake.Jobs[0], Company));
    }

    [Fact]
    public void A_job_label_renders()
    {
        var intake = AnIntake(1);

        AssertIsPdf(new RepairPrintService().JobLabel(intake.Jobs[0], Company));
    }

    [Fact]
    public void A_long_document_number_does_not_burst_the_header()
    {
        // The header barcode is the stretching renderer precisely so this fits.
        var intake = AnIntake(1);
        intake.IntakeNumber = "INT-2026-000000000199";

        AssertIsPdf(new RepairPrintService().IntakeReceipt(intake, PrintSize.A4, Company));
    }

    [Fact]
    public void A_logo_is_drawn_into_every_paper_size()
    {
        var branded = Company with { Logo = TinyPng };
        var intake = AnIntake(2);
        var printer = new RepairPrintService();

        AssertIsPdf(printer.IntakeReceipt(intake, PrintSize.A4, branded));
        AssertIsPdf(printer.IntakeReceipt(intake, PrintSize.Pos, branded));
        AssertIsPdf(printer.DeviceLabels(intake, branded));
        AssertIsPdf(printer.JobCard(intake.Jobs[0], branded));
    }

    [Fact]
    public void An_unconfigured_platform_still_prints()
    {
        // Nobody has been to /admin/company yet: no name, no logo, no footer.
        var intake = AnIntake(2);

        AssertIsPdf(new RepairPrintService()
            .IntakeReceipt(intake, PrintSize.A4, CompanyBranding.Empty));
    }

    private static void AssertIsPdf(byte[] pdf)
    {
        Assert.True(pdf.Length > 1000, "document came back suspiciously small");
        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
    }
}
