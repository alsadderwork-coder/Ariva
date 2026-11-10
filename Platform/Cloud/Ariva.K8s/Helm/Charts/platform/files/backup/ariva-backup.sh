#!/usr/bin/env bash
# Ariva scheduled database backup (ARV-182; wiki 04 section 10.3a). The chart's CronJob ariva-db-backup runs this in a pod of
# the TimescaleDB image (bash, psql, pg_dump of the server's version, Python 3 with pyca/cryptography, jq, coreutils), as the
# image's postgres user, with a read-only root filesystem. It takes one backup set into the backup claim and keeps the newest
# ARIVA_BACKUP_KEEP sets it took:
#
#   <dir>/ariva-scheduled-<namespace>-<UTC time>.dump.age       the database (scripts/ops/ariva-db.sh backup, as an operator's
#                                                               backup), encrypted while it streams to the custodian's
#                                                               recipient only
#   <dir>/ariva-scheduled-<namespace>-<UTC time>.manifest.json  the set's manifest (ariva-backup-set/1, as ariva-ops.mjs writes
#                                                               it, with takenBy cronjob/ariva-db-backup): the database's
#                                                               versions and scripts (ariva-db.sh manifest) and the file's
#                                                               SHA-256, unsigned
#
# Nothing here can sign or decrypt: the cluster holds the custodian's public recipient only, and the manifest is signed outside
# the cluster by an operator (ssh-keygen -Y sign -n ariva-backup), as ARV-126 requires before a restore trusts it.
#
# Inputs, all from the CronJob's environment and each checked against a strict pattern first (CWE-78):
#   ARIVA_BACKUP_RECIPIENT     the custodian's age recipient (age1...); an identity is refused without being repeated
#   ARIVA_BACKUP_KEEP          the sets kept, 1 to 90
#   ARIVA_BACKUP_NAMESPACE     the release's namespace (the downward API), which names the sets
#   ARIVA_BACKUP_DIRECTORY     the backup claim's mount (/backups)
#   ARIVA_BACKUP_SCRIPTS       where ariva-db.sh and ariva-age.py are mounted (/opt/ariva-backup)
#   ARIVA_DATABASE_HOST, ARIVA_DATABASE_PORT   the database Service (timescaledb, 5432)
#   POSTGRES_DB, ARIVA_MIGRATION_LOGIN, ARIVA_MIGRATION_PASSWORD   read by ariva-db.sh, which checks them; the password reaches
#                              ariva-db.sh alone, in its environment, never a command line or another program here (CWE-532)
#   PGSSLMODE, PGSSLROOTCERT, PGREQUIREAUTH, PGCHANNELBINDING   libpq's, for ariva-db.sh's connections (scram-sha-256 only;
#                              verify-full against the database CA with channel binding in k8s-prd)
#
# What a set's hash proves (CWE-501, the ARV-182 review). The claim is shared storage that others may write. Each file is written
# under .partial, flushed and renamed, and its SHA-256 and size are taken from what this run wrote (the encryptor's own count
# of its output, and the manifest from a private copy in the pod's /tmp), never read back from the claim; after the rename the
# published file must have that hash and size, or the run fails and removes it. So a file put in place of a .partial, or of a
# published file before the check, fails the run, and the hashes logged and written into the manifest are those of the bytes
# this run produced. A file changed on the claim after the run is caught by the operator's check before signing (the log line).
#
# A set is complete or absent: a run that fails removes the names it created (its .partial files, and a name once its rename
# happened), whatever is at them by then, a file another writer put in place of one included; it never removes a name it did not
# create (a file already at a set's name stops the run untouched), and keeps every older set (sets are pruned only after the new
# one is complete). Pruning touches this CronJob's sets alone: a regular file named
# ariva-scheduled-<namespace>-<UTC time>.dump.age whose manifest beside it says takenBy
# cronjob/ariva-db-backup for this namespace and this file; an operator's set (ariva-<namespace>-<time>, ariva-ops.mjs) or
# anything else is left alone, and a removal that fails is reported. One run at a time (a lock on the claim, besides the
# CronJob's concurrencyPolicy Forbid). The last line on standard output is the outcome, which the pod's log keeps:
# "ariva-backup: outcome=succeeded set=<name> bytes=<n> sha256=<hex> manifest_sha256=<hex> kept=<n>" or "ariva-backup:
# outcome=failed step=<step>"; names, sizes and hashes only. Exit codes: 0 done, 1 failed, 2 refused input.
set -euo pipefail
set -o noclobber
umask 077
export LC_ALL=C

