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
#   check-dump [--input FILE|-] [--allow-extension NAME]...
#                                   reads a dump's table of contents (kinds, names, owners; not the SQL) and refuses one
#                                   this script would not restore
#   restore --input FILE [--runtime-login NAME] [--reader-login NAME] [--allow-extension NAME]... [--allow-login NAME]...
#           [--allow-setting NAME]...
#                                   restores a dump into an empty database (see the order below)
#   restore --input - --dump-checked [...]   the same from stdin, after check-dump has read the same stream
#   check [--runtime-login NAME] [--reader-login NAME] [--allow-extension NAME]... [--allow-login NAME]... [--allow-setting NAME]...
#                                   owners, the runtime role's privileges, members of Ariva's roles, no C, untrusted-language
#                                   or SECURITY DEFINER function (but Ariva's retention purge exactly as script 0054 ships
#                                   it, grants included), other language, extension or event trigger outside the
#                                   extensions, TimescaleDB's extensions at the image's versions, schema_version and
#                                   TimescaleDB jobs. Since ARV-183 also: no role but the bootstrap superuser and the
#                                   migration login with SUPERUSER, CREATEROLE, REPLICATION or BYPASSRLS, and none holding
#                                   such a role or one of PostgreSQL's server, data or signal roles; Ariva's group roles
#                                   holding no role; no setting for a role's or the database's sessions but the runtime
#                                   login's statement timeout; no ALTER SYSTEM setting; no rule but a view's; no function
#                                   outside the extensions but Ariva's own with a body its scripts ship (ARIVA_FUNCTIONS);
#                                   nothing added to plpgsql; the system's functions as in template1; no publication or
#                                   subscription; and no function or operator under a system name outside the extensions
#                                   (planted_objects). A DBA-provided server's own extension, login and setting pass when
#                                   named with --allow-extension, --allow-login and --allow-setting (plain names)
#   keyring [--certificate-sha256 HEX]...   the Data Protection keys and the certificate each needs
#   after-kafka-loss --confirm-stream-stopped [--dry-run]
#                                   re-sends the newest event of every key of the compacted topics and clears the
#                                   stream worker's saved positions, after Kafka's topics were recreated empty
#   update-extensions               updates TimescaleDB's extensions in the database to the versions the image ships
#                                   (ALTER EXTENSION ... UPDATE), after the pod restarted on a newer image and with the
#                                   Ariva deployments scaled to 0 (ARV-236; wiki 04 section 6.8)
#
# Logins (CWE-269). Every Ariva step runs as the migration login (the database owner: CREATEROLE, never superuser) over
# TCP to 127.0.0.1, with the password the pod already holds in its environment (ARIVA_MIGRATION_PASSWORD, from the
# timescaledb-credentials secret); the password is never on a command line. TimescaleDB allows a logical restore only to
# a superuser: timescaledb_pre_restore() stops its background workers, and pg_restore writes the extension's catalog and
# creates chunks in _timescaledb_internal, which belong to the extension owner. Those three steps run as the database
# superuser over the pod's local socket (peer authentication: no password exists for it here); every object keeps the
# owner the dump records, the migration login. The extensions' update is the other superuser step (ARV-236; proposed,
# pending the owner's decision, docs/product/decisions.md): only an extension's owner may update it, here the superuser
# the image created it as, and its update scripts create C functions. It differs from the restore's steps: it runs
# TimescaleDB's own update scripts on a live database, whose extension schema (public) the migration login owns and can
# write, so an object that login planted there could be picked up by a script running as the superuser (PostgreSQL's
# "Security Considerations for Extensions"). Before any superuser statement the command refuses functions and operators
# named as the system's or the extensions' own outside the extensions, and any other extension (planted_objects); that
# narrows the risk, it does not close it: it relies on TimescaleDB's scripts qualifying everything else, which ARV-236
# did not review. check reads one read-only view as the superuser too (ARV-183): pg_file_settings, which only a superuser
# may read, for the settings ALTER SYSTEM wrote into postgresql.auto.conf, with a statement that names nothing through the
# search path, and only when planted_objects finds nothing. Every superuser session runs with the search path pinned to the
# system catalog by a startup option, which no setting of the database owner overrides, and the restore mode functions are
# named with their schema (ARV-183 review). Nothing else runs as the superuser. The runtime login is never in a backup: the
# migration job
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

