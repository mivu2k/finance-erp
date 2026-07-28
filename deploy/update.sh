#!/usr/bin/env bash
# One-command update of a running Finance ERP deployment.
#
#   ./deploy/update.sh 192.168.1.50
#   FINANCE_ERP_HOST=192.168.1.50 ./deploy/update.sh
#
# Publishes the current working tree, backs up the database, ships the build to
# /opt/finance-erp and restarts the service. If the app doesn't come back
# healthy the previous build is restored automatically — the database is not
# rolled back, so keep the dump this prints if you need to go further back.
#
# Requires: SSH key auth to root@<host>, and rsync on both ends.
set -euo pipefail

HOST="${1:-${FINANCE_ERP_HOST:-}}"
SSH_USER="${FINANCE_ERP_SSH_USER:-root}"
APP_DIR="${FINANCE_ERP_APP_DIR:-/opt/finance-erp}"
SERVICE="${FINANCE_ERP_SERVICE:-finance-erp}"
DB_NAME="${FINANCE_ERP_DB:-finance_erp}"
APP_USER="${FINANCE_ERP_APP_USER:-finance-erp}"
HEALTH_URL="${FINANCE_ERP_HEALTH_URL:-http://localhost:5000/}"
KEEP_BACKUPS="${FINANCE_ERP_KEEP_BACKUPS:-10}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$REPO_ROOT/publish"
STAMP="$(date +%Y%m%d-%H%M%S)"

die() { echo "error: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

[ -n "$HOST" ] || die "no host given — pass it as an argument or set FINANCE_ERP_HOST"

SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$SSH_USER@$HOST")

step "Checking connection to $SSH_USER@$HOST"
"${SSH[@]}" true 2>/dev/null \
  || die "cannot ssh to $SSH_USER@$HOST without a password — set up key auth (ssh-copy-id $SSH_USER@$HOST)"
"${SSH[@]}" "systemctl cat $SERVICE >/dev/null 2>&1" \
  || die "service '$SERVICE' not found on $HOST — is this the right box?"

# Local build first: no point touching the server if this fails.
step "Publishing (Release)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
rm -rf "$PUBLISH_DIR"
dotnet publish "$REPO_ROOT/src/FinanceERP.Web" -c Release -o "$PUBLISH_DIR" --nologo -v quiet
[ -f "$PUBLISH_DIR/FinanceERP.Web.dll" ] || die "publish produced no FinanceERP.Web.dll"
echo "built $(find "$PUBLISH_DIR" -type f | wc -l) files ($(du -sh "$PUBLISH_DIR" | cut -f1))"

step "Backing up database and current build"
"${SSH[@]}" bash -euo pipefail -s -- "$DB_NAME" "$APP_DIR" "$STAMP" "$KEEP_BACKUPS" <<'REMOTE'
DB_NAME="$1"; APP_DIR="$2"; STAMP="$3"; KEEP="$4"
mkdir -p /var/backups/finance-erp
mysqldump --single-transaction --routines "$DB_NAME" | gzip > "/var/backups/finance-erp/db-$STAMP.sql.gz"
echo "database  -> /var/backups/finance-erp/db-$STAMP.sql.gz"
# Snapshot the binaries so a bad release can be put back without a rebuild.
rm -rf "$APP_DIR.prev"
cp -a "$APP_DIR" "$APP_DIR.prev"
echo "binaries  -> $APP_DIR.prev"
ls -1t /var/backups/finance-erp/db-*.sql.gz 2>/dev/null | tail -n +$((KEEP + 1)) | xargs -r rm --
REMOTE

step "Stopping $SERVICE"
"${SSH[@]}" "systemctl stop $SERVICE"

step "Uploading build to $APP_DIR"
# Runtime state lives inside APP_DIR alongside the binaries and is NOT in the
# publish output, so --delete would destroy it. uploads/ is receipt attachments
# (unrecoverable), keys/ is DataProtection (losing it logs everyone out and
# breaks existing auth cookies), appsettings.Production.json holds the DB
# password. Never drop these excludes.
rsync -a --delete \
  --exclude 'appsettings.Production.json' \
  --exclude 'uploads/' \
  --exclude 'keys/' \
  --exclude 'logs/' \
  -e "ssh -o BatchMode=yes" \
  "$PUBLISH_DIR/" "$SSH_USER@$HOST:$APP_DIR/"
# This ships the local working tree, which may not match any commit, so drop
# the stamp self-update.sh keys off — it must redeploy rather than skip.
"${SSH[@]}" "rm -f $APP_DIR/.deployed-revision; chown -R $APP_USER:$APP_USER $APP_DIR"

step "Starting $SERVICE (migrations apply on boot)"
"${SSH[@]}" "systemctl start $SERVICE"

step "Waiting for the app to come back"
healthy=0
for _ in $(seq 1 30); do
  sleep 2
  code="$("${SSH[@]}" "curl -s -o /dev/null -w '%{http_code}' --max-time 5 '$HEALTH_URL' || true")"
  # A redirect to the login page is a healthy, unauthenticated response.
  case "$code" in 200|302) healthy=1; break ;; esac
done

if [ "$healthy" -ne 1 ]; then
  echo
  echo "!! app did not become healthy — rolling the binaries back" >&2
  "${SSH[@]}" bash -euo pipefail -s -- "$SERVICE" "$APP_DIR" "$APP_USER" <<'REMOTE'
SERVICE="$1"; APP_DIR="$2"; APP_USER="$3"
systemctl stop "$SERVICE" || true
# Roll back only against a snapshot that actually holds a build. Never delete
# APP_DIR outright: uploads/ and keys/ live there and have no other copy.
if [ -f "$APP_DIR.prev/FinanceERP.Web.dll" ]; then
  rsync -a --delete \
    --exclude 'appsettings.Production.json' \
    --exclude 'uploads/' \
    --exclude 'keys/' \
    --exclude 'logs/' \
    --exclude '.deployed-revision' \
    "$APP_DIR.prev"/ "$APP_DIR"/
  chown -R "$APP_USER:$APP_USER" "$APP_DIR"
  echo "previous build restored"
else
  echo "no usable snapshot at $APP_DIR.prev — leaving the new build in place; nothing deleted" >&2
fi
systemctl start "$SERVICE" || true
REMOTE
  echo "previous build restored. Recent log:" >&2
  "${SSH[@]}" "journalctl -u $SERVICE -n 40 --no-pager" >&2
  echo >&2
  echo "the database was NOT rolled back — if the new migrations broke it, restore with:" >&2
  echo "  ssh $SSH_USER@$HOST 'zcat /var/backups/finance-erp/db-$STAMP.sql.gz | mysql $DB_NAME'" >&2
  exit 1
fi

step "Deployed"
"${SSH[@]}" "systemctl is-active $SERVICE >/dev/null && echo 'service: active'"
"${SSH[@]}" "journalctl -u $SERVICE -n 15 --no-pager | sed 's/^/  /'"
echo
echo "rollback if needed:"
echo "  ssh $SSH_USER@$HOST 'systemctl stop $SERVICE && rm -rf $APP_DIR && mv $APP_DIR.prev $APP_DIR && systemctl start $SERVICE'"
echo "  ssh $SSH_USER@$HOST 'zcat /var/backups/finance-erp/db-$STAMP.sql.gz | mysql $DB_NAME'"
