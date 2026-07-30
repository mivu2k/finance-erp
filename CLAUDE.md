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
shared/ErpPlatform.Shared.Printing       the company letterhead, drawn into QuestPDF
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
- **Employee and director spend have separate expense heads** — `5200 Employee
  Expenses` and `5400 Director Expenses` sub-trees. The accountant's classification
  picker on a request only offers the side that request belongs to, so the trial
  balance and income statement separate the two with no filtering. `SeedChartOfAccounts`
  now adds only missing codes, so existing installs pick up new heads on startup.
- **Voucher lines carry `PersonId`/`PersonName`** wherever the money is traceable to
  one person (payment requests, advances). That's what the ledger's Person filter and
  `ReportFilter.PersonId` read; the aggregated payroll voucher deliberately has none.
- **A project is optional on a payment request.** It used to be mandatory whenever any
  project existed; that check is gone from both `RequestEdit` and `SubmitAsync`.
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
- **A job's parts and labour are a priced list, not free text.** `JobWorkItem`
  (Part/Labor/Service/Misc, qty, unit price, `Billable`) is what the workshop records
  per device, and it is the only thing a quotation is built from. `Diagnosis` stays
  as the technician's narrative; it does not price anything.
- **A quotation is built from job work items, one job or the whole intake.**
  `BuildForJobAsync` covers one device; `BuildForIntakeAsync` is the collective case
  and folds the device name into each line while keeping `QuotationItem.RepairJobId`,
  so per-device reporting still works. Both return an *unsaved* quotation — the
  editor opens on it (`/repair/quotations/new?jobId=` / `?intakeId=`) so prices can be
  adjusted before anything becomes a document. Non-billable lines never reach a price.
- **A quotation carries two independent approvals**, the customer's and the
  manager's. It only reaches Approved when both say yes; either rejection kills it.
  A sales order copies its amounts off the quotation rather than referencing it, so
  editing an estimate can never move a bill that's already out.
- **Gate passes separate issuing from the gate.** Whoever raises the pass can't mark
  the goods through — that's `gatepass.passes.complete`, held by Gate Security. A
  pass stops being editable the moment it leaves Issued.
- **Demo goods support partial returns**: the issuance stays open until the last
  item is ticked back.
- **Parts carry no stock quantity.** The workshop buys against a job, so what is
  tracked is cost, not count. `PartPurchase` is the only thing that sets a part's
  cost; last cost, weighted-average cost and margin are derived from it. An older
  invoice entered late updates the average but never overwrites a newer last cost.
- **One company profile heads every document in every app.** `CompanyProfile` is a
  single row in `erp_identity` (name, logo bytes, address, contact, tax number,
  footer note), edited at `/admin/company` under `platform.company.manage`. Print
  code never sees the entity: `ToBranding()` flattens it to `CompanyBranding` in
  Shared.Kernel, which is dependency-free so Finance's `PdfDocument` DTO can carry
  it without pulling in EF Core. `Letterhead` (Shared.Printing) draws the A4 header,
  the A4 footer and the thermal-roll variants; Finance keeps its own blue-ruled
  header but reads the same profile. `ICompanyProfileService` caches the row
  process-wide and drops the cache on save, so a logo change is live immediately.
  The old Finance-only `Company.Name` setting is gone from `/finance/admin/settings`
  and is backfilled into the profile once, on startup.
- **Every document carries a Code 128 barcode *and* a QR code of its own number**,
  and `/repair/scan` resolves any of them — or a device serial — back to its record.
  Both payloads are the bare document number, so the bench scanner and a phone land
  on the same place. `Barcode` and `QrCode` live in Shared.Kernel; `BarcodeRenderer`
  draws both into QuestPDF. `QrCode` is byte mode, EC level M, versions 1-10 —
  written out rather than taken from a package, for the same reason Code 128 was.
  A collective intake prints both symbologies for the intake itself and again for
  every device on it.
- **Delivery captures who collected the device**, not just a status change: the
  delivery note is signed against that name.
- **Attendance is derived, never raw.** `AttendancePunch` is exactly what was
  scanned and is never edited; `AttendanceDay` is the judged summary and
  is rebuilt from punches. First punch of the day is the arrival, last is the
  departure — staff punch several times a day and the records carry no reliable
  in/out flag, so bracketing is the only defensible reading. One punch alone is
  `Incomplete`, not a guessed departure.
- **A hand-corrected day is stamped `AttendanceSource.Manual` and the rebuild
  skips it.** That flag is the only thing stopping the next device sync from
  silently undoing a correction; there's a regression test for it.
- **Approved leave outranks punches** when deriving a day. Pending leave does not.
- **Leave balances hold days while a request is open** (`Pending`), so two
  requests can't spend the same entitlement before either is decided.

## Plain Ledger — the hand-ledger module

Single-entry books arranged in a tree, for the informal money-lending case: you
took 100,000 from Mr A (a main ledger), passed 50,000 each to Mr B and Mr C (two
sub-ledgers under it), and each of those keeps its own record. Deliberately *not*
the accounting module.

