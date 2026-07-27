# Dev environment (WSL)

The development environment is **self-contained and persistent**. It survives WSL
restarts, so normally you never rebuild it — you just start it:

```bash
./dev.sh up      # database + app  → http://localhost:5080
./dev.sh down    # stop both
./dev.sh status  # what's running
./dev.sh logs    # tail the app log
./dev.sh db      # SQL shell on finance_erp
./dev.sh reset   # wipe the database and re-seed
```

Login: `admin@financeerp.local` / `ChangeMe!123`

## Where things live

| Component | Path | Notes |
|---|---|---|
| .NET 10 SDK | `~/.dotnet` | installed via the official install script |
| `dotnet-ef` tool | `~/.dotnet/tools` | needs `DOTNET_ROOT=$HOME/.dotnet` set |
| MariaDB + ICU binaries | `~/.local/finance-erp-dev/pkg` | unpacked `.deb`s, not apt-installed |
| Database files | `~/.local/finance-erp-dev/mysql/data` | **your data — persists** |
| Runtime logs | `~/.local/finance-erp-dev/logs` | `app.log`, `mariadb.log` |

Nothing is registered with apt/dpkg and nothing is written to `/usr`, `/etc`, or
`/var`. `dpkg -l | grep -E 'mariadb|libicu|dotnet'` returns nothing. Windows is
entirely unaffected — WSL keeps its filesystem inside its own virtual disk.

Connection string (dev): `Server=localhost;Port=3306;Database=finance_erp;User=finance;Password=DevPassword1!`

## The port is 5080, not 5188

`appsettings.Development.json` sets `"urls": "http://localhost:5080"`, which
overrides both `launchSettings.json` (which still says 5188) and the
`ASPNETCORE_URLS` environment variable.

## Known benign startup message

```
[ERR] The model for context 'AppDbContext' has pending changes.
```

EF Core logs this, but it does not throw — migrations apply, seeding completes,
and the app serves normally. Scaffolding a migration to "fix" it produces an
**empty** `Up()`/`Down()`, i.e. there is no schema difference; the mismatch is in
non-schema model metadata. Ignore it unless real schema changes stop applying.

## Rebuilding from scratch

Only needed on a fresh WSL distro or if `~/.local/finance-erp-dev` is deleted.
No root required at any point.

```bash
# 1. .NET SDK
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
echo 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"' >> ~/.bashrc
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc

# 2. Unpack MariaDB + ICU + deps without installing them
ENV_DIR="$HOME/.local/finance-erp-dev"; mkdir -p "$ENV_DIR/debs" "$ENV_DIR/pkg"
cd "$ENV_DIR/debs"
apt-get download libicu78 mariadb-server mariadb-server-core mariadb-common \
  mariadb-client mariadb-client-core libmariadb3 mysql-common libdbi-perl \
  liburing2 libaio1t64 libnuma1 libncurses6 libtinfo6
for d in *.deb; do dpkg-deb -x "$d" "$ENV_DIR/pkg"; done

# 3. Initialise the database
export LD_LIBRARY_PATH="$ENV_DIR/pkg/usr/lib/x86_64-linux-gnu"
"$ENV_DIR/pkg/usr/bin/mariadb-install-db" --basedir="$ENV_DIR/pkg/usr" \
  --datadir="$ENV_DIR/mysql/data" --auth-root-authentication-method=normal --skip-test-db

# 4. Start it and create the app database
cd /path/to/repo && ./dev.sh up   # (will fail on the DB grant the first time)
"$ENV_DIR/pkg/usr/bin/mariadb" --socket="$ENV_DIR/mysql/run/mysqld.sock" -u root <<'SQL'
CREATE DATABASE IF NOT EXISTS finance_erp CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'finance'@'localhost' IDENTIFIED BY 'DevPassword1!';
CREATE USER IF NOT EXISTS 'finance'@'127.0.0.1' IDENTIFIED BY 'DevPassword1!';
GRANT ALL PRIVILEGES ON finance_erp.* TO 'finance'@'localhost';
GRANT ALL PRIVILEGES ON finance_erp.* TO 'finance'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
./dev.sh up
```

Migrations and seeding run automatically on first start.

## Backing up your dev data

```bash
~/.local/finance-erp-dev/pkg/usr/bin/mariadb-dump \
  --socket=$HOME/.local/finance-erp-dev/mysql/run/mysqld.sock \
  -u root --single-transaction finance_erp | gzip > ~/finance_erp_backup.sql.gz
```

For production deployment (Proxmox LXC, systemd, nginx) see [DEPLOYMENT.md](DEPLOYMENT.md).
