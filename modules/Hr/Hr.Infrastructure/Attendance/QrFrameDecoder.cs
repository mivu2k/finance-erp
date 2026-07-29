using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Hr.Infrastructure.Attendance;

/// <summary>
/// Reads a QR code out of a still frame captured from a webcam.
/// </summary>
/// <remarks>
/// Decoding happens here rather than in the browser deliberately: the Shape
/// Detection API a browser would use is absent on several of the platforms a
/// kiosk PC might run, and the alternative is shipping a third-party decoder into
/// the page. A frame is a few kilobytes on a LAN, so the round trip costs less
/// than the compatibility problem it avoids.
/// </remarks>
public interface IQrFrameDecoder
{
    /// <summary>The QR payload in this frame, or null if there isn't one.</summary>
    string? Decode(byte[] image);
}

public class QrFrameDecoder : IQrFrameDecoder
{
    public string? Decode(byte[] image)
    {
        // A frame that isn't a decodable image is routine, not exceptional: the
        // browser can hand us a truncated capture, and SkiaSharp throws rather than
        // returning null for it. At several frames a second an unguarded throw here
        // is a continuous stream of 500s.
        SKBitmap? bitmap;
        try
        {
            bitmap = SKBitmap.Decode(image);
        }
        catch (Exception)
        {
            return null;
        }

        if (bitmap is null) return null;
        using var _ = bitmap;

        // ZXing wants 8-bit luminance. Doing the conversion here rather than
        // letting a binding guess the pixel layout keeps it independent of
        // whatever the browser encoded.
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, y);
            luminance[y * bitmap.Width + x] = (byte)((p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000);
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                // A phone held at an angle under a webcam is the normal case here,
                // not the exception, so pay for the harder search.
                TryHarder = true
            }
        };

        return reader.Decode(new RGBLuminanceSource(
            luminance, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.Gray8))?.Text;
    }
}