- **Nesting is unlimited.** `ParentLedgerId` null is what makes a ledger "main";
  a sub-ledger can split further, because money passed on rarely stops at one hop.
- **A ledger's balance is a custody figure**, not a relationship figure: opening
  balance plus every entry's signed amount, i.e. how much of that pot is still
  accounted for there. `Own` is the ledger alone; `Rollup` adds every descendant,
  which on a main ledger is how much of the original money is still out in the
  tree. Fully distributing a main leaves `Own` nil and `Rollup` unchanged.
- **A transfer is always a linked pair** — `Out` on the source, `In` on the
  destination, sharing a `TransferGroup` guid, written in one transaction.
  Amending or deleting either half moves both; a one-sided transfer would leave
  the two statements permanently disagreeing.
- **`LedgerNature` (Payable/Receivable) is what makes the accounting
  unambiguous.** Without it "In" means opposite things on the main and the sub
  ledgers of one tree: on a Payable ledger money in is cash arriving and a debt
  building, on a Receivable ledger money in is cash leaving and a claim building.
  `/ledger/settings` documents the resulting four debit/credit cases.
- **Posting to the real books is opt-in and per ledger.** It happens only when the
  ledger has a `FinanceAccountId` *and* a cash account is configured; otherwise the
  module is purely informal and never touches the trial balance. A **transfer posts
  once, on the receiving side only** — cash physically moves a single time, and what
  you do with money after taking it doesn't change what you owe for it. If Finance
  refuses a journal, the entry still stands with `PostedVoucherId` null so the gap
  is visible rather than the entry being lost.
- **Modules reach the books through `IBookkeepingPoster`** (Shared.Kernel), not by
  referencing `src/FinanceERP.*` — same reasoning as `IPlatformUserDirectory` for
  users. Finance registers the implementation; `NullBookkeepingPoster` stands in
  when accounting isn't wired up, so the module builds and runs without it.

## State of the project

Seven apps behind one login, chosen from the portal at `/`:

| App | Route | Database | State |
|---|---|---|---|
| Finance | `/finance` | `finance_erp` | mature — see below |
| Repair | `/repair` | `erp_repair` | ported from Laravel, plus purchasing, barcodes and 15 reports |
| Gate Pass & Demo Goods | `/gatepass` | `erp_gatepass` | complete |
| HR | `/hr` | `erp_hr` | employee master, kiosk attendance, leave |
| Inventory | `/inventory` | `erp_inventory` | products → models → accessories, stock ledger, full CRUD |
| Auto | `/auto` | `erp_auto` | company vehicle fleet + maintenance history, full CRUD |
| Plain Ledger | `/ledger` | `erp_ledger` | hand-ledger tree: main/sub ledgers, paired transfers, optional posting to Finance |


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

1. **Finance is the untested module.** 132 tests exist —
   `tests/ErpPlatform.Shared.Tests` (27, Code 128 and QR round-trip through decoders —
   QR against ZXing, a test-only dependency),
   `tests/Hr.Tests` (42, the rotating attendance token and attendance arithmetic),
   `tests/Repair.Tests` (39, job workflow, quotation and purchase pricing, the
   collective-quotation save path, plus PDF render smoke tests) and
   `tests/Inventory.Tests` (10, stock ledger arithmetic, cache rebuild and the
   delete-while-holding-stock guards), and
   `tests/Ledger.Tests` (14, the worked 1-lac-to-two-people scenario: transfer
   pairing, tree rollup, re-parent cycle guard). The
   integration tests create and drop their own throwaway databases and skip when no
   server is reachable. Nothing covers Finance or Auto: `VoucherService`,
   `PaymentRequestService.SettleAsync`, `PayrollService.GenerateAsync`/`PayRunAsync`
   and `CloseFiscalYearAsync` remain subtle and unverified.
2. **Receipt endpoint is auth-only** (`Program.cs`, `/files/receipts/{name}`) — any
   logged-in user can fetch any receipt by filename; no ownership or permission check.
3. `README.md` predates year-close, reconciliation, utilities, projects, the
   justification flow and SMTP.
4. No CI.

## Repair printing and reports

Eleven printable documents, at `/repair/print/*`:

| Step | Document | Sizes |
|---|---|---|
| Receiving | intake receipt | A4, 80mm |
| Receiving | device labels (one per device) | 62mm roll |
| Workshop | job card, device label | A4, 62mm |
| Commercial | quotation, invoice | A4, invoice also 80mm |
| Delivering | delivery note | A4, 80mm |
| Purchasing | goods received note | A4 |

**Barcodes must be drawn with the stretching `Barcode()` renderer inside a
fixed-width container.** `BarcodeFixed()` sets its own width and will overflow — an
11-character number at 1.2 modules/pt busts a 170pt header and QuestPDF throws a
layout exception. Only use `BarcodeFixed` where the container is known to be wider.
The 62mm device label is *not* wide enough once a QR sits beside the bars: ~159pt of
usable width against ~133pt of fixed bars plus a 44pt QR. `tests/Repair.Tests`
`PrintRenderTests` generates every document precisely because QuestPDF only throws
at render time — run it after touching any print layout. It covers the logo path and
the unconfigured (`CompanyBranding.Empty`) case too.

