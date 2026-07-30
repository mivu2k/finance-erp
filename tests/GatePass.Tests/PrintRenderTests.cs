using ErpPlatform.Shared.Kernel;
using GatePass.Domain;
using GatePass.Infrastructure;
using Xunit;

namespace GatePass.Tests;

/// <summary>
/// QuestPDF only discovers a broken layout when it actually renders, so the only way
/// to know a document still builds is to build it. These exist because the A4 item
/// table used to hardcode five columns while the headers were passed in: adding a
/// sixth produced more cells than columns and nothing failed until print time.
/// </summary>
public class PrintRenderTests
{
    private readonly IGatePassPrintService _print = new GatePassPrintService();

    /// <summary>Logo bytes are exercised separately — this is the configured case.</summary>
    private static CompanyBranding Branded() => new()
    {
        Name = "MEI Engineering (Pvt) Ltd",
        Address = "Plot 42, Korangi Industrial Area, Karachi",
        Contact = "+92 21 3512 3456",
        TaxNumber = "1234567-8",
        FooterNote = "Goods remain the property of the company until released."
    };

    private static GatePassRecord Pass(GatePassDirection direction, bool returnable = false) => new()
    {
        PassNumber = "GP-OUT-26-0007",
        Direction = direction,
        PersonName = "Imran Sheikh",
        PersonPhone = "0300-1234567",
        PersonCnic = "42101-1234567-1",
        CompanyName = "Sheikh Traders",
        VehicleNumber = "KX-4821",
        Department = "Workshop",
        Purpose = "Return of repaired compressor",
        AuthorizedByName = "Store Manager",
        IssuedAtUtc = new DateTime(2026, 7, 30, 9, 15, 0, DateTimeKind.Utc),
        IsReturnable = returnable,
        ExpectedReturnOn = returnable ? new DateOnly(2026, 8, 15) : null,
        Notes = "Handled with care.",
        Items =
        [
            new GatePassItem
            {
                Description = "Micro compressor NX-1200", SerialNumber = "SN-99120",
                Quantity = 2, Unit = "pcs", Remarks = "Repaired"
            },
            // No unit and no serial: both columns must still render.
            new GatePassItem { Description = "Assorted fittings", Quantity = 12.5m },
            new GatePassItem
            {
                Description = "Very long description that has to wrap inside its column " +
                              "without pushing the numeric columns out of the table",
                SerialNumber = "SN-00001", Quantity = 1, Unit = "box", Remarks = "Fragile"
            }
        ]
    };

    private static DemoIssuance Demo() => new()
    {
        IssuanceNumber = "DEMO-26-0003",
        CustomerName = "Gulberg Textiles",
        CustomerPhone = "042-35678901",
        CustomerReference = "PO-5512",
        Department = "Sales",
        ReferenceLetter = "Letter dated 2026-07-20",
        IssuedByName = "Sales Executive",
        IssuedAtUtc = new DateTime(2026, 7, 30, 11, 0, 0, DateTimeKind.Utc),
        ExpectedReturnOn = new DateOnly(2026, 8, 20),
        Status = DemoStatus.Issued,
        Notes = "Demo for evaluation.",
        Items =
        [
            new DemoIssuanceItem
            {
                Description = "Generator NX-1700", SerialNumber = "SN-77012",
                Quantity = 1, Accessories = "Charger, manual"
            },
            new DemoIssuanceItem { Description = "Spare cable", Quantity = 3 }
        ]
    };

    [Theory]
    [InlineData(PrintVariant.A4)]
    [InlineData(PrintVariant.Pos)]
    public void An_outward_pass_renders(PrintVariant variant)
    {
        var pdf = _print.GatePass(Pass(GatePassDirection.Outward), variant, Branded());
        Assert.NotEmpty(pdf);
    }

    [Theory]
    [InlineData(PrintVariant.A4)]
    [InlineData(PrintVariant.Pos)]
    public void An_inward_pass_renders(PrintVariant variant)
    {
        var pdf = _print.GatePass(Pass(GatePassDirection.Inward), variant, Branded());
        Assert.NotEmpty(pdf);
    }

    [Theory]
    [InlineData(PrintVariant.A4)]
    [InlineData(PrintVariant.Pos)]
    public void A_returnable_pass_renders(PrintVariant variant)
    {
        var pdf = _print.GatePass(Pass(GatePassDirection.Outward, returnable: true), variant, Branded());
        Assert.NotEmpty(pdf);
    }

    [Theory]
    [InlineData(PrintVariant.A4)]
    [InlineData(PrintVariant.Pos)]
    public void A_demo_issuance_renders(PrintVariant variant)
    {
        var pdf = _print.DemoIssuance(Demo(), variant, Branded());
        Assert.NotEmpty(pdf);
    }

    /// <summary>
    /// A fresh install has no company profile, so every document has to survive an
    /// empty letterhead rather than throwing on a missing name or logo.
    /// </summary>
    [Theory]
    [InlineData(PrintVariant.A4)]
    [InlineData(PrintVariant.Pos)]
    public void Documents_render_with_no_company_profile_configured(PrintVariant variant)
    {
        Assert.NotEmpty(_print.GatePass(Pass(GatePassDirection.Outward), variant, CompanyBranding.Empty));
        Assert.NotEmpty(_print.DemoIssuance(Demo(), variant, CompanyBranding.Empty));
    }

    [Fact]
    public void A_pass_with_a_single_bare_item_renders()
    {
        var pass = Pass(GatePassDirection.Inward);
        pass.Items = [new GatePassItem { Description = "One thing", Quantity = 1 }];
        Assert.NotEmpty(_print.GatePass(pass, PrintVariant.A4, Branded()));
    }
}
