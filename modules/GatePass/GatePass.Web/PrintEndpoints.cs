using ErpPlatform.Shared.Identity;
using GatePass.Domain;
using GatePass.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GatePass.Web;

/// <summary>
/// Printable documents for passes and issuances. Both paper sizes from the Laravel
/// app are kept: <c>a4</c> for the signed file copy, <c>pos</c> for the gate slip.
/// </summary>
public static class PrintEndpoints
{
    public static IEndpointRouteBuilder MapGatePassPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gatepass/print");

        group.MapGet("/pass/{id:int}/{variant?}", async (
            int id, string? variant, IGatePassService passes, IGatePassPrintService print, ICompanyProfileService companies) =>
        {
            var pass = await passes.GetAsync(id);
            if (pass is null) return Results.NotFound();

            var pdf = print.GatePass(pass, Parse(variant), await companies.GetBrandingAsync());
            return Results.File(pdf, "application/pdf", $"{pass.PassNumber}.pdf");
        }).RequireAuthorization(GatePassPermissions.PassesView);

        group.MapGet("/demo/{id:int}/{variant?}", async (
            int id, string? variant, IDemoIssuanceService demos, IGatePassPrintService print, ICompanyProfileService companies) =>
        {
            var demo = await demos.GetAsync(id);
            if (demo is null) return Results.NotFound();

            var pdf = print.DemoIssuance(demo, Parse(variant), await companies.GetBrandingAsync());
            return Results.File(pdf, "application/pdf", $"{demo.IssuanceNumber}.pdf");
        }).RequireAuthorization(GatePassPermissions.DemosView);

        return app;
    }

    private static PrintVariant Parse(string? variant) =>
        string.Equals(variant, "pos", StringComparison.OrdinalIgnoreCase)
            ? PrintVariant.Pos
            : PrintVariant.A4;
}