# ARV-183: every function body Ariva's scripts ship, as name:SHA-256 of the text between its dollar quotes (what PostgreSQL
# stores as its source). OpsScriptTests holds this list equal to every CREATE FUNCTION of Ariva.Infra/Timescale/Scripts, the
# bodies a later script replaced included, since a backup taken before that script holds the older one. check reports
# any other function outside the extensions, in any schema, and one of these names with another body.
readonly ARIVA_FUNCTIONS="advertising_media_guard:0ab8da7a9d52b3ddaa476898c7585a7b9d0ce0dceec2c7daf34cb25f4dfd164d,\
advertising_media_guard:2b454da6258f0a91d18a87b3978039af67d57f6475cffe9a5d6c607581a632e3,\
advertising_playlist_guard:2e0ba89e6033c9331e127d4556458e0f0fb149a54ad0d81070b5ad732b440a60,\
advertising_schedule_guard:4db8de8d13bee5fdb96ff795f78e5f1c3777c11d21ff179ef4cc21aeb28e6652,\
alert_keep_raise:52af781790c93f2eab47e7cc2b99b6e592da013f16804c725a0dc1b20b5f4805,\
alert_keep_raise:7da479242dafa3d0ffae451c6f31cd9e9301a2be48720b166dc10ef985dc4190,\
alert_rule_identity:b1bbde2c3162fc34a948fb06dca87e4334b5dcec9c474a7f387f52dfdf9efee1,\
ariva_ensure_runtime_login:6864027ae91766a330f29144c95cdaafbc7d7d277c67b93f1e0fd38587aadd03,\
ariva_ensure_validation_reader_login:80844ccdbfee88483d1b2b8f3eb810b19c9886998d9ab90174ebfcaef9317acb,\
ariva_retention_purge:535ad38b539587c302aa4ea85e1c3508d55e5d5df6bf4f0675bbadf0076f78d7,\
ariva_set_runtime_statement_timeout:5a0d58d71b054d30960d5d9be54b37eca1f2c381bb2d46ffe1eaf580b762f3e7,\
desk_observation_insert_check:2cec641a82b38ac75a04c2d5e2fa56bff709120a60ad43736e223e732a4e092d,\
device_calibration_immutable:bf09d2d16ccc2c8a83249a30dab1326829c0e145d31a4abc0f979a8c06dde2b4,\
display_advertising_allowed:0b9b9a687ec18357b07dcb499073d7895bb8fcf81d2c2ab28d832f5c7aa8e9b3,\
email_message_keep:be17d981bf62df9a81a6216a0e7a801ddd5f4d3688a56150780bb94caa36a5c9,\
integration_idempotency_keep:090a6fb49b47c68248a5809533facd01c5e8b8116f824e620a2637f3af0fc815,\
manual_count_insert_check:39f6d887ecfed6f78085e47336450138cd2bd68ec8220a1f205ce304a67304f0,\
queue_bin_final_is_immutable:3887eb144c4b6ce0b8135a3b675c5aa3365cd752692791295bd4c0c42f8b3197,\
replay_run_anchor:da462239cbad0dca9f4cb3c4d5fe3612af58f00fd3e111f33c352ff0e24ca8e4,\
validation_campaign_desk_check:a12dc13172c817c1a7f6662d5d45782db9c1df76c56f2550def4d37e01738b8c,\
validation_campaign_insert_check:0c0ee8ca632bd09a390330e2e97610ccb18bb74f8246d7822c1d74150dc94f0d,\
validation_campaign_line_check:23d90836dc9e23a4e8b15ca0dd1539e11cf25c120377a1a631f100ccc1a69ab3,\
validation_campaign_transition:05c0f590b6a34cb5c9c7bb765205b22eb39c292b6f418019a3c1d84c4a6f0a0f,\
validation_campaign_zone_check:b82a8a504091647d7b1f784f5f814be849183efc386cff6e4ad97a4f5e4503c2,\
validation_capture_insert_check:3ecccd13ffbe0cbe5c7a8bc013d3b3941187b26cc863741e598434481fc454e3,\
validation_result_revision_insert_check:7d9544808d9d86aa5d4cc65442a19b1e82f08754e8709b073d7b5361eb1ba342,\
validation_row_immutable:088cf6b77bcb43ae7ac661717cbfb339ff2b47a9b86711d4b9b3a3bb9645ed1c,\
zone_health_bin_final_is_immutable:546495444ddf733639f5cc6b2e5d09ec19c72d4dec1c8406d69fd905a722333d,\
zone_profile_geometry_immutable:ffb5713b5a9b0bbca725c0208082cfe8b3dcbb4bd525317f08bb29b6391af07d,\
zone_profile_immutable:c3c11583ac7dd5add9f4bafea3c4ba323d27a97cd9adeebd77b06ef9f4ea5a13"

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
usage() { say "usage: ariva-db.sh <backup|manifest|check-dump|restore|check|keyring|after-kafka-loss|update-extensions> [options]; see the header of this file"; exit 2; }

valid_identifier() { [[ "$1" =~ ^[a-z_][a-z0-9_]{0,62}$ ]]; }
valid_file() { [[ "$1" == "-" ]] || { [[ "$1" =~ ^/[A-Za-z0-9._/-]{1,255}$ ]] && [[ "$1" != *..* ]]; }; }
valid_sha256() { [[ "$1" =~ ^[0-9A-Fa-f]{64}$ ]]; }
valid_setting() { [[ "$1" =~ ^[a-z_][a-z0-9_]{0,62}(\.[a-z_][a-z0-9_]{0,62})?$ ]]; }

check_settings() {
  valid_identifier "$DATABASE" || die "the database name is not a plain lower-case identifier"
  valid_identifier "$LOGIN" || die "set ARIVA_MIGRATION_LOGIN (or --login) to the migration login, a plain lower-case identifier"
  valid_identifier "$SUPERUSER" || die "the superuser name is not a plain lower-case identifier"
  [[ "$PORT" =~ ^[0-9]{1,5}$ ]] || die "the port is not a number"
  [[ "$SOCKET_DIR" =~ ^/[A-Za-z0-9._/-]{1,200}$ && "$SOCKET_DIR" != *..* ]] || die "the socket directory is not a plain absolute path"
  [[ -n "$MIGRATION_PASSWORD" ]] || die "ARIVA_MIGRATION_PASSWORD is not set (the TimescaleDB pod has it from the timescaledb-credentials secret)"
}

