# Finance ERP

Enterprise Finance & Accounting Management System built with **ASP.NET Core 10**, **Blazor Server**, **MudBlazor**, **EF Core (Pomelo/MySQL)**, **ASP.NET Core Identity + permission-based RBAC**, **QuestPDF** and **ClosedXML**.

## Features

- **Double-entry accounting core** — every business action (payment request, advance, loan, investment, petty cash float) posts a balanced voucher to the general ledger automatically.
- **Chart of Accounts** — unlimited depth, system accounts protected, per-account balances, auto-created sub-accounts for employees/third parties/directors.
- **Vouchers** — CPV/CRV/BPV/BRV/JV/ADJ with auto numbering (`CPV-2026-00001`), Excel-style multi-row editor with live debit/credit balancing, draft → post → void lifecycle (posted vouchers are immutable).
- **Payment request workflow** — Employee → Manager → Admin → Accountant → paid, with approval trail, comments, notifications at each hop, and automatic voucher creation on payment. Director fund requests skip manager approval.
- **Employee advances** — request, approve, disburse (auto voucher against a per-employee advance sub-account), installment schedule, repayments, late-day tracking.
- **Petty cash** — floats assigned by directors (auto-posted), daily cash book with opening/received/paid/closing.
- **Third parties, loans (given/taken, interest split), investments (deposit/profit/loss/withdrawal)** — all with automatic ledger posting.
- **Reports** — trial balance, income statement, balance sheet, general ledger, cash book, cash-flow and expense charts; period presets (today/week/month/quarter/half/year/custom); **PDF (QuestPDF)** and **Excel (ClosedXML)** export.
- **RBAC** — permissions stored in the database as role claims; policies generated dynamically; every nav item, page and action is permission-gated. Admin UI includes a full role × permission matrix.
- **Audit trail** — every create/update/delete captured with user, IP, browser, old/new values. Financial data is soft-deleted, never destroyed.
- **UI** — MudBlazor, responsive (mobile drawer), dark/light mode, server-side pagination, global snackbars, notification bell.

## Solution layout (Clean Architecture)

```
src/
  FinanceERP.Domain          entities, enums, permission catalog (no dependencies)
  FinanceERP.Application     service interfaces + DTOs
  FinanceERP.Infrastructure  EF Core (MySQL), Identity stores, services, PDF/Excel, seeding
  FinanceERP.Web             Blazor Server UI, auth pages, export endpoints
deploy/                      systemd unit + nginx config for production
```

## Run locally

1. MySQL 8 running locally. Create a database and user:
   ```sql
   CREATE DATABASE finance_erp CHARACTER SET utf8mb4;
   CREATE USER 'finance'@'localhost' IDENTIFIED BY 'yourpassword';
   GRANT ALL PRIVILEGES ON finance_erp.* TO 'finance'@'localhost';
   ```
2. Set the connection string (edit `src/FinanceERP.Web/appsettings.json` or use user-secrets/env vars).
3. `dotnet run --project src/FinanceERP.Web`

Migrations run and roles/permissions/COA are seeded automatically on startup.
Default login: `admin@financeerp.local` / `ChangeMe!123` (override via `Seed:AdminEmail` / `Seed:AdminPassword`). **Change it immediately.**

## Deployment

See [DEPLOYMENT.md](DEPLOYMENT.md) for the full Proxmox LXC guide (Debian 12 container + MySQL + nginx + systemd).
