using ErpPlatform.Shared.Identity;
using ErpPlatform.Shared.Printing;
using ErpPlatform.Shared.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Repair.Domain;
using Repair.Infrastructure;
using Repair.Infrastructure.Reports;

namespace Repair.Web;

/// <summary>
/// Every printable document, at every step of the flow. Each carries a Code 128
/// barcode and a QR code of its own number, so a scanner in the workshop can get
/// from a piece of paper back to the record, and each is headed with the platform
/// company profile from <see cref="ICompanyProfileService"/>.
/// </summary>
public static class PrintEndpoints
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapRepairPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var print = app.MapGroup("/repair/print");

        // --- receiving ---

        print.MapGet("/intake/{id:int}/{size?}", async (
            int id, string? size, IIntakeService intakes, IRepairPrintService printer, ICompanyProfileService companies) =>
        {
            var intake = await intakes.GetAsync(id);
            if (intake is null) return Results.NotFound();

            return Pdf(printer.IntakeReceipt(intake, Size(size), await companies.GetBrandingAsync()),
                $"{intake.IntakeNumber}-receipt");
        }).RequireAuthorization(RepairPermissions.IntakesView);

        // ?template= picks a saved layout; omitted uses the default for device
        // labels, and with none configured the built-in 62mm layout.
        print.MapGet("/intake/{id:int}/labels", async (
            int id, int? template, IIntakeService intakes, IRepairPrintService printer,
            ICompanyProfileService companies, ILabelTemplateService labels) =>
        {
            var intake = await intakes.GetAsync(id);
            if (intake is null) return Results.NotFound();

            return Pdf(printer.DeviceLabels(intake, await companies.GetBrandingAsync(),
                    await ResolveLabelAsync(labels, template)),
                $"{intake.IntakeNumber}-labels");
        }).RequireAuthorization(RepairPermissions.IntakesView);

        // --- workshop ---

        print.MapGet("/job/{id:int}", async (
            int id, IRepairJobService jobs, IRepairPrintService printer, ICompanyProfileService companies) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Pdf(printer.JobCard(job, await companies.GetBrandingAsync()), $"{job.JobNumber}-job-card");
        }).RequireAuthorization(RepairPermissions.JobsView);

        print.MapGet("/job/{id:int}/label", async (
            int id, int? template, IRepairJobService jobs, IRepairPrintService printer,
            ICompanyProfileService companies, ILabelTemplateService labels) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Pdf(printer.JobLabel(job, await companies.GetBrandingAsync(),
                    await ResolveLabelAsync(labels, template)),
                $"{job.JobNumber}-label");
        }).RequireAuthorization(RepairPermissions.JobsView);

        // --- delivering ---

        print.MapGet("/delivery/{id:int}/{size?}", async (
            int id, string? size, IRepairJobService jobs, IRepairPrintService printer, ICompanyProfileService companies) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Pdf(printer.DeliveryNote(job, Size(size), await companies.GetBrandingAsync()),
                $"{job.JobNumber}-delivery");
        }).RequireAuthorization(RepairPermissions.JobsView);

        // --- commercial ---

        print.MapGet("/quotation/{id:int}", async (
            int id, IQuotationService quotations, IRepairPrintService printer, ICompanyProfileService companies) =>
        {
            var quotation = await quotations.GetAsync(id);
            if (quotation is null) return Results.NotFound();

            return Pdf(printer.Quotation(quotation, await companies.GetBrandingAsync()), quotation.QuotationNumber);
        }).RequireAuthorization(RepairPermissions.QuotationsView);

        print.MapGet("/invoice/{id:int}/{size?}", async (
            int id, string? size, ISalesOrderService orders, IRepairPrintService printer, ICompanyProfileService companies) =>
        {
            var order = await orders.GetAsync(id);
            if (order is null) return Results.NotFound();

            return Pdf(printer.Invoice(order, Size(size), await companies.GetBrandingAsync()),
                $"{order.OrderNumber}-invoice");
        }).RequireAuthorization(RepairPermissions.OrdersView);

        // --- purchasing ---

        print.MapGet("/purchase/{id:int}", async (
            int id, IPurchaseService purchases, IRepairPrintService printer, ICompanyProfileService companies) =>
        {
            var purchase = await purchases.GetAsync(id);
            if (purchase is null) return Results.NotFound();

            return Pdf(printer.PurchaseNote(purchase, await companies.GetBrandingAsync()), purchase.PurchaseNumber);
        }).RequireAuthorization(RepairPermissions.PurchasesView);

        MapReportEndpoints(app);
        return app;
    }

    private static void MapReportEndpoints(IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/repair/reports/export");

        reports.MapGet("/{kind}/{format}", async (
            string kind, string format, DateOnly? from, DateOnly? to,
            ReportTableBuilder builder, IReportExportService export, ICompanyProfileService companies) =>
        {
            if (!Enum.TryParse<ReportKind>(kind, ignoreCase: true, out var reportKind))
                return Results.BadRequest($"Unknown report '{kind}'.");

            var range = Range(from, to);
            var definition = ReportCatalog.Find(reportKind);
            var tables = await builder.BuildAsync(reportKind, range);
            var subtitle = Subtitle(definition, range);

            return Render(format, export, await companies.GetBrandingAsync(),
                definition.Title, subtitle, tables,
                $"{reportKind}-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}");
        }).RequireAuthorization(RepairPermissions.ReportsView);

        // The whole pack in one file, for a management review.
        reports.MapGet("/all/{format}", async (
            string format, DateOnly? from, DateOnly? to,
            ReportTableBuilder builder, IReportExportService export, ICompanyProfileService companies) =>
        {
            var range = Range(from, to);
            var tables = await builder.BuildAllAsync(range);

            return Render(format, export, await companies.GetBrandingAsync(), "Repair Reports",
                $"{range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd}", tables,
                $"repair-reports-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}");
        }).RequireAuthorization(RepairPermissions.ReportsView);
    }

    private static IResult Render(
        string format, IReportExportService export, CompanyBranding company,
        string title, string subtitle, IReadOnlyList<ReportTable> tables, string fileName) =>
        format.ToLowerInvariant() switch
        {
            "xlsx" or "excel" => Results.File(
                export.ToExcel(title, tables), ExcelContentType, $"{fileName}.xlsx"),
            "pdf" => Results.File(
                export.ToPdf(title, subtitle, company, tables),
                "application/pdf", $"{fileName}.pdf"),
            _ => Results.BadRequest("Format must be xlsx or pdf.")
        };

    private static string Subtitle(ReportDefinition definition, ReportRange range) =>
        definition.UsesDateRange
            ? $"{range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd}"
            : $"As at {DateTime.Now:yyyy-MM-dd HH:mm}";

    private static ReportRange Range(DateOnly? from, DateOnly? to) =>
        from is { } f && to is { } t ? new ReportRange(f, t) : ReportRange.ThisMonth();

    private static PrintSize Size(string? size) =>
        string.Equals(size, "pos", StringComparison.OrdinalIgnoreCase)
            ? PrintSize.Pos
            : PrintSize.A4;

    /// <summary>
    /// Turns a saved template into the flat spec the renderer wants, or null when
    /// none applies — null means the built-in layout, so labels keep working on an
    /// install where nobody has configured one.
    /// </summary>
    private static async Task<LabelTemplateSpec?> ResolveLabelAsync(
        ILabelTemplateService labels, int? templateId)
    {
        var t = templateId is { } id
            ? await labels.GetAsync(id)
            : await labels.GetDefaultAsync(LabelDocumentTypes.RepairDevice);

        // A template saved for another kind of record would ask for fields a job
        // can't supply, so it is ignored rather than printed half-empty.
        if (t is null || t.DocumentType != LabelDocumentTypes.RepairDevice) return null;

        return new LabelTemplateSpec(
            t.WidthMm, t.HeightMm, t.MarginMm, t.SelectedFields(),
            t.ShowTitle, t.ShowCompanyName, t.ShowBarcode, t.ShowQrCode, t.FontScale);
    }

    private static IResult Pdf(byte[] bytes, string fileName) =>
        Results.File(bytes, "application/pdf", $"{fileName}.pdf");
}
