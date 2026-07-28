-- One-time data migration: move ASP.NET Identity out of the accounts database and
-- into the shared platform identity database.
--
-- Context: before the multi-app split, users, roles and permission claims lived in
-- the same database as the accounting tables. They now live in `erp_identity`,
-- shared by every app, while each business module keeps its own database.
--
-- RUN THIS AFTER the app has started once against the new build (so that
-- `erp_identity` exists and has been migrated by IdentitySeeder), and BEFORE the
-- accounts migration `SplitIdentityIntoSharedDatabase` drops the old AspNet*
-- tables. In practice:
--
--   1. Deploy the new build but do not start it.
--   2. Start it once with the accounts connection pointed at a scratch database,
--      or run `dotnet ef database update -p shared/ErpPlatform.Shared.Identity`
--      to create erp_identity.
--   3. Run this script.
--   4. Start the app normally; the accounts migration then drops the stale tables.
--
-- Adjust the two schema names below if yours differ.
SET @accounts := 'finance_erp';
SET @identity := 'erp_identity';

-- Users. FullName/EmployeeCode/IsActive carry over; LedgerAccountId and
-- DepartmentId deliberately do not — they are accounting concerns and are
-- recreated in the accounts database as EmployeeProfiles rows (see below).
INSERT IGNORE INTO erp_identity.AspNetUsers (
    Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed,
    PasswordHash, SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed,
    TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount,
    FullName, EmployeeCode, ManagerId, IsActive, LastLoginUtc)
SELECT
    Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed,
    PasswordHash, SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed,
    TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount,
    FullName, EmployeeCode, ManagerId, IsActive, NULL
FROM finance_erp.AspNetUsers;

-- Roles. Everything that isn't the platform Super Admin is a Finance role now,
-- so it is scoped to the finance module — that scoping is what grants its holders
-- the Finance tile on the portal.
INSERT IGNORE INTO erp_identity.AspNetRoles
    (Id, Name, NormalizedName, ConcurrencyStamp, ModuleKey, Description, IsSystem)
SELECT
    Id, Name, NormalizedName, ConcurrencyStamp,
    CASE WHEN Name = 'Super Admin' THEN NULL ELSE 'finance' END,
    NULL, 1
FROM finance_erp.AspNetRoles;

INSERT IGNORE INTO erp_identity.AspNetUserRoles (UserId, RoleId)
SELECT UserId, RoleId FROM finance_erp.AspNetUserRoles;

INSERT IGNORE INTO erp_identity.AspNetUserClaims (Id, UserId, ClaimType, ClaimValue)
SELECT Id, UserId, ClaimType, ClaimValue FROM finance_erp.AspNetUserClaims;

INSERT IGNORE INTO erp_identity.AspNetUserLogins
    (LoginProvider, ProviderKey, ProviderDisplayName, UserId)
SELECT LoginProvider, ProviderKey, ProviderDisplayName, UserId FROM finance_erp.AspNetUserLogins;

INSERT IGNORE INTO erp_identity.AspNetUserTokens (UserId, LoginProvider, Name, Value)
SELECT UserId, LoginProvider, Name, Value FROM finance_erp.AspNetUserTokens;

-- Permission claims. The values gained a module prefix in the split
-- ("Vouchers.Post" -> "finance.vouchers.post"), so they are rewritten on the way
-- across. Any claim that is not a permission is copied unchanged.
INSERT IGNORE INTO erp_identity.AspNetRoleClaims (Id, RoleId, ClaimType, ClaimValue)
SELECT Id, RoleId, ClaimType,
       CASE WHEN ClaimType = 'permission' AND ClaimValue NOT LIKE '%.%.%'
            THEN CONCAT('finance.', LOWER(ClaimValue))
            ELSE ClaimValue END
FROM finance_erp.AspNetRoleClaims;

-- Accounts-side employee profiles. The accounting fields that used to hang off
-- the user record now live here, keyed by Identity user id. The app also rebuilds
-- this table on every startup, so this step only preserves department and ledger
-- account assignments that would otherwise be lost.
INSERT IGNORE INTO finance_erp.EmployeeProfiles
    (UserId, FullName, Email, EmployeeCode, IsActive, DepartmentId, LedgerAccountId, CreatedAtUtc)
SELECT Id, FullName, Email, EmployeeCode, IsActive, DepartmentId, LedgerAccountId, UTC_TIMESTAMP()
FROM finance_erp.AspNetUsers;
