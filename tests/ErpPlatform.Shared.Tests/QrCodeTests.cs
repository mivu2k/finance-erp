using ErpPlatform.Shared.Kernel;
using Xunit;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace ErpPlatform.Shared.Tests;

/// <summary>
/// Same bargain as <see cref="BarcodeTests"/>: a QR code that decodes to the wrong
/// job is worse than none at all. These run the generated matrix through ZXing's
/// decoder, which checks placement, masking, the format bits, block interleaving
/// and the Reed-Solomon parity together — an independent implementation reading
/// what ours wrote.
/// </summary>
public class QrCodeTests
{
    [Theory]
    [InlineData("JOB-26-0042")]
    [InlineData("INT-26-0001")]
    [InlineData("QTN-26-0001")]
    [InlineData("SO-26-0117")]
    [InlineData("7")]
    [InlineData("Mixed 123 Text 4567890")]
    [InlineData("https://erp.local/repair/scan?code=JOB-26-0042")]
    public void Encoded_qr_codes_decode_back_to_the_original(string text)
    {
        Assert.Equal(text, Decode(QrCode.Encode(text)));
    }

    [Fact]
    public void A_payload_needing_a_bigger_symbol_still_decodes()
    {
        // Long enough to push past version 1 and into multiple error-correction
        // blocks, which is where interleaving goes wrong if it goes wrong.
        var text = string.Join(",", Enumerable.Range(1, 15).Select(i => $"JOB-26-{i:0000}"));

        Assert.Equal(text, Decode(QrCode.Encode(text)));
    }

    [Fact]
    public void Non_ascii_survives_the_round_trip()
    {
        Assert.Equal("Küche — naïve", Decode(QrCode.Encode("Küche — naïve")));
    }

    [Fact]
    public void The_smallest_symbol_that_fits_is_chosen()
    {
        // Version 1 holds 14 bytes at level M; 15 must step up to version 2 (25 modules).
        Assert.Equal(21, QrCode.Encode(new string('A', 14)).Size);
        Assert.Equal(25, QrCode.Encode(new string('A', 15)).Size);
    }

    [Fact]
    public void A_payload_too_large_for_version_10_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('A', 214)));
    }

    [Fact]
    public void An_empty_payload_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(""));
    }

    private static string Decode(QrCode.Matrix matrix)
    {
        var bits = new BitMatrix(matrix.Size, matrix.Size);
        for (var y = 0; y < matrix.Size; y++)
        for (var x = 0; x < matrix.Size; x++)
            if (matrix[x, y])
                bits[x, y] = true;

        return new Decoder().decode(bits, null).Text;
    }
}
