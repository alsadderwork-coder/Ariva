#!/usr/bin/env bash
# Ariva database operations (ARV-126): backup, restore, checks, the Data Protection key ring report and the recovery
# after a Kafka loss. Runs INSIDE the TimescaleDB pod or container (the image has bash, psql, pg_dump and pg_restore of
# the server's own version), never inside an Ariva host. scripts/ops/ariva-ops.mjs copies it into the pod and runs it;
# by hand:
#
#   kubectl -n <namespace> cp scripts/ops/ariva-db.sh timescaledb-0:/tmp/ariva-db.sh -c timescaledb
#   kubectl -n <namespace> exec timescaledb-0 -c timescaledb -- bash /tmp/ariva-db.sh check
#
# Commands (stdout carries data only: a dump, a manifest or a report; messages go to stderr):
#   backup [--output FILE|-]        pg_dump (custom format) as the migration login; "-" (the default) streams to stdout
#   manifest                        JSON about the database (versions, scripts, key ring certificates); no secret
#   check-dump [--input FILE|-]     reads a dump's table of contents (kinds, names, owners; not the SQL) and refuses one
#                                   this script would not restore
#   restore --input FILE [--runtime-login NAME] [--reader-login NAME]
#                                   restores a dump into an empty database (see the order below)
#   restore --input - --dump-checked [...]   the same from stdin, after check-dump has read the same stream
#   check [--runtime-login NAME] [--reader-login NAME]
#                                   owners, the runtime role's privileges, members of Ariva's roles, no C, untrusted-language
#                                   or SECURITY DEFINER function (but Ariva's retention purge exactly as script 0054 ships
#                                   it, grants included), other language, extension or event trigger outside the
#                                   extensions, schema_version and TimescaleDB jobs
#   keyring [--certificate-sha256 HEX]...   the Data Protection keys and the certificate each needs
#   after-kafka-loss --confirm-stream-stopped [--dry-run]
#                                   re-sends the newest event of every key of the compacted topics and clears the
#                                   stream worker's saved positions, after Kafka's topics were recreated empty
#
# Logins (CWE-269). Every Ariva step runs as the migration login (the database owner: CREATEROLE, never superuser) over
# TCP to 127.0.0.1, with the password the pod already holds in its environment (ARIVA_MIGRATION_PASSWORD, from the
# timescaledb-credentials secret); the password is never on a command line. TimescaleDB allows a logical restore only to
# a superuser: timescaledb_pre_restore() stops its background workers, and pg_restore writes the extension's catalog and
# creates chunks in _timescaledb_internal, which belong to the extension owner. Those three steps, and nothing else, run as
# the database superuser over the pod's local socket (peer authentication: no password exists for it here). Every object
# keeps the owner the dump records, the migration login. The runtime login is never in a backup: the migration job
# (api-main --migrate) creates it after a restore with the deployment's own password.
#
# What may be restored (CWE-345). The superuser runs the dump's SQL, so its origin is the gate, and that gate is outside
# this script: ariva-ops.mjs restores only a file whose private copy has the SHA-256 of a manifest signed by an allowed
# signer (or a SHA-256 the operator gives from their own log). check-dump reads the table of contents only, never the SQL
# of each entry, and check after the restore looks for what a forged dump would leave; both are defence in depth.
#
# Inputs (CWE-78). Every value is checked against a strict pattern before use, every command is an argument array (no
# eval, no shell given a command string, no string built into a command line), and values reach SQL only as psql
# variables that psql quotes as literals (:'name').
set -euo pipefail
umask 077
export LC_ALL=C

readonly COMPACTED_TOPICS=(
  ariva.device.registry-changed.v1
  ariva.device.zone-health.v1
  ariva.topology.zone-profile-activated.v1
  ariva.topology.desk-changed.v1
  ariva.flow.nowcast.v1
  ariva.desk.state-changed.v1
)
readonly TIMESCALE_EXTENSIONS=(timescaledb timescaledb_toolkit)

# The migration password stays in this shell; children get it only as PGPASSWORD of the migration login's connections.
MIGRATION_PASSWORD="${ARIVA_MIGRATION_PASSWORD:-}"
export -n ARIVA_MIGRATION_PASSWORD 2>/dev/null || true
unset ARIVA_MIGRATION_PASSWORD POSTGRES_PASSWORD PGPASSWORD

