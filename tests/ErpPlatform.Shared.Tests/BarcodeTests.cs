using ErpPlatform.Shared.Kernel;
using Xunit;

namespace ErpPlatform.Shared.Tests;

/// <summary>
/// A wrong barcode is worse than no barcode: it scans cleanly as the wrong job.
/// These tests decode the generated pattern back to text, which exercises the
/// symbol table, the set switching and the check digit together.
/// </summary>
public class BarcodeTests
{
    [Theory]
    [InlineData("JOB-26-0042")]
    [InlineData("INT-26-0001")]
    [InlineData("SO-26-0117")]
    [InlineData("12345678")]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData("QTN-26-0001")]
    [InlineData("Mixed 123 Text 4567890")]
    public void Encoded_barcodes_decode_back_to_the_original(string text)
    {
        var pattern = Barcode.Encode(text);

        Assert.Equal(text, Decode(pattern));
    }

    [Fact]
    public void A_long_digit_run_packs_two_digits_per_symbol()
    {
        // Set C is the reason a 12-digit number still fits on an 80mm slip.
        var packed = Barcode.Encode("123456789012");
        var loose = Barcode.Encode("ABCDEFGHIJKL");

        Assert.True(packed.TotalModules < loose.TotalModules,
            "digits should encode more densely than letters");
    }

    [Fact]
    public void The_pattern_starts_and_ends_with_a_bar()
    {
        // Odd module count means bar-first, bar-last — a scanner needs both.
        var pattern = Barcode.Encode("JOB-26-0042");

        Assert.Equal(1, pattern.Modules.Count % 2);
    }

    [Fact]
    public void Every_module_is_a_legal_width()
    {
        var pattern = Barcode.Encode("JOB-26-0042");

        Assert.All(pattern.Modules, m => Assert.InRange(m, 1, 4));
    }

    [Fact]
    public void A_character_outside_set_B_is_rejected_rather_than_mangled()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Encode("café"));
        Assert.Throws<ArgumentException>(() => Barcode.Encode("line\nbreak"));
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Encode(""));
    }

    [Fact]
    public void Svg_output_is_self_contained_and_carries_the_text()
    {
        var svg = Barcode.ToSvg("JOB-26-0042");

        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg);
        Assert.Contains("JOB-26-0042", svg);
        Assert.DoesNotContain("http://", svg.Replace("http://www.w3.org/2000/svg", ""));
    }

    [Fact]
    public void Data_uri_is_usable_in_an_img_tag()
    {
        var uri = Barcode.ToDataUri("JOB-26-0042");

        Assert.StartsWith("data:image/svg+xml;base64,", uri);
    }

    // --- a minimal Code 128 decoder, used only to verify the encoder ---

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

    private static string Decode(Barcode.Pattern pattern)
    {
        var digits = string.Concat(pattern.Modules);

        var codes = new List<int>();
        var i = 0;
        while (i < digits.Length)
        {
            // The stop symbol is seven modules wide; every other symbol is six.
            var width = digits.Length - i == 7 ? 7 : 6;
            var chunk = digits.Substring(i, width);

            var code = Array.IndexOf(Symbols, chunk);
            Assert.True(code >= 0, $"unknown symbol '{chunk}'");
            codes.Add(code);
            i += width;
        }

        Assert.Equal(106, codes[^1]);        // stop
        codes.RemoveAt(codes.Count - 1);

        var check = codes[^1];
        codes.RemoveAt(codes.Count - 1);

        var expected = codes[0];
        for (var n = 1; n < codes.Count; n++) expected += codes[n] * n;
        Assert.Equal(expected % 103, check);

        var setC = codes[0] == 105;
        Assert.True(codes[0] is 104 or 105, "must start in set B or C");

        var text = new System.Text.StringBuilder();
        foreach (var code in codes.Skip(1))
        {
            if (code == 99) { setC = true; continue; }
            if (code == 100) { setC = false; continue; }

            if (setC) text.Append(code.ToString("00"));
            else text.Append((char)(code + 32));
        }

        return text.ToString();
    }
}
