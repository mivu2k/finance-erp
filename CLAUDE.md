# Finance ERP — working notes

Read this before exploring the tree; it captures what costs the most to re-derive.

## Start the app

```bash
./dev.sh up      # → http://localhost:5080, login admin@financeerp.local / ChangeMe!123
./dev.sh status  # check before assuming it's down
```

The environment is already installed and persists across WSL restarts — **do not
reinstall .NET or MariaDB**. Details and rebuild-from-scratch steps: [SETUP.md](SETUP.md).

Shell environment needed for any `dotnet` command:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export LD_LIBRARY_PATH="$HOME/.local/finance-erp-dev/pkg/usr/lib/x86_64-linux-gnu"
```

`LD_LIBRARY_PATH` is mandatory — ICU is unpacked locally, not system-installed, and
.NET hard-fails at startup without it.

## Architecture

Clean architecture, .NET 10, Blazor Server, MudBlazor, EF Core + Pomelo/MySQL.

```
src/FinanceERP.Domain          entities, enums, Security/Permissions.cs (no deps)
src/FinanceERP.Application     service interfaces (Interfaces/IServices.cs) + DTOs
src/FinanceERP.Infrastructure  EF Core, Identity, service impls, PDF/Excel, seeding
src/FinanceERP.Web             Blazor UI, auth pages, export endpoints
```

`Application/Interfaces/IServices.cs` is the fastest map of what the system does —
every service contract is there with XML docs on the non-obvious flows.

## Rules that matter

- **Everything posts to the ledger.** No module writes financial state directly;
  they all call `IVoucherService.PostSystemVoucherAsync`. Preserve that — it's the
  guarantee that the books balance.
- **Posted vouchers are immutable.** Correction path is void, or
  `DuplicateAsDraftAsync` to fix and repost. Drafts soft-delete via `DeleteDraftAsync`.
- **Financial data is soft-deleted**, never hard-deleted.
- **Permissions are data, not code.** `Domain/Security/Permissions.cs` is the catalog
  (59 permissions); they live in `AspNetRoleClaims` and policies are generated
  dynamically by `PermissionPolicyProvider`. Adding a permission means adding the
  constant *and* granting it in the role matrix at `/admin/roles`.
- Every page, nav item and action is permission-gated — match that when adding UI.

## Non-obvious flows

- **Payment requests**: Employee → Manager → Admin → Accountant → paid. Director fund
  requests skip manager approval. Voucher is created on payment.
- **Advance-kind requests** have a second lifecycle after payment: disburse → justify
  → approve justification → settle. `SettleAsync(..., AdvanceDifferenceHandling)`
  decides what happens to the disbursed-vs-justified gap (20,000 taken, 17,000 spent):
  `SettleNow` moves the 3,000 through cash immediately, `Outstanding` parks it on the
  employee's advance account / an employee payable for `RecordAdvanceReturnAsync` to
  clear later, and `RecoverFromPayroll` turns it into a salary-deductible
  `EmployeeAdvance` that the next payroll run recovers. The chosen disposition and any
  amount cleared so far live on `PaymentRequest.DifferenceHandling`/`ClearedDifference`.
- **Payroll**: `SalaryStructure` (basic + allowance/deduction lines, effective-dated;
  saving a new one supersedes the old) → `PayrollRun` (draft → pending → approved →
  paid). `GenerateAsync` rebuilds payslips from structures, pro-rating basic *and*
  allowances by attendance, then pulls due advance instalments — capped at what's left
  after other deductions so net pay can't go negative; the shortfall rolls to next
  month. Everything is snapshotted onto the payslip so an approved run can't shift
  under a later catalog edit. `PayRunAsync` posts one voucher aggregated by ledger
  head: Dr salary expense, Cr deduction liabilities, Cr employee advance accounts,
  Cr cash/bank for net pay. Advance instalments are then marked repaid via
  `ApplyPayrollDeductionAsync`, which posts nothing — the payroll voucher already
  credited the advance account. Watch that distinction; double-posting is the easy bug.
- **Year close**: `CloseFiscalYearAsync` moves income/expense into Retained Earnings
  and locks the books through that date.

## State of the project

Implemented and wired end-to-end (service + page + nav): accounts, vouchers, ledger,
day book, payment requests, advances, director funds, petty cash, third parties,
loans, investments, utilities, reconciliation, payroll (pay components, salary
structures, runs, payslips), reports (trial balance, income statement, balance sheet,
cash flow, project spend), PDF/Excel export, per-record printable documents, audit
trail, notifications, global search, optional SMTP.

Printing: `/export/*` (ExportEndpoints) is table/report downloads; `/print/*`
(PrintEndpoints) renders a single record as a signable A4 document via
`IExportService.DocumentToPdf(PdfDocument)`. Ownership-scoped records (requests,
advances, payslips) are readable by their owner without the module-wide permission.

Known gaps, roughly in priority order:

1. **No tests at all** — no test project in `FinanceERP.slnx`. Highest-value work is
   covering `VoucherService`, `PaymentRequestService.SettleAsync`,
   `PayrollService.GenerateAsync`/`PayRunAsync` and `CloseFiscalYearAsync`, where the
   arithmetic is subtle and unverified.
2. **Receipt endpoint is auth-only** (`Program.cs`, `/files/receipts/{name}`) — any
   logged-in user can fetch any receipt by filename; no ownership or permission check.
3. `README.md` predates year-close, reconciliation, utilities, projects, the
   justification flow and SMTP.
4. No CI.

## Gotchas

- Port is **5080** (`appsettings.Development.json` `"urls"` wins over
  `launchSettings.json`'s stale 5188 and over `ASPNETCORE_URLS`).
- `[ERR] The model for context 'AppDbContext' has pending changes` on startup is
  **benign** — EF logs it without throwing. Scaffolding a migration yields an empty
  `Up()`/`Down()`; there is no schema drift. Don't chase it.
- Don't `pkill -f "dotnet run"` — the pattern matches the agent's own shell and kills
  the session. Use `pkill -x FinanceERP.Web`.
- Build emits ~67 warnings, all cosmetic (NuGet locale metadata, one unused ctor
  param in `LoanService.cs:10`, one `[SupplyParameterFromForm]` initializer in
  `Login.razor:56`). Zero errors is the expected baseline.
