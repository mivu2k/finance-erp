using System.Text;

namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// Code 128 encoder. Returns the bar/space pattern as module widths so the caller
/// can draw it — into a PDF, an SVG, or anything else.
/// </summary>
/// <remarks>
/// Written out rather than taken from a package: the symbology is small and fixed,
/// and every document in the platform depends on it, so it's worth owning. Code
/// 128 is the right choice here — it carries the full ASCII range (our numbers
/// look like <c>JOB-26-0042</c>), packs digit pairs into single symbols, and every
/// cheap USB scanner reads it out of the box as keyboard input.
/// </remarks>
public static class Barcode
{
    /// <summary>Bar and space widths, in modules, starting with a bar.</summary>
    public record Pattern(IReadOnlyList<int> Modules, string Text)
    {
        /// <summary>Total width in modules — what a caller scales to fit.</summary>
        public int TotalModules => Modules.Sum();
    }

    // Each symbol is six digits: the widths of bar, space, bar, space, bar, space.
    private static readonly string[] Symbols =
    [
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213",
        "221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
        "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211",
        "212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
        "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331",
        "231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214",
        "112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
        "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141",
        "214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
        "114131","311141","411131","211412","211214","211232","2331112"
    ];

    private const int StartB = 104;
    private const int StartC = 105;
    private const int CodeB = 100;
    private const int CodeC = 99;
    private const int Stop = 106;

    /// <summary>
    /// Encodes text as Code 128. Switches between sets B and C so runs of digits
    /// pack two to a symbol, which keeps document numbers physically short enough
    /// to fit on an 80mm slip and a device label.
    /// </summary>
    /// <exception cref="ArgumentException">The text contains a character Code 128 set B can't carry.</exception>
    public static Pattern Encode(string text)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Nothing to encode.", nameof(text));

        foreach (var c in text)
            if (c is < ' ' or > '~')
                throw new ArgumentException(
                    $"'{c}' can't be encoded in Code 128 set B.", nameof(text));

        var codes = new List<int>();
        var position = 0;
        var inSetC = ShouldStartInC(text);

        codes.Add(inSetC ? StartC : StartB);

        while (position < text.Length)
        {
            if (inSetC)
            {
                if (DigitsAt(text, position) >= 2)
                {
                    codes.Add(int.Parse(text.Substring(position, 2)));
                    position += 2;
                }
                else
                {
                    codes.Add(CodeB);
                    inSetC = false;
                }
            }
            else
            {
                // Only worth switching to C for a decent run, since the switch
                // itself costs a symbol.
                var run = DigitsAt(text, position);
                var enough = run >= (position + run == text.Length ? 4 : 6) && run % 2 == 0;

                if (enough)
                {
                    codes.Add(CodeC);
                    inSetC = true;
                }
                else
                {
                    codes.Add(text[position] - 32);
                    position++;
                }
            }
        }

        // Modulo-103 check symbol, weighted by position.
        var checksum = codes[0];
        for (var i = 1; i < codes.Count; i++) checksum += codes[i] * i;
        codes.Add(checksum % 103);

        codes.Add(Stop);

        var modules = new List<int>();
        foreach (var code in codes)
            foreach (var width in Symbols[code])
                modules.Add(width - '0');

        return new Pattern(modules, text);
    }

    /// <summary>Renders the barcode as a standalone SVG, for showing on screen.</summary>
    public static string ToSvg(string text, int height = 60, int moduleWidth = 2,
        bool showText = true)
    {
        var pattern = Encode(text);
        var width = pattern.TotalModules * moduleWidth;
        var textHeight = showText ? 16 : 0;

        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" ")
           .Append($"height=\"{height + textHeight}\" viewBox=\"0 0 {width} {height + textHeight}\">")
           .Append($"<rect width=\"{width}\" height=\"{height + textHeight}\" fill=\"#fff\"/>");

        var x = 0;
        var isBar = true;
        foreach (var module in pattern.Modules)
        {
            var w = module * moduleWidth;
            if (isBar)
                svg.Append($"<rect x=\"{x}\" y=\"0\" width=\"{w}\" height=\"{height}\" fill=\"#000\"/>");
            x += w;
            isBar = !isBar;
        }

        if (showText)
            svg.Append($"<text x=\"{width / 2}\" y=\"{height + 13}\" text-anchor=\"middle\" ")
               .Append($"font-family=\"monospace\" font-size=\"12\">{System.Net.WebUtility.HtmlEncode(text)}</text>");

        return svg.Append("</svg>").ToString();
    }

    /// <summary>An SVG barcode as a data URI, ready for an <c>img</c> tag.</summary>
    public static string ToDataUri(string text, int height = 60, int moduleWidth = 2,
        bool showText = true) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(ToSvg(text, height, moduleWidth, showText)));

    private static bool ShouldStartInC(string text) =>
        DigitsAt(text, 0) >= 4 || (DigitsAt(text, 0) == text.Length && text.Length % 2 == 0);

    private static int DigitsAt(string text, int start)
    {
        var count = 0;
        while (start + count < text.Length && char.IsAsciiDigit(text[start + count])) count++;
        return count;
    }
}
