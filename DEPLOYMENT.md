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

```bash
mysql_secure_installation   # set root password, remove test db
mysql -u root -p <<'SQL'
CREATE DATABASE finance_erp CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'finance'@'localhost' IDENTIFIED BY 'STRONG_PASSWORD_HERE';
GRANT ALL PRIVILEGES ON finance_erp.* TO 'finance'@'localhost';
FLUSH PRIVILEGES;
SQL
```

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
    "DefaultConnection": "Server=localhost;Port=3306;Database=finance_erp;User=finance;Password=STRONG_PASSWORD_HERE;"
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
2. Create real users under **Administration → Users**, assign roles.
3. Review the permission matrix under **Administration → Roles & Permissions**.

## 9. Updates

```bash
systemctl stop finance-erp
# copy new publish output over /opt/finance-erp (keep appsettings.Production.json)
systemctl start finance-erp    # migrations apply automatically
```

## 10. Backups

```bash
# /etc/cron.daily/finance-erp-backup
#!/bin/sh
mysqldump --single-transaction finance_erp | gzip > /var/backups/finance_erp_$(date +%F).sql.gz
find /var/backups -name 'finance_erp_*.sql.gz' -mtime +30 -delete
```

Plus Proxmox-level vzdump snapshots of the whole container (Datacenter → Backup).
