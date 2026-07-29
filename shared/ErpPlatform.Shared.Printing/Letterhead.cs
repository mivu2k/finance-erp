using ErpPlatform.Shared.Kernel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ErpPlatform.Shared.Printing;

/// <summary>
/// The company letterhead, drawn identically on every document in every app.
/// </summary>
/// <remarks>
/// Each module keeps its own page setup — paper size, margins, body — but the
/// branding block and the footer strip live here so a logo change lands on all
/// four apps at once instead of three near-identical copies drifting apart.
/// </remarks>
public static class Letterhead
{
    /// <summary>Logo box on an A4 page, in points. Wide enough for a wordmark.</summary>
    private const float LogoWidth = 130;
    private const float LogoHeight = 46;

    /// <summary>
    /// Full-width A4 header: logo and company details on the left, document title
    /// and whatever the module wants (a barcode, a QR) on the right.
    /// </summary>
    public static void CompanyHeader(
        this IContainer container, CompanyBranding company, string title,
        Action<IContainer>? rightSide = null)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Row(brand =>
                {
                    if (company.HasLogo)
                        brand.ConstantItem(LogoWidth).PaddingRight(10).AlignMiddle()
                            .MaxHeight(LogoHeight).Image(company.Logo!).FitArea();

                    brand.RelativeItem().Column(c =>
                    {
                        c.Item().Text(company.Name).FontSize(15).Bold();

                        if (!string.IsNullOrWhiteSpace(company.Tagline))
                            c.Item().Text(company.Tagline).FontSize(8)
                                .FontColor(Colors.Grey.Darken1);

                        if (!string.IsNullOrWhiteSpace(company.Address))
                            c.Item().Text(company.Address!).FontSize(8)
                                .FontColor(Colors.Grey.Darken2);

                        if (!string.IsNullOrWhiteSpace(company.Contact))
                            c.Item().Text(company.Contact!).FontSize(8)
                                .FontColor(Colors.Grey.Darken2);

                        if (!string.IsNullOrWhiteSpace(company.TaxNumber))
                            c.Item().Text($"Tax No. {company.TaxNumber}").FontSize(8)
                                .FontColor(Colors.Grey.Darken2);

                        c.Item().PaddingTop(4).Text(title).FontSize(12).SemiBold();
                    });
                });

                if (rightSide is not null)
                    row.AutoItem().PaddingLeft(10).AlignRight().Element(rightSide);
            });

            col.Item().PaddingTop(6).LineHorizontal(1);
        });
    }

    /// <summary>
    /// Footer strip: the document number and print stamp, the company's small print
    /// if it has any, and the page counter.
    /// </summary>
    public static void CompanyFooter(
        this IContainer container, CompanyBranding company, string documentNumber)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);

            if (!string.IsNullOrWhiteSpace(company.FooterNote))
                col.Item().PaddingBottom(2).Text(company.FooterNote)
                    .FontSize(7).FontColor(Colors.Grey.Darken1);

            col.Item().Row(r =>
            {
                r.RelativeItem()
                    .Text($"{documentNumber} · printed {DateTime.Now:yyyy-MM-dd HH:mm}")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);

                r.ConstantItem(100).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });
    }

    /// <summary>
    /// Centred branding for a narrow thermal roll: logo above the name, then the
    /// bare essentials. Address and tax number only — a receipt has no room for more.
    /// </summary>
    public static void PosCompanyHeader(
        this ColumnDescriptor col, CompanyBranding company, string title, float rollWidthPoints)
    {
        if (company.HasLogo)
            col.Item().AlignCenter().MaxHeight(34).MaxWidth(rollWidthPoints * 0.7f)
                .Image(company.Logo!).FitArea();

        col.Item().AlignCenter().Text(company.Name).Bold().FontSize(10);

        if (!string.IsNullOrWhiteSpace(company.Address))
            col.Item().AlignCenter().Text(company.Address!).FontSize(6.5f);

        if (!string.IsNullOrWhiteSpace(company.Contact))
            col.Item().AlignCenter().Text(company.Contact!).FontSize(6.5f);

        if (!string.IsNullOrWhiteSpace(company.TaxNumber))
            col.Item().AlignCenter().Text($"Tax No. {company.TaxNumber}").FontSize(6.5f);

        col.Item().PaddingTop(2).AlignCenter().Text(title).Bold().FontSize(9);
    }

    /// <summary>Closing small print on a thermal roll.</summary>
    public static void PosCompanyFooter(this ColumnDescriptor col, CompanyBranding company)
    {
        if (!string.IsNullOrWhiteSpace(company.FooterNote))
            col.Item().PaddingTop(4).AlignCenter().Text(company.FooterNote).FontSize(6.5f);

        col.Item().PaddingTop(4).AlignCenter()
            .Text(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).FontSize(7);
    }
}