readonly TAKEN_BY="cronjob/ariva-db-backup"

say() { printf 'ariva-backup: %s\n' "$*" >&2; }
refuse() { say "refused: $*"; printf 'ariva-backup: outcome=failed step=input\n'; exit 2; }

valid_name() { [[ "$1" =~ ^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$ ]]; }
valid_path() { [[ "$1" =~ ^/[A-Za-z0-9._/-]{1,200}$ && "$1" != *..* ]]; }

RECIPIENT="${ARIVA_BACKUP_RECIPIENT:-}"
KEEP="${ARIVA_BACKUP_KEEP:-}"
NAMESPACE="${ARIVA_BACKUP_NAMESPACE:-}"
DIRECTORY="${ARIVA_BACKUP_DIRECTORY:-/backups}"
SCRIPTS="${ARIVA_BACKUP_SCRIPTS:-/opt/ariva-backup}"
HOST="${ARIVA_DATABASE_HOST:-}"
PORT="${ARIVA_DATABASE_PORT:-5432}"
# The password stays in this shell and reaches ariva-db.sh alone (its environment, below); no other child inherits it.
PASSWORD="${ARIVA_MIGRATION_PASSWORD:-}"
export -n ARIVA_MIGRATION_PASSWORD 2>/dev/null || true
unset ARIVA_MIGRATION_PASSWORD PGPASSWORD POSTGRES_PASSWORD

shopt -s nocasematch
[[ "$RECIPIENT" != *AGE-SECRET-KEY-* ]] || refuse "ARIVA_BACKUP_RECIPIENT holds an age identity (a private key): give the custodian's public recipient (age1...) only; the identity never enters the cluster"
shopt -u nocasematch
[[ "$RECIPIENT" =~ ^age1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$ ]] || refuse "ARIVA_BACKUP_RECIPIENT is not an age X25519 recipient (age1 and 58 lower-case Bech32 characters)"
[[ "$KEEP" =~ ^[1-9][0-9]?$ ]] && ((KEEP <= 90)) || refuse "ARIVA_BACKUP_KEEP must be a whole number from 1 to 90"
valid_name "$NAMESPACE" || refuse "ARIVA_BACKUP_NAMESPACE must be a Kubernetes namespace name"
valid_path "$DIRECTORY" || refuse "ARIVA_BACKUP_DIRECTORY must be a plain absolute path"
valid_path "$SCRIPTS" || refuse "ARIVA_BACKUP_SCRIPTS must be a plain absolute path"
[[ "$HOST" =~ ^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?$ ]] || refuse "ARIVA_DATABASE_HOST must be a lower-case host name or IPv4 address"
[[ "$PORT" =~ ^[1-9][0-9]{0,4}$ ]] && ((PORT <= 65535)) || refuse "ARIVA_DATABASE_PORT must be a port number"
[[ -n "$PASSWORD" ]] || refuse "ARIVA_MIGRATION_PASSWORD is not set (the CronJob takes it from the database credentials secret)"
[[ -d "$DIRECTORY" && -w "$DIRECTORY" ]] || refuse "the backup directory $DIRECTORY is not a writable directory (the backup claim's mount)"
for file in ariva-db.sh ariva-age.py; do
  [[ -f "$SCRIPTS/$file" ]] || refuse "$SCRIPTS/$file is missing (the ConfigMap ariva-db-backup-scripts)"
done
# The recipient's checksum and key, before anything connects (the encryptor names what is wrong, never the value).
python3 -I "$SCRIPTS/ariva-age.py" --recipient "$RECIPIENT" --check || refuse "ARIVA_BACKUP_RECIPIENT is not a usable age recipient"

STEP=start
NAME=""
WORK=""
# The names this run created on the claim: a .partial until it is renamed, then the published name. A name another writer
# created is never listed, so a failure never removes it; a file another writer puts at a listed name goes with that name.
WRITTEN=()

