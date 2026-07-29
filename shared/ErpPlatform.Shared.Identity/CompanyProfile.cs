using ErpPlatform.Shared.Kernel;
using Microsoft.EntityFrameworkCore;

namespace ErpPlatform.Shared.Identity;

/// <summary>
/// The letterhead: who we are, as it appears on every printed document across
/// every app. Exactly one row.
/// </summary>
/// <remarks>
/// This lives in the shared identity database because it is the only database all
/// four apps can see, and branding is plainly platform-wide rather than any one
/// module's business data. Modules only ever read it, through
/// <see cref="ICompanyProfileService"/> — the no-writes rule still holds.
/// </remarks>
public class CompanyProfile
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    /// <summary>Line under the name — a trading style or strapline.</summary>
    public string? Tagline { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    /// <summary>NTN / VAT / tax registration, printed on commercial documents.</summary>
    public string? TaxNumber { get; set; }

    /// <summary>Small print at the foot of every page — bank details, a disclaimer.</summary>
    public string? FooterNote { get; set; }

    /// <summary>
    /// The logo itself, stored inline rather than on disk: it is one small image
    /// read on every print, and keeping it in the row means a database restore
    /// brings the letterhead back with it.
    /// </summary>
    public byte[]? Logo { get; set; }
    public string? LogoContentType { get; set; }
    public string? LogoFileName { get; set; }

    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    public bool HasLogo => Logo is { Length: > 0 };

    /// <summary>Flattens the row into what the print stacks actually draw.</summary>
    public CompanyBranding ToBranding() => new()
    {
        Name = Name,
        Tagline = Blank(Tagline),
        Address = Blank(string.Join(", ",
            new[] { AddressLine1, AddressLine2, City, Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)))),
        Contact = Blank(string.Join("  ·  ",
            new[] { Phone, Email, Website }.Where(s => !string.IsNullOrWhiteSpace(s)))),
        TaxNumber = Blank(TaxNumber),
        FooterNote = Blank(FooterNote),
        Logo = Logo
    };

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Reads and updates the one company profile row.</summary>
public interface ICompanyProfileService
{
    /// <summary>
    /// The current profile, cached. Never null — an unconfigured platform gets a
    /// blank profile rather than a null check at every print site.
    /// </summary>
    Task<CompanyProfile> GetAsync(CancellationToken ct = default);

    /// <summary>The profile flattened for a print stack. What every PDF endpoint wants.</summary>
    async Task<CompanyBranding> GetBrandingAsync(CancellationToken ct = default) =>
        (await GetAsync(ct)).ToBranding();

    Task SaveAsync(CompanyProfile profile, string? modifiedBy, CancellationToken ct = default);
    Task SetLogoAsync(byte[]? logo, string? contentType, string? fileName, string? modifiedBy,
        CancellationToken ct = default);
}

public class CompanyProfileService(PlatformIdentityDbContext db) : ICompanyProfileService
{
    /// <summary>
    /// Every printed page reads this, so it is cached process-wide and dropped on
    /// save. One process hosts every app, so a single static is enough.
    /// </summary>
    private static CompanyProfile? _cached;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<CompanyProfile> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        await Gate.WaitAsync(ct);
        try
        {
            return _cached ??= await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(ct)
                               ?? new CompanyProfile();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(CompanyProfile profile, string? modifiedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("The company name is what heads every document — it can't be blank.");

        var row = await RowAsync(ct);
        row.Name = profile.Name.Trim();
        row.Tagline = profile.Tagline;
        row.AddressLine1 = profile.AddressLine1;
        row.AddressLine2 = profile.AddressLine2;
        row.City = profile.City;
        row.Country = profile.Country;
        row.Phone = profile.Phone;
        row.Email = profile.Email;
        row.Website = profile.Website;
        row.TaxNumber = profile.TaxNumber;
        row.FooterNote = profile.FooterNote;
        Stamp(row, modifiedBy);

        await db.SaveChangesAsync(ct);
        Invalidate();
    }

    public async Task SetLogoAsync(byte[]? logo, string? contentType, string? fileName,
        string? modifiedBy, CancellationToken ct = default)
    {
        var row = await RowAsync(ct);
        row.Logo = logo;
        row.LogoContentType = logo is null ? null : contentType;
        row.LogoFileName = logo is null ? null : fileName;
        Stamp(row, modifiedBy);

        await db.SaveChangesAsync(ct);
        Invalidate();
    }

    private async Task<CompanyProfile> RowAsync(CancellationToken ct)
    {
        var row = await db.CompanyProfiles.FirstOrDefaultAsync(ct);
        if (row is not null) return row;

        row = new CompanyProfile();
        db.CompanyProfiles.Add(row);
        return row;
    }

    private static void Stamp(CompanyProfile row, string? modifiedBy)
    {
        row.ModifiedAtUtc = DateTime.UtcNow;
        row.ModifiedBy = modifiedBy;
    }

    private static void Invalidate() => _cached = null;
}
