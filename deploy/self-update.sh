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
# Every database the platform owns. erp_identity holds the users, roles and the
# company profile and has no other copy, so backing up finance_erp alone would
# leave a failed migration unrecoverable. Override with a space-separated list.
DATABASES="${FINANCE_ERP_DBS:-erp_identity finance_erp erp_repair erp_gatepass erp_hr erp_inventory erp_auto erp_ledger erp_tender}"
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

# Compare against what is actually DEPLOYED, not against the checkout. A fresh
# clone already sits at origin/main while the running binaries are still old,
# so comparing the checkout would skip the very first deploy and report success.
STAMP_FILE="$APP_DIR/.deployed-revision"
deployed_rev="$(cat "$STAMP_FILE" 2>/dev/null || echo none)"

if [ "$deployed_rev" = "$remote_rev" ] && [ "${FINANCE_ERP_FORCE:-0}" != "1" ]; then
    echo "already deployed at ${deployed_rev:0:8} — nothing to do"
    echo "(set FINANCE_ERP_FORCE=1 to rebuild anyway)"
    exit 0
fi

if [ "$deployed_rev" = none ]; then
    echo "no deployment stamp found — treating this as a first deploy"
else
    echo "deployed ${deployed_rev:0:8} -> building ${remote_rev:0:8}"
fi

git -c advice.detachedHead=false checkout --quiet "$BRANCH"
git reset --hard --quiet "origin/$BRANCH"
if [ "$deployed_rev" != none ]; then
    git log --oneline "${deployed_rev}..${remote_rev}" 2>/dev/null | sed 's/^/  /' || true
fi

# Build before touching the running app, so a compile error costs no downtime.
step "Building"
BUILD_DIR="$(mktemp -d /tmp/finance-erp-build.XXXXXX)"
trap 'rm -rf "$BUILD_DIR"' EXIT
dotnet publish "$SRC_DIR/src/FinanceERP.Web" -c Release -o "$BUILD_DIR" --nologo -v quiet
[ -f "$BUILD_DIR/FinanceERP.Web.dll" ] || die "build produced no FinanceERP.Web.dll"

step "Backing up"
mkdir -p /var/backups/finance-erp
for db in $DATABASES; do
    # A database the deploy hasn't created yet is not an error on a first run.
    if ! mysql -N -e "SHOW DATABASES LIKE '$db'" | grep -q .; then
        echo "database $db does not exist yet — skipped"
        continue
    fi
    mysqldump --single-transaction --routines "$db" \
        | gzip > "/var/backups/finance-erp/$db-$STAMP.sql.gz"
    echo "database -> /var/backups/finance-erp/$db-$STAMP.sql.gz"
done

have_snapshot=0
if [ -d "$APP_DIR" ]; then
    rm -rf "$APP_DIR.prev"
    cp -a "$APP_DIR" "$APP_DIR.prev"
    have_snapshot=1
    echo "binaries -> $APP_DIR.prev"
else
    # First deploy onto an empty box: there is nothing to roll back to, so the
    # rollback path below must not try (and must never delete anything).
    mkdir -p "$APP_DIR"
    echo "no existing install at $APP_DIR — first deploy, no rollback snapshot"
fi
# Prune per database, so keeping 10 means 10 rounds rather than 2.
for db in $DATABASES; do
    ls -1t "/var/backups/finance-erp/$db-"*.sql.gz 2>/dev/null \
        | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm --
done

step "Installing"
systemctl stop "$SERVICE"
# Runtime state lives inside APP_DIR alongside the binaries and is NOT produced
# by the build, so it must survive the swap: uploads/ is receipt attachments
# (unrecoverable if deleted), keys/ is DataProtection (losing it invalidates
# every auth cookie), appsettings.Production.json holds the DB password.
# rsync's excludes replace old binaries while leaving that state alone.
command -v rsync >/dev/null || die "rsync not installed (apt install -y rsync)"
rsync -a --delete \
    --exclude 'appsettings.Production.json' \
    --exclude 'uploads/' \
    --exclude 'keys/' \
    --exclude 'logs/' \
    --exclude '.deployed-revision' \
    "$BUILD_DIR"/ "$APP_DIR"/
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
    echo "!! app did not come back" >&2
    systemctl stop "$SERVICE" || true

    # Roll back only when a verified snapshot exists. Deleting APP_DIR without
    # one destroys uploads/ and keys/ outright — a failed start must never cost
    # data, so in that case the new build is simply left in place to debug.
    if [ "$have_snapshot" -eq 1 ] && [ -f "$APP_DIR.prev/FinanceERP.Web.dll" ]; then
        echo "restoring the previous build from $APP_DIR.prev" >&2
        # Roll back binaries while keeping live runtime state and settings.
        rsync -a --delete \
            --exclude 'appsettings.Production.json' \
            --exclude 'uploads/' \
            --exclude 'keys/' \
            --exclude 'logs/' \
            --exclude '.deployed-revision' \
            "$APP_DIR.prev"/ "$APP_DIR"/
        chown -R "$APP_USER:$APP_USER" "$APP_DIR"
        git reset --hard --quiet "$local_rev"
        systemctl start "$SERVICE" || true
        echo "previous build restored." >&2
    else
        echo "no usable snapshot at $APP_DIR.prev — leaving the new build in place." >&2
        echo "Nothing was deleted; fix forward from the log below." >&2
    fi

    journalctl -u "$SERVICE" -n 40 --no-pager >&2
    echo >&2
    echo "the databases were NOT rolled back — if a migration broke one:" >&2
    for db in $DATABASES; do
        echo "  zcat /var/backups/finance-erp/$db-$STAMP.sql.gz | mysql $db" >&2
    done
    exit 1
fi

# Written only after the health check passes, so a rolled-back deploy is
# correctly retried next run rather than being recorded as live.
printf '%s\n' "$remote_rev" > "$STAMP_FILE"
chown "$APP_USER:$APP_USER" "$STAMP_FILE"

step "Updated to ${remote_rev:0:8}"
journalctl -u "$SERVICE" -n 15 --no-pager | sed 's/^/  /'
echo
echo "rollback:  systemctl stop $SERVICE && rm -rf $APP_DIR && mv $APP_DIR.prev $APP_DIR && systemctl start $SERVICE"
echo "db restore: for db in $DATABASES; do zcat /var/backups/finance-erp/\$db-$STAMP.sql.gz | mysql \$db; done"
