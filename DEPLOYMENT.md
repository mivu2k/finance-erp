# Deploying the MEI ERP platform on a Proxmox LXC

Target: a Debian 12 LXC container running MariaDB, the app under systemd, nginx in
front.

This is **one process hosting four apps**, chosen from a portal at `/` after a
single login:

| App | Route | Database |
|---|---|---|
| Finance | `/finance` | `finance_erp` |
| Repair | `/repair` | `erp_repair` |
| Gate Pass & Demo Goods | `/gatepass` | `erp_gatepass` |
| HR | `/hr` | `erp_hr` |

Plus `erp_identity`, the one shared database holding users, roles, permissions,
per-user app access and the company letterhead. **Five databases in total** — that
matters for every backup and restore instruction below.

## 1. Create the container (Proxmox host)

```bash
# Download a template once (host shell):
pveam update
pveam download local debian-12-standard_12.7-1_amd64.tar.zst

# Create the container (adjust IDs/storage/bridge to your setup):
pct create 210 local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst \
  --hostname mei-erp \
  --cores 2 --memory 4096 --swap 512 \
  --rootfs local-lvm:24 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --unprivileged 1 --features nesting=1 \
  --onboot 1
pct start 210
pct enter 210
```

4 GB RAM is the comfortable figure now that four apps, PDF rendering (QuestPDF)
and the attendance poller share one process; the app idles around 300–400 MB and
spikes while generating report packs. 16 GB of disk is enough to start, but
receipt attachments and job photos accumulate in `uploads/` — see §11.

## 2. Inside the container — base packages

```bash
apt update && apt upgrade -y
apt install -y curl gnupg ca-certificates nginx mariadb-server git
```

> MariaDB from Debian repos is fully compatible (Pomelo auto-detects the server
> version). If you specifically want Oracle MySQL 8, add the MySQL APT repo instead.

## 3. Install the .NET 10 runtime

```bash
curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o /tmp/msprod.deb
dpkg -i /tmp/msprod.deb && apt update
apt install -y aspnetcore-runtime-10.0
```

