using ErpPlatform.Shared.Kernel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ErpPlatform.Shared.Printing;

/// <summary>Draws a Code 128 barcode into a PDF as a row of filled rectangles.</summary>
/// <remarks>
/// Lives in the shared printing library rather than one module: every module that
/// prints a document puts the same Code 128 and QR on it, and the label renderer
/// needs it too.
/// </remarks>
public static class BarcodeRenderer
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
    /// Draws a QR code of <paramref name="text"/> at a fixed edge length in points.
    /// </summary>
    /// <remarks>
    /// Always fixed-size, never stretched: a QR code has to stay square, and phone
    /// cameras need roughly 20pt (7mm) of edge before they lock on reliably.
    /// </remarks>
    public static void QrCode(this IContainer container, string text, float size = 60)
    {
        QrCode.Matrix matrix;
        try
        {
            matrix = ErpPlatform.Shared.Kernel.QrCode.Encode(text);
        }
        catch (ArgumentException)
        {
            // Never fail a whole document over an unencodable payload.
            container.Text(text).FontSize(8);
            return;
        }

        // The quiet zone is part of the symbol — without it a scanner sitting on a
        // busy label can't find the finder patterns.
        const int quiet = 4;
        var cells = matrix.Size + quiet * 2;
        var cell = size / cells;

        container.Width(size).Height(size).Column(col =>
        {
            for (var y = 0; y < cells; y++)
            {
                col.Item().Height(cell).Row(row =>
                {
                    for (var x = 0; x < cells; x++)
                    {
                        var c = row.ConstantItem(cell);
                        var inside = x >= quiet && y >= quiet
                                     && x < cells - quiet && y < cells - quiet;
                        if (inside && matrix[x - quiet, y - quiet]) c.Background(Colors.Black);
                    }
                });
            }
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
