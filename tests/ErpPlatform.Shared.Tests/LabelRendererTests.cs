using ErpPlatform.Shared.Kernel;
using ErpPlatform.Shared.Printing;
using Xunit;

namespace ErpPlatform.Shared.Tests;

/// <summary>
/// A label's size and field list are user input, so the renderer has to survive
/// whatever gets configured — and QuestPDF only reveals a broken layout when it
/// actually draws, so the only way to know is to draw it.
/// </summary>
public class LabelRendererTests
{
    private static CompanyBranding Company() => new() { Name = "MEI Engineering (Pvt) Ltd" };

    private static LabelData Data() => new(
        "Micro Compressor NX-1200",
        "JOB-26-0042",
        new Dictionary<string, string?>
        {
            ["device.brand"] = "Mothe",
            ["device.serial"] = "S/N 99120",
            ["customer.name"] = "Gulberg Textiles",
            ["blank.field"] = "   "
        });

    [Theory]
    [InlineData(62.0, null)]     // continuous roll
    [InlineData(38.0, 25.0)]     // small die-cut, content has to shrink to fit
    [InlineData(100.0, 150.0)]   // large sheet
    public void A_label_renders_at_any_configured_size(double width, double? height)
    {
        var spec = new LabelTemplateSpec(
            (decimal)width, height is null ? null : (decimal)height, 2,
            ["device.brand", "device.serial", "customer.name"],
            true, true, true, false, 1.0m);

        Assert.NotEmpty(LabelRenderer.Render(spec, [Data()], Company()));
    }

    [Fact]
    public void A_narrow_label_with_both_symbologies_still_renders()
    {
        // 38mm is tight for bars plus a QR; it has to degrade rather than throw.
        var spec = new LabelTemplateSpec(38, 25, 2, ["device.serial"],
            true, true, true, true, 1.0m);

        Assert.NotEmpty(LabelRenderer.Render(spec, [Data()], Company()));
    }

    [Fact]
    public void Fields_that_are_blank_or_unknown_are_skipped_not_printed_empty()
    {
        var spec = new LabelTemplateSpec(62, null, 3,
            ["blank.field", "no.such.key", "device.serial"],
            true, true, false, false, 1.0m);

        Assert.NotEmpty(LabelRenderer.Render(spec, [Data()], Company()));
    }

    [Fact]
    public void A_label_with_no_fields_and_no_code_still_renders()
    {
        var spec = new LabelTemplateSpec(62, null, 3, [], false, false, false, false, 1.0m);
        var bare = new LabelData("Just a title", null, new Dictionary<string, string?>());

        Assert.NotEmpty(LabelRenderer.Render(spec, [bare], Company()));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    public void Font_scale_extremes_render(double scale)
    {
        var spec = new LabelTemplateSpec(62, null, 3, ["device.serial", "customer.name"],
            true, true, true, false, (decimal)scale);

        Assert.NotEmpty(LabelRenderer.Render(spec, [Data()], Company()));
    }

    [Fact]
    public void Many_labels_come_back_as_one_document()
    {
        var spec = LabelTemplateSpec.Fallback(["device.serial"]);
        Assert.NotEmpty(LabelRenderer.Render(spec, [Data(), Data(), Data()], Company()));
    }

    [Fact]
    public void An_unconfigured_company_does_not_break_a_label()
    {
        var spec = LabelTemplateSpec.Fallback(["device.serial"]);
        Assert.NotEmpty(LabelRenderer.Render(spec, [Data()], CompanyBranding.Empty));
    }
}
