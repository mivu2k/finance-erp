using ErpPlatform.Shared.Kernel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Repair.Infrastructure;

/// <summary>Draws a Code 128 barcode into a PDF as a row of filled rectangles.</summary>
internal static class BarcodeRenderer
{
    /// <summary>
    /// Renders <paramref name="text"/> to fill the container's width.
    /// </summary>
    /// <param name="height">Bar height in points. Below about 25 cheap scanners struggle.</param>
    /// <param name="showText">Print the human-readable number under the bars.</param>
    public static void Barcode(
        this IContainer container, string text, float height = 34, bool showText = true)
    {
        Barcode.Pattern pattern;
        try
        {
            pattern = ErpPlatform.Shared.Kernel.Barcode.Encode(text);
        }
        catch (ArgumentException)
        {
            // Never fail a whole document over an unencodable number.
            container.Text(text).FontSize(9);
            return;
        }

        container.Column(col =>
        {
            col.Item().Height(height).Row(row =>
            {
                var isBar = true;
                foreach (var module in pattern.Modules)
                {
                    var cell = row.RelativeItem(module);
                    if (isBar) cell.Background(Colors.Black);
                    isBar = !isBar;
                }
            });

            if (showText)
                col.Item().PaddingTop(2).AlignCenter()
                    .Text(text).FontSize(8).FontFamily(Fonts.Consolas);
        });
    }

    /// <summary>
    /// A fixed-width barcode, for slips where the bars shouldn't stretch across the
    /// whole roll.
    /// </summary>
    public static void BarcodeFixed(
        this IContainer container, string text, float moduleWidth = 1.1f,
        float height = 30, bool showText = true)
    {
        Barcode.Pattern pattern;
        try
        {
            pattern = ErpPlatform.Shared.Kernel.Barcode.Encode(text);
        }
        catch (ArgumentException)
        {
            container.Text(text).FontSize(8);
            return;
        }

        container.Width(pattern.TotalModules * moduleWidth).Column(col =>
        {
            col.Item().Height(height).Row(row =>
            {
                var isBar = true;
                foreach (var module in pattern.Modules)
                {
                    var cell = row.ConstantItem(module * moduleWidth);
                    if (isBar) cell.Background(Colors.Black);
                    isBar = !isBar;
                }
            });

            if (showText)
                col.Item().PaddingTop(2).AlignCenter()
                    .Text(text).FontSize(7).FontFamily(Fonts.Consolas);
        });
    }
}
