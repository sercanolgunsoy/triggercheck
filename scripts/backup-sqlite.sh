#!/usr/bin/env bash
# Online backup of the TriggerCheck SQLite database (safe while the app is running in WAL mode).
#
# Uses `VACUUM INTO`, which reads a consistent snapshot inside one read transaction (committed WAL content
# included) and writes a compact, self-contained copy - unlike `cp`, which can tear a database whose WAL has not
# been checkpointed. The copy is integrity-checked before it is kept; only the newest KEEP backups are retained.
#
# Usage: scripts/backup-sqlite.sh [DB_PATH] [BACKUP_DIR] [KEEP]
#   DB_PATH     default $TRIGGERCHECK_DB or /data/triggercheck.db
#   BACKUP_DIR  default $BACKUP_DIR      or /data/backups
#   KEEP        default $BACKUP_KEEP     or 14
# Requires sqlite3 >= 3.27. Exit code != 0 on any failure (use it in cron / alerting).
set -euo pipefail

DB="${1:-${TRIGGERCHECK_DB:-/data/triggercheck.db}}"
DIR="${2:-${BACKUP_DIR:-/data/backups}}"
KEEP="${3:-${BACKUP_KEEP:-14}}"

fail() { echo "backup-sqlite: $*" >&2; exit 1; }

command -v sqlite3 >/dev/null 2>&1 || fail "sqlite3 not found (install the sqlite3 CLI, >= 3.27)"
[[ -f "$DB" ]] || fail "database not found: $DB"
[[ "$KEEP" =~ ^[1-9][0-9]*$ ]] || fail "KEEP must be a positive integer (got '$KEEP')"
mkdir -p "$DIR"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
out="$DIR/triggercheck-$stamp.db"
tmp="$out.partial"
rm -f "$tmp"
trap 'rm -f "$tmp"' EXIT

# Single quotes inside the path must be doubled for the SQL string literal.
sql_path="${tmp//\'/\'\'}"
# .timeout: wait (instead of failing) while the app holds a write lock.
sqlite3 -cmd ".timeout 30000" "$DB" "VACUUM INTO '$sql_path';"

check="$(sqlite3 "$tmp" "PRAGMA integrity_check;")"
[[ "$check" == "ok" ]] || fail "integrity check of the backup failed: $check"

mv "$tmp" "$out"
trap - EXIT
echo "backup-sqlite: wrote $out ($(wc -c <"$out" | tr -d ' ') bytes)"

# Retention: timestamped names sort chronologically; delete everything but the newest KEEP.
count=0
while IFS= read -r old; do
  count=$((count + 1))
  if (( count > KEEP )); then
    rm -f -- "$old"
    echo "backup-sqlite: removed old backup $old"
  fi
done < <(find "$DIR" -maxdepth 1 -type f -name 'triggercheck-*.db' | sort -r)