on_exit() {
  local status=$?
  local file
  if ((status != 0)); then
    # Whatever this run wrote goes; older sets stay as they were.
    for file in "${WRITTEN[@]}"; do rm -f -- "$file"; done
    printf 'ariva-backup: outcome=failed step=%s\n' "$STEP"
  fi
  if [[ -n "$WORK" && -d "$WORK" ]]; then
    rm -f -- "$WORK/dump.digest" "$WORK/manifest.json"
    rmdir -- "$WORK" 2>/dev/null || true
  fi
}
trap on_exit EXIT

# ariva-db.sh's command with this run's database address (ariva-db.sh takes the command first); the migration password in its
# environment only.
db() {
  local command="$1"
  shift
  ARIVA_MIGRATION_PASSWORD="$PASSWORD" bash "$SCRIPTS/ariva-db.sh" "$command" --host "$HOST" --port "$PORT" "$@"
}

# Takes a name out of WRITTEN (a .partial once renamed, which is gone).
unlist() {
  local kept=() file
  for file in "${WRITTEN[@]}"; do [[ "$file" == "$1" ]] || kept+=("$file"); done
  WRITTEN=("${kept[@]}")
}

# Flushes a written file to the disk, then gives it its final name (no existing file is replaced); the final name is this run's
# only once the rename happened. The directory's own flush is best effort: some network file systems refuse it for a directory.
publish() {
  sync -- "$1"
  # mv -n never replaces a file: coreutils 9.2 to 9.4 fail when they skip one, older and newer ones succeed and leave the source.
  if ! mv -n -T -- "$1" "$2" || [[ -e "$1" ]]; then
    say "error: ${2##*/} already exists (another writer of the claim) or the claim refused the rename; this run's file stays out of it"
    return 1
  fi
  unlist "$1"
  WRITTEN+=("$2")
  sync -- "$DIRECTORY" 2>/dev/null || true
}

# The published file is the bytes this run wrote: their SHA-256 and size, taken from the stream, never read from the claim.
same_as_written() {
  local file="$1" sha256="$2" bytes="$3" found size
  [[ -f "$file" && ! -L "$file" ]] || { say "error: ${file##*/} on the claim is not a regular file"; return 1; }
  found="$(sha256sum -- "$file")"
  found="${found%% *}"
  size="$(stat -c %s -- "$file")"
  [[ "$found" == "$sha256" && "$size" == "$bytes" ]] && return 0
  say "error: ${file##*/} on the claim is not what this run wrote (written: sha256 $sha256, $bytes bytes; found: sha256 $found, $size bytes): another writer replaced it; this run fails and keeps no set"
  return 1
}

# A set this CronJob took of this namespace: its manifest beside it says so, and names this dump as its only file.
ours() {
  local manifest="$1.manifest.json"
  [[ -f "$manifest" && ! -L "$manifest" ]] || return 1
  jq -e --arg taken "$TAKEN_BY" --arg ns "$NAMESPACE" --arg file "${1##*/}.dump.age" \
    '.format == "ariva-backup-set/1" and .takenBy == $taken and .namespace == $ns and (.files | length) == 1 and .files[0].file == $file' \
    "$manifest" >/dev/null 2>&1
}

