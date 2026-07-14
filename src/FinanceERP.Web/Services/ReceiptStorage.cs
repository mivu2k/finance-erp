using Microsoft.AspNetCore.Components.Forms;

namespace FinanceERP.Web.Services;

/// <summary>
/// Stores uploaded receipts under {ContentRoot}/uploads/receipts (outside wwwroot,
/// so files are only reachable through the authenticated /files/receipts endpoint).
/// </summary>
public class ReceiptStorage(IWebHostEnvironment env)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".pdf"];

    public string Root => Path.Combine(env.ContentRootPath, "uploads", "receipts");

    public async Task<(string StoredName, string OriginalName)> SaveAsync(IBrowserFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException($"Only images and PDFs are allowed ({string.Join(", ", AllowedExtensions)}).");
        if (file.Size > MaxBytes)
            throw new InvalidOperationException("Receipt must be 5 MB or smaller.");

        Directory.CreateDirectory(Root);
        var storedName = $"{Guid.NewGuid():N}{ext}";
        await using var target = File.Create(Path.Combine(Root, storedName));
        await file.OpenReadStream(MaxBytes, ct).CopyToAsync(target, ct);
        return (storedName, file.Name);
    }

    public string? Resolve(string storedName)
    {
        // Path.GetFileName strips any traversal attempt.
        var path = Path.Combine(Root, Path.GetFileName(storedName));
        return File.Exists(path) ? path : null;
    }

    public static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };
}
