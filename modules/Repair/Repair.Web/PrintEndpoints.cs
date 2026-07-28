using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Repair.Domain;
using Repair.Infrastructure;

namespace Repair.Web;

/// <summary>
/// The printable documents carried over from the Laravel Blade templates:
/// job card, intake receipt, quotation and invoice.
/// </summary>
public static class PrintEndpoints
{
    // TODO: read this from the platform settings once they're shared across apps.
    private const string CompanyName = "MEI";

    public static IEndpointRouteBuilder MapRepairPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/repair/print");

        group.MapGet("/job/{id:int}", async (
            int id, IRepairJobService jobs, IRepairPrintService print) =>
        {
            var job = await jobs.GetAsync(id);
            if (job is null) return Results.NotFound();

            return Results.File(print.JobCard(job, CompanyName),
                "application/pdf", $"{job.JobNumber}-job-card.pdf");
        }).RequireAuthorization(RepairPermissions.JobsView);

        group.MapGet("/intake/{id:int}", async (
            int id, IIntakeService intakes, IRepairPrintService print) =>
        {
            var intake = await intakes.GetAsync(id);
            if (intake is null) return Results.NotFound();

            return Results.File(print.IntakeReceipt(intake, CompanyName),
                "application/pdf", $"{intake.IntakeNumber}-receipt.pdf");
        }).RequireAuthorization(RepairPermissions.IntakesView);

        group.MapGet("/quotation/{id:int}", async (
            int id, IQuotationService quotations, IRepairPrintService print) =>
        {
            var quotation = await quotations.GetAsync(id);
            if (quotation is null) return Results.NotFound();

            return Results.File(print.Quotation(quotation, CompanyName),
                "application/pdf", $"{quotation.QuotationNumber}.pdf");
        }).RequireAuthorization(RepairPermissions.QuotationsView);

        group.MapGet("/invoice/{id:int}", async (
            int id, ISalesOrderService orders, IRepairPrintService print) =>
        {
            var order = await orders.GetAsync(id);
            if (order is null) return Results.NotFound();

            return Results.File(print.Invoice(order, CompanyName),
                "application/pdf", $"{order.OrderNumber}-invoice.pdf");
        }).RequireAuthorization(RepairPermissions.OrdersView);

        return app;
    }
}