# Keeps the newest KEEP sets this CronJob took of this namespace, by name (the UTC time sorts as text), and removes the rest:
# each one's dump, manifest and an operator's signature beside it. Anything else on the claim is left alone; a removal that fails
# is reported and its set counts as kept. Prints the number of this CronJob's sets left.
prune() {
  local file name sets=() failed=0
  shopt -s nullglob
  for file in "$DIRECTORY"/ariva-scheduled-"$NAMESPACE"-*.dump.age; do
    name="${file##*/}"
    [[ "$name" =~ ^ariva-scheduled-${NAMESPACE}-[0-9]{8}T[0-9]{6}Z\.dump\.age$ && -f "$file" && ! -L "$file" ]] || continue
    if ours "${file%.dump.age}"; then
      sets+=("${file%.dump.age}")
    else
      say "left alone: ${name%.dump.age} (no manifest of this CronJob beside it)"
    fi
  done
  # A .partial file is a run of this CronJob that was killed before its rename (a deadline, a node lost): it never became a set.
  for file in "$DIRECTORY"/ariva-scheduled-"$NAMESPACE"-*.partial; do
    name="${file##*/}"
    [[ "$name" =~ ^ariva-scheduled-${NAMESPACE}-[0-9]{8}T[0-9]{6}Z\.(dump\.age|manifest\.json)\.partial$ && -f "$file" && ! -L "$file" ]] || continue
    rm -f -- "$file" || say "error: the leftover $name could not be removed"
  done
  shopt -u nullglob
  local sorted=()
  if ((${#sets[@]} > 0)); then
    mapfile -t sorted < <(printf '%s\n' "${sets[@]}" | sort)
  fi
  local remove=$((${#sorted[@]} - KEEP))
  ((remove > 0)) || remove=0
  local i
  for ((i = 0; i < remove; i++)); do
    if rm -f -- "${sorted[i]}.dump.age" "${sorted[i]}.manifest.json" "${sorted[i]}.manifest.json.sig"; then
      say "removed the set ${sorted[i]##*/} (keeping the newest $KEEP)"
    else
      failed=$((failed + 1))
      say "error: the set ${sorted[i]##*/} could not be removed whole; what is left of it stays"
    fi
  done
  printf '%s' "$((${#sorted[@]} - remove + failed))"
}

main() {
  STEP=lock
  exec 9>>"$DIRECTORY/.ariva-backup.lock"
  flock -n 9 || { say "error: another backup is running on this claim"; return 1; }
  WORK="$(mktemp -d /tmp/ariva-backup.XXXXXX)"

  STEP=name
  NAME="ariva-scheduled-${NAMESPACE}-$(date -u +%Y%m%dT%H%M%SZ)"
  local dump="$DIRECTORY/$NAME.dump.age" manifest="$DIRECTORY/$NAME.manifest.json" file
  for file in "$dump" "$dump.partial" "$manifest" "$manifest.partial"; do
    [[ ! -e "$file" && ! -L "$file" ]] || { say "error: ${file##*/} already exists"; return 1; }
  done

  STEP=manifest
  say "backing up database ${POSTGRES_DB:-ariva} at $HOST:$PORT into $NAME"
  local database
  database="$(db manifest)"

  STEP=backup
  local sha256 bytes
  WRITTEN+=("$dump.partial")
  db backup --output - | python3 -I "$SCRIPTS/ariva-age.py" --recipient "$RECIPIENT" --digest "$WORK/dump.digest" > "$dump.partial"
  read -r sha256 bytes < "$WORK/dump.digest"
  [[ "$sha256" =~ ^[0-9a-f]{64}$ && "$bytes" =~ ^[0-9]+$ ]] || { say "error: the encryptor reported no hash of what it wrote"; return 1; }
  publish "$dump.partial" "$dump"
  same_as_written "$dump" "$sha256" "$bytes"

  STEP=set-manifest
  local manifest_sha256 manifest_bytes
  jq -n --arg taken "$TAKEN_BY" --arg namespace "$NAMESPACE" --argjson database "$database" --arg file "$NAME.dump.age" --argjson bytes "$bytes" --arg sha256 "$sha256" \
    '{format: "ariva-backup-set/1", takenBy: $taken, namespace: $namespace, database: $database, files: [{label: "database", file: $file, bytes: $bytes, sha256: $sha256}]}' \
    > "$WORK/manifest.json"
  manifest_sha256="$(sha256sum -- "$WORK/manifest.json")"
  manifest_sha256="${manifest_sha256%% *}"
  manifest_bytes="$(stat -c %s -- "$WORK/manifest.json")"
  WRITTEN+=("$manifest.partial")
  cat -- "$WORK/manifest.json" > "$manifest.partial"
  publish "$manifest.partial" "$manifest"
  same_as_written "$manifest" "$manifest_sha256" "$manifest_bytes"

  STEP=retention
  WRITTEN=()
  local kept
  kept="$(prune)"
  STEP=done
  say "done: check both SHA-256 values against this line, then sign $NAME.manifest.json outside the cluster (ssh-keygen -Y sign -n ariva-backup) before a restore relies on it"
  printf 'ariva-backup: outcome=succeeded set=%s bytes=%s sha256=%s manifest_sha256=%s kept=%s\n' "$NAME" "$bytes" "$sha256" "$manifest_sha256" "$kept"
}

main
