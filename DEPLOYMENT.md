# Deploying Finance ERP on a Proxmox LXC

Target: a Debian 12 LXC container running MySQL 8/MariaDB, the app under systemd, nginx in front.

## 1. Create the container (Proxmox host)

In the Proxmox UI (or shell):

```bash
# Download a template once (host shell):
pveam update
pveam download local debian-12-standard_12.7-1_amd64.tar.zst

# Create the container (adjust IDs/storage/bridge to your setup):
pct create 210 local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst \
  --hostname finance-erp \
  --cores 2 --memory 2048 --swap 512 \
  --rootfs local-lvm:16 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --unprivileged 1 --features nesting=1 \
  --onboot 1
pct start 210
pct enter 210
```

2 GB RAM / 2 cores is comfortable; the app itself idles around 200–300 MB.

## 2. Inside the container — base packages

```bash
apt update && apt upgrade -y
apt install -y curl gnupg ca-certificates nginx mariadb-server git
```

> MariaDB from Debian repos is fully compatible with the app (Pomelo auto-detects the server version). If you specifically want Oracle MySQL 8, add the MySQL APT repo instead.

## 3. Install the .NET 10 runtime

```bash
curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o /tmp/msprod.deb
dpkg -i /tmp/msprod.deb && apt update
apt install -y aspnetcore-runtime-10.0
```

(If the package isn't available yet for your distro, use the install script: `curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet`.)

## 4. Database

The platform is four apps over five databases: one shared identity database and
one per app. Create them all — the app migrates each on boot but will not create
them.

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
> `deploy/migrate-identity-out-of-accounts.sql` before starting the new build, or
> the accounts migration drops the old `AspNet*` tables and takes your users with
> them. A fresh install needs nothing.

## 5. Publish and install the app

On your dev machine (or clone + build in the container):

```bash
dotnet publish src/FinanceERP.Web -c Release -o publish
scp -r publish/* root@<container-ip>:/opt/finance-erp/
```

In the container:

```bash
useradd -r -s /usr/sbin/nologin finance-erp
mkdir -p /opt/finance-erp/logs
chown -R finance-erp:finance-erp /opt/finance-erp
```

Configure production settings **outside** the repo — create `/opt/finance-erp/appsettings.Production.json`:

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
  }
}
```

```bash
chmod 600 /opt/finance-erp/appsettings.Production.json
chown finance-erp: /opt/finance-erp/appsettings.Production.json
```

## 6. systemd service

```bash
cp deploy/finance-erp.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now finance-erp
journalctl -u finance-erp -f     # watch first boot: migrations + seeding run here
```

## 7. nginx reverse proxy

```bash
cp deploy/nginx-finance-erp.conf /etc/nginx/sites-available/finance-erp
ln -s /etc/nginx/sites-available/finance-erp /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx
```

Blazor Server uses WebSockets — the provided config includes the required `Upgrade`/`Connection` headers. Without them the UI will fall back or disconnect.

### HTTPS

If the container is reachable from the internet, use certbot (`apt install certbot python3-certbot-nginx && certbot --nginx -d erp.example.com`). On a LAN-only setup, terminate TLS on your existing reverse proxy (NPM/Traefik/Caddy on another LXC) and point it at this container's port 80 — remember to enable WebSocket support there too.

## 8. First login & hardening

1. Browse to the container IP → log in with the seeded admin → **change the password immediately** (top-right menu → My profile → Password).
2. Set the letterhead under **Administration → Company Profile** — name, logo,
   address and footer. Every printed document in all four apps uses it, and it
   starts blank.
3. Create real users under **Administration → Users**, assign roles and app access.
4. Review the permission matrix under **Administration → Roles & Permissions**.

## 9. Updates

Two scripted paths, both of which back up every database, snapshot the current
binaries, restart the service (migrations apply on boot), wait for a health
check, and roll the code back automatically if the app doesn't come back.

### A. Push from your dev machine — `deploy/update.sh`

Nothing extra is needed on the container: it publishes locally and rsyncs the
output. Requires SSH key auth (`ssh-copy-id root@<container-ip>`).

```bash
./deploy/update.sh <container-ip>
# or: FINANCE_ERP_HOST=<container-ip> ./deploy/update.sh
```

### B. Pull on the container — `deploy/self-update.sh`

The container builds from GitHub itself. This needs the .NET **SDK** and a
checkout, which section 3 doesn't install — one-time setup:

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
`main` to your live books without anyone watching, and while the script rolls
back a failed *start*, it cannot undo a migration that applied successfully but
was wrong. Tagged releases or a manual trigger are safer for financial data.

```bash
cp /opt/src/finance-erp/deploy/finance-erp-update.{service,timer} /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now finance-erp-update.timer
systemctl list-timers finance-erp-update    # confirm next run
journalctl -u finance-erp-update -f         # watch an update happen
```

Trigger one by hand with `systemctl start finance-erp-update`.

### Rollback

Both scripts print the exact commands afterwards. In short:

```bash
systemctl stop finance-erp
rm -rf /opt/finance-erp && mv /opt/finance-erp.prev /opt/finance-erp
systemctl start finance-erp
# and, only if a migration needs undoing:
zcat /var/backups/finance-erp/db-<stamp>.sql.gz | mysql finance_erp
```

Settings the scripts honour, if your install differs from this guide:
`FINANCE_ERP_APP_DIR`, `FINANCE_ERP_SRC`, `FINANCE_ERP_SERVICE`,
`FINANCE_ERP_DB`, `FINANCE_ERP_APP_USER`, `FINANCE_ERP_BRANCH`,
`FINANCE_ERP_HEALTH_URL`, `FINANCE_ERP_KEEP_BACKUPS`.

## 10. Backups

```bash
# /etc/cron.daily/finance-erp-backup
#!/bin/sh
mysqldump --single-transaction finance_erp | gzip > /var/backups/finance_erp_$(date +%F).sql.gz
find /var/backups -name 'finance_erp_*.sql.gz' -mtime +30 -delete
```

Plus Proxmox-level vzdump snapshots of the whole container (Datacenter → Backup).
