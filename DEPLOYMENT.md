# Deploying the MEI ERP platform — Ubuntu 24.04 LXC

A start-to-finish runbook for a **fresh** container. Every step ends with a check —
run it, and don't move on until it prints what it says it should. Most deployment
pain is a step that half-failed three steps earlier.

Target: Ubuntu 24.04 LXC on Proxmox, MariaDB, the app under systemd, nginx in front.

> **Have an existing install?** A fresh install is far simpler, because the schema is
> created from scratch and there is no data migration at all. If you have real books
> on an older container, do the fresh install first, confirm it works, *then* follow
> [Appendix A](#appendix-a--carrying-data-over-from-an-old-container). Don't upgrade
> the old container in place unless you have a specific reason to.

## What you are installing

One process hosting four apps, chosen from a portal at `/` after a single login:

| App | Route | Database |
|---|---|---|
| Finance | `/finance` | `finance_erp` |
| Repair | `/repair` | `erp_repair` |
| Gate Pass & Demo Goods | `/gatepass` | `erp_gatepass` |
| HR | `/hr` | `erp_hr` |

Plus `erp_identity` — the one shared database holding users, roles, permissions,
per-user app access and the company letterhead. **Five databases.** That matters for
every backup and restore instruction below.

---

## 1. Create the container

On the **Proxmox host** shell:

```bash
pveam update
pveam available | grep ubuntu-24.04           # confirm the current filename
pveam download local ubuntu-24.04-standard_24.04-2_amd64.tar.zst
```

```bash
pct create 210 local:vztmpl/ubuntu-24.04-standard_24.04-2_amd64.tar.zst \
  --hostname mei-erp \
  --cores 2 --memory 4096 --swap 512 \
  --rootfs local-lvm:24 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --unprivileged 1 --features nesting=1 \
  --onboot 1
pct start 210
pct enter 210
```

**Why 4 GB:** four apps, PDF rendering and the attendance poller share one process
(~300–400 MB idle). If you later build on this box (§11 path B) a Release build peaks
around 1.5 GB *while the app is still running* — a 2 GB container gets OOM-killed
mid-build.

**Check** — you should be at a root prompt inside the container:

```bash
head -2 /etc/os-release              # expect Ubuntu 24.04
ip -4 addr show eth0 | grep inet     # note this IP, you need it later
```

---

## 2. Base packages

```bash
apt update && apt upgrade -y
apt install -y curl ca-certificates nginx mariadb-server git rsync libicu74 tzdata
timedatectl set-timezone Asia/Karachi     # or yours — attendance timestamps depend on it
```

Two of these are non-obvious, and both fail in ways that look like something else:

- **`libicu74`** — .NET refuses to start without ICU, and the error talks about
  globalization without ever saying "ICU is missing". LXC templates are minimal
  enough to lack it.
- **`rsync`** — both update paths in §11 use it to replace binaries while preserving
  `uploads/` and `keys/`. They abort without it.

**Check:**

```bash
systemctl is-active mariadb          # expect: active
command -v rsync nginx git           # expect three paths
timedatectl | head -3
```

---

## 3. Install .NET 10

Ubuntu 24.04 may carry .NET 10 in its own feed. Try that first — it gets security
updates through `apt`:

```bash
apt-cache policy aspnetcore-runtime-10.0
```

**If it shows a candidate version:**

```bash
apt install -y aspnetcore-runtime-10.0
```

**If it says `Unable to locate package`**, use Microsoft's installer script. Do *not*
add the packages.microsoft.com apt repo on Ubuntu 24.04 — it collides with Ubuntu's
own .NET packages and produces `dotnet-host` conflicts that are tedious to unpick:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
```

Installed this way, .NET is **not** updated by `apt` — re-run the script for patch
releases.

**Check:**

```bash
dotnet --list-runtimes
```

You must see **both** `Microsoft.NETCore.App 10.x` and `Microsoft.AspNetCore.App 10.x`.
If only the first appears, you installed the base runtime — the app needs ASP.NET Core.

---

## 4. Create the five databases

On Ubuntu, MariaDB's `root` authenticates over a **unix socket**. `mysql -u root -p`
prompts for a password that doesn't exist and fails; since you are already root, just
run `mysql`:

```bash
mysql <<'SQL'
CREATE DATABASE erp_identity CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE finance_erp  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE erp_repair   CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE erp_gatepass CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE erp_hr       CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

CREATE USER 'finance'@'localhost' IDENTIFIED BY 'CHANGE_THIS_PASSWORD';
GRANT ALL PRIVILEGES ON erp_identity.* TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON finance_erp.*  TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON erp_repair.*   TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON erp_gatepass.* TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON erp_hr.*       TO 'finance'@'localhost';
FLUSH PRIVILEGES;
SQL
```

Choose a password with **no `;` and no `"`** — it goes into both a connection string
and a JSON file, and each uses one of those characters structurally. `@`, `!`, `#`
are fine.

**Check** — must succeed *as the finance user* and list all five:

```bash
mysql -u finance -p -e "SHOW DATABASES;"
```

Nothing later works if this doesn't. Fix it here.

---

## 5. Install the application

Two ways to get the binaries onto the box. **Option A keeps everything on the
server** — no dev machine involved — and leaves a checkout in place so the
pull-based updates in §11 work with nothing further to install. Pick one.

### Option A — build on the container (recommended)

Needs the .NET **SDK**, not just the runtime installed in §3. The SDK includes the
runtime, so §3 is not wasted either way:

```bash
apt install -y dotnet-sdk-10.0
# if apt has no such package, the same script from §3 installs the SDK by default:
#   /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet

dotnet --list-sdks          # must print an SDK, not just runtimes
```

Clone and publish straight into place:

```bash
mkdir -p /opt/src
git clone https://github.com/mivu2k/finance-erp.git /opt/src/finance-erp
cd /opt/src/finance-erp
dotnet publish src/FinanceERP.Web -c Release -o /opt/finance-erp
```

The first build downloads the whole NuGet dependency tree and takes several minutes
on 2 cores. It needs roughly 1.5 GB of RAM — fine on the 4 GB from §1, OOM-killed on
a 2 GB container.

> Only publish directly into `/opt/finance-erp` on a **first** install, while the
> directory is empty. `dotnet publish` does not remove files it no longer produces,
> so for updates use §11, which rsyncs with `--delete` and preserves your state
> directories.

### Option B — build on your dev machine

```bash
# on your dev machine, in the repo
dotnet publish src/FinanceERP.Web -c Release -o publish
rsync -a publish/ root@<container-ip>:/opt/finance-erp/
```

Keeps the SDK and the ~2 GB it costs off the server, at the price of needing a
working dev machine for every deploy.

### Then, either way

**Do not skip this** — §6 and §7 both fail without the service account:

```bash
id finance-erp || useradd -r -s /usr/sbin/nologin finance-erp
mkdir -p /opt/finance-erp/{logs,keys,uploads/receipts}
chown -R finance-erp:finance-erp /opt/finance-erp
```

Those three directories are runtime state that lives beside the binaries and is
**not** produced by a build:

| Directory | Holds | Cost of losing it |
|---|---|---|
| `keys/` | DataProtection keys | Everyone logged out; existing auth cookies invalid |
| `uploads/receipts/` | Receipt attachments on payment requests | Gone — no other copy exists |
| `logs/` | Serilog files | History only |

The update scripts exclude all three from their rsync for exactly this reason.
**Never `rm -rf /opt/finance-erp`.**

**Check:**

```bash
ls /opt/finance-erp/FinanceERP.Web.dll && echo "binaries present"
id finance-erp
ls -ld /opt/finance-erp/keys /opt/finance-erp/uploads/receipts
```

Both directories must be owned by `finance-erp`. The app writes DataProtection keys
into `keys/` at startup and will not start if it cannot.

---

## 6. Configuration

Create `/opt/finance-erp/appsettings.Production.json`. This generates it so you
can't mistype the password in one of the five places:

```bash
read -rsp "finance DB password: " DBPW; echo

python3 - "$DBPW" <<'PY'
import json, sys
pw = sys.argv[1]
cfg = {
    "ConnectionStrings": {
        name: f"Server=localhost;Port=3306;Database={db};User=finance;Password={pw};"
        for name, db in [
            ("IdentityConnection", "erp_identity"),
            ("AccountsConnection", "finance_erp"),
            ("RepairConnection",   "erp_repair"),
            ("GatePassConnection", "erp_gatepass"),
            ("HrConnection",       "erp_hr"),
        ]
    },
    "Seed": {
        "AdminEmail": "admin@yourcompany.com",
        "AdminPassword": "Change-This-Once!1",
    },
    "Attendance": {"Enabled": True, "IntervalMinutes": 15},
}
json.dump(cfg, open("/opt/finance-erp/appsettings.Production.json", "w"), indent=2)
print("written")
PY

chmod 600 /opt/finance-erp/appsettings.Production.json
chown finance-erp: /opt/finance-erp/appsettings.Production.json
unset DBPW
```

Edit the `Seed` values before first boot — they create the first admin and are read
**only** while no admin exists.

- **`Attendance`** drives the ZKTeco poller. Harmless with no terminals configured;
  `"Enabled": false` switches it off entirely.
- **SMTP is optional** and omitted above. Notifications stay off unless you add an
  `Smtp` block with a non-empty `Host`. Nothing else depends on it.
- There is **no `DefaultConnection`** any more. The build looks up each of the five
  names explicitly and never falls back.

**Check** — prints the file, five connection strings, no syntax error:

```bash
python3 -m json.tool /opt/finance-erp/appsettings.Production.json
```

---

## 7. systemd service

```bash
curl -fsSL https://raw.githubusercontent.com/mivu2k/finance-erp/main/deploy/finance-erp.service \
  -o /etc/systemd/system/finance-erp.service
systemctl daemon-reload
systemctl enable --now finance-erp
```

The service binds `http://127.0.0.1:5000` — loopback only, nginx in front.

**Check** — first boot runs every migration and the seeder, so give it a minute:

```bash
systemctl status finance-erp --no-pager
journalctl -u finance-erp -n 50 --no-pager
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5000/
```

`302` is success — the redirect to the login page. `000` means it isn't listening;
read the journal.

> `[ERR] The model for context '...' has pending changes` appears once per database
> context at startup. It is **benign** — EF logs it without throwing. Don't chase it.

**Confirm the schema built and the admin seeded:**

```bash
mysql -u finance -p -e "SELECT COUNT(*) AS users FROM erp_identity.AspNetUsers;"
```

Expect `1`.

---

## 8. nginx

```bash
curl -fsSL https://raw.githubusercontent.com/mivu2k/finance-erp/main/deploy/nginx-finance-erp.conf \
  -o /etc/nginx/sites-available/finance-erp
sed -i "s/erp.example.com/$(hostname -I | awk '{print $1}')/" /etc/nginx/sites-available/finance-erp
ln -sf /etc/nginx/sites-available/finance-erp /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx
```

Blazor Server needs WebSockets; the supplied config carries the `Upgrade`/`Connection`
headers and a 100s read timeout to keep circuits alive. Without them the UI
disconnects every few seconds in a way that looks like an application bug.

**Check** — in the container, then from your desktop browser:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost/       # expect 302
```

### HTTPS

Internet-facing: `apt install -y certbot python3-certbot-nginx && certbot --nginx -d erp.example.com`.
LAN-only: terminate TLS on your existing reverse proxy and point it at this
container's port 80 — **enable WebSocket support there too**.

---

## 9. First login

Browse to `http://<container-ip>/` and sign in with the `Seed` credentials.

1. **Change the admin password immediately** — top-right menu → My profile → Password.
2. **Administration → Company Profile** — name, logo, address, contact, tax number,
   footer note. This is the letterhead on every printed document in all four apps,
   and it starts blank.
3. **Administration → Users** — create real users; assign roles **and** app access.
   A role is scoped to one app: holding it both admits the user to that app's tile on
   the portal and decides what they can do inside it.
4. **Administration → Roles & Permissions** — review the matrix.
5. **Finance → Settings** — currency and the low-cash alert threshold.

> **App access is a sign-in claim.** It is stamped onto the login cookie so the portal
> and nav never hit the database. A user you just granted access to must sign out and
> back in before the tile appears.

---

## 10. Backups

Set this up **now**, not later:

```bash
cat > /etc/cron.daily/mei-erp-backup <<'EOF'
#!/bin/sh
set -e
DEST=/var/backups/mei-erp
mkdir -p "$DEST"
STAMP=$(date +%F)

for db in erp_identity finance_erp erp_repair erp_gatepass erp_hr; do
    mysqldump --single-transaction --routines "$db" | gzip > "$DEST/$db-$STAMP.sql.gz"
done

# Receipt attachments and DataProtection keys are in no database.
tar czf "$DEST/files-$STAMP.tar.gz" -C /opt/finance-erp uploads keys

find "$DEST" -name '*.gz' -mtime +30 -delete
EOF
chmod +x /etc/cron.daily/mei-erp-backup
/etc/cron.daily/mei-erp-backup
ls -lh /var/backups/mei-erp/
```

Root's socket auth means the job needs no stored password. Add Proxmox vzdump
snapshots of the whole container too (Datacenter → Backup), and **test a restore
once** — an untested backup is a guess.

---

## 11. Updates

Two paths. Both back up all five databases, snapshot the binaries, restart,
health-check, and roll the *code* back automatically if the app doesn't return.

### A. Push from your dev machine — `deploy/update.sh`

Builds locally, ships the result. Needs the .NET SDK on your machine, `rsync` on both
ends, and key-based SSH (`ssh-copy-id root@<container-ip>`).

```bash
./deploy/update.sh <container-ip>
```

Ships your **working tree**, which may match no commit — fine for a hotfix, but then
nothing on GitHub records what is running.

### B. Pull on the container — `deploy/self-update.sh`

The container fetches `main`, builds it, and swaps the binaries in. Needs the
**SDK**, not just the runtime installed in §3:

**If you used §5 Option A, the SDK and the checkout are already there** — just make
the script executable and skip to running it. Otherwise:

```bash
apt install -y dotnet-sdk-10.0
# or, if apt has no such package:
#   /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet

mkdir -p /opt/src
git clone https://github.com/mivu2k/finance-erp.git /opt/src/finance-erp
```

```bash
chmod +x /opt/src/finance-erp/deploy/self-update.sh
dotnet --list-sdks          # must print an SDK, not just runtimes
command -v rsync            # must print a path
```

```bash
/opt/src/finance-erp/deploy/self-update.sh
```

- Builds **before** stopping the service, so a compile error costs no downtime.
- Compares against `/opt/finance-erp/.deployed-revision`, not the checkout — a fresh
  clone already sits at `origin/main` while old binaries run, so comparing the
  checkout would skip the first deploy and wrongly report success.
- Exits immediately if nothing moved; `FINANCE_ERP_FORCE=1` rebuilds anyway.
- **`git reset --hard` runs against `/opt/src/finance-erp`** — never edit anything
  there. It is a build input; local changes vanish without warning.
- Another branch: `FINANCE_ERP_BRANCH=my-branch /opt/src/finance-erp/deploy/self-update.sh`.
- The repo is **public**, so the clone needs no credentials. If it ever goes private,
  use a deploy key — not a personal access token on the server.

### Unattended (optional)

A timer can run path B nightly. Weigh it up: it deploys whatever is on `main` to your
live books with nobody watching, and while it rolls back a failed *start*, it cannot
undo a migration that applied successfully but was wrong.

```bash
cp /opt/src/finance-erp/deploy/finance-erp-update.{service,timer} /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now finance-erp-update.timer
systemctl list-timers finance-erp-update
```

Runs 03:15 with 15m jitter, 30-minute build timeout, and **hardcodes
`/opt/src/finance-erp`**. Overriding any `FINANCE_ERP_*` for the timer means adding
`Environment=` lines to the unit — the script's defaults do not reach a systemd
service.

### Rollback

Roll binaries back by **syncing over** the directory, never deleting it — `uploads/`
and `keys/` live there and are not in the snapshot:

```bash
systemctl stop finance-erp
rsync -a --delete \
  --exclude 'appsettings.Production.json' \
  --exclude 'uploads/' --exclude 'keys/' --exclude 'logs/' \
  --exclude '.deployed-revision' \
  /opt/finance-erp.prev/ /opt/finance-erp/
chown -R finance-erp:finance-erp /opt/finance-erp
systemctl start finance-erp
```

Databases only if a migration needs undoing — dumps are named per database:

```bash
zcat /var/backups/finance-erp/erp_identity-<stamp>.sql.gz | mysql erp_identity
zcat /var/backups/finance-erp/finance_erp-<stamp>.sql.gz  | mysql finance_erp
```

### Script settings

| Variable | Default |
|---|---|
| `FINANCE_ERP_APP_DIR` | `/opt/finance-erp` |
| `FINANCE_ERP_SRC` | `/opt/src/finance-erp` (path B) |
| `FINANCE_ERP_SERVICE` | `finance-erp` |
| `FINANCE_ERP_DBS` | `erp_identity finance_erp erp_repair erp_gatepass erp_hr` |
| `FINANCE_ERP_APP_USER` | `finance-erp` |
| `FINANCE_ERP_BRANCH` | `main` (path B) |
| `FINANCE_ERP_HEALTH_URL` | `http://localhost:5000/` |
| `FINANCE_ERP_KEEP_BACKUPS` | `10` (per database) |
| `FINANCE_ERP_HOST` / `FINANCE_ERP_SSH_USER` | — / `root` (path A) |

---

## 12. Biometric attendance (optional)

HR polls ZKTeco terminals (uFace 800 and the standalone range) over **TCP 4370**. The
vendor SDK is 32-bit Windows COM and cannot run here, so the protocol is implemented
in-process.

- The container must reach each terminal's IP on port 4370. Separate VLAN? Open it.
- Add terminals under **HR → Devices**, then *Test connection* — it also warns when
  the terminal clock is more than 5 minutes off, the top cause of wrong attendance.
- `Attendance:IntervalMinutes` sets the interval (default 15). Sync is idempotent:
  devices keep their whole log and are re-read in full, deduped.

> This has never been run against real hardware. The wire format is covered by unit
> tests reproducing the device's own encoding, but first contact with a live terminal
> is unproven. *Test connection* is the first thing to try.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Couldn't find a valid ICU package` | `libicu74` missing (§2) |
| `Access denied for user 'finance'@'localhost'` | Wrong password in the config, or grants not applied. Prove it with `mysql -u finance -p` first |
| `ConnectionStrings:IdentityConnection is not configured` | Config still has a single `DefaultConnection`. The build needs all five names (§6) |
| `Unable to locate package aspnetcore-runtime-10.0` | Use the installer script (§3) |
| `Unknown database 'erp_identity'` | §4 was skipped or partially run |
| Service `active`, `curl` returns `000` | Read `journalctl -u finance-erp -n 50` — almost always a database connection failure |
| UI connects then drops every few seconds | WebSockets not proxied — check nginx **and** any upstream proxy (§8) |
| `502 Bad Gateway` right after a successful login | nginx proxy buffers too small for the auth cookie. `tail /var/log/nginx/error.log` shows `upstream sent too big header`. The supplied config sets 32k; an upstream proxy in front needs the same |
| `Assets file project.assets.json not found` | `dotnet restore` in the checkout first (path B) |
| Login works but an app tile is missing | App access applies at **next sign-in** — sign out and back in (§9) |
| `mysql -u root -p` fails on a fresh box | MariaDB root uses socket auth on Ubuntu — run `mysql` as root, no `-u root -p` |
| `chown: invalid spec: 'finance-erp:'` | The service account was never created — the `useradd` at the end of §5 |

---

## Appendix A — carrying data over from an old container

Only if you have real data on a pre-split install, where users and accounts shared one
`finance_erp`. Do this **after** the fresh install above is confirmed working.

On the **old** container:

```bash
systemctl stop finance-erp
mysqldump --single-transaction --routines finance_erp | gzip > /root/old-finance.sql.gz
tar czf /root/old-files.tar.gz -C /opt/finance-erp uploads keys
```

Copy both across, then on the **new** container:

```bash
systemctl stop finance-erp
zcat /root/old-finance.sql.gz | mysql finance_erp
tar xzf /root/old-files.tar.gz -C /opt/finance-erp
chown -R finance-erp:finance-erp /opt/finance-erp

mysql < /opt/src/finance-erp/deploy/migrate-identity-out-of-accounts.sql
mysql -e "SELECT COUNT(*) FROM erp_identity.AspNetUsers;"     # must be > 0

systemctl start finance-erp
```

`migrate-identity-out-of-accounts.sql` copies users, roles and permission claims out
of the old `finance_erp.AspNet*` tables into `erp_identity`. The accounts migration
then drops the stale originals on the next boot — which is why the count check above
is not optional.

Two things people forget:

- **Restoring `keys/`** — without the old DataProtection keys, every existing session
  and auth cookie is invalid.
- **Restoring `uploads/`** — receipt attachments exist nowhere else.

---

## Known gaps

- **`/files/receipts/{name}` is auth-only.** Any logged-in user who knows a stored
  filename can fetch any receipt; there is no ownership or permission check.
  Filenames are GUIDs, so this is obscurity rather than a control.
- **No CI.** Run `dotnet test` yourself before deploying.
