using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using Tender.Domain;
using Tender.Infrastructure;
using Xunit;

namespace Tender.Tests;

/// <summary>
/// QuestPDF throws at layout time, not at build time, so a barcode or QR that
/// overflows its container only shows up when the document is actually generated.
/// A 62mm roll leaves roughly 159pt of usable width, and that is exactly where a
/// fixed-width Code 128 beside a QR stops fitting — hence rendering for real here.
/// </summary>
public class FileStickerRenderTests
{
    private static readonly CompanyBranding Company = new()
    {
        Name = "Middle East Instruments",
        Tagline = "Laboratory equipment sales and service",
        Address = "Plot 12, Industrial Estate, Korangi, Karachi, Pakistan",
        Contact = "+92 21 111 2222  ·  service@mei.example",
        TaxNumber = "1234567-8",
        FooterNote = "Payment due within 30 days."
    };

    /// <summary>A 2x2 PNG stands in for the real logo — the layout only cares it fits its box.</summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFUlEQVR4nGP8z8Dwn4GBgYEJRsAAAA" +
        "D//wMAAwAB/6xVwQAAAABJRU5ErkJggg==");

    private static PhysicalFile AFile(FileOwnerType type = FileOwnerType.Tender) => new()
    {
        Id = 1,
        FileNumber = "FILE-26-0042",
        OwnerType = type,
        OwnerId = 7,
        OwnerReference = type == FileOwnerType.Tender ? "KWSB/PROC/2026/114" : "PRJ-2026-014",
        OwnerTitle = "Supply, installation and commissioning of laboratory equipment",
        Location = "Cabinet 3, Shelf B",
        VolumeNumber = "II",
        OpenedOn = new DateOnly(2026, 7, 1),
        Status = FileStatus.Issued,
        HolderName = "Abdul Rehman Chaudhry",
        Movements =
        [
            new FileMovement
            {
                Id = 1, Action = FileMovementAction.Opened,
                MovedOn = new DateOnly(2026, 7, 1), RecordedByName = "System"
            },
            new FileMovement
            {
                Id = 2, Action = FileMovementAction.Issued, MovedOn = new DateOnly(2026, 7, 20),
                ToHolderName = "Abdul Rehman Chaudhry", Purpose = "Technical evaluation meeting",
                DueBack = new DateOnly(2026, 7, 25), RecordedByName = "Records Clerk",
                FromLocation = "Cabinet 3, Shelf B"
            }
        ]
    };

    [Theory]
    [InlineData(FileOwnerType.Tender)]
    [InlineData(FileOwnerType.Project)]
    public void The_builtin_sticker_renders(FileOwnerType type)
    {
        var pdf = new TenderPrintService().FileStickers([AFile(type)], Company);
        Assert.True(pdf.Length > 500);
    }

    [Fact]
    public void The_builtin_sticker_renders_with_a_logo()
    {
        var company = new CompanyBranding
        {
            Name = Company.Name,
            Tagline = Company.Tagline,
            Address = Company.Address,
            Contact = Company.Contact,
            TaxNumber = Company.TaxNumber,
            FooterNote = Company.FooterNote,
            Logo = TinyPng
        };

        var pdf = new TenderPrintService().FileStickers([AFile()], company);
        Assert.True(pdf.Length > 500);
    }

    /// <summary>An install where nobody configured a company profile still prints.</summary>
    [Fact]
    public void The_builtin_sticker_renders_unconfigured()
    {
        var pdf = new TenderPrintService().FileStickers([AFile()], CompanyBranding.Empty);
        Assert.True(pdf.Length > 500);
    }

    [Fact]
    public void A_whole_roll_of_stickers_renders()
    {
        var files = Enumerable.Range(1, 25).Select(i =>
        {
            var f = AFile(i % 2 == 0 ? FileOwnerType.Tender : FileOwnerType.Project);
            f.Id = i;
            f.FileNumber = $"FILE-26-{i:0000}";
            return f;
        }).ToList();

        var pdf = new TenderPrintService().FileStickers(files, Company);
        Assert.True(pdf.Length > 2000);
    }

    /// <summary>
    /// The user-defined template path, at the narrowest stock anyone is likely to
    /// load, with both symbologies switched on — the case that overflows if the
    /// renderer ever stops stretching the barcode to its container.
    /// </summary>
    [Fact]
    public void A_narrow_user_defined_template_renders_with_barcode_and_qr()
    {
        var template = new LabelTemplateSpec(
            WidthMm: 38, HeightMm: 25, MarginMm: 2,
            FieldKeys: ["file.number", "owner.reference", "owner.title", "file.location"],
            ShowTitle: true, ShowCompanyName: true, ShowBarcode: true, ShowQrCode: true,
            FontScale: 1.0m);

        var pdf = new TenderPrintService().FileStickers([AFile()], Company, template);
        Assert.True(pdf.Length > 500);
    }

    [Fact]
    public void A_continuous_roll_template_renders()
    {
        var template = new LabelTemplateSpec(
            WidthMm: 62, HeightMm: null, MarginMm: 3,
            FieldKeys: ["file.number", "file.kind", "owner.reference", "owner.title",
                        "file.opened", "file.location", "file.volume", "file.status", "file.holder"],
            ShowTitle: true, ShowCompanyName: true, ShowBarcode: true, ShowQrCode: true,
            FontScale: 1.2m);

        var pdf = new TenderPrintService().FileStickers([AFile()], Company, template);
        Assert.True(pdf.Length > 500);
    }

    [Fact]
    public void The_movement_register_renders()
    {
        var pdf = new TenderPrintService().FileMovementRegister(AFile(), Company);
        Assert.True(pdf.Length > 1000);
    }

    /// <summary>A file nobody has moved yet still prints a register.</summary>
    [Fact]
    public void The_movement_register_renders_with_no_movements()
    {
        var file = AFile();
        file.Movements = [];

        var pdf = new TenderPrintService().FileMovementRegister(file, CompanyBranding.Empty);
        Assert.True(pdf.Length > 500);
    }
}