DATABASE="${POSTGRES_DB:-ariva}"
LOGIN="${ARIVA_MIGRATION_LOGIN:-}"
SUPERUSER="${POSTGRES_USER:-postgres}"
PORT=5432
SOCKET_DIR=/var/run/postgresql

say() { printf 'ariva-db: %s\n' "$*" >&2; }
die() { say "error: $*"; exit 1; }
usage() { say "usage: ariva-db.sh <backup|manifest|check-dump|restore|check|keyring|after-kafka-loss> [options]; see the header of this file"; exit 2; }

valid_identifier() { [[ "$1" =~ ^[a-z_][a-z0-9_]{0,62}$ ]]; }
valid_file() { [[ "$1" == "-" ]] || { [[ "$1" =~ ^/[A-Za-z0-9._/-]{1,255}$ ]] && [[ "$1" != *..* ]]; }; }
valid_sha256() { [[ "$1" =~ ^[0-9A-Fa-f]{64}$ ]]; }

check_settings() {
  valid_identifier "$DATABASE" || die "the database name is not a plain lower-case identifier"
  valid_identifier "$LOGIN" || die "set ARIVA_MIGRATION_LOGIN (or --login) to the migration login, a plain lower-case identifier"
  valid_identifier "$SUPERUSER" || die "the superuser name is not a plain lower-case identifier"
  [[ "$PORT" =~ ^[0-9]{1,5}$ ]] || die "the port is not a number"
  [[ "$SOCKET_DIR" =~ ^/[A-Za-z0-9._/-]{1,200}$ && "$SOCKET_DIR" != *..* ]] || die "the socket directory is not a plain absolute path"
  [[ -n "$MIGRATION_PASSWORD" ]] || die "ARIVA_MIGRATION_PASSWORD is not set (the TimescaleDB pod has it from the timescaledb-credentials secret)"
}

# psql as the migration login over TCP; SQL comes on stdin, values as psql variables.
as_migration() {
  PGPASSWORD="$MIGRATION_PASSWORD" PGAPPNAME=ariva-ops PGCONNECT_TIMEOUT=15 psql --no-psqlrc --quiet --no-password \
    --set=ON_ERROR_STOP=1 --host=127.0.0.1 --port="$PORT" --username="$LOGIN" --dbname="$DATABASE" "$@"
}

# psql as the database superuser over the local socket (peer authentication; the restore's TimescaleDB steps only).
as_superuser() {
  PGAPPNAME=ariva-ops psql --no-psqlrc --quiet --no-password --set=ON_ERROR_STOP=1 \
    --host="$SOCKET_DIR" --port="$PORT" --username="$SUPERUSER" --dbname="$DATABASE" "$@"
}

# The migration login must own the database and must be neither superuser nor replication nor bypassrls (CWE-269).
require_migration_login() {
  local row
  row="$(as_migration --tuples-only --no-align --field-separator='|' <<'SQL'
SELECT r.rolsuper, d.datdba = r.oid, r.rolreplication, r.rolbypassrls
  FROM pg_roles r, pg_database d
 WHERE r.rolname = current_user AND d.datname = current_database();
SQL
)" || die "cannot sign in as the migration login $LOGIN to database $DATABASE over 127.0.0.1"
  [[ "$row" == "f|t|f|f" ]] || die "the migration login must own database $DATABASE and be neither superuser, replication nor bypassrls (found superuser|owner|replication|bypassrls = $row)"
}

# Members of Ariva's group roles other than the migration login and the named runtime and reader logins.
extra_members() {
  as_migration --tuples-only --no-align --set=runtime="$1" --set=reader="$2" <<'SQL'
SELECT coalesce(string_agg(g.rolname || ' has member ' || r.rolname, ', ' ORDER BY g.rolname, r.rolname), '')
  FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles r ON r.oid = m.member
 WHERE g.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader') AND r.rolname <> current_user
   AND NOT (g.rolname = 'ariva_runtime' AND r.rolname = :'runtime')
   AND NOT (g.rolname = 'ariva_validation_reader' AND r.rolname = :'reader');
SQL
}