# psql as the migration login over TCP; SQL comes on stdin, values as psql variables. ON_DATABASE names another database of
# the server for one call (a constant: check reads template1's system functions, ARV-183).
as_migration() {
  PGPASSWORD="$MIGRATION_PASSWORD" PGAPPNAME=ariva-ops PGCONNECT_TIMEOUT=15 psql --no-psqlrc --quiet --no-password \
    --set=ON_ERROR_STOP=1 --host=127.0.0.1 --port="$PORT" --username="$LOGIN" --dbname="${ON_DATABASE:-$DATABASE}" "$@"
}

# psql as the database superuser over the local socket (peer authentication; the restore's TimescaleDB steps, the
# extensions' update and check's read of the ALTER SYSTEM settings only: OpsScriptTests holds every call to one of five
# fixed statements). The search path is the system catalog's for the whole session, a startup option that wins over any
# setting the database owner made (ALTER DATABASE ... SET search_path), so no object the migration login can create in a
# schema of its own is picked up by a name in these statements (ARV-183 review); a caller's startup options come first.
as_superuser() {
  PGOPTIONS="${PGOPTIONS:+$PGOPTIONS }-c search_path=pg_catalog,pg_temp" PGAPPNAME=ariva-ops psql --no-psqlrc --quiet --no-password --set=ON_ERROR_STOP=1 \
    --host="$SOCKET_DIR" --port="$PORT" --username="$SUPERUSER" --dbname="$DATABASE" "$@"
}

# The migration login must own the database and must be neither superuser nor replication nor bypassrls (CWE-269).
require_migration_login() {
  local row
  row="$(as_migration_catalog --tuples-only --no-align --field-separator='|' <<'SQL'
SELECT r.rolsuper, d.datdba = r.oid, r.rolreplication, r.rolbypassrls
  FROM pg_roles r, pg_database d
 WHERE r.rolname = current_user AND d.datname = current_database();
SQL
)" || die "cannot sign in as the migration login $LOGIN to database $DATABASE over 127.0.0.1"
  [[ "$row" == "f|t|f|f" ]] || die "the migration login must own database $DATABASE and be neither superuser, replication nor bypassrls (found superuser|owner|replication|bypassrls = $row)"
}