(If the package isn't available for your distro yet: `curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet`.)

If you plan to build **on** the container (§10 path B), install the SDK instead:
`apt install -y dotnet-sdk-10.0`.

## 4. Databases

Create all five. The app migrates each on boot but will **not** create them.

```bash
mysql_secure_installation   # set root password, remove test db
mysql -u root -p <<'SQL'
CREATE DATABASE erp_identity CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE finance_erp  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE erp_repair   CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE erp_gatepass CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE erp_hr       CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'finance'@'localhost' IDENTIFIED BY 'STRONG_PASSWORD_HERE';
GRANT ALL PRIVILEGES ON erp_identity.* TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON finance_erp.*  TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON erp_repair.*   TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON erp_gatepass.* TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON erp_hr.*       TO 'finance'@'localhost';
FLUSH PRIVILEGES;
SQL
```

> **Upgrading an install that predates the identity split?** Run
> `deploy/migrate-identity-out-of-accounts.sql` **before** starting the new build,
> or the accounts migration drops the old `AspNet*` tables and takes your users
> with it. A fresh install needs nothing.

## 5. Publish and install the app

On your dev machine (or clone + build in the container):

```bash
dotnet publish src/FinanceERP.Web -c Release -o publish
scp -r publish/* root@<container-ip>:/opt/finance-erp/
```

In the container:

```bash
useradd -r -s /usr/sbin/nologin finance-erp
mkdir -p /opt/finance-erp/{logs,keys,uploads/receipts}
chown -R finance-erp:finance-erp /opt/finance-erp
```

Those three directories are **runtime state that lives alongside the binaries** and
is not part of a publish. Losing them costs you real things:

| Directory | Holds | If you delete it |
|---|---|---|
| `keys/` | DataProtection keys | Everyone is logged out; existing auth cookies break |
| `uploads/receipts/` | Receipt attachments on payment requests | Gone — no other copy exists |
| `logs/` | Serilog files | Only history is lost |

The update scripts exclude all three from their rsync for exactly this reason.
**Never `rm -rf /opt/finance-erp`** — see §10.

## 6. Production configuration

Configure **outside** the repo — create `/opt/finance-erp/appsettings.Production.json`:

```json
{
  "ConnectionStrings": {
    "IdentityConnection": "Server=localhost;Port=3306;Database=erp_identity;User=finance;Password=STRONG_PASSWORD_HERE;",
    "AccountsConnection": "Server=localhost;Port=3306;Database=finance_erp;User=finance;Password=STRONG_PASSWORD_HERE;",
    "RepairConnection":   "Server=localhost;Port=3306;Database=erp_repair;User=finance;Password=STRONG_PASSWORD_HERE;",
    "GatePassConnection": "Server=localhost;Port=3306;Database=erp_gatepass;User=finance;Password=STRONG_PASSWORD_HERE;",
    "HrConnection":       "Server=localhost;Port=3306;Database=erp_hr;User=finance;Password=STRONG_PASSWORD_HERE;"
  },
  "Seed": {
    "AdminEmail": "admin@yourcompany.com",
    "AdminPassword": "A-Strong-One-Time-Password!1"
  },
  "Attendance": {
    "Enabled": true,
    "IntervalMinutes": 15
  },
  "Smtp": {
    "Host": "",
    "Port": 587,
    "User": "",
    "Password": "",
    "From": "erp@yourcompany.com",
    "EnableSsl": true
  }
}
```

```bash
chmod 600 /opt/finance-erp/appsettings.Production.json
chown finance-erp: /opt/finance-erp/appsettings.Production.json
```

- **`Seed`** creates the first admin on first boot only. Both keys must be present
  or no admin is seeded.
- **`Attendance`** drives the ZKTeco poller — set `Enabled: false` if you have no
  biometric terminals, or it will log connection failures every interval.
- **`Smtp`** is entirely optional. Email notifications stay off unless `Smtp:Host`
  is non-empty; everything else in the app works without it.

## 7. systemd service

```bash
cp deploy/finance-erp.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now finance-erp
journalctl -u finance-erp -f     # watch first boot: migrations + seeding run here
```

The service binds `http://127.0.0.1:5000` — loopback only, with nginx in front.

> `[ERR] The model for context '...' has pending changes` on startup is **benign**
> and appears once per database context. EF logs it without throwing. Don't chase it.

## 8. nginx reverse proxy

```bash
cp deploy/nginx-finance-erp.conf /etc/nginx/sites-available/finance-erp
ln -s /etc/nginx/sites-available/finance-erp /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx
```

Blazor Server uses WebSockets — the provided config includes the required
`Upgrade`/`Connection` headers. Without them the UI disconnects or falls back.

### HTTPS

If the container is reachable from the internet, use certbot (`apt install certbot
python3-certbot-nginx && certbot --nginx -d erp.example.com`). On a LAN-only setup,
terminate TLS on your existing reverse proxy (NPM/Traefik/Caddy on another LXC) and
point it at this container's port 80 — enable WebSocket support there too.

## 9. First login & setup

1. Browse to the container IP → log in with the seeded admin → **change the
   password immediately** (top-right menu → My profile → Password).
2. **Administration → Company Profile** — set the name, upload the logo, fill in
   the address, contact details, tax number and footer note. This is the letterhead
   on every printed document in all four apps, and it starts effectively blank.
3. **Administration → Users** — create real users, assign roles *and* app access.
   A role is scoped to one app: holding it both admits the user to that app's tile
   on the portal and decides what they can do inside it.
4. **Administration → Roles & Permissions** — review the permission matrix.
5. If you use biometric attendance, add your terminals under **HR → Devices** and
   hit *Test connection* first — see §12.

> **Access changes apply at next sign-in.** App access is stamped onto the login
> cookie as claims so the portal and nav never hit the database. A user you just
> granted an app to must sign out and back in.

## 10. Updates

Two scripted paths. Both back up **every database**, snapshot the current binaries,
restart the service (migrations apply on boot), wait for a health check, and roll
the *code* back automatically if the app doesn't come back.

### A. Push from your dev machine — `deploy/update.sh`

Nothing extra is needed on the container: it publishes locally and rsyncs the
output. Requires SSH key auth (`ssh-copy-id root@<container-ip>`).

```bash
./deploy/update.sh <container-ip>
# or: FINANCE_ERP_HOST=<container-ip> ./deploy/update.sh
```

This ships your **working tree**, which may not match any commit.

### B. Pull on the container — `deploy/self-update.sh`

The container builds from GitHub itself. Needs the .NET **SDK** and a checkout:

```bash
apt install -y git dotnet-sdk-10.0
mkdir -p /opt/src && cd /opt/src
git clone https://github.com/mivu2k/finance-erp.git
chmod +x /opt/src/finance-erp/deploy/self-update.sh
```

Then, whenever you want to update:

```bash
/opt/src/finance-erp/deploy/self-update.sh
```

It exits immediately if `origin/main` hasn't moved (`FINANCE_ERP_FORCE=1` to
rebuild anyway).

