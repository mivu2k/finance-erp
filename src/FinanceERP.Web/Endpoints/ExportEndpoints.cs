using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Security;

namespace FinanceERP.Web.Endpoints;

/// <summary>Report download endpoints (PDF / Excel). All require Reports.Export permission.</summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/export").RequireAuthorization(Permissions.ReportsExport);

        group.MapGet("/ledger", async (IReportService reports, IExportService export,
            DateOnly? from, DateOnly? to, int? accountId, int? departmentId, int? costCenterId,
            int? projectId, string format = "xlsx") =>
        {
            var rows = await reports.GeneralLedgerAsync(new ReportFilter
            {
                From = from, To = to, AccountId = accountId, DepartmentId = departmentId,
                CostCenterId = costCenterId, ProjectId = projectId
            });
            string[] headers = ["Date", "Voucher", "Account", "Description", "Debit", "Credit", "Balance"];
            if (format == "pdf")
            {
                var pdf = export.TableToPdf("General Ledger", $"{from} — {to}", headers,
                    rows.Select(r => new[] { r.Date.ToString("yyyy-MM-dd"), r.VoucherNo, $"{r.AccountCode} {r.AccountName}", r.Description ?? "", r.Debit.ToString("N2"), r.Credit.ToString("N2"), r.RunningBalance.ToString("N2") }));
                return Results.File(pdf, "application/pdf", "general-ledger.pdf");
            }
            var xlsx = export.TableToExcel("General Ledger", headers,
                rows.Select(r => new object?[] { r.Date, r.VoucherNo, $"{r.AccountCode} {r.AccountName}", r.Description, r.Debit, r.Credit, r.RunningBalance }));
            return Results.File(xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "general-ledger.xlsx");
        });

        group.MapGet("/trial-balance", async (IReportService reports, IExportService export,
            DateOnly? asOf, string format = "xlsx") =>
        {
            var rows = await reports.TrialBalanceAsync(asOf);
            string[] headers = ["Code", "Account", "Type", "Debit", "Credit"];
            if (format == "pdf")
                return Results.File(export.TableToPdf("Trial Balance", $"As of {asOf ?? DateOnly.FromDateTime(DateTime.Today)}", headers,
                    rows.Select(r => new[] { r.Code, r.Name, r.Type, r.Debit.ToString("N2"), r.Credit.ToString("N2") })),
                    "application/pdf", "trial-balance.pdf");
            return Results.File(export.TableToExcel("Trial Balance", headers,
                rows.Select(r => new object?[] { r.Code, r.Name, r.Type, r.Debit, r.Credit })),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "trial-balance.xlsx");
        });

        group.MapGet("/income-statement", async (IReportService reports, IExportService export,
            DateOnly from, DateOnly to, string format = "xlsx") =>
        {
            var rows = await reports.IncomeStatementAsync(from, to);
            string[] headers = ["Code", "Account", "Type", "Expense", "Income"];
            if (format == "pdf")
                return Results.File(export.TableToPdf("Income Statement", $"{from} — {to}", headers,
                    rows.Select(r => new[] { r.Code, r.Name, r.Type, r.Debit.ToString("N2"), r.Credit.ToString("N2") })),
                    "application/pdf", "income-statement.pdf");
            return Results.File(export.TableToExcel("Income Statement", headers,
                rows.Select(r => new object?[] { r.Code, r.Name, r.Type, r.Debit, r.Credit })),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "income-statement.xlsx");
        });

        group.MapGet("/balance-sheet", async (IReportService reports, IExportService export,
            DateOnly asOf, string format = "xlsx") =>
        {
            var rows = await reports.BalanceSheetAsync(asOf);
            string[] headers = ["Code", "Account", "Type", "Assets", "Liabilities & Equity"];
            if (format == "pdf")
                return Results.File(export.TableToPdf("Balance Sheet", $"As of {asOf}", headers,
                    rows.Select(r => new[] { r.Code, r.Name, r.Type, r.Debit.ToString("N2"), r.Credit.ToString("N2") })),
                    "application/pdf", "balance-sheet.pdf");
            return Results.File(export.TableToExcel("Balance Sheet", headers,
                rows.Select(r => new object?[] { r.Code, r.Name, r.Type, r.Debit, r.Credit })),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "balance-sheet.xlsx");
        });

        group.MapGet("/cash-book", async (IReportService reports, IExportService export,
            DateOnly? from, DateOnly? to, string format = "xlsx") =>
        {
            var rows = await reports.CashBookAsync(new ReportFilter { From = from, To = to });
            string[] headers = ["Date", "Voucher", "Account", "Description", "Receipt", "Payment", "Balance"];
            if (format == "pdf")
                return Results.File(export.TableToPdf("Cash Book", $"{from} — {to}", headers,
                    rows.Select(r => new[] { r.Date.ToString("yyyy-MM-dd"), r.VoucherNo, r.AccountName, r.Description ?? "", r.Debit.ToString("N2"), r.Credit.ToString("N2"), r.RunningBalance.ToString("N2") })),
                    "application/pdf", "cash-book.pdf");
            return Results.File(export.TableToExcel("Cash Book", headers,
                rows.Select(r => new object?[] { r.Date, r.VoucherNo, r.AccountName, r.Description, r.Debit, r.Credit, r.RunningBalance })),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "cash-book.xlsx");
        });
    }
}
