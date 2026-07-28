#!/usr/bin/env bash
# Local dev environment for Finance ERP (WSL, no root required).
#
# Everything lives under ~/.local/finance-erp-dev — a self-contained MariaDB and
# the ICU libraries .NET needs, unpacked from .deb files rather than installed
# system-wide. Nothing is registered with apt/dpkg and nothing touches Windows.
#
#   ./dev.sh up      start database + app   (http://localhost:5080)
#   ./dev.sh down    stop both
#   ./dev.sh status   what is running
#   ./dev.sh logs    tail the app log
#   ./dev.sh db [name]  open a SQL shell (default: finance_erp)
#   ./dev.sh reset   drop every module database and re-seed from scratch
set -euo pipefail

ENV_DIR="$HOME/.local/finance-erp-dev"
PKG="$ENV_DIR/pkg"
DATA="$ENV_DIR/mysql/data"
RUN="$ENV_DIR/mysql/run"
SOCK="$RUN/mysqld.sock"
LOG_DIR="$ENV_DIR/logs"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

export PATH="$HOME/.dotnet:$PATH"
export LD_LIBRARY_PATH="$PKG/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"

mkdir -p "$LOG_DIR" "$RUN"

die() { echo "error: $*" >&2; exit 1; }

check_env() {
    [ -x "$PKG/usr/sbin/mariadbd" ] || die "environment missing at $ENV_DIR — see SETUP.md to rebuild it"
    command -v dotnet >/dev/null || die ".NET SDK not found at ~/.dotnet — see SETUP.md"
}

db_running() { [ -S "$SOCK" ] && "$PKG/usr/bin/mariadb-admin" --socket="$SOCK" -u root ping >/dev/null 2>&1; }

# One database per app, plus the shared identity database every app authenticates
# against. The accounts module keeps the original `finance_erp` name so existing
# installs upgrade in place rather than starting empty.
DATABASES=(erp_identity finance_erp erp_repair erp_gatepass erp_hr)

db_ensure() {
    local sql=""
    for d in "${DATABASES[@]}"; do
        sql+="CREATE DATABASE IF NOT EXISTS $d CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
        sql+="GRANT ALL PRIVILEGES ON $d.* TO 'finance'@'localhost';"
        sql+="GRANT ALL PRIVILEGES ON $d.* TO 'finance'@'127.0.0.1';"
    done
    # The HR integration tests spin up and drop their own throwaway databases.
    sql+="GRANT ALL PRIVILEGES ON \`erp\_hr\_test%\`.* TO 'finance'@'localhost';"
    sql+="GRANT ALL PRIVILEGES ON \`erp\_hr\_test%\`.* TO 'finance'@'127.0.0.1';"
    sql+="FLUSH PRIVILEGES;"
    "$PKG/usr/bin/mariadb" --socket="$SOCK" -u root -e "$sql"
}
app_running() { pgrep -x FinanceERP.Web >/dev/null 2>&1; }

db_up() {
    if db_running; then echo "database already running"; return; fi
    echo "starting database..."
    nohup "$PKG/usr/sbin/mariadbd" \
        --basedir="$PKG/usr" --datadir="$DATA" \
        --socket="$SOCK" --pid-file="$RUN/mysqld.pid" \
        --port=3306 --bind-address=127.0.0.1 \
        > "$LOG_DIR/mariadb.log" 2>&1 &
    for _ in $(seq 1 30); do db_running && { echo "database ready"; return; }; sleep 1; done
    die "database failed to start — see $LOG_DIR/mariadb.log"
}

app_up() {
    if app_running; then echo "app already running at http://localhost:5080"; return; fi
    echo "starting app (building first run, ~1 min)..."
    # Port comes from appsettings.Development.json ("urls": http://localhost:5080),
    # which overrides both launchSettings.json and ASPNETCORE_URLS.
    ASPNETCORE_ENVIRONMENT=Development nohup dotnet run \
        --project "$REPO/src/FinanceERP.Web" --no-launch-profile \
        > "$LOG_DIR/app.log" 2>&1 &
    for _ in $(seq 1 90); do
        curl -fsS -o /dev/null "http://localhost:5080/Account/Login" 2>/dev/null && {
            echo "app ready → http://localhost:5080"
            echo "login: admin@financeerp.local / ChangeMe!123"
            return
        }
        sleep 2
    done
    die "app failed to start — run './dev.sh logs'"
}

case "${1:-up}" in
    up)
        check_env; db_up; db_ensure; app_up ;;
    down)
        pkill -x FinanceERP.Web 2>/dev/null && echo "app stopped" || echo "app not running"
        if db_running; then
            "$PKG/usr/bin/mariadb-admin" --socket="$SOCK" -u root shutdown && echo "database stopped"
        else
            echo "database not running"
        fi ;;
    status)
        db_running && echo "database: running" || echo "database: stopped"
        app_running && echo "app:      running → http://localhost:5080" || echo "app:      stopped" ;;
    logs)
        tail -f "$LOG_DIR/app.log" ;;
    db)
        check_env; db_up
        "$PKG/usr/bin/mariadb" --socket="$SOCK" -u root "${2:-finance_erp}" ;;
    reset)
        check_env; db_up
        read -rp "This destroys all data in ${DATABASES[*]}. Type 'yes' to continue: " confirm
        [ "$confirm" = "yes" ] || die "aborted"
        pkill -x FinanceERP.Web 2>/dev/null || true
        for d in "${DATABASES[@]}"; do
            "$PKG/usr/bin/mariadb" --socket="$SOCK" -u root -e "DROP DATABASE IF EXISTS $d;"
        done
        db_ensure
        echo "databases reset — migrations and seeding run on next start"
        app_up ;;
    *)
        sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
esac
