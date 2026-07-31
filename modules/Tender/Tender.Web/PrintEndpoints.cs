using ErpPlatform.Shared.Identity;
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

        return app;
    }

    private static IResult Pdf(byte[] bytes, string fileName) =>
        Results.File(bytes, "application/pdf", $"{fileName}.pdf");
}