# Members of Ariva's group roles other than the migration login and the named runtime and reader logins.
extra_members() {
  as_migration_catalog --tuples-only --no-align --set=runtime="$1" --set=reader="$2" <<'SQL'
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
# Allowed: the TimescaleDB extensions and their comments (the image creates them) and the extensions named after the dump
# (--allow-extension, a DBA-provided server's own, ARV-183), TimescaleDB's catalog data, the ACL of schema public, and
# Ariva's objects owned by the migration login. Refused: other extensions, languages, event triggers, foreign servers,
# publications, large objects and anything owned by another role (a dump of another deployment, or one that makes the
# runtime login own an object).
check_toc() {
  local toc="$1"; shift
  local known=("${TIMESCALE_EXTENSIONS[@]}" "$@") line entries=0 rest kind owner name schema problems=0 ext e
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
        for e in "${known[@]}"; do [[ "$name" == "$e" ]] && ext=1; done
        ((ext == 1)) || { say "refused: extension $name (a DBA-provided server's own: name it with --allow-extension)"; problems=$((problems + 1)); }
        continue ;;
      COMMENT)
        if [[ "$rest" == "COMMENT - EXTENSION "* ]]; then
          name="${rest#COMMENT - EXTENSION }"; name="${name% }"
          ext=0
          for e in "${known[@]}"; do [[ "$name" == "$e" ]] && ext=1; done
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
  local input="-" extensions=()
  while (($#)); do
    case "$1" in
      --input) (($# >= 2)) || usage; input="$2"; shift 2 ;;
      --allow-extension) (($# >= 2)) || usage; valid_identifier "$2" || die "--allow-extension must be a plain lower-case identifier"; extensions+=("$2"); shift 2 ;;
      *) usage ;;
    esac
  done
  valid_file "$input" || die "--input must be - or a plain absolute path"
  valid_identifier "$LOGIN" || die "set ARIVA_MIGRATION_LOGIN (or --login) to the migration login"
  local toc
  toc="$(read_toc "$input")"
  check_toc "$toc" "${extensions[@]}"
}

RESTORING=0
end_restore_mode() {
  if ((RESTORING == 1)); then
    RESTORING=0
    say "leaving TimescaleDB's restore mode"
    as_superuser --tuples-only --no-align <<'SQL' >/dev/null || say "timescaledb_post_restore() failed; run it as the superuser before anything else"
SELECT public.timescaledb_post_restore();
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
  local input="" checked=0 runtime="" reader="" extensions=() allowed=()
  while (($#)); do
    case "$1" in
      --input) (($# >= 2)) || usage; input="$2"; shift 2 ;;
      --dump-checked) checked=1; shift ;;
      --runtime-login) (($# >= 2)) || usage; runtime="$2"; shift 2 ;;
      --reader-login) (($# >= 2)) || usage; reader="$2"; shift 2 ;;
      --allow-extension) (($# >= 2)) || usage; valid_identifier "$2" || die "--allow-extension must be a plain lower-case identifier"
        extensions+=("$2"); allowed+=(--allow-extension "$2"); shift 2 ;;
      --allow-login) (($# >= 2)) || usage; valid_identifier "$2" || die "--allow-login must be a plain lower-case identifier"; allowed+=(--allow-login "$2"); shift 2 ;;
      --allow-setting) (($# >= 2)) || usage; valid_setting "$2" || die "--allow-setting must be a setting's name (lower-case, an optional prefix and a dot)"
        allowed+=(--allow-setting "$2"); shift 2 ;;
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
    check_toc "$toc" "${extensions[@]}"
  fi

  require_migration_login
  local state
  state="$(as_migration_catalog --tuples-only --no-align --field-separator='|' <<'SQL'
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
  privileged="$(as_migration_catalog --tuples-only --no-align <<'SQL'
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
SELECT public.timescaledb_pre_restore();
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
  cmd_check "${logins[@]}" "${allowed[@]}"
  say "restore finished. Next: run the migration job (api-main --migrate) so the runtime and validation reader logins exist with this deployment's passwords, restore the files, start the hosts, then run keyring with the Data Protection certificate."
}

cmd_check() {
  local runtime="" reader="" extensions="" logins_allowed="" settings_allowed=""
  while (($#)); do
    case "$1" in
      --runtime-login) (($# >= 2)) || usage; runtime="$2"; shift 2 ;;
      --reader-login) (($# >= 2)) || usage; reader="$2"; shift 2 ;;
      --allow-extension) (($# >= 2)) || usage; valid_identifier "$2" || die "--allow-extension must be a plain lower-case identifier"
        extensions="${extensions:+$extensions,}$2"; shift 2 ;;
      --allow-login) (($# >= 2)) || usage; valid_identifier "$2" || die "--allow-login must be a plain lower-case identifier"
        logins_allowed="${logins_allowed:+$logins_allowed,}$2"; shift 2 ;;
      --allow-setting) (($# >= 2)) || usage; valid_setting "$2" || die "--allow-setting must be a setting's name (lower-case, an optional prefix and a dot)"
        settings_allowed="${settings_allowed:+$settings_allowed,}$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  check_settings
  [[ -z "$runtime" ]] || valid_identifier "$runtime" || die "--runtime-login must be a plain lower-case identifier"
  [[ -z "$reader" ]] || valid_identifier "$reader" || die "--reader-login must be a plain lower-case identifier"
  require_migration_login
  # The report runs as the migration login with the catalog's search path for the session (ARV-183 review): a setting of the
  # database or an object the login created cannot change what a name in it means, or what it reports.
  local report
  report="$(as_migration_catalog --tuples-only --no-align --set=runtime="$runtime" --set=reader="$reader" --set=allowed_extensions="$extensions" \
    --set=allowed_logins="$logins_allowed" --set=allowed_settings="$settings_allowed" --set=ariva_functions="$ARIVA_FUNCTIONS" <<'SQL'
WITH RECURSIVE me AS (SELECT oid FROM pg_roles WHERE rolname = current_user),
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
    SELECT extname AS name FROM pg_extension WHERE extname NOT IN ('plpgsql', 'timescaledb', 'timescaledb_toolkit')
       AND NOT extname = ANY (string_to_array(:'allowed_extensions', ','))),
-- ARV-183: what plpgsql holds beyond its three handler functions and its language. An object added to an extension
-- (ALTER EXTENSION ... ADD, the superuser's) is skipped by every check that leaves the extensions out.
plpgsql_members AS (
    SELECT pg_describe_object(d.classid, d.objid, d.objsubid) AS name
      FROM pg_depend d JOIN pg_extension e ON e.oid = d.refobjid
     WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e' AND e.extname = 'plpgsql'
       AND pg_describe_object(d.classid, d.objid, d.objsubid) NOT IN
           ('function plpgsql_call_handler()', 'function plpgsql_inline_handler(internal)', 'function plpgsql_validator(oid)', 'language plpgsql')),
-- ARV-183 (and its review): the roles whose power a member can take: a superuser, CREATEROLE, REPLICATION or BYPASSRLS,
-- and PostgreSQL's roles that run programs on the server, read or write its files or every table, signal other sessions or
-- create subscriptions.
powerful AS (
    SELECT oid, rolname FROM pg_roles
     WHERE rolsuper OR rolcreaterole OR rolreplication OR rolbypassrls
        OR rolname IN ('pg_execute_server_program', 'pg_read_server_files', 'pg_write_server_files', 'pg_read_all_data',
                       'pg_write_all_data', 'pg_signal_backend', 'pg_create_subscription')),
-- Every role and each role it holds, directly or through others; every grant counts (with the admin option a member may
-- grant itself the rest).
held (member, roleid) AS (
    SELECT member, roleid FROM pg_auth_members
    UNION
    SELECT h.member, m.roleid FROM held h JOIN pg_auth_members m ON m.member = h.roleid),
-- A role with one of the four attributes but the bootstrap superuser (oid 10) and the migration login (CREATEROLE, to create
-- the runtime and reader logins; require_migration_login checks the rest), login or not, and any role that holds a
-- powerful one, the migration login included; a DBA's own login named with --allow-login passes.
privileged_roles AS (
    SELECT r.rolname || ' (' || concat_ws(', ', CASE WHEN r.rolsuper THEN 'superuser' END, CASE WHEN r.rolcreaterole THEN 'createrole' END,
                                         CASE WHEN r.rolreplication THEN 'replication' END, CASE WHEN r.rolbypassrls THEN 'bypassrls' END) || ')' AS name
      FROM pg_roles r
     WHERE (r.rolsuper OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls) AND r.oid <> 10 AND r.rolname <> current_user
       AND NOT r.rolname = ANY (string_to_array(:'allowed_logins', ','))
    UNION
    SELECT r.rolname || ' (holds ' || p.rolname || ')'
      FROM held h JOIN pg_roles r ON r.oid = h.member JOIN powerful p ON p.oid = h.roleid
     WHERE h.member <> 10 AND NOT r.rolname = ANY (string_to_array(:'allowed_logins', ','))),
-- ARV-183 review: Ariva's group roles hold no role, and the named reader login only the reader role (the runtime login's
-- own line checks it the same way).
held_by_ariva AS (
    SELECT r.rolname || ' holds ' || g.rolname AS name
      FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member JOIN pg_roles g ON g.oid = m.roleid
     WHERE r.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader')
        OR (:'reader' <> '' AND r.rolname = :'reader' AND g.rolname <> 'ariva_validation_reader')),
-- ARV-183 review: settings made for a role's sessions (ALTER ROLE ... SET), in this database or in every one, but the
-- runtime login's statement timeout in this database (script 0063) and the names --allow-setting gives; settings for every
-- session of the database are planted_objects' line.
role_settings AS (
    SELECT coalesce(r.rolname, 'role ' || s.setrole) || ' ' || split_part(c, '=', 1) AS name
      FROM pg_db_role_setting s LEFT JOIN pg_roles r ON r.oid = s.setrole CROSS JOIN LATERAL unnest(s.setconfig) c
     WHERE s.setrole <> 0 AND s.setdatabase IN (0, (SELECT oid FROM pg_database WHERE datname = current_database()))
       AND NOT split_part(c, '=', 1) = ANY (string_to_array(:'allowed_settings', ','))
       AND NOT (split_part(c, '=', 1) = 'statement_timeout' AND s.setdatabase <> 0
                AND s.setrole IN (SELECT m.member FROM pg_auth_members m WHERE m.roleid = 'ariva_runtime'::regrole))),
-- ARV-183: a rule created after the server's own (pg_settings has two) that is not a view's (_RETURN), outside the extensions:
-- it rewrites what its table's statements do.
rules AS (
    SELECT n.nspname || '.' || c.relname || ' ' || r.rulename AS name
      FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE r.oid >= 16384 AND r.rulename <> '_RETURN'
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid AND d.deptype = 'e')),
-- ARV-183: a function created after the server's own (oid 16384 and above) outside the extensions, in any schema, pg_catalog
-- included, but Ariva's own: in public, owned by the migration login, plpgsql, with a body one of its scripts ships.
other_functions AS (
    SELECT n.nspname || '.' || p.proname AS name
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
     WHERE p.oid >= 16384
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
       AND NOT (n.nspname = 'public' AND p.proowner = (SELECT oid FROM me) AND l.lanname = 'plpgsql'
                AND p.proname || ':' || encode(sha256(convert_to(p.prosrc, 'UTF8')), 'hex') = ANY (string_to_array(:'ariva_functions', ',')))),
-- ARV-183 review: a publication or a subscription of this database (a subscription connects out to another server).
replication AS (
    SELECT 'publication ' || pubname AS name FROM pg_publication
    UNION ALL
    SELECT 'subscription ' || subname FROM pg_subscription WHERE subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())),
event_triggers AS (
    SELECT e.evtname AS name FROM pg_event_trigger e
     WHERE NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_event_trigger'::regclass AND d.objid = e.oid AND d.deptype = 'e')),
extra_members AS (
    SELECT g.rolname || ' has member ' || r.rolname AS name
      FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles r ON r.oid = m.member
     WHERE g.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader') AND r.rolname <> current_user
       AND NOT (g.rolname = 'ariva_runtime' AND r.rolname = :'runtime')
       AND NOT (g.rolname = 'ariva_validation_reader' AND r.rolname = :'reader'))
SELECT 'schema_version|' || (SELECT count(*) FROM public.schema_version) || ' scripts, latest ' || coalesce((SELECT max(script_name) FROM public.schema_version), 'none')
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
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'held roles|Ariva''s group roles hold no role' || CASE WHEN :'reader' = '' THEN '' ELSE ', ' || :'reader' || ' only ariva_validation_reader' END
                      ELSE 'FAIL held roles|' || string_agg(name, ', ' ORDER BY name) END FROM held_by_ariva
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'functions|no C, untrusted-language or SECURITY DEFINER function outside the extensions, but the retention purge as script 0054 ships it'
                      ELSE 'FAIL functions|' || count(*) || ' outside the extensions: ' || string_agg(name, ', ' ORDER BY name) END FROM risky_functions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'languages|plpgsql only'
                      ELSE 'FAIL languages|' || string_agg(name, ', ' ORDER BY name) END FROM other_languages
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'extensions|plpgsql, timescaledb and timescaledb_toolkit'
                                                || CASE WHEN :'allowed_extensions' = '' THEN ' only' ELSE ', and allowed: ' || replace(:'allowed_extensions', ',', ', ') END
                      ELSE 'FAIL extensions|' || string_agg(name, ', ' ORDER BY name) || ' (a DBA-provided server''s own: name it with --allow-extension)' END FROM other_extensions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'plpgsql|its three handler functions and its language only'
                      ELSE 'FAIL plpgsql|also holds ' || string_agg(name, ', ' ORDER BY name) || ' (added to the extension, where the function checks do not look)' END FROM plpgsql_members
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'logins|no role but the bootstrap superuser and the migration login is a superuser or has createrole, replication or bypassrls, and none holds such a role or one of the server, data or signal roles'
                                                || CASE WHEN :'allowed_logins' = '' THEN '' ELSE ', but allowed: ' || replace(:'allowed_logins', ',', ', ') END
                      ELSE 'FAIL logins|' || string_agg(name, ', ' ORDER BY name) || ' (a DBA''s own login: name it with --allow-login)' END FROM privileged_roles
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'role settings|none but the runtime login''s statement timeout'
                                                || CASE WHEN :'allowed_settings' = '' THEN '' ELSE ', and allowed: ' || replace(:'allowed_settings', ',', ', ') END
                      ELSE 'FAIL role settings|' || string_agg(name, ', ' ORDER BY name) || ' (ALTER ROLE ... SET; a DBA''s own: name it with --allow-setting)' END FROM role_settings
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'rules|none but the views'' own'
                      ELSE 'FAIL rules|' || string_agg(name, ', ' ORDER BY name) END FROM rules
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'other functions|none outside the extensions but Ariva''s own, each with a body its scripts ship'
                      ELSE 'FAIL other functions|' || count(*) || ' outside the extensions, or under an Ariva name with another body or owner: ' || string_agg(name, ', ' ORDER BY name) END FROM other_functions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'replication|no publication or subscription'
                      ELSE 'FAIL replication|' || string_agg(name, ', ' ORDER BY name) END FROM replication
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'extension versions|TimescaleDB''s extensions at the versions the image ships'
                      ELSE 'FAIL extension versions|' || string_agg(name || ' ' || installed_version || ' (the image ships ' || default_version
                           || CASE WHEN has_path THEN '): run update-extensions' ELSE '; it has no update from it): roll the image forward' END,
                           ', ' ORDER BY name) || ' (ARV-236)' END
                 FROM (SELECT a.name, a.installed_version, a.default_version,
                              EXISTS (SELECT 1 FROM pg_extension_update_paths(a.name) u
                                       WHERE u.source = a.installed_version AND u.target = a.default_version AND u.path IS NOT NULL) AS has_path
                         FROM pg_available_extensions a
                        WHERE a.name IN ('timescaledb', 'timescaledb_toolkit') AND a.installed_version IS NOT NULL
                          AND a.installed_version <> a.default_version) behind
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
  # ARV-183 review: the system's own functions against template1's (a function replaced in place keeps its oid).
  report+=$'\n'"$(system_functions_line)"
  # ARV-183 review: functions and operators under system or TimescaleDB names outside the extensions, and settings for every
  # session of the database (ALTER DATABASE ... SET), which update-extensions refuses too (other extensions have their own
  # line). While there is any, the superuser reads nothing: the session's pinned search path already keeps such objects out
  # of its statement, and this keeps the superuser away from a database the migration login has been preparing.
  local planted read=1
  planted="$(planted_objects "$settings_allowed")" || read=0
  planted="$(grep -v '^extension ' <<<"$planted" || true)"
  if ((read == 0)); then
    report+=$'\n''FAIL planted|could not be read as the migration login'
  elif [[ -z "$planted" ]]; then
    report+=$'\n''planted|no function or operator under a system or TimescaleDB name outside the extensions, no setting for every session of the database'
  else
    report+=$'\n'"FAIL planted|${planted//$'\n'/, } (the extensions' update refuses these too; find out who created them, wiki 10 section 4.13)"
  fi
  local names name kept=()
  if ((read == 0)) || [[ -n "$planted" ]]; then
    report+=$'\n''FAIL settings|ALTER SYSTEM settings not read: the superuser reads nothing while the line above fails'
  elif names="$(auto_conf_settings)"; then
    while IFS= read -r name; do
      [[ -z "$name" || ",$settings_allowed," == *",$name,"* ]] || kept+=("$name")
    done <<<"$names"
    if ((${#kept[@]} == 0)); then
      report+=$'\n''settings|no ALTER SYSTEM setting (postgresql.auto.conf)'"${settings_allowed:+ but the allowed: ${settings_allowed//,/, }}"
    else
      local joined
      joined="$(IFS=,; printf '%s' "${kept[*]}")"
      report+=$'\n'"FAIL settings|ALTER SYSTEM set ${joined//,/, } (postgresql.auto.conf): find out who, then ALTER SYSTEM RESET each and restart the server (a DBA's own: name it with --allow-setting)"
    fi
  else
    report+=$'\n''FAIL settings|postgresql.auto.conf could not be read as the superuser over the local socket (run check in the database pod or on the server)'
  fi
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

# The settings ALTER SYSTEM wrote (postgresql.auto.conf), one name a line; pg_file_settings is readable by a superuser only
# (ARV-183). The statement names nothing through the search path, and the session pins it to the catalog anyway.
auto_conf_settings() {
  as_superuser --tuples-only --no-align <<'SQL'
SELECT DISTINCT s.name FROM pg_catalog.pg_file_settings s WHERE s.sourcefile OPERATOR(pg_catalog.~~) '%/postgresql.auto.conf' ORDER BY 1;
SQL
}

# The system's own functions of a database (oid below 16384), one oid|name|SHA-256 line each, the hash over what defines
# the function but its grants (ARV-183 review). CREATE OR REPLACE keeps a function's oid, so a system function a forged dump
# replaced in Ariva's database differs from the same one in template1 (or postgres), which a restore never touches.
system_functions() {
  valid_identifier "$1" || die "not a database name: $1"
  ON_DATABASE="$1" as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT p.oid || '|' || p.proname || '|' || encode(sha256(convert_to(concat_ws('|', p.proname, p.pronamespace, p.proowner, p.prolang, p.prokind,
       p.prosecdef, p.proleakproof, p.proisstrict, p.proretset, p.provolatile, p.proparallel, p.prorettype, p.proargtypes, p.proallargtypes,
       p.proargmodes, p.proconfig, p.prosrc, p.probin, p.prosupport), 'UTF8')), 'hex')
  FROM pg_proc p WHERE p.oid < 16384 ORDER BY 1;
SQL
}

system_functions_line() {
  local ours theirs reference changed
  ours="$(system_functions "$DATABASE")" || { printf '%s' "FAIL system functions|$DATABASE's could not be read"; return; }
  for reference in template1 postgres; do
    if theirs="$(system_functions "$reference" 2>/dev/null)"; then
      changed="$(comm -3 <(sort <<<"$ours") <(sort <<<"$theirs") | cut -d'|' -f2 | sort -u | paste -sd, -)"
      if [[ -z "$changed" ]]; then
        printf '%s' "system functions|the same as in $reference"
      else
        printf '%s' "FAIL system functions|${changed//,/, } differ from $reference's (replaced in place in $DATABASE, or changed in $reference)"
      fi
      return
    fi
  done
  printf '%s' "FAIL system functions|neither template1 nor postgres could be read as the migration login to compare them (CONNECT on template1)"
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

# Reads for the extensions' update, as the migration login with the search path fixed to the system catalog for the session
# (a startup option, which wins over the login's own settings), so no object or setting of that login changes what a
# name in these statements means; every catalog name is qualified as well.
as_migration_catalog() {
  PGOPTIONS='-c search_path=pg_catalog,pg_temp' as_migration "$@"
}

# TimescaleDB's extensions whose installed version is not the one the image ships, as name|installed|shipped|update path
# (t or f) lines. No update path: the image is older than the database's extension (a rollback of the database image).
extensions_behind() {
  as_migration_catalog --tuples-only --no-align --field-separator='|' <<'SQL'
SELECT a.name, a.installed_version, a.default_version,
       EXISTS (SELECT 1 FROM pg_catalog.pg_extension_update_paths(a.name) u
                WHERE u.source = a.installed_version AND u.target = a.default_version AND u.path IS NOT NULL)
  FROM pg_catalog.pg_available_extensions a
 WHERE a.name IN ('timescaledb', 'timescaledb_toolkit') AND a.installed_version IS NOT NULL
   AND a.installed_version <> a.default_version
 ORDER BY a.name;
SQL
}

# What the extensions' update scripts could pick up instead of the system's or their own objects: a function, procedure,
# aggregate or operator outside the three extensions (plpgsql, timescaledb, timescaledb_toolkit) named as one in
# pg_catalog or one of theirs, any other extension, and settings made for every session of this database (ALTER DATABASE
# ... SET, which the database owner may make and the superuser's update session would inherit; Ariva makes none), one line
# per setting. One line per object; none expected. $1: setting names to leave out, comma-separated (check's --allow-setting;
# the extensions' update gives none).
planted_objects() {
  as_migration_catalog --tuples-only --no-align --set=allowed_settings="${1:-}" <<'SQL'
WITH members AS (
    SELECT d.classid, d.objid
      FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid
     WHERE d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass AND d.deptype = 'e'
       AND e.extname IN ('plpgsql', 'timescaledb', 'timescaledb_toolkit')),
function_names AS (
    SELECT p.proname FROM pg_catalog.pg_proc p
     WHERE p.pronamespace = 'pg_catalog'::pg_catalog.regnamespace
        OR EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND m.objid = p.oid)),
operator_names AS (
    SELECT o.oprname FROM pg_catalog.pg_operator o
     WHERE o.oprnamespace = 'pg_catalog'::pg_catalog.regnamespace
        OR EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_operator'::pg_catalog.regclass AND m.objid = o.oid))
SELECT 'function ' || n.nspname || '.' || p.proname
  FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
   AND p.proname IN (SELECT proname FROM function_names)
   AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND m.objid = p.oid)
UNION
SELECT 'operator ' || n.nspname || '.' || o.oprname
  FROM pg_catalog.pg_operator o JOIN pg_catalog.pg_namespace n ON n.oid = o.oprnamespace
 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
   AND o.oprname IN (SELECT oprname FROM operator_names)
   AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_operator'::pg_catalog.regclass AND m.objid = o.oid)
UNION
SELECT 'extension ' || e.extname FROM pg_catalog.pg_extension e WHERE e.extname NOT IN ('plpgsql', 'timescaledb', 'timescaledb_toolkit')
UNION
SELECT 'database setting ' || c
  FROM pg_catalog.pg_db_role_setting s JOIN pg_catalog.pg_database d ON d.oid = s.setdatabase
       CROSS JOIN LATERAL pg_catalog.unnest(s.setconfig) c
 WHERE d.datname = pg_catalog.current_database() AND s.setrole = 0
   AND NOT pg_catalog.split_part(c, '=', 1) = ANY (pg_catalog.string_to_array(:'allowed_settings', ','))
ORDER BY 1;
SQL
}

# Sessions of Ariva's logins on the database other than this one, as "login (count)" lines: the migration login and every
# member of ariva_runtime or ariva_validation_reader (the hosts). A row of another login's session shows its login but not
# always its backend type, so a row without one counts; TimescaleDB's background workers do not.
ariva_sessions() {
  as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT a.usename || ' (' || count(*) || ')'
  FROM pg_catalog.pg_stat_activity a
 WHERE a.datname = pg_catalog.current_database() AND a.pid <> pg_catalog.pg_backend_pid()
   AND (a.backend_type IS NULL OR a.backend_type = 'client backend')
   AND (a.usename = CURRENT_USER
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles g ON g.oid = m.roleid
                    WHERE m.member = a.usesysid AND g.rolname IN ('ariva_runtime', 'ariva_validation_reader')))
 GROUP BY a.usename
 ORDER BY 1;
SQL
}

# ARV-236: a newer image ships newer versions of the extensions, and the database keeps the one it has until the
# extension's owner updates it. It runs with the Ariva deployments scaled to 0 (wiki 04 section 6.8): a connection that
# loaded the old library is closed by TimescaleDB at its next statement (FATAL), and a long transaction would hold the
# update's locks. In order, before any statement as the superuser: refuse an extension the image cannot update (no update
# path), a session of an Ariva login, and anything planted_objects finds; then each ALTER EXTENSION as the first and only statement of a psql
# session of its own, since TimescaleDB loads its library at a session's first statement and refuses the update once the
# old one is loaded ("cannot be updated after the old version has already been loaded"); psql without psqlrc sends nothing
# before it, and lock_timeout (a startup option, not a statement) ends a wait for a lock after 30 seconds with nothing
# changed. The statements are constants: no value reaches them.
cmd_update_extensions() {
  (($# == 0)) || usage
  check_settings
  require_migration_login
  local behind name installed shipped path planted
  behind="$(extensions_behind)"
  if [[ -z "$behind" ]]; then
    say "TimescaleDB's extensions in $DATABASE are at the versions the image ships: nothing to update"
    return
  fi
  while IFS='|' read -r name installed shipped path; do
    [[ -n "$name" && "$path" != "t" ]] || continue
    die "$name $installed has no update to $shipped, the version the image ships: the image is older than the database's extension (for example a rollback of the database image after update-extensions); roll the image forward (wiki 18)"
  done <<<"$behind"
  # The hosts scaled to 0: their pods may take a few seconds to end after the scale (up to 10 here, then refused).
  local sessions attempt
  for attempt in 1 2 3 4 5 6; do
    sessions="$(ariva_sessions)"
    [[ -n "$sessions" && "$attempt" -lt 6 ]] || break
    sleep 2
  done
  [[ -z "$sessions" ]] || die "Ariva's logins still have sessions on $DATABASE: ${sessions//$'\n'/, }. Scale the Ariva deployments to 0 first (kubectl -n <namespace> scale deployment --all --replicas=0) and wait for their pods to end; nothing was changed"
  planted="$(planted_objects "")"
  [[ -z "$planted" ]] || die "refused before any statement as the superuser: ${planted//$'\n'/, }. The extensions' update scripts run as the superuser with the extension's schema on their search path, so objects named as the system's or TimescaleDB's own, outside the extensions, could run in their place. Find out who created them (wiki 10 section 4.13), remove them, then run update-extensions again"
  while IFS='|' read -r name installed shipped path; do
    [[ -n "$name" ]] || continue
    say "updating $name from $installed to $shipped (superuser over the local socket, the session's first statement)"
    case "$name" in
      timescaledb)
        PGOPTIONS='-c lock_timeout=30s' as_superuser <<'SQL'
ALTER EXTENSION timescaledb UPDATE;
SQL
        ;;
      timescaledb_toolkit)
        PGOPTIONS='-c lock_timeout=30s' as_superuser <<'SQL'
ALTER EXTENSION timescaledb_toolkit UPDATE;
SQL
        ;;
      *) die "unexpected extension $name" ;;
    esac
  done <<<"$behind"
  behind="$(extensions_behind)"
  [[ -z "$behind" ]] || die "an extension is still behind the image after its update: ${behind//$'\n'/, }"
  say "done: TimescaleDB's extensions in $DATABASE are at the versions the image ships; scale the deployments back, run check, and take a new backup (a backup restores only into its own TimescaleDB version)"
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
    update-extensions) cmd_update_extensions "${rest[@]}" ;;
    *) usage ;;
  esac
}

main "$@"