cmd_backup() {
  local output="-"
  while (($#)); do
    case "$1" in
      --output) (($# >= 2)) || usage; output="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  valid_file "$output" || die "--output must be - or a plain absolute path"
  check_settings
  require_migration_login
  local has_schema
  has_schema="$(as_migration --tuples-only --no-align <<'SQL'
SELECT to_regclass('public.schema_version') IS NOT NULL;
SQL
)"
  [[ "$has_schema" == "t" ]] || die "database $DATABASE has no schema_version table: not an Ariva database"
  say "backing up database $DATABASE as $LOGIN (custom format)"
  local args=(--format=custom --no-password --host=127.0.0.1 --port="$PORT" --username="$LOGIN" --dbname="$DATABASE")
  if [[ "$output" != "-" ]]; then
    args+=(--file="$output")
  fi
  PGPASSWORD="$MIGRATION_PASSWORD" PGAPPNAME=ariva-ops pg_dump "${args[@]}"
  say "backup finished"
}

cmd_manifest() {
  (($# == 0)) || usage
  check_settings
  require_migration_login
  as_migration --tuples-only --no-align <<'SQL'
SELECT json_build_object(
    'format', 'ariva-backup-manifest/1',
    'database', current_database(),
    'takenUtc', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'postgres', current_setting('server_version'),
    'extensions', (SELECT json_object_agg(extname, extversion ORDER BY extname) FROM pg_extension),
    'scripts', (SELECT count(*) FROM schema_version),
    'latestScript', (SELECT max(script_name) FROM schema_version),
    'hypertables', (SELECT count(*) FROM timescaledb_information.hypertables),
    'keyRingCertificates', (SELECT coalesce(json_agg(DISTINCT upper(substring(xml from '<encryptedKey [^>]*certificate="([0-9A-Fa-f]{64})"'))), '[]'::json)
                              FROM data_protection_key WHERE xml ~ '<encryptedKey [^>]*certificate="[0-9A-Fa-f]{64}"'));
SQL
}

# The kind of a table of contents entry: the longest known descriptor its text starts with.
toc_kind() {
  local rest="$1" kind
  for kind in "TABLE DATA" "SEQUENCE SET" "SEQUENCE OWNED BY" "FK CONSTRAINT" "DEFAULT ACL" "MATERIALIZED VIEW DATA" "MATERIALIZED VIEW" \
    "EVENT TRIGGER" "FOREIGN DATA WRAPPER" "FOREIGN TABLE" "USER MAPPING" "PROCEDURAL LANGUAGE" "LARGE OBJECT" "BLOB" "PUBLICATION TABLE" \
    "PUBLICATION" "SUBSCRIPTION" "ROW SECURITY" "POLICY" "SERVER" "CHECK CONSTRAINT" "TEXT SEARCH" "OPERATOR" "ACCESS METHOD" "DATABASE" \
    "ACL" "COMMENT" "CONSTRAINT" "DEFAULT" "EXTENSION" "FUNCTION" "PROCEDURE" "AGGREGATE" "INDEX" "SEQUENCE" "TABLE" "TRIGGER" "VIEW" \
    "TYPE" "DOMAIN" "SCHEMA" "CAST" "COLLATION" "CONVERSION" "RULE" "STATISTICS"; do
    if [[ "$rest" == "$kind "* ]]; then
      printf '%s' "$kind"
      return
    fi
  done
  printf '%s' "UNKNOWN"
}

# Refuses a dump this script would not restore (defence in depth; the SHA-256 recorded at backup is the integrity check).
# Allowed: the TimescaleDB extensions and their comments (the image creates them), TimescaleDB's catalog data, the ACL of
# schema public, and Ariva's objects owned by the migration login. Refused: other extensions, languages, event triggers,
# foreign servers, publications, large objects and anything owned by another role (a dump of another deployment, or one
# that makes the runtime login own an object).
check_toc() {
  local toc="$1" line entries=0 rest kind owner name schema problems=0 ext e
  while IFS= read -r line; do
    [[ -z "$line" || "$line" == ";"* ]] && continue
    if [[ ! "$line" =~ ^[0-9]+\;\ [0-9]+\ [0-9]+\ (.*)$ ]]; then
      say "refused: unreadable entry: ${line:0:120}"; problems=$((problems + 1)); continue
    fi
    rest="${BASH_REMATCH[1]}"
    entries=$((entries + 1))
    kind="$(toc_kind "$rest")"
    owner=""
    [[ "$rest" != *" " ]] && owner="${rest##* }"
    case "$kind" in
      EXTENSION)
        name="${rest#EXTENSION - }"; name="${name% }"
        ext=0
        for e in "${TIMESCALE_EXTENSIONS[@]}"; do [[ "$name" == "$e" ]] && ext=1; done
        ((ext == 1)) || { say "refused: extension $name"; problems=$((problems + 1)); }
        continue ;;
      COMMENT)
        if [[ "$rest" == "COMMENT - EXTENSION "* ]]; then
          name="${rest#COMMENT - EXTENSION }"; name="${name% }"
          ext=0
          for e in "${TIMESCALE_EXTENSIONS[@]}"; do [[ "$name" == "$e" ]] && ext=1; done
          ((ext == 1)) || { say "refused: comment on extension $name"; problems=$((problems + 1)); }
          continue
        fi ;;
      "TABLE DATA" | "SEQUENCE SET")
        schema="${rest#"$kind" }"; schema="${schema%% *}"
        if [[ "$schema" == _timescaledb_catalog || "$schema" == _timescaledb_config || "$schema" == _timescaledb_internal ]]; then
          continue
        fi ;;
      ACL)
        [[ "$rest" == "ACL - SCHEMA public pg_database_owner" ]] && continue ;;
      INDEX | TABLE | VIEW | "MATERIALIZED VIEW" | "MATERIALIZED VIEW DATA" | SEQUENCE | "SEQUENCE OWNED BY" | FUNCTION | PROCEDURE | \
        AGGREGATE | TRIGGER | CONSTRAINT | "FK CONSTRAINT" | "CHECK CONSTRAINT" | DEFAULT | "DEFAULT ACL" | TYPE | DOMAIN) ;;
      *)
        say "refused: entry of kind $kind: ${rest:0:120}"; problems=$((problems + 1)); continue ;;
    esac
    if [[ "$owner" != "$LOGIN" ]]; then
      say "refused: owned by ${owner:-nobody}, not the migration login $LOGIN: ${rest:0:120}"
      problems=$((problems + 1))
    fi
  done <<<"$toc"
  ((entries > 0)) || die "the dump has no entries (not a custom-format dump of an Ariva database)"
  if ((problems > 0)); then
    die "$problems entries of the dump are refused; nothing was restored"
  fi
  grep -q " TABLE public schema_version $LOGIN\$" <<<"$toc" || die "the dump has no schema_version table owned by $LOGIN: not an Ariva backup of this deployment"
  say "dump checked: $entries entries, every object owned by $LOGIN"
}

