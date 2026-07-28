using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Repair.Domain;
using Repair.Infrastructure;
using Repair.Infrastructure.Reports;

namespace Repair.Web;

/// <summary>
/// Every printable document, at every step of the flow. Each carries a Code 128
/// barcode of its own number, so a scanner in the workshop can get from a piece of
/// paper back to the record.
/// </summary>
public static class PrintEndpoints
{
    // TODO: read this from the platform settings once they're shared across apps.
    private const string CompanyName = "MEI";

    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapRepairPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var print = app.MapGroup("/repair/print");

        // --- receiving ---

        print.MapGet("/intake/{id:int}/{size?}", async (
            int id, string? size, IIntakeService intakes, IRepairPrintService printer) =>
        {
            var intake = await intakes.GetAsync(id);
            if (intake is null) return Results.NotFound();

            return Pdf(printer.IntakeReceipt(intake, Size(size), CompanyName),
                $"{intake.IntakeNumber}-receipt");
        }).RequireAuthorization(RepairPermissions.IntakesView);

        print.MapGet("/intake/{id:int}/labels", async (
            int id, IIntakeService intakes, IRepairPrintService printer) =>
        {
            var intake = await intakes.GetAsync(id);
            if (intake is null) return Results.NotFound();

            return Pdf(printer.DeviceLabels(intake, CompanyName), $"{intake.IntakeNumber}-labels");
        }).RequireAuthorization(RepairPermissions.IntakesView);

        // --- workshop ---

        print.MapGet("/job/{id:int}", async (
            int id, IRepairJobService jobs, IRepairPrintService printer) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Pdf(printer.JobCard(job, CompanyName), $"{job.JobNumber}-job-card");
        }).RequireAuthorization(RepairPermissions.JobsView);

        print.MapGet("/job/{id:int}/label", async (
            int id, IRepairJobService jobs, IRepairPrintService printer) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Pdf(printer.JobLabel(job, CompanyName), $"{job.JobNumber}-label");
        }).RequireAuthorization(RepairPermissions.JobsView);

        // --- delivering ---

        print.MapGet("/delivery/{id:int}/{size?}", async (
            int id, string? size, IRepairJobService jobs, IRepairPrintService printer) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Pdf(printer.DeliveryNote(job, Size(size), CompanyName),
                $"{job.JobNumber}-delivery");
        }).RequireAuthorization(RepairPermissions.JobsView);

        // --- commercial ---

        print.MapGet("/quotation/{id:int}", async (
            int id, IQuotationService quotations, IRepairPrintService printer) =>
        {
            var quotation = await quotations.GetAsync(id);
            if (quotation is null) return Results.NotFound();

            return Pdf(printer.Quotation(quotation, CompanyName), quotation.QuotationNumber);
        }).RequireAuthorization(RepairPermissions.QuotationsView);

        print.MapGet("/invoice/{id:int}/{size?}", async (
            int id, string? size, ISalesOrderService orders, IRepairPrintService printer) =>
        {
            var order = await orders.GetAsync(id);
            if (order is null) return Results.NotFound();

            return Pdf(printer.Invoice(order, Size(size), CompanyName),
                $"{order.OrderNumber}-invoice");
        }).RequireAuthorization(RepairPermissions.OrdersView);

        // --- purchasing ---

        print.MapGet("/purchase/{id:int}", async (
            int id, IPurchaseService purchases, IRepairPrintService printer) =>
        {
            var purchase = await purchases.GetAsync(id);
            if (purchase is null) return Results.NotFound();

            return Pdf(printer.PurchaseNote(purchase, CompanyName), purchase.PurchaseNumber);
        }).RequireAuthorization(RepairPermissions.PurchasesView);

        MapReportEndpoints(app);
        return app;
    }

    private static void MapReportEndpoints(IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/repair/reports/export");

        reports.MapGet("/{kind}/{format}", async (
            string kind, string format, DateOnly? from, DateOnly? to,
            ReportTableBuilder builder, IReportExportService export) =>
        {
            if (!Enum.TryParse<ReportKind>(kind, ignoreCase: true, out var reportKind))
                return Results.BadRequest($"Unknown report '{kind}'.");

            var range = Range(from, to);
            var definition = ReportCatalog.Find(reportKind);
            var tables = await builder.BuildAsync(reportKind, range);
            var subtitle = Subtitle(definition, range);

            return Render(format, export, definition.Title, subtitle, tables,
                $"{reportKind}-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}");
        }).RequireAuthorization(RepairPermissions.ReportsView);

        // The whole pack in one file, for a management review.
        reports.MapGet("/all/{format}", async (
            string format, DateOnly? from, DateOnly? to,
            ReportTableBuilder builder, IReportExportService export) =>
        {
            var range = Range(from, to);
            var tables = await builder.BuildAllAsync(range);

            return Render(format, export, "Repair Reports",
                $"{range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd}", tables,
                $"repair-reports-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}");
        }).RequireAuthorization(RepairPermissions.ReportsView);
    }

    private static IResult Render(
        string format, IReportExportService export, string title, string subtitle,
        IReadOnlyList<ReportTable> tables, string fileName) =>
        format.ToLowerInvariant() switch
        {
            "xlsx" or "excel" => Results.File(
                export.ToExcel(title, tables), ExcelContentType, $"{fileName}.xlsx"),
            "pdf" => Results.File(
                export.ToPdf(title, subtitle, CompanyName, tables),
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

    private static IResult Pdf(byte[] bytes, string fileName) =>
        Results.File(bytes, "application/pdf", $"{fileName}.pdf");
}
