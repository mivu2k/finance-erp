namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// The letterhead as a print stack sees it: what to draw, with nothing about where
/// it is stored.
/// </summary>
/// <remarks>
/// Lives in the kernel so every layer can name it — the storage entity
/// (<c>CompanyProfile</c>, in the shared identity database), the PDF helpers, and
/// Finance's <c>PdfDocument</c> DTO, which must not take a dependency on EF Core to
/// put a logo on a page.
/// </remarks>
public record CompanyBranding
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Line under the name — a trading style or strapline.</summary>
    public string? Tagline { get; init; }
    /// <summary>Address as printed, already joined.</summary>
    public string? Address { get; init; }
    /// <summary>Phone / email / web on one line, already joined.</summary>
    public string? Contact { get; init; }
    /// <summary>NTN / VAT / tax registration, printed on commercial documents.</summary>
    public string? TaxNumber { get; init; }
    /// <summary>Small print at the foot of every page — bank details, a disclaimer.</summary>
    public string? FooterNote { get; init; }

    public byte[]? Logo { get; init; }

    public bool HasLogo => Logo is { Length: > 0 };

    /// <summary>
    /// What an unconfigured platform prints. A blank name is better than a
    /// placeholder someone forgets to change before the first invoice goes out.
    /// </summary>
    public static readonly CompanyBranding Empty = new();
}
