using ErpPlatform.Shared.Identity;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Web;

/// <summary>
/// Printable inventory documents and reports.
/// </summary>
/// <remarks>
/// Reports are rendered from the same built <c>InventoryReport</c> the screen shows,
/// so the paper and the page can't disagree. Money-bearing reports are gated on the
/// cost permission here as well as in the UI — a URL is reachable without the screen.
/// </remarks>
public static class PrintEndpoints
{
    public static IEndpointRouteBuilder MapInventoryPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var print = app.MapGroup("/inventory/print");

        print.MapGet("/report/{key}", async (
            string key, HttpContext http,
            IInventoryReportService reports, IInventoryPrintService printer,
            ICompanyProfileService companies) =>
        {
            var definition = reports.Find(key);
            if (definition is null) return Results.NotFound();

            var canSeeMoney = http.User.HasClaim(
                PermissionCatalog.ClaimType, InventoryPermissions.CostsView);

            // Refused outright rather than served with the money columns stripped:
            // a report called "valuation" with no values is a confusing document.
            if (definition.ShowsMoney && !canSeeMoney) return Results.Forbid();

            var report = await reports.BuildAsync(key, canSeeMoney);
            return Pdf(printer.Report(report, await companies.GetBrandingAsync(), definition.Landscape),
                $"inventory-{key}");
        }).RequireAuthorization(InventoryPermissions.ReportsView);

        print.MapGet("/grn/{id:int}", async (
            int id, IGoodsReceiptService receipts, IInventoryPrintService printer,
            ICompanyProfileService companies, InventoryDbContext db) =>
        {
            var receipt = await receipts.GetAsync(id);
            if (receipt is null) return Results.NotFound();

            var supplierName = receipt.Supplier?.Name
                ?? await db.Suppliers.AsNoTracking()
                    .Where(s => s.Id == receipt.SupplierId).Select(s => s.Name).FirstOrDefaultAsync()
                ?? "-";
            var warehouseName = receipt.WarehouseId is { } w
                ? await db.Warehouses.AsNoTracking()
                    .Where(x => x.Id == w).Select(x => x.Name).FirstOrDefaultAsync()
                : null;

            return Pdf(printer.GoodsReceiptNote(receipt, supplierName, warehouseName,
                await companies.GetBrandingAsync()), receipt.ReceiptNumber);
        }).RequireAuthorization(InventoryPermissions.PurchaseManage);

        print.MapGet("/purchase-order/{id:int}", async (
            int id, IPurchaseOrderService orders, IInventoryPrintService printer,
            ICompanyProfileService companies, InventoryDbContext db) =>
        {
            var order = await orders.GetAsync(id);
            if (order is null) return Results.NotFound();

            var warehouseName = order.WarehouseId is { } w
                ? await db.Warehouses.AsNoTracking()
                    .Where(x => x.Id == w).Select(x => x.Name).FirstOrDefaultAsync()
                : null;

            return Pdf(printer.PurchaseOrderDocument(order, warehouseName,
                await companies.GetBrandingAsync()), order.OrderNumber);
        }).RequireAuthorization(InventoryPermissions.PurchaseManage);

        return app;
    }

    private static IResult Pdf(byte[] bytes, string fileName) =>
        Results.File(bytes, "application/pdf", $"{fileName}.pdf");
}
