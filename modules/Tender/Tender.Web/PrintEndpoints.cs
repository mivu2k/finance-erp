using ErpPlatform.Shared.Identity;
using ErpPlatform.Shared.Printing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tender.Domain;
using Tender.Infrastructure;

namespace Tender.Web;

/// <summary>Printable tender documents and reports.</summary>
public static class PrintEndpoints
{
    public static IEndpointRouteBuilder MapTenderPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var print = app.MapGroup("/tender/print");

        print.MapGet("/report/{key}", async (
            string key, ITenderReportService reports, ITenderPrintService printer, ICompanyProfileService companies) =>
        {
            var definition = reports.Find(key);
            if (definition is null) return Results.NotFound();

            var report = await reports.BuildAsync(key);
            return Pdf(printer.Report(report, await companies.GetBrandingAsync(), definition.Landscape),
                $"tender-{key}");
        }).RequireAuthorization(TenderPermissions.ReportsView);

        print.MapGet("/summary/{id:int}", async (
            int id, ITenderService tenders, ITenderPrintService printer, ICompanyProfileService companies) =>
        {
            var tender = await tenders.GetAsync(id);
            if (tender is null) return Results.NotFound();

            return Pdf(printer.TenderSummarySheet(tender, await companies.GetBrandingAsync()),
                $"{tender.TenderNumber}-summary");
        }).RequireAuthorization(TenderPermissions.TendersView);

        print.MapGet("/security-register/{id:int}", async (
            int id, ITenderService tenders, ITenderPrintService printer, ICompanyProfileService companies) =>
        {
            var tender = await tenders.GetAsync(id);
            if (tender is null) return Results.NotFound();

            return Pdf(printer.SecurityRegister(tender, await companies.GetBrandingAsync()),
                $"{tender.TenderNumber}-securities");
        }).RequireAuthorization(TenderPermissions.TendersView);

        print.MapGet("/file-sticker/{id:int}", async (
            int id, int? templateId, IFileRegistryService files, ITenderPrintService printer,
            ICompanyProfileService companies, ILabelTemplateService labels) =>
        {
            var file = await files.GetAsync(id);
            if (file is null) return Results.NotFound();

            return Pdf(printer.FileStickers([file], await companies.GetBrandingAsync(),
                    await ResolveLabelAsync(labels, templateId)),
                $"{file.FileNumber}-sticker");
        }).RequireAuthorization(TenderPermissions.FilesView);

        // The whole registry on one roll — what you print after a bulk file-opening
        // session rather than clicking through one record at a time.
        print.MapGet("/file-stickers", async (
            int? templateId, FileStatus? status, FileOwnerType? ownerType,
            IFileRegistryService files, ITenderPrintService printer,
            ICompanyProfileService companies, ILabelTemplateService labels) =>
        {
            var list = await files.ListAsync(new FileFilter(Status: status, OwnerType: ownerType));
            if (list.Count == 0) return Results.NotFound();

            return Pdf(printer.FileStickers(list, await companies.GetBrandingAsync(),
                    await ResolveLabelAsync(labels, templateId)),
                "file-stickers");
        }).RequireAuthorization(TenderPermissions.FilesView);

        print.MapGet("/file-movements/{id:int}", async (
            int id, IFileRegistryService files, ITenderPrintService printer,
            ICompanyProfileService companies) =>
        {
            var file = await files.GetAsync(id);
            if (file is null) return Results.NotFound();

            return Pdf(printer.FileMovementRegister(file, await companies.GetBrandingAsync()),
                $"{file.FileNumber}-movements");
        }).RequireAuthorization(TenderPermissions.FilesView);

        return app;
    }

    /// <summary>
    /// Turns a saved template into the flat spec the renderer wants, or null when none
    /// applies — null means the built-in layout, so stickers keep working on an install
    /// where nobody has configured one.
    /// </summary>
    private static async Task<LabelTemplateSpec?> ResolveLabelAsync(
        ILabelTemplateService labels, int? templateId)
    {
        var t = templateId is { } id
            ? await labels.GetAsync(id)
            : await labels.GetDefaultAsync(LabelDocumentTypes.TenderFile);

        // A template saved for another kind of record would ask for fields a file
        // can't supply, so it is ignored rather than printed half-empty.
        if (t is null || t.DocumentType != LabelDocumentTypes.TenderFile) return null;

        return new LabelTemplateSpec(
            t.WidthMm, t.HeightMm, t.MarginMm, t.SelectedFields(),
            t.ShowTitle, t.ShowCompanyName, t.ShowBarcode, t.ShowQrCode, t.FontScale);
    }

    private static IResult Pdf(byte[] bytes, string fileName) =>
        Results.File(bytes, "application/pdf", $"{fileName}.pdf");
}