Fifteen reports at `/repair/reports`, each exportable to Excel and PDF, plus an
"all" pack. `ReportCatalog` lists them; `ReportTableBuilder` shapes every report
into the same flat table so the screen, the Excel and the PDF can't disagree.
`repair.reports.financial` is separate from `repair.reports.view` so a supervisor
can see throughput without seeing margin.

## Attendance: kiosk, card and rotating QR

There are no biometric terminals. People clock in at an **attendance station** — a
PC by a door with an NFC reader and a QR scanner plugged in, showing `/hr/kiosk/{token}`
in full screen.

- **Both readers are keyboards.** They type what they read and press Enter, so
  there is no driver, no SDK and no device protocol. The kiosk is one permanently
  focused off-screen input; lose focus and scans go nowhere.
- **What arrived is decided by looking at it**, not by which reader sent it: a
  payload that parses as a rotating token is a QR, anything else is a card UID.
- **A webcam works as well as a USB scanner.** The kiosk can capture frames and
  post them to `/hr/kiosk/{token}/frame`, which decodes server-side with ZXing and
  returns the payload; the page then feeds it through the same scan path as the
  hardware reader. Decoding is server-side because the browser API for it is absent
  on several platforms a kiosk PC might run, and the alternative is shipping a
  third-party decoder into the page. **That endpoint answers 204 for everything
  except a successful decode** — an unreadable frame and an empty doorway are the
  ordinary case at three frames a second, and a status the error-page middleware
  wants to re-execute would turn each one into wasted work.
- SkiaSharp needs `SkiaSharp.NativeAssets.Linux.NoDependencies` to decode a frame on
  the container; without it the first camera scan throws `DllNotFoundException`.
- **The kiosk is unauthenticated** — nobody logs into a machine by a door. The
  token in the URL is the station's credential, so it is re-issuable from
  `/hr/stations` when a link leaks or a PC walks.
- **`Employee.CardNumber` is unique.** Two people on one card would merge their
  attendance, which is worse than no attendance.
- **The QR rotates every 30 seconds** — HMAC over (employee, half-minute) against a
  per-employee secret, like an authenticator app. A photograph of someone's screen
  is worthless a minute later, which is the entire point of not using a static
  badge. Verification accepts ±1 step for the walk to the scanner. The secret is
  created on first view, so an employee who never uses the QR never has one stored;
  re-issuing it kills every code already on screen, which is the answer to a lost
  phone.
- **A punch stores the QR's time-step, never the token.** A stored token is a
  stored credential.
- **Repeat scans inside 45 seconds are ignored.** Card readers fire twice on a slow
  swipe and people re-present when unsure; a stray second punch changes a derived day.
- Everything still ends at `AttendanceSyncService.RebuildAsync`, so the derivation
  rules are unchanged: first punch in, last punch out, approved leave outranking
  both, manual corrections never overwritten.

## Gotchas

- **Every module database is created by `./dev.sh up`** (`DATABASES` in that script).
  `./dev.sh db <name>` opens a shell on one; `./dev.sh reset` drops them all.
- **A drifting device clock is the top cause of wrong attendance.** Test connection
  on `/hr/devices` warns when the terminal is more than 5 minutes off.
- Changing a shift or holiday doesn't retro-fix past days — hit **Recompute Month**
  on the monthly report.
- **`array.Contains(x)` inside an EF predicate binds to the `ReadOnlySpan` overload**
  and throws at query time. Use a `List<T>` — that's why `JobWorkflow.Open` is one.
- **A manual `BeginTransactionAsync` needs the execution strategy.** Every module
  registers its DbContext with `EnableRetryOnFailure`, and EF then refuses a
  user-initiated transaction outright: wrap it in
  `db.Database.CreateExecutionStrategy().ExecuteAsync(...)` so a retried attempt
  re-runs the whole unit. `StockService.AdjustAsync`, `IntakeService.ReceiveAsync`
  and `PurchaseService.ReceiveAsync` all do this.
- **Don't hand a loaded entity to a navigation property on something you're about
  to `Add()`.** Query-time fixup leaves that object wired into its own graph
  (`Customer.Intakes` → `Jobs` → `WorkItems` → `Part`), `Add()` cascades through
  every reachable navigation, and two rows sharing one lookup row become two
  untracked instances of the same key — "cannot be tracked because another
  instance with the same key value is already being tracked", even under
  `AsNoTracking`. `QuotationService.SaveAsync` nulls `Customer`/`RepairJob`/
  `Intake`/`Items[].Part` before `Add()` for exactly this reason; only the scalar
  FK is ever persisted. `AsSplitQuery()` reduces the blast radius on the read side
  but does not fix the save.
- **Deletes are soft everywhere** (`ModuleDbContext` turns `Remove` into a flag
  update), so history keeps resolving. Anything holding a live quantity or a
  dependent record refuses deletion rather than orphaning it: Inventory won't drop
  a product/model/accessory with stock on it, Auto takes a vehicle's maintenance
  history with it, and HR won't drop a department or designation an employee still
  points at.
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