### Fully unattended (optional)

A timer can run path B nightly. Weigh this up first: it deploys whatever is on
`main` to your live books with nobody watching, and while the script rolls back a
failed *start*, it cannot undo a migration that applied successfully but was
wrong. Tagged releases or a manual trigger are safer for financial data.

```bash
cp /opt/src/finance-erp/deploy/finance-erp-update.{service,timer} /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now finance-erp-update.timer
systemctl list-timers finance-erp-update    # confirm next run
journalctl -u finance-erp-update -f         # watch an update happen
```

Trigger one by hand with `systemctl start finance-erp-update`.

### Rollback

Both scripts print the exact commands afterwards. Roll the binaries back by
**syncing over** the app directory, never by deleting it — `uploads/` and `keys/`
live there and the snapshot does not contain them:

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

Only if a migration needs undoing — restore the database(s) it touched. Dumps are
named per database:

```bash
zcat /var/backups/finance-erp/erp_identity-<stamp>.sql.gz | mysql erp_identity
zcat /var/backups/finance-erp/finance_erp-<stamp>.sql.gz  | mysql finance_erp
# ...and so on for erp_repair, erp_gatepass, erp_hr
```

A release usually migrates only some databases; restoring one that didn't change
is harmless but pointless. `journalctl -u finance-erp` shows which migrations ran.

### Script settings

Environment variables both scripts honour, if your install differs from this guide:

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

## 11. Backups

The update scripts only back up *at deploy time*. Run a standing daily backup too —
all five databases plus the file state that has no other copy:

```bash
# /etc/cron.daily/mei-erp-backup
#!/bin/sh
set -e
DEST=/var/backups/mei-erp
mkdir -p "$DEST"
STAMP=$(date +%F)

for db in erp_identity finance_erp erp_repair erp_gatepass erp_hr; do
    mysqldump --single-transaction --routines "$db" | gzip > "$DEST/$db-$STAMP.sql.gz"
done

# Receipt attachments and the DataProtection keys are not in any database.
tar czf "$DEST/files-$STAMP.tar.gz" -C /opt/finance-erp uploads keys

find "$DEST" -name '*.gz' -mtime +30 -delete
```

```bash
chmod +x /etc/cron.daily/mei-erp-backup
```

Plus Proxmox-level vzdump snapshots of the whole container (Datacenter → Backup).
Test a restore at least once — an untested backup is a guess.

## 12. Biometric attendance (optional)

HR polls ZKTeco terminals (uFace 800 and the standalone range) directly over
**TCP 4370** — the vendor SDK is 32-bit Windows COM and can't run here, so the
protocol is implemented in-process.

- The container must reach each terminal's IP on port 4370. If they're on a
  separate VLAN, open that path.
- Add terminals under **HR → Devices**, then use *Test connection* — it also warns
  when the terminal's clock is more than 5 minutes off, which is the top cause of
  wrong attendance.
- Polling interval is `Attendance:IntervalMinutes` (default 15). Sync is
  idempotent: devices keep their whole log and are re-read in full, deduped.
- Set `Attendance:Enabled: false` if you have no terminals.

> This has never been run against real hardware. The wire format is covered by unit
> tests that reproduce the device's own encoding, but first contact with a live
> terminal is unproven — test connection is the first thing to try.

## 13. Known gaps

- **`/files/receipts/{name}` is auth-only.** Any logged-in user who knows or
  guesses a stored filename can fetch any receipt; there is no ownership or
  permission check. Filenames are GUIDs, so this is obscurity rather than a
  control. Worth knowing if your user base is wider than your accounts team.
- **No CI.** Nothing runs the test suite except you: `dotnet test` before you
  deploy.