read_toc() {
  local input="$1" toc
  if [[ "$input" == "-" ]]; then
    toc="$(pg_restore --list)" || die "the input is not a custom-format dump"
    # Read the rest of the stream, so its writer (the decryption) authenticates all of it before the restore pass.
    cat >/dev/null
  else
    [[ -f "$input" ]] || die "no dump at $input"
    toc="$(pg_restore --list "$input")" || die "$input is not a custom-format dump"
  fi
  printf '%s' "$toc"
}

cmd_check_dump() {
  local input="-"
  while (($#)); do
    case "$1" in
      --input) (($# >= 2)) || usage; input="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  valid_file "$input" || die "--input must be - or a plain absolute path"
  valid_identifier "$LOGIN" || die "set ARIVA_MIGRATION_LOGIN (or --login) to the migration login"
  local toc
  toc="$(read_toc "$input")"
  check_toc "$toc"
}

RESTORING=0
end_restore_mode() {
  if ((RESTORING == 1)); then
    RESTORING=0
    say "leaving TimescaleDB's restore mode"
    as_superuser --tuples-only --no-align <<'SQL' >/dev/null || say "timescaledb_post_restore() failed; run it as the superuser before anything else"
SELECT timescaledb_post_restore();
SQL
  fi
}

on_restore_exit() {
  local status=$?
  end_restore_mode
  if ((status != 0)); then
    say "the restore did not finish: the database is partly restored. Start again from an empty database (a new volume) after fixing the cause."
  fi
}

cmd_restore() {
  local input="" checked=0 runtime="" reader=""
  while (($#)); do
    case "$1" in
      --input) (($# >= 2)) || usage; input="$2"; shift 2 ;;
      --dump-checked) checked=1; shift ;;
      --runtime-login) (($# >= 2)) || usage; runtime="$2"; shift 2 ;;
      --reader-login) (($# >= 2)) || usage; reader="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  [[ -n "$input" ]] || usage
  valid_file "$input" || die "--input must be - or a plain absolute path"
  [[ -z "$runtime" ]] || valid_identifier "$runtime" || die "--runtime-login must be a plain lower-case identifier"
  [[ -z "$reader" ]] || valid_identifier "$reader" || die "--reader-login must be a plain lower-case identifier"
  check_settings
  if [[ "$input" == "-" ]]; then
    ((checked == 1)) || die "a restore from stdin needs --dump-checked: run check-dump on the same stream first (ariva-ops.mjs does)"
  else
    local toc
    toc="$(read_toc "$input")"
    check_toc "$toc"
  fi

  require_migration_login
  local state
  state="$(as_migration --tuples-only --no-align --field-separator='|' <<'SQL'
SELECT to_regclass('public.schema_version') IS NULL,
       (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
           AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')),
       (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb');
SQL
)"
  local empty relations version
  IFS='|' read -r empty relations version <<<"$state"
  [[ "$empty" == "t" && "$relations" == "0" ]] || die "database $DATABASE is not empty ($relations relations in public); a restore goes into an empty database only"
  [[ -n "$version" ]] || die "the timescaledb extension is not installed in $DATABASE; create it first (the chart's image does)"
  say "restoring into $DATABASE (TimescaleDB $version), objects owned by $LOGIN"

  say "creating Ariva's group roles as $LOGIN"
  local privileged
  privileged="$(as_migration --tuples-only --no-align <<'SQL'
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_migration') THEN
        CREATE ROLE ariva_migration NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_runtime') THEN
        CREATE ROLE ariva_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_validation_reader') THEN
        CREATE ROLE ariva_validation_reader NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
END
$$;
GRANT ariva_migration TO CURRENT_USER;
SELECT string_agg(rolname, ', ') FROM pg_roles
 WHERE rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader')
   AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolcanlogin OR rolreplication OR rolbypassrls);
SQL
)"
  [[ -z "$privileged" ]] || die "these Ariva group roles exist with privileges they must not have: $privileged"
  local members
  members="$(extra_members "$runtime" "$reader")"
  [[ -z "$members" ]] || die "Ariva's group roles already have other members ($members); name this deployment's logins with --runtime-login and --reader-login, or restore into a server of its own"

  trap on_restore_exit EXIT
  say "entering TimescaleDB's restore mode (superuser over the local socket)"
  as_superuser --tuples-only --no-align <<'SQL' >/dev/null
SELECT timescaledb_pre_restore();
SQL
  RESTORING=1
  say "restoring the dump (superuser over the local socket; owners as recorded in the dump)"
  local args=(--exit-on-error --no-password --host="$SOCKET_DIR" --port="$PORT" --username="$SUPERUSER" --dbname="$DATABASE")
  if [[ "$input" == "-" ]]; then
    PGAPPNAME=ariva-ops pg_restore "${args[@]}"
  else
    PGAPPNAME=ariva-ops pg_restore "${args[@]}" "$input"
  fi
  end_restore_mode
  trap - EXIT

  say "updating planner statistics as $LOGIN"
  as_migration <<'SQL'
SET client_min_messages = error;
ANALYZE;
SQL
  local logins=()
  [[ -z "$runtime" ]] || logins+=(--runtime-login "$runtime")
  [[ -z "$reader" ]] || logins+=(--reader-login "$reader")
  cmd_check "${logins[@]}"
  say "restore finished. Next: run the migration job (api-main --migrate) so the runtime and validation reader logins exist with this deployment's passwords, restore the files, start the hosts, then run keyring with the Data Protection certificate."
}

cmd_check() {
  local runtime="" reader=""
  while (($#)); do
    case "$1" in
      --runtime-login) (($# >= 2)) || usage; runtime="$2"; shift 2 ;;
      --reader-login) (($# >= 2)) || usage; reader="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  check_settings
  [[ -z "$runtime" ]] || valid_identifier "$runtime" || die "--runtime-login must be a plain lower-case identifier"
  [[ -z "$reader" ]] || valid_identifier "$reader" || die "--reader-login must be a plain lower-case identifier"
  require_migration_login
  local report
  report="$(as_migration --tuples-only --no-align --set=runtime="$runtime" --set=reader="$reader" <<'SQL'
WITH me AS (SELECT oid FROM pg_roles WHERE rolname = current_user),
foreign_owned AS (
    SELECT n.nspname || '.' || c.relname AS name
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname IN ('public', '_timescaledb_internal') AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
       AND c.relowner <> (SELECT oid FROM me)
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
    UNION ALL
    SELECT 'function public.' || p.proname
      FROM pg_proc p
     WHERE p.pronamespace = 'public'::regnamespace AND p.proowner <> (SELECT oid FROM me)
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')),
runtime_owned AS (
    SELECT count(*) AS n FROM pg_class WHERE relowner IN (SELECT oid FROM pg_roles WHERE rolname = 'ariva_runtime' OR (:'runtime' <> '' AND rolname = :'runtime'))),
-- What a forged dump would leave behind (defence in depth; the signed manifest is the gate): code the superuser could run.
risky_functions AS (
    SELECT n.nspname || '.' || p.proname || ' (' || l.lanname || CASE WHEN p.prosecdef THEN ', SECURITY DEFINER' ELSE '' END || ')' AS name
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
     WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
       AND (p.prosecdef OR NOT l.lanpltrusted OR l.lanname NOT IN ('sql', 'plpgsql'))
       -- ARV-111a: Ariva's one SECURITY DEFINER function (script 0054, the retention purge the Cronz job calls), exactly as
       -- shipped: its name and arguments, the migration login as owner, plpgsql, its settings (search path and time zone),
       -- the SHA-256 of its body (OpsScriptTests keeps this hash equal to the script) and its grants: EXECUTE for the owner
       -- and ariva_runtime only, without grant option (a NULL ACL would mean PUBLIC may run it). Anything else under its name
       -- is reported.
       AND NOT (p.oid = to_regprocedure('public.ariva_retention_purge(text, integer)') AND p.proowner = (SELECT oid FROM me)
                AND l.lanname = 'plpgsql' AND p.proconfig = ARRAY['search_path=pg_catalog, pg_temp', 'TimeZone=UTC']
                AND encode(sha256(convert_to(p.prosrc, 'UTF8')), 'hex') = '535ad38b539587c302aa4ea85e1c3508d55e5d5df6bf4f0675bbadf0076f78d7'
                AND p.proacl IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) a
                                 WHERE a.grantee NOT IN ((SELECT oid FROM me), 'ariva_runtime'::regrole) OR a.is_grantable))),
other_languages AS (
    SELECT lanname AS name FROM pg_language WHERE lanispl AND lanname <> 'plpgsql'),
other_extensions AS (
    SELECT extname AS name FROM pg_extension WHERE extname NOT IN ('plpgsql', 'timescaledb', 'timescaledb_toolkit')),
event_triggers AS (
    SELECT e.evtname AS name FROM pg_event_trigger e
     WHERE NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_event_trigger'::regclass AND d.objid = e.oid AND d.deptype = 'e')),
extra_members AS (
    SELECT g.rolname || ' has member ' || r.rolname AS name
      FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles r ON r.oid = m.member
     WHERE g.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader') AND r.rolname <> current_user
       AND NOT (g.rolname = 'ariva_runtime' AND r.rolname = :'runtime')
       AND NOT (g.rolname = 'ariva_validation_reader' AND r.rolname = :'reader'))
SELECT 'schema_version|' || (SELECT count(*) FROM schema_version) || ' scripts, latest ' || coalesce((SELECT max(script_name) FROM schema_version), 'none')
UNION ALL SELECT 'hypertables|' || (SELECT count(*) FROM timescaledb_information.hypertables) || ' hypertables, '
    || (SELECT count(*) FROM timescaledb_information.jobs WHERE application_name NOT LIKE 'Telemetry%') || ' TimescaleDB jobs'
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'owners|every object belongs to ' || current_user
                      ELSE 'FAIL owners|' || count(*) || ' objects belong to another role, for example ' || min(name) END FROM foreign_owned
UNION ALL SELECT CASE WHEN has_schema_privilege('ariva_runtime', 'public', 'CREATE') THEN 'FAIL runtime role|ariva_runtime may create objects in public'
                      ELSE 'runtime role|ariva_runtime has no CREATE on public' END
UNION ALL SELECT CASE WHEN (SELECT n FROM runtime_owned) = 0 THEN 'runtime role|owns nothing' ELSE 'FAIL runtime role|owns ' || (SELECT n FROM runtime_owned) || ' relations' END
UNION ALL SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader')
                                     AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolcanlogin OR rolreplication OR rolbypassrls))
                      THEN 'FAIL group roles|an Ariva group role is privileged or can sign in' ELSE 'group roles|NOLOGIN, unprivileged' END
UNION ALL SELECT 'migration login|' || current_user || ' owns the database; not superuser, replication or bypassrls'
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'group members|only the migration login and the named logins'
                      ELSE 'FAIL group members|' || string_agg(name, ', ' ORDER BY name) || ' (name this deployment''s logins with --runtime-login and --reader-login)' END
                 FROM extra_members
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'functions|no C, untrusted-language or SECURITY DEFINER function outside the extensions, but the retention purge as script 0054 ships it'
                      ELSE 'FAIL functions|' || count(*) || ' outside the extensions: ' || string_agg(name, ', ' ORDER BY name) END FROM risky_functions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'languages|plpgsql only'
                      ELSE 'FAIL languages|' || string_agg(name, ', ' ORDER BY name) END FROM other_languages
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'extensions|plpgsql, timescaledb and timescaledb_toolkit only'
                      ELSE 'FAIL extensions|' || string_agg(name, ', ' ORDER BY name) END FROM other_extensions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'event triggers|none outside the extensions'
                      ELSE 'FAIL event triggers|' || string_agg(name, ', ' ORDER BY name) END FROM event_triggers
UNION ALL SELECT CASE
        WHEN :'runtime' = '' THEN 'runtime login|not checked (give --runtime-login after the migration job)'
        WHEN NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'runtime') THEN 'FAIL runtime login|' || :'runtime' || ' does not exist: run the migration job'
        WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'runtime' AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls OR NOT rolcanlogin))
            THEN 'FAIL runtime login|' || :'runtime' || ' is privileged or cannot sign in'
        WHEN (SELECT array_agg(g.rolname::text ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
               WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = :'runtime')) IS DISTINCT FROM ARRAY['ariva_runtime']
            THEN 'FAIL runtime login|' || :'runtime' || ' holds roles other than ariva_runtime'
        WHEN has_schema_privilege(:'runtime', 'public', 'CREATE') THEN 'FAIL runtime login|' || :'runtime' || ' may create objects in public'
        ELSE 'runtime login|' || :'runtime' || ' holds ariva_runtime only (DML)' END;
SQL
)"
  local line failed=0
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    if [[ "$line" == FAIL* ]]; then
      failed=1
      printf 'FAIL  %s\n' "${line#FAIL }"
    else
      printf 'ok    %s\n' "$line"
    fi
  done <<<"$report"
  ((failed == 0)) || die "check failed"
}

cmd_keyring() {
  local wanted=() value
  while (($#)); do
    case "$1" in
      --certificate-sha256)
        (($# >= 2)) || usage
        value="${2//:/}"
        valid_sha256 "$value" || die "--certificate-sha256 takes the certificate's SHA-256 fingerprint (64 hex digits, colons allowed)"
        wanted+=("${value^^}"); shift 2 ;;
      *) usage ;;
    esac
  done
  check_settings
  require_migration_login
  local list
  list="$(IFS=,; printf '{%s}' "${wanted[*]}")"
  # Text only: the key XML is matched with bounded regular expressions, never parsed (a planted row cannot reach an XML parser).
  local report
  report="$(as_migration --tuples-only --no-align --field-separator=' ' --set=wanted="$list" <<'SQL'
WITH k AS (
    SELECT friendly_name,
           substring(xml from '^<key id="([0-9a-fA-F-]{36})"') AS key_id,
           substring(xml from '<expirationDate>([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2}))</expirationDate>') AS expires,
           upper(substring(xml from '<encryptedKey [^>]*certificate="([0-9A-Fa-f]{64})"')) AS certificate,
           substring(xml from '<encryptedKey [^>]*algorithm="([A-Za-z0-9+-]{1,40})"') AS algorithm,
           xml ~ '^<revocation' AS revocation
      FROM data_protection_key)
SELECT CASE
         WHEN revocation THEN 'revocation ' || friendly_name
         WHEN key_id IS NULL THEN 'FAIL unreadable ' || friendly_name
         WHEN algorithm IS DISTINCT FROM 'RSA-OAEP-256+A256GCM' THEN 'legacy ' || key_id || ' (older format: read only while DataProtection:LegacyFormatUntil admits it)'
         -- Compared as text, never cast (a planted row cannot make the report fail): UTC times only, anything else counts as needed.
         ELSE CASE WHEN expires IS NULL OR expires !~ '(Z|\+00:00)$'
                        OR left(expires, 19) > to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS') THEN
                     CASE WHEN cardinality(:'wanted'::text[]) = 0 THEN 'needs '
                          WHEN certificate = ANY (:'wanted'::text[]) THEN 'covered '
                          ELSE 'FAIL missing ' END
                   ELSE 'expired ' END
              || key_id || ' expires ' || coalesce(expires, 'unknown') || ' certificate ' || certificate
       END
  FROM k ORDER BY friendly_name;
SQL
)"
  local line failed=0 keys=0
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    keys=$((keys + 1))
    printf '%s\n' "$line"
    [[ "$line" == FAIL* ]] && failed=1
  done <<<"$report"
  ((keys > 0)) || say "the key ring is empty (no host has started on this database yet)"
  if ((failed == 1)); then
    die "a key the hosts still need is wrapped under a certificate that was not given: restore it with that certificate (DataProtection:Previous) or the values under it cannot be read"
  fi
  if ((${#wanted[@]} > 0 && keys > 0)); then
    say "every unexpired key is wrapped under one of the given certificates"
  fi
}

cmd_after_kafka_loss() {
  local confirmed=0 dry=false
  while (($#)); do
    case "$1" in
      --confirm-stream-stopped) confirmed=1; shift ;;
      --dry-run) dry=true; shift ;;
      *) usage ;;
    esac
  done
  ((confirmed == 1)) || die "scale api-stream to 0 first (its saved positions are cleared), then pass --confirm-stream-stopped"
  check_settings
  require_migration_login
  local topics
  topics="$(IFS=,; printf '{%s}' "${COMPACTED_TOPICS[*]}")"
  say "re-sending the newest event of every key of the compacted topics and clearing stream positions (dry run: $dry)"
  as_migration --tuples-only --no-align --field-separator=' ' --set=topics="$topics" --set=dry="$dry" <<'SQL'
BEGIN;
WITH latest AS (
    SELECT DISTINCT ON (topic, message_key) seq, sent_on
      FROM outbox_message
     WHERE topic = ANY (:'topics'::text[])
     ORDER BY topic, message_key, seq DESC),
marked AS (
    UPDATE outbox_message o
       SET sent_on = NULL, attempts = 0, next_attempt_on = NULL, last_error = NULL
      FROM latest l
     WHERE o.seq = l.seq AND l.sent_on IS NOT NULL
    RETURNING o.topic)
SELECT 'republish', topic, count(*) FROM marked GROUP BY topic ORDER BY topic;
WITH cleared AS (DELETE FROM stream_offset RETURNING 1)
SELECT 'stream_offset', 'cleared', count(*) FROM cleared;
\if :dry
ROLLBACK;
\else
COMMIT;
\endif
SQL
  if [[ "$dry" == true ]]; then
    say "dry run: nothing was changed"
  else
    say "done: the outbox relay sends the marked events; start api-stream again, it reads the recreated topics from their start"
  fi
}

main() {
  (($# >= 1)) || usage
  local command="$1"; shift
  local rest=()
  while (($#)); do
    case "$1" in
      --database) (($# >= 2)) || usage; DATABASE="$2"; shift 2 ;;
      --login) (($# >= 2)) || usage; LOGIN="$2"; shift 2 ;;
      --superuser) (($# >= 2)) || usage; SUPERUSER="$2"; shift 2 ;;
      --port) (($# >= 2)) || usage; PORT="$2"; shift 2 ;;
      --socket-dir) (($# >= 2)) || usage; SOCKET_DIR="$2"; shift 2 ;;
      *) rest+=("$1"); shift ;;
    esac
  done
  case "$command" in
    backup) cmd_backup "${rest[@]}" ;;
    manifest) cmd_manifest "${rest[@]}" ;;
    check-dump) cmd_check_dump "${rest[@]}" ;;
    restore) cmd_restore "${rest[@]}" ;;
    check) cmd_check "${rest[@]}" ;;
    keyring) cmd_keyring "${rest[@]}" ;;
    after-kafka-loss) cmd_after_kafka_loss "${rest[@]}" ;;
    *) usage ;;
  esac
}

main "$@"
