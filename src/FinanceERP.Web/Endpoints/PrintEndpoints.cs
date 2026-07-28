using System.Security.Claims;
using FinanceERP.Application.DTOs;
using FinanceERP.Application.Interfaces;
using FinanceERP.Domain.Entities;
using FinanceERP.Domain.Enums;
using FinanceERP.Domain.Security;
using FinanceERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceERP.Web.Endpoints;

/// <summary>
/// "Print this form" — one endpoint per record type, each rendering the record as a
/// signable A4 document. Ownership-scoped records (requests, advances, payslips) are
/// readable by the person they belong to even without the module-wide view permission.
/// </summary>
public static class PrintEndpoints
{
    public static void MapPrintEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/print").RequireAuthorization();

        // ------------------------------------------------------------------ voucher
        group.MapGet("/voucher/{id:int}", async (int id, ClaimsPrincipal user,
            AppDbContext db, IExportService export) =>
        {
            if (!user.HasPermission(Permissions.VouchersView)) return Results.Forbid();

            var v = await db.Vouchers
                .Include(x => x.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Account)
                .Include(x => x.Lines).ThenInclude(l => l.Project)
                .Include(x => x.Lines).ThenInclude(l => l.Department)
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (v is null) return Results.NotFound();

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = VoucherTitle(v.Type),
                DocumentNo = v.VoucherNo,
                Subtitle = v.Narration,
                Watermark = v.Status switch
                {
                    VoucherStatus.Draft => "DRAFT",
                    VoucherStatus.Void => "VOID",
                    _ => null
                },
                Fields =
                [
                    new PdfField("Date", v.Date.ToString("yyyy-MM-dd")),
                    new PdfField("Type", v.Type.ToString()),
                    new PdfField("Status", v.Status.ToString(), Emphasise: true),
                    new PdfField("Source", v.SourceId is null ? v.Source : $"{v.Source} #{v.SourceId}"),
                    new PdfField("Posted by", v.PostedBy),
                    new PdfField("Posted at", v.PostedAtUtc?.ToString("yyyy-MM-dd HH:mm"))
                ],
                TableHeaders = ["Account", "Description", "Project", "Debit", "Credit"],
                RightAlignedColumns = [3, 4],
                TableRows = v.Lines.Select(l => new[]
                {
                    $"{l.Account.Code} — {l.Account.Name}",
                    l.Description ?? "",
                    l.Project?.Name ?? l.Department?.Name ?? "",
                    l.Debit == 0 ? "" : l.Debit.ToString("N2"),
                    l.Credit == 0 ? "" : l.Credit.ToString("N2")
                }).ToList(),
                TableFooter = ["TOTAL", "", "", v.TotalDebit.ToString("N2"), v.TotalCredit.ToString("N2")],
                Signatures = ["Prepared by", "Checked by", "Approved by", "Received by"],
                FooterNote = $"Voucher {v.VoucherNo}"
            };
            return Pdf(export.DocumentToPdf(doc), $"{v.VoucherNo}.pdf");
        });

        // ---------------------------------------------------------- payment request
        group.MapGet("/request/{id:int}", async (int id, ClaimsPrincipal user,
            AppDbContext db, IExportService export) =>
        {
            var r = await db.PaymentRequests
                .Include(x => x.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Account)
                .Include(x => x.Approvals.OrderBy(a => a.Id))
                .Include(x => x.Department).Include(x => x.Project)
                .Include(x => x.Voucher).Include(x => x.SettlementVoucher)
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            if (!user.HasPermission(Permissions.RequestsViewAll) && !user.Owns(r.RequesterId))
                return Results.Forbid();

            var isAdvance = r.Kind == RequestKind.Advance;
            var justified = r.Lines.Sum(l => l.Amount);
            var difference = r.TotalAmount - justified;

            var totals = new List<PdfField>();
            if (isAdvance && r.Status >= RequestStatus.JustificationPending)
            {
                totals.Add(new PdfField("Advance disbursed", r.TotalAmount.ToString("N2")));
                totals.Add(new PdfField("Justified", justified.ToString("N2")));
                totals.Add(new PdfField(
                    difference >= 0 ? "Unspent" : "Overspent", Math.Abs(difference).ToString("N2"), Emphasise: true));
            }
            else
            {
                totals.Add(new PdfField("Total", r.TotalAmount.ToString("N2"), Emphasise: true));
            }

            var fields = new List<PdfField>
            {
                new("Requester", r.RequesterName),
                new("Status", r.Status.ToString(), Emphasise: true),
                new("Kind", r.IsDirectorRequest ? "Director Funds" : r.Kind.ToString()),
                new("Raised", r.CreatedAtUtc.ToString("yyyy-MM-dd")),
                new("Department", r.Department?.Name),
                new("Project", r.Project?.Name),
                new("Purpose", r.Purpose)
            };
            if (r.Voucher is not null)
                fields.Add(new PdfField(isAdvance ? "Disbursement voucher" : "Payment voucher", r.Voucher.VoucherNo));
            if (r.SettlementVoucher is not null)
                fields.Add(new PdfField("Settlement voucher", r.SettlementVoucher.VoucherNo));
            if (isAdvance && r.DifferenceHandling is { } handling && difference != 0)
                fields.Add(new PdfField("Difference handling", handling switch
                {
                    AdvanceDifferenceHandling.SettleNow => "Settled in cash at settlement",
                    AdvanceDifferenceHandling.RecoverFromPayroll => "Recovered from salary",
                    _ => $"Outstanding (cleared so far: {r.ClearedDifference:N2})"
                }));

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = r.IsDirectorRequest ? "Director Fund Request"
                    : isAdvance ? "Advance Request" : "Payment Request",
                DocumentNo = r.RequestNo,
                Subtitle = r.Purpose,
                Watermark = r.Status switch
                {
                    RequestStatus.Draft => "DRAFT",
                    RequestStatus.Rejected => "REJECTED",
                    RequestStatus.Cancelled => "CANCELLED",
                    _ => null
                },
                Fields = fields,
                TableHeaders = ["Description", "Category", "Account", "Amount"],
                RightAlignedColumns = [3],
                TableRows = r.Lines.Select(l => new[]
                {
                    l.Reason ?? l.Description ?? "",
                    l.Category ?? "",
                    l.Account is null ? "" : $"{l.Account.Code} — {l.Account.Name}",
                    l.Amount.ToString("N2")
                }).ToList(),
                TableFooter = ["TOTAL", "", "", r.Lines.Sum(l => l.Amount).ToString("N2")],
                Totals = totals,
                Approvals = r.Approvals.Select(a => new PdfApprovalRow(
                    a.Level, a.ActorName, a.Action.ToString(), a.Comment, a.CreatedAtUtc)).ToList(),
                Signatures = ["Requested by", "Approved by", "Accounts", "Received by"],
                FooterNote = $"{r.RequestNo} · {r.RequesterName}"
            };
            return Pdf(export.DocumentToPdf(doc), $"{r.RequestNo}.pdf");
        });

        // --------------------------------------------------------- employee advance
        group.MapGet("/advance/{id:int}", async (int id, ClaimsPrincipal user,
            IAdvanceService advances, AppDbContext db, IExportService export) =>
        {
            var a = await advances.GetAsync(id);
            if (a is null) return Results.NotFound();
            if (!user.HasPermission(Permissions.AdvancesViewAll) && !user.Owns(a.EmployeeId))
                return Results.Forbid();

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = "Employee Advance",
                DocumentNo = a.AdvanceNo,
                Subtitle = a.Reason,
                Watermark = a.Status switch
                {
                    AdvanceStatus.Draft => "DRAFT",
                    AdvanceStatus.Rejected => "REJECTED",
                    AdvanceStatus.Cancelled => "CANCELLED",
                    AdvanceStatus.Settled => "SETTLED",
                    _ => null
                },
                Fields =
                [
                    new PdfField("Employee", a.EmployeeName),
                    new PdfField("Status", a.Status.ToString(), Emphasise: true),
                    new PdfField("Amount", a.Amount.ToString("N2"), Emphasise: true),
                    new PdfField("Raised", a.CreatedAtUtc.ToString("yyyy-MM-dd")),
                    new PdfField("Instalments", $"{a.InstallmentCount} × {a.MonthlyDeduction:N2}"),
                    new PdfField("Due date", a.DueDate?.ToString("yyyy-MM-dd")),
                    new PdfField("Approved by", a.ApprovedBy),
                    new PdfField("Approved at", a.ApprovedAtUtc?.ToString("yyyy-MM-dd HH:mm"))
                ],
                TableHeaders = ["Instalment", "Due date", "Amount", "Paid", "Status"],
                RightAlignedColumns = [2, 3],
                TableRows = a.Installments.Select(i => new[]
                {
                    $"#{i.Number}",
                    i.DueDate.ToString("yyyy-MM-dd"),
                    i.Amount.ToString("N2"),
                    i.PaidAmount.ToString("N2"),
                    i.Status.ToString()
                }).ToList(),
                TableFooter = ["TOTAL", "", a.Amount.ToString("N2"), a.RepaidAmount.ToString("N2"), ""],
                Totals =
                [
                    new PdfField("Advance amount", a.Amount.ToString("N2")),
                    new PdfField("Repaid", a.RepaidAmount.ToString("N2")),
                    new PdfField("Outstanding", a.OutstandingBalance.ToString("N2"), Emphasise: true)
                ],
                Signatures = ["Employee", "Approved by", "Accounts"],
                FooterNote = $"{a.AdvanceNo} · {a.EmployeeName}"
            };
            return Pdf(export.DocumentToPdf(doc), $"{a.AdvanceNo}.pdf");
        });

        // -------------------------------------------------------------------- loan
        group.MapGet("/loan/{id:int}", async (int id, ClaimsPrincipal user,
            ILoanService loans, AppDbContext db, IExportService export) =>
        {
            if (!user.HasPermission(Permissions.LoansView)) return Results.Forbid();
            var l = await loans.GetAsync(id);
            if (l is null) return Results.NotFound();

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = l.Direction == LoanDirection.Taken ? "Loan Taken — Schedule" : "Loan Given — Schedule",
                DocumentNo = l.LoanNo,
                Subtitle = l.ThirdParty.Name,
                Watermark = l.Status == LoanStatus.Settled ? "SETTLED"
                    : l.Status == LoanStatus.Defaulted ? "DEFAULTED" : null,
                Fields =
                [
                    new PdfField("Counterparty", l.ThirdParty.Name),
                    new PdfField("Direction", l.Direction.ToString()),
                    new PdfField("Principal", l.Principal.ToString("N2"), Emphasise: true),
                    new PdfField("Interest rate", $"{l.InterestRatePercent:0.##}%"),
                    new PdfField("Start date", l.StartDate.ToString("yyyy-MM-dd")),
                    new PdfField("Due date", l.DueDate?.ToString("yyyy-MM-dd")),
                    new PdfField("Status", l.Status.ToString(), Emphasise: true),
                    new PdfField("Notes", l.Notes)
                ],
                TableHeaders = ["Instalment", "Due date", "Amount", "Interest", "Paid", "Status"],
                RightAlignedColumns = [2, 3, 4],
                TableRows = l.Installments.OrderBy(i => i.Number).Select(i => new[]
                {
                    $"#{i.Number}",
                    i.DueDate.ToString("yyyy-MM-dd"),
                    i.Amount.ToString("N2"),
                    i.InterestPortion.ToString("N2"),
                    i.PaidAmount.ToString("N2"),
                    i.Status.ToString()
                }).ToList(),
                Totals =
                [
                    new PdfField("Principal", l.Principal.ToString("N2")),
                    new PdfField("Repaid", l.RepaidAmount.ToString("N2")),
                    new PdfField("Remaining", l.RemainingBalance.ToString("N2"), Emphasise: true)
                ],
                Signatures = ["Prepared by", "Approved by", "Counterparty"],
                FooterNote = $"{l.LoanNo} · {l.ThirdParty.Name}"
            };
            return Pdf(export.DocumentToPdf(doc), $"{l.LoanNo}.pdf");
        });

        // -------------------------------------------------------------- investment
        group.MapGet("/investment/{id:int}", async (int id, ClaimsPrincipal user,
            IInvestmentService investments, AppDbContext db, IExportService export) =>
        {
            if (!user.HasPermission(Permissions.InvestmentsView)) return Results.Forbid();
            var i = await investments.GetAsync(id);
            if (i is null) return Results.NotFound();

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = "Investment Statement",
                DocumentNo = $"INV-{i.Id:D5}",
                Subtitle = i.Name,
                Watermark = i.Status == InvestmentStatus.Closed ? "CLOSED" : null,
                Fields =
                [
                    new PdfField("Investment", i.Name, Emphasise: true),
                    new PdfField("Type", i.InvestmentType),
                    new PdfField("Invested", i.Amount.ToString("N2"), Emphasise: true),
                    new PdfField("Expected ROI", $"{i.ExpectedRoiPercent:0.##}%"),
                    new PdfField("Start date", i.StartDate.ToString("yyyy-MM-dd")),
                    new PdfField("Status", i.Status.ToString()),
                    new PdfField("Notes", i.Notes)
                ],
                TableHeaders = ["Date", "Transaction", "Amount", "Notes"],
                RightAlignedColumns = [2],
                TableRows = i.Transactions.OrderBy(t => t.Date).ThenBy(t => t.Id).Select(t => new[]
                {
                    t.Date.ToString("yyyy-MM-dd"),
                    t.Type.ToString(),
                    t.Amount.ToString("N2"),
                    t.Notes ?? ""
                }).ToList(),
                Totals =
                [
                    new PdfField("Principal invested", i.Amount.ToString("N2")),
                    new PdfField("Profit to date", i.TotalProfit.ToString("N2")),
                    new PdfField("Withdrawn", i.TotalWithdrawn.ToString("N2")),
                    new PdfField("Net position",
                        (i.Amount + i.TotalProfit - i.TotalWithdrawn).ToString("N2"), Emphasise: true)
                ],
                Signatures = ["Prepared by", "Approved by"],
                FooterNote = i.Name
            };
            return Pdf(export.DocumentToPdf(doc), $"investment-{i.Id}.pdf");
        });

        // ------------------------------------------------------------ utility bill
        group.MapGet("/utility-bill/{id:int}", async (int id, ClaimsPrincipal user,
            AppDbContext db, IExportService export) =>
        {
            if (!user.HasPermission(Permissions.UtilitiesView)) return Results.Forbid();

            var b = await db.UtilityBills
                .Include(x => x.Connection).ThenInclude(c => c.Location)
                .Include(x => x.Connection).ThenInclude(c => c.ExpenseAccount)
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (b is null) return Results.NotFound();

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = "Utility Bill",
                DocumentNo = $"UB-{b.Id:D5}",
                Subtitle = $"{b.Connection.Type} — {b.Connection.Name} ({b.Connection.Location.Name})",
                Watermark = b.VoucherId is null ? "UNPAID" : null,
                Fields =
                [
                    new PdfField("Bill month", b.BillMonth.ToString("MMMM yyyy"), Emphasise: true),
                    new PdfField("Location", b.Connection.Location.Name),
                    new PdfField("Utility", b.Connection.Type.ToString()),
                    new PdfField("Connection", b.Connection.Name),
                    new PdfField("Consumer #", b.Connection.ConsumerNumber),
                    new PdfField("Provider", b.Connection.Provider),
                    new PdfField("Due date", b.DueDate?.ToString("yyyy-MM-dd")),
                    new PdfField("Expense head", b.Connection.ExpenseAccount is null
                        ? null
                        : $"{b.Connection.ExpenseAccount.Code} — {b.Connection.ExpenseAccount.Name}"),
                    new PdfField("Status", b.VoucherId is null ? "Unpaid" : "Paid", Emphasise: true),
                    new PdfField("Paid date", b.PaidDate?.ToString("yyyy-MM-dd"))
                ],
                Totals = [new PdfField("Amount payable", b.Amount.ToString("N2"), Emphasise: true)],
                Notes = b.Notes,
                Signatures = ["Verified by", "Approved by", "Paid by"],
                FooterNote = $"{b.Connection.Location.Name} · {b.Connection.Name}"
            };
            return Pdf(export.DocumentToPdf(doc), $"utility-bill-{b.Id}.pdf");
        });

        // ----------------------------------------------------------------- payslip
        group.MapGet("/payslip/{id:int}", async (int id, ClaimsPrincipal user,
            IPayrollService payroll, AppDbContext db, IExportService export) =>
        {
            var slip = await payroll.GetPayslipAsync(id);
            if (slip is null) return Results.NotFound();
            if (!user.HasPermission(Permissions.PayrollView) && !user.Owns(slip.EmployeeId))
                return Results.Forbid();

            return Pdf(export.DocumentToPdf(BuildPayslipDocument(slip, await CompanyNameAsync(db))),
                $"payslip-{slip.PayrollRun.PeriodMonth:yyyy-MM}-{slip.EmployeeName}.pdf");
        });

        // ------------------------------------------------------------- payroll run
        group.MapGet("/payroll-run/{id:int}", async (int id, ClaimsPrincipal user,
            IPayrollService payroll, AppDbContext db, IExportService export) =>
        {
            if (!user.HasPermission(Permissions.PayrollView)) return Results.Forbid();
            var run = await payroll.GetRunAsync(id);
            if (run is null) return Results.NotFound();

            var doc = new PdfDocument
            {
                CompanyName = await CompanyNameAsync(db),
                Title = "Payroll Register",
                DocumentNo = run.RunNo,
                Subtitle = run.PeriodMonth.ToString("MMMM yyyy"),
                Watermark = run.Status switch
                {
                    PayrollRunStatus.Draft => "DRAFT",
                    PayrollRunStatus.Cancelled => "CANCELLED",
                    PayrollRunStatus.Paid => null,
                    _ => "UNPAID"
                },
                Fields =
                [
                    new PdfField("Period", run.PeriodMonth.ToString("MMMM yyyy"), Emphasise: true),
                    new PdfField("Pay date", run.PayDate.ToString("yyyy-MM-dd")),
                    new PdfField("Status", run.Status.ToString(), Emphasise: true),
                    new PdfField("Employees", run.Payslips.Count.ToString()),
                    new PdfField("Department", run.Department?.Name ?? "All"),
                    new PdfField("Project", run.Project?.Name),
                    new PdfField("Approved by", run.ApprovedBy),
                    new PdfField("Voucher", run.Voucher?.VoucherNo)
                ],
                TableHeaders = ["Employee", "Basic", "Allowances", "Gross", "Deductions", "Advance", "Net Pay"],
                RightAlignedColumns = [1, 2, 3, 4, 5, 6],
                TableRows = run.Payslips.Select(p => new[]
                {
                    p.EmployeeName,
                    p.EarnedBasic.ToString("N2"),
                    p.TotalAllowances.ToString("N2"),
                    p.GrossPay.ToString("N2"),
                    p.TotalDeductions.ToString("N2"),
                    p.AdvanceDeduction.ToString("N2"),
                    p.NetPay.ToString("N2")
                }).ToList(),
                TableFooter =
                [
                    "TOTAL",
                    run.Payslips.Sum(p => p.EarnedBasic).ToString("N2"),
                    run.Payslips.Sum(p => p.TotalAllowances).ToString("N2"),
                    run.TotalGross.ToString("N2"),
                    run.Payslips.Sum(p => p.TotalDeductions).ToString("N2"),
                    run.Payslips.Sum(p => p.AdvanceDeduction).ToString("N2"),
                    run.TotalNet.ToString("N2")
                ],
                Totals =
                [
                    new PdfField("Gross payroll", run.TotalGross.ToString("N2")),
                    new PdfField("Total deductions", run.TotalDeductions.ToString("N2")),
                    new PdfField("Net disbursed", run.TotalNet.ToString("N2"), Emphasise: true)
                ],
                Notes = run.Notes,
                Signatures = ["Prepared by", "Checked by", "Approved by", "Paid by"],
                FooterNote = $"{run.RunNo} · {run.PeriodMonth:MMMM yyyy}"
            };
            return Pdf(export.DocumentToPdf(doc), $"{run.RunNo}.pdf");
        });

        // All payslips in a run, one page each — what actually gets handed out.
        group.MapGet("/payroll-run/{id:int}/payslips", async (int id, ClaimsPrincipal user,
            IPayrollService payroll, AppDbContext db, IExportService export) =>
        {
            if (!user.HasPermission(Permissions.PayrollView)) return Results.Forbid();
            var run = await payroll.GetRunAsync(id);
            if (run is null) return Results.NotFound();
            if (run.Payslips.Count == 0) return Results.BadRequest("This run has no payslips.");

            var company = await CompanyNameAsync(db);
            var merged = export.DocumentsToPdf(run.Payslips.Select(slip =>
            {
                slip.PayrollRun = run; // GetRunAsync loads slips off the run, not the back-reference
                return BuildPayslipDocument(slip, company);
            }));
            return Pdf(merged, $"payslips-{run.PeriodMonth:yyyy-MM}.pdf");
        });
    }

    private static PdfDocument BuildPayslipDocument(Payslip slip, string company)
    {
        var run = slip.PayrollRun;
        var earnings = slip.Lines.Where(l => l.Kind == PayComponentKind.Allowance).ToList();
        var deductions = slip.Lines.Where(l => l.Kind == PayComponentKind.Deduction).ToList();

        // Earnings and deductions sit side by side, the way a payslip is normally read;
        // the shorter column is padded so the rows stay aligned.
        var rows = new List<string[]>();
        for (var i = 0; i < Math.Max(earnings.Count, deductions.Count); i++)
        {
            var e = i < earnings.Count ? earnings[i] : null;
            var d = i < deductions.Count ? deductions[i] : null;
            rows.Add([
                e?.Name ?? "",
                e is null ? "" : e.Amount.ToString("N2"),
                d?.Name ?? "",
                d is null ? "" : d.Amount.ToString("N2")
            ]);
        }

        var totalDeductions = slip.TotalDeductions + slip.AdvanceDeduction;

        return new PdfDocument
        {
            CompanyName = company,
            Title = "Payslip",
            DocumentNo = $"{run.RunNo}/{slip.Id}",
            Subtitle = $"{slip.EmployeeName} — {run.PeriodMonth:MMMM yyyy}",
            Watermark = run.Status == PayrollRunStatus.Paid ? null : "UNPAID",
            Fields =
            [
                new PdfField("Employee", slip.EmployeeName, Emphasise: true),
                new PdfField("Employee code", slip.EmployeeCode),
                new PdfField("Pay period", run.PeriodMonth.ToString("MMMM yyyy")),
                new PdfField("Pay date", run.PayDate.ToString("yyyy-MM-dd")),
                new PdfField("Contracted basic", slip.BasicSalary.ToString("N2")),
                new PdfField("Days paid", $"{slip.WorkingDays - slip.AbsentDays:0.##} / {slip.WorkingDays}")
            ],
            TableHeaders = ["Earnings", "Amount", "Deductions", "Amount"],
            RightAlignedColumns = [1, 3],
            TableRows = rows,
            TableFooter =
            [
                "Gross Pay", slip.GrossPay.ToString("N2"),
                "Total Deductions", totalDeductions.ToString("N2")
            ],
            Totals =
            [
                new PdfField("Gross pay", slip.GrossPay.ToString("N2")),
                new PdfField("Less: deductions", totalDeductions.ToString("N2")),
                new PdfField("NET PAY", slip.NetPay.ToString("N2"), Emphasise: true)
            ],
            Notes = slip.Notes,
            Signatures = ["Prepared by", "Approved by", "Received by (employee)"],
            FooterNote = "This is a computer-generated payslip."
        };
    }

    private static async Task<string> CompanyNameAsync(AppDbContext db) =>
        (await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.CompanyName))?.Value ?? "";

    private static string VoucherTitle(VoucherType type) => type switch
    {
        VoucherType.CashPayment => "Cash Payment Voucher",
        VoucherType.CashReceipt => "Cash Receipt Voucher",
        VoucherType.BankPayment => "Bank Payment Voucher",
        VoucherType.BankReceipt => "Bank Receipt Voucher",
        VoucherType.Adjustment => "Adjustment Voucher",
        _ => "Journal Voucher"
    };

    private static IResult Pdf(byte[] bytes, string fileName)
    {
        // Inline so the browser's PDF viewer opens it — the user prints from there.
        var safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        return Results.File(bytes, "application/pdf", safe, enableRangeProcessing: false);
    }

    private static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(Permissions.ClaimType, permission);

    private static bool Owns(this ClaimsPrincipal user, string ownerId) =>
        !string.IsNullOrEmpty(ownerId) &&
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value == ownerId;
}
