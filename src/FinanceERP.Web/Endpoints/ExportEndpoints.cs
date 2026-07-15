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

        group.MapGet("/project-report", async (IReportService reports, IExportService export,
            DateOnly from, DateOnly to, string format = "xlsx") =>
        {
            var rows = await reports.ProjectBreakdownAsync(from, to);
            string[] headers = ["Project", "Net Spend"];
            var total = rows.Sum(r => r.Amount);
            if (format == "pdf")
            {
                var pdfRows = rows.Select(r => new[] { r.Category, r.Amount.ToString("N2") }).ToList();
                pdfRows.Add(["TOTAL", total.ToString("N2")]);
                return Results.File(export.TableToPdf("Project Report", $"{from} — {to}", headers, pdfRows),
                    "application/pdf", "project-report.pdf");
            }
            var xrows = rows.Select(r => new object?[] { r.Category, r.Amount }).ToList();
            xrows.Add(["TOTAL", total]);
            return Results.File(export.TableToExcel("Project Report", headers, xrows),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "project-report.xlsx");
        });

        group.MapGet("/utility-bills", async (IUtilityService utilities, IExportService export,
            DateOnly? from, DateOnly? to, int? locationId, int? connectionId, int? type, bool? paid,
            string format = "xlsx") =>
        {
            var bills = await utilities.ListBillsAsync(new UtilityBillFilter
            {
                From = from, To = to, LocationId = locationId, ConnectionId = connectionId,
                Type = type is null ? null : (FinanceERP.Domain.Entities.UtilityType)type, Paid = paid
            }, max: 5000);
            string[] headers = ["Month", "Location", "Type", "Connection", "Consumer #", "Provider", "Due", "Amount", "Status", "Paid Date"];
            var total = bills.Sum(b => b.Amount);
            if (format == "pdf")
            {
                var rows = bills.Select(b => new[]
                {
                    b.BillMonth.ToString("MMM yyyy"), b.Connection.Location.Name, b.Connection.Type.ToString(),
                    b.Connection.Name, b.Connection.ConsumerNumber ?? "", b.Connection.Provider ?? "",
                    b.DueDate?.ToString("yyyy-MM-dd") ?? "", b.Amount.ToString("N2"),
                    b.VoucherId is null ? "Unpaid" : "Paid", b.PaidDate?.ToString("yyyy-MM-dd") ?? ""
                }).ToList();
                rows.Add(["", "", "", "", "", "", "TOTAL", total.ToString("N2"), "", ""]);
                return Results.File(export.TableToPdf("Utility Bills", $"{from:yyyy-MM} — {to:yyyy-MM}", headers, rows),
                    "application/pdf", "utility-bills.pdf");
            }
            var xrows = bills.Select(b => new object?[]
            {
                b.BillMonth, b.Connection.Location.Name, b.Connection.Type.ToString(), b.Connection.Name,
                b.Connection.ConsumerNumber, b.Connection.Provider, b.DueDate, b.Amount,
                b.VoucherId is null ? "Unpaid" : "Paid", b.PaidDate
            }).ToList();
            xrows.Add([null, null, null, null, null, null, "TOTAL", total, null, null]);
            return Results.File(export.TableToExcel("Utility Bills", headers, xrows),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "utility-bills.xlsx");
        });

        group.MapGet("/day-book", async (IReportService reports, IExportService export,
            DateOnly? from, DateOnly? to, int? voucherType, string format = "xlsx") =>
        {
            var rows = await reports.GeneralLedgerAsync(new ReportFilter
            {
                From = from, To = to,
                VoucherType = voucherType is null ? null : (FinanceERP.Domain.Enums.VoucherType)voucherType
            });
            string[] headers = ["Date", "Voucher", "Account", "Description", "Debit", "Credit"];

            // Group by voucher: header row per voucher, then its lines, then a grand total.
            var pdfRows = new List<string[]>();
            var xlsxRows = new List<object?[]>();
            foreach (var g in rows.GroupBy(r => r.VoucherNo))
            {
                var first = g.First();
                pdfRows.Add([first.Date.ToString("yyyy-MM-dd"), g.Key, "", "", "", ""]);
                xlsxRows.Add([first.Date, g.Key, null, null, null, null]);
                foreach (var r in g)
                {
                    pdfRows.Add(["", "", $"{r.AccountCode} {r.AccountName}", r.Description ?? "",
                        r.Debit == 0 ? "" : r.Debit.ToString("N2"), r.Credit == 0 ? "" : r.Credit.ToString("N2")]);
                    xlsxRows.Add([null, null, $"{r.AccountCode} {r.AccountName}", r.Description,
                        r.Debit == 0 ? null : r.Debit, r.Credit == 0 ? null : (object)r.Credit]);
                }
            }
            var totalD = rows.Sum(r => r.Debit);
            var totalC = rows.Sum(r => r.Credit);
            pdfRows.Add(["", "", "", "GRAND TOTAL", totalD.ToString("N2"), totalC.ToString("N2")]);
            xlsxRows.Add([null, null, null, "GRAND TOTAL", totalD, totalC]);

            if (format == "pdf")
                return Results.File(export.TableToPdf("Day Book — Combined Ledger", $"{from} — {to}", headers, pdfRows),
                    "application/pdf", "day-book.pdf");
            return Results.File(export.TableToExcel("Day Book", headers, xlsxRows),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "day-book.xlsx");
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
