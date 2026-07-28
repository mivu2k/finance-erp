#!/usr/bin/env bash
# Runs ON the Proxmox container. Pulls the latest main from GitHub, rebuilds,
# and restarts the service — with a database backup and automatic rollback.
#
#   /opt/src/finance-erp/deploy/self-update.sh
#
# One-time setup (as root, see the header of DEPLOYMENT.md section 9):
#   apt install -y git dotnet-sdk-10.0
#   mkdir -p /opt/src && cd /opt/src
#   git clone https://github.com/mivu2k/finance-erp.git
#   chmod +x /opt/src/finance-erp/deploy/self-update.sh
set -euo pipefail

SRC_DIR="${FINANCE_ERP_SRC:-/opt/src/finance-erp}"
APP_DIR="${FINANCE_ERP_APP_DIR:-/opt/finance-erp}"
SERVICE="${FINANCE_ERP_SERVICE:-finance-erp}"
DB_NAME="${FINANCE_ERP_DB:-finance_erp}"
APP_USER="${FINANCE_ERP_APP_USER:-finance-erp}"
BRANCH="${FINANCE_ERP_BRANCH:-main}"
HEALTH_URL="${FINANCE_ERP_HEALTH_URL:-http://localhost:5000/}"
KEEP_BACKUPS="${FINANCE_ERP_KEEP_BACKUPS:-10}"

STAMP="$(date +%Y%m%d-%H%M%S)"
die() { echo "error: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

[ "$(id -u)" -eq 0 ] || die "run as root"
[ -d "$SRC_DIR/.git" ] || die "no git checkout at $SRC_DIR — see the setup notes at the top of this script"
command -v dotnet >/dev/null || die "dotnet not installed"
dotnet --list-sdks | grep -q . || die "only the .NET runtime is installed; building here needs the SDK (apt install -y dotnet-sdk-10.0)"

step "Fetching $BRANCH"
cd "$SRC_DIR"
git fetch --quiet origin "$BRANCH"
local_rev="$(git rev-parse HEAD)"
remote_rev="$(git rev-parse "origin/$BRANCH")"

if [ "$local_rev" = "$remote_rev" ] && [ "${FINANCE_ERP_FORCE:-0}" != "1" ]; then
    echo "already up to date at ${local_rev:0:8} — nothing to do"
    echo "(set FINANCE_ERP_FORCE=1 to rebuild anyway)"
    exit 0
fi

git -c advice.detachedHead=false checkout --quiet "$BRANCH"
git reset --hard --quiet "origin/$BRANCH"
echo "${local_rev:0:8} -> ${remote_rev:0:8}"
git log --oneline "${local_rev}..${remote_rev}" 2>/dev/null | sed 's/^/  /' || true

# Build before touching the running app, so a compile error costs no downtime.
step "Building"
BUILD_DIR="$(mktemp -d /tmp/finance-erp-build.XXXXXX)"
trap 'rm -rf "$BUILD_DIR"' EXIT
dotnet publish "$SRC_DIR/src/FinanceERP.Web" -c Release -o "$BUILD_DIR" --nologo -v quiet
[ -f "$BUILD_DIR/FinanceERP.Web.dll" ] || die "build produced no FinanceERP.Web.dll"

step "Backing up"
mkdir -p /var/backups/finance-erp
mysqldump --single-transaction --routines "$DB_NAME" | gzip > "/var/backups/finance-erp/db-$STAMP.sql.gz"
echo "database -> /var/backups/finance-erp/db-$STAMP.sql.gz"
rm -rf "$APP_DIR.prev"
cp -a "$APP_DIR" "$APP_DIR.prev"
echo "binaries -> $APP_DIR.prev"
ls -1t /var/backups/finance-erp/db-*.sql.gz 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm --

step "Installing"
systemctl stop "$SERVICE"
# appsettings.Production.json holds the DB password and is not in the repo,
# so preserve whatever is live rather than overwriting it.
cp -a "$APP_DIR/appsettings.Production.json" "$BUILD_DIR/" 2>/dev/null || true
rm -rf "${APP_DIR:?}"/*
cp -a "$BUILD_DIR"/. "$APP_DIR/"
chown -R "$APP_USER:$APP_USER" "$APP_DIR"

step "Starting $SERVICE (migrations apply on boot)"
systemctl start "$SERVICE"

step "Waiting for the app"
healthy=0
for _ in $(seq 1 30); do
    sleep 2
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$HEALTH_URL" || true)"
    # A redirect to the login page is a healthy unauthenticated response.
    case "$code" in 200|302) healthy=1; break ;; esac
done

if [ "$healthy" -ne 1 ]; then
    echo
    echo "!! app did not come back — rolling the binaries back" >&2
    systemctl stop "$SERVICE" || true
    cp -a "$APP_DIR/appsettings.Production.json" /tmp/ 2>/dev/null || true
    rm -rf "$APP_DIR"
    mv "$APP_DIR.prev" "$APP_DIR"
    cp -a /tmp/appsettings.Production.json "$APP_DIR/" 2>/dev/null || true
    chown -R "$APP_USER:$APP_USER" "$APP_DIR"
    systemctl start "$SERVICE" || true
    git reset --hard --quiet "$local_rev"
    journalctl -u "$SERVICE" -n 40 --no-pager >&2
    echo >&2
    echo "code rolled back. The database was NOT — if a migration broke it:" >&2
    echo "  zcat /var/backups/finance-erp/db-$STAMP.sql.gz | mysql $DB_NAME" >&2
    exit 1
fi

step "Updated to ${remote_rev:0:8}"
journalctl -u "$SERVICE" -n 15 --no-pager | sed 's/^/  /'
echo
echo "rollback:  systemctl stop $SERVICE && rm -rf $APP_DIR && mv $APP_DIR.prev $APP_DIR && systemctl start $SERVICE"
echo "db restore: zcat /var/backups/finance-erp/db-$STAMP.sql.gz | mysql $DB_NAME"
