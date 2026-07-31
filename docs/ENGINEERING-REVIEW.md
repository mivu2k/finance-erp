# Engineering review — MEI ERP platform

A senior-developer pass over the whole tree, and what was done about it.
Written 2026-07-31. Update the status column as items move.

## Verdict

The **architecture is sound** and better than most in-house ERPs: real module
isolation with no cross-database foreign keys, permissions as data rather than
hardcoded checks, append-only ledgers with derived-not-stored balances, nullable
enabled across all 39 projects, zero raw SQL, no package version drift, and a
`CLAUDE.md` that records *why* the non-obvious decisions were made.

The gap to industry standard was never architecture. It was **engineering hygiene**
— no automated gates — plus a small number of systemic correctness issues that a
gate would have caught.

## What was fixed

| # | Finding | Status |
|---|---|---|
| 1 | No CI whatsoever — no `.github`, nothing stopping a broken commit reaching `main` while `self-update.sh` deploys it unattended at 03:15 | **Done** — `.github/workflows/ci.yml`: restore, build in Release, test against a MariaDB service container, fail on skipped tests, upload results |
| 2 | Integration tests reported **pass** when the database was unreachable (`if (!_available) return;`) — a green run asserting nothing | **Done** — `IntegrationDatabase.Require` skips locally, **throws in CI**. Verified both ways: 31 skipped locally, 31 failed under `CI=true` |
| 3 | "Today" computed two ways — `DateTime.Today` (server-local) in 52 places, `DateOnly.FromDateTime(DateTime.UtcNow)` in 35. In Asia/Karachi these disagree for five hours every night | **Done** — `IBusinessClock` in the kernel, business timezone from `Platform:TimeZone`. Entity `IsOverdue` properties became `IsOverdueOn(today)` — an entity must not read the machine clock |
| 4 | No optimistic concurrency anywhere — two users editing the same record silently last-write-wins | **Done** — `IConcurrencyChecked` opt-in, stamped in `ModuleDbContext.SaveChangesAsync`, applied to stock items, deliveries, goods receipts and physical files. Proven by test |
| 5 | `/files/receipts/{name}` was auth-only: any signed-in user could fetch anyone's receipt | **Done** — ownership check; returns 404 (not 403) so the response can't confirm a file exists |
| 6 | Warnings that describe invisible runtime failures were only warnings | **Done** — `Directory.Build.props` promotes `RZ10012`, `BL0008`, `CS4014` to errors and silences the cosmetic ones |
| 7 | A working dev DB password committed in `appsettings.json` | **Done** — placeholder in tracked config; dev credentials in a gitignored override that `dev.sh` seeds from `.example` |
| 8 | No health checks — deploys verified by curling `/` for a 302 | **Done** — `/health/live` (no DB) and `/health/ready` (checks databases), both anonymous |
| 9 | No `.editorconfig`, `global.json`, or solution-wide build settings | **Done** |
| 10 | New projects were missing from the solution file, so a solution build silently skipped them | **Done** — Tender ×3 and TestSupport added |

### Bugs the new gates caught immediately

Turning warnings into errors found two live defects on the first build:

- **`AppSwitcher` had never rendered.** `ErpPlatform.Shared.Web.Portal` was missing
  from `_Imports.razor`, so Razor treated `<AppSwitcher />` as an unknown HTML
  element. The app-switcher button silently did not exist in the platform shell.
- **`TaskBoard` had the same defect** the day it was written — page returned 200
  with the whole section missing.

This is the single best argument for item 6: both failures were invisible at
runtime and produced no error of any kind.

## What is still open

Ranked. None are blocking, all are real.

1. **The clock migration is partial.** Decision-making sites (overdue, expiry,
   attendance validation, alerts) now use `IBusinessClock`. Roughly 20 service files
   and ~30 Razor date-picker defaults still call `DateTime.Today`/`UtcNow` directly.
   The Razor ones are UI defaults and low-risk; the service ones should be migrated
   module by module. **Do not add new ones** — inject the clock.
2. **No HTTP-level tests.** Everything is service-level. There is no
   `WebApplicationFactory` or bUnit coverage, which is exactly why both rendering
   bugs above were invisible to a 255-test suite. This is the highest-value
   remaining test work.
3. **300 `ScopeFactory.CreateScope()` calls and 134 `catch → Snackbar` blocks** in
   Razor pages. Blazor Server's DbContext-lifetime workaround, written longhand.
   Extract one helper owning scoping *and* error-to-snackbar, apply to new code,
   retrofit opportunistically. Also `AddDbContextFactory` is the officially correct
   registration for Blazor Server; all nine modules use `AddDbContext`.
4. **Concurrency tokens cover Inventory and the file registry, not Finance.**
   `Voucher`, `PaymentRequest` and `PayrollRun` are the remaining contended
   money-bearing aggregates. The mechanism exists; it is one interface and a
   migration per module.
5. **Observability is thin.** Only 15 files touch `ILogger`; no request correlation,
   no structured log shipping, no rate limit on login (Identity lockout is on, which
   covers the worst of it).
6. **`README.md` is stale** — predates year-close, reconciliation, SMTP, Tender,
   Inventory sales and the file registry.
7. **18 near-identical CRUD services.** Not urgent; resist over-abstracting. It is
   the reason each new module costs what it does.
8. **64k lines of generated migrations vs 31k of code.** Worth squashing to a
   baseline at some point.

## Conventions this review establishes

- **A build warning that hides a runtime failure is an error.** See
  `Directory.Build.props`. If you add a warning suppression, say why in a comment.
- **A test that cannot run must not report success.** Guard every integration test
  with `IntegrationDatabase.Require(_available)` and mark it `[SkippableFact]`.
  A suite that finishes suspiciously fast is skipping, not passing.
- **Never read the clock directly.** Inject `IBusinessClock`. Entities take a
  `DateOnly today` parameter rather than reading it themselves, which is what makes
  date-boundary behaviour testable with `FixedClock`.
- **Add `IConcurrencyChecked`** to any record where two people editing at once
  would cost money, stock, or someone's work.
