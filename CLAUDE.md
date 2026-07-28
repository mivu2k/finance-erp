# MEI ERP platform — working notes

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

A multi-app platform, not one app. .NET 10, Blazor Server, MudBlazor, EF Core +
Pomelo/MySQL. **One process** hosts every app; the isolation is at the database
and project level, not the process level.

```
shared/ErpPlatform.Shared.Kernel       BaseEntity, ICurrentUserService (no deps)
shared/ErpPlatform.Shared.Persistence  ModuleDbContext (audit + soft delete), DocumentSequence
shared/ErpPlatform.Shared.Identity     the ONE identity database + module/permission registries
shared/ErpPlatform.Shared.Web          PlatformShell chrome, portal, /admin/users, /admin/roles

modules/Hr/{Domain,Infrastructure,Web}        → erp_hr        → /hr
modules/GatePass/{Domain,Infrastructure,Web}  → erp_gatepass  → /gatepass
modules/Repair/{Domain,Infrastructure,Web}    → erp_repair    → /repair
src/FinanceERP.{Domain,Application,Infrastructure}  → finance_erp → /finance

src/FinanceERP.Web             the host: composes every module, owns auth pages
```

Each `*.Web` is a Razor Class Library with its own pages, layout and nav; the host
lists them in `Program.cs` (`AddAdditionalAssemblies`) **and** `Components/Routes.razor`
(`AdditionalAssemblies`) — miss either and routes 404.

`Application/Interfaces/IServices.cs` is the fastest map of the Finance app. Each
other module's contracts live next to their implementation in `*.Infrastructure`.

### The identity model — read this before touching auth

`erp_identity` is the only shared database. It holds users, roles, permission
claims, the module catalog and per-user app access. **No business module writes to
it**, and there are no cross-database foreign keys anywhere: a module that needs a
person stores the Identity user id as a string plus a display-name snapshot, and
reads the rest through `IPlatformUserDirectory`.

- **A role is scoped to one app** (`ApplicationRole.ModuleKey`). Holding it both
  admits the user to that app's tile on the portal *and* decides what they can do
  inside it. A null `ModuleKey` means a platform-wide role (Super Admin).
- `UserModuleAccess` is a per-user grant/deny override on top. **Deny wins.**
- Permissions are namespaced by module — `finance.vouchers.post`,
  `repair.jobs.assign`. Modules register theirs via `ModuleRegistry.Register` in
  their `AddXxxModule`; `PermissionPolicyProvider` builds policies from that
  catalog, and also understands `module:{key}` policies.
- Module access is stamped onto the principal as `module` claims at sign-in, so the
  portal and nav never hit the database. **Access changes apply at next sign-in.**

Adding an app means: a `ModuleDefinition` in `AppModules.All`, a
`ModuleRegistration` (permissions + default roles), an `AddXxxModule`, a connection
string, and the two assembly lists in the host.

## Rules that matter

These apply to the **Finance** module specifically:

- **Everything posts to the ledger.** No module writes financial state directly;
  they all call `IVoucherService.PostSystemVoucherAsync`. Preserve that — it's the
  guarantee that the books balance.
- **Posted vouchers are immutable.** Correction path is void, or
  `DuplicateAsDraftAsync` to fix and repost. Drafts soft-delete via `DeleteDraftAsync`.
- **Financial data is soft-deleted**, never hard-deleted.
- **Permissions are data, not code.** `Domain/Security/Permissions.cs` is Finance's
  catalog; they live in `AspNetRoleClaims` and policies are generated dynamically.
  Adding one means adding the constant, describing it in
  `Infrastructure/FinanceModule.cs`, *and* granting it at `/admin/roles`.
- **`EmployeeProfile` is the accounts-side mirror** of a platform user, refreshed
  on startup by `DbSeeder.SyncEmployeeProfilesAsync`. Payroll queries it rather than
  reaching into the identity database. Department and ledger account are owned here
  and never overwritten by the sync.
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
- **Repair pipeline** is a state machine in `JobWorkflow`, not a free-text status:
  Received → Diagnosing → WaitingApproval → InProgress → Completed → Delivered, with
  Cancelled available until delivery. Delivered and Cancelled are terminal.
- **A quotation carries two independent approvals**, the customer's and the
  manager's. It only reaches Approved when both say yes; either rejection kills it.
  A sales order copies its amounts off the quotation rather than referencing it, so
  editing an estimate can never move a bill that's already out.
- **Gate passes separate issuing from the gate.** Whoever raises the pass can't mark
  the goods through — that's `gatepass.passes.complete`, held by Gate Security. A
  pass stops being editable the moment it leaves Issued.
- **Demo goods support partial returns**: the issuance stays open until the last
  item is ticked back.

## State of the project

Four apps behind one login, chosen from the portal at `/`:

| App | Route | Database | State |
|---|---|---|---|
| Finance | `/finance` | `finance_erp` | mature — see below |
| Repair | `/repair` | `erp_repair` | ported from Laravel, end-to-end |
| Gate Pass & Demo Goods | `/gatepass` | `erp_gatepass` | complete |
| HR | `/hr` | `erp_hr` | employee master + documents |


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

Ported from the Laravel `dir-repair` app: customers, intakes, jobs, diagnoses,
quotations, sales orders, payments, parts, tracking board, and four PDFs (job card,
intake receipt, quotation, invoice). Job photos are modelled but there's no upload
UI yet. Not ported: the customer-facing tracking page, Excel report exports.

Known gaps, roughly in priority order:

1. **No tests at all** — no test project in the solution. Highest-value work is
   covering `VoucherService`, `PaymentRequestService.SettleAsync`,
   `PayrollService.GenerateAsync`/`PayRunAsync`, `CloseFiscalYearAsync`,
   `JobWorkflow`, `QuotationService.Recalculate` and `SalesOrderService`'s payment
   arithmetic — all subtle and unverified.
2. **Receipt endpoint is auth-only** (`Program.cs`, `/files/receipts/{name}`) — any
   logged-in user can fetch any receipt by filename; no ownership or permission check.
3. `README.md` predates year-close, reconciliation, utilities, projects, the
   justification flow and SMTP.
4. No CI.

## Gotchas

- **Every module database is created by `./dev.sh up`** (`DATABASES` in that script).
  `./dev.sh db <name>` opens a shell on one; `./dev.sh reset` drops them all.
- **`array.Contains(x)` inside an EF predicate binds to the `ReadOnlySpan` overload**
  and throws at query time. Use a `List<T>` — that's why `JobWorkflow.Open` is one.
- **Existing installs upgrading past the identity split** must run
  `deploy/migrate-identity-out-of-accounts.sql` before the accounts migration drops
  the old `AspNet*` tables. A fresh database needs nothing.
- Port is **5080** (`appsettings.Development.json` `"urls"` wins over
  `launchSettings.json`'s stale 5188 and over `ASPNETCORE_URLS`).
- `[ERR] The model for context '...' has pending changes` on startup is **benign**
  and now appears once per context. EF logs it without throwing; scaffolding a
  migration yields an empty `Up()`/`Down()`. Verified, not drift. Don't chase it.
- Don't `pkill -f "dotnet run"` — the pattern matches the agent's own shell and kills
  the session. Use `pkill -x FinanceERP.Web`.
- Build emits ~67 warnings, all cosmetic (NuGet locale metadata, one unused ctor
  param in `LoanService.cs:10`, one `[SupplyParameterFromForm]` initializer in
  `Login.razor:56`). Zero errors is the expected baseline.
