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
#   backup and manifest take --host NAME (ARV-182): the database server at that name instead of 127.0.0.1, for the chart's
#                                   scheduled backup (a CronJob pod of the same image beside the database pod, which reaches
#                                   the server through its Service); every other command refuses it, since its superuser steps
#                                   go to this pod's own socket and must never meet another server than its TCP steps
#   check-dump [--input FILE|-] [--allow-extension NAME]... [--timescaledb-version VERSION]
#                                   reads a dump's table of contents (kinds, names, owners; not the SQL) and refuses one
#                                   this script would not restore; since ARV-244 also reads the TimescaleDB version the dump
#                                   records (its _timescaledb_catalog.metadata row), refuses a dump that records none or
#                                   another than the one given, and prints it on stdout ("timescaledb 2.31.0")
#   restore --input FILE [--timescaledb-version VERSION] [--runtime-login NAME] [--reader-login NAME] [--allow-extension NAME]...
#           [--allow-login NAME]... [--allow-setting NAME]...
#                                   restores a dump into an empty database (see the order below); since ARV-244 into the
#                                   TimescaleDB version the dump records: refused before anything changes when the image does
#                                   not ship it, created at it first when the database has another, updated to the image's
#                                   after the restore (as update-extensions does)
#   restore --input - --dump-checked --timescaledb-version VERSION [...]
#                                   the same from stdin, after check-dump has read the same stream; VERSION is the signed
#                                   manifest's, or the one check-dump printed (ariva-ops.mjs passes it)
#   check [--runtime-login NAME] [--reader-login NAME] [--allow-extension NAME]... [--allow-login NAME]... [--allow-setting NAME]...
#                                   owners, the runtime role's privileges, members of Ariva's roles, no C, untrusted-language
#                                   or SECURITY DEFINER function (but Ariva's retention purge exactly as script 0054 ships
#                                   it and its key ring purge exactly as script 0072 ships it, grants included), other
#                                   language, extension or event trigger outside the
#                                   extensions, TimescaleDB's extensions at the image's versions, schema_version and
#                                   TimescaleDB jobs. Since ARV-183 also: no role but the bootstrap superuser and the
#                                   migration login with SUPERUSER, CREATEROLE, REPLICATION or BYPASSRLS, and none holding
#                                   such a role or one of PostgreSQL's server, data or signal roles; Ariva's group roles
#                                   holding no role; no setting for a role's or the database's sessions but the runtime
#                                   login's statement timeout, temporary file cap (ARV-290), idle transaction limit and
#                                   keepalives (ARV-292), and the migration login
#                                   able to set that cap; no privilege on a parameter but its SET on temp_file_limit (the
#                                   cluster's, its review), and with --runtime-login that login's own two caps in the
#                                   database; no ALTER SYSTEM setting; no rule but a view's; no function
#                                   outside the extensions but Ariva's own with a body its scripts ship (ARIVA_FUNCTIONS);
#                                   nothing added to plpgsql; the system's functions as in template1; no publication or
#                                   subscription; and no function or operator under a system name outside the extensions
#                                   (planted_objects). Since ARV-189 also the runtime role's privileges on every relation of
#                                   public as the scripts leave them (ARIVA_RUNTIME_PRIVILEGES) and the views of public as
#                                   the scripts create them (ARIVA_VIEWS), both compared once the database is at
#                                   ARIVA_RUNTIME_PRIVILEGES_AT (a database behind it fails, but right after restore, whose
#                                   migration job follows); outside public each relation judged by what it is, not by its
#                                   name: an inheritance child no wider than its parent and only TimescaleDB's chunks
#                                   inheriting, a compressed chunk no wider than its hypertable, a continuous aggregate's
#                                   table and views at most SELECT, no view (in public too) wider than what it reads,
#                                   nothing on any other relation created after initdb, no sequence it may set, no
#                                   extension or system table granting more than it was created with; no CREATE on any
#                                   schema or the database for the role or its logins; no grant to a role that holds
#                                   ariva_runtime (the runtime login), with grant option, or on Ariva's relations to
#                                   PUBLIC; and default privileges as script 0001 leaves them and nothing else. A
#                                   DBA-provided server's own extension, login and setting pass when
#                                   named with --allow-extension, --allow-login and --allow-setting (plain names). Since
#                                   ARV-262 also: Ariva's objects in public as a database the same scripts migrated (their
#                                   columns, constraints, indexes, triggers, grants and function definitions; ARIVA_SCHEMA,
#                                   compared once the database is at ARIVA_SCHEMA_AT), TimescaleDB's extensions holding
#                                   exactly the image's members (ARIVA_EXTENSION_MEMBERS), the system catalog's functions,
#                                   operators, types, casts, aggregates and languages as this PostgreSQL version ships them
#                                   (ARIVA_SYSTEM_FUNCTIONS, a digest taken outside the database), and no setting from a file
#                                   but the server's postgresql.conf (postgresql.auto.conf or a file an include names).
#                                   Since ARV-263 also: TimescaleDB's jobs as a database the same scripts migrated (each
#                                   job's procedure, owner, hypertable, schedule and configuration, a retention period
#                                   within the bounds the migration job holds it to; ARIVA_SCHEMA), no toast table granting
#                                   the runtime role or a login of it anything and none of them using pg_toast, no grant on
#                                   a system catalog function beyond initdb's (pg_init_privs), and pg_maintain among the
#                                   roles no login may hold; since ARV-263b pg_read_all_stats, pg_read_all_settings and
#                                   pg_monitor too (initdb's own memberships among those roles are the server's). It
#                                   finds what a careless forged dump leaves; it cannot prove a careful one absent (the
#                                   superuser that ran it can change what check reads): the signed manifest is the gate
#   schema [--full]                 what check compares: one <object>=<hash> line per object of Ariva's in public and per
#                                   TimescaleDB job, each
#                                   TimescaleDB extension's members digest and the system functions' digest; with --full
#                                   each object's normalised definition and each extension member, one line each, so that
#                                   two databases' outputs compare line by line (read-only, as the migration login)
#   keyring [--certificate-sha256 HEX]...   the Data Protection keys, the certificate each needs and the signing certificate
#                                   that signed it (ARV-211; "the certificate" for a key written before ARV-211)
#   after-kafka-loss --confirm-stream-stopped [--dry-run]
#                                   re-sends the newest event of every key of the compacted topics and clears the
#                                   stream worker's saved positions, after Kafka's topics were recreated empty
#   grant-settings                  grants the migration login SET on temp_file_limit, a superuser setting the migration job
#                                   sets on the runtime login (ARV-290), on a server first started before it (the chart's
#                                   init script grants it on a new volume), to ariva_migration, so after a first migration;
#                                   then run the migration job again
#   update-extensions               updates TimescaleDB's extensions in the database to the versions the image ships
#                                   (ALTER EXTENSION ... UPDATE), after the pod restarted on a newer image and with the
#                                   Ariva deployments scaled to 0 (ARV-236; wiki 04 section 6.8)
#
# Logins (CWE-269). Every Ariva step runs as the migration login (the database owner: CREATEROLE, never superuser) over
# TCP to 127.0.0.1 (backup and manifest: to --host), with the password the pod already holds in its environment (ARIVA_MIGRATION_PASSWORD, from the
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
# did not review. check reads two read-only views as the superuser too (ARV-183, ARV-262): pg_file_settings and pg_settings,
# whose files only a superuser may see, for the settings any file but the server's postgresql.conf holds (ALTER SYSTEM's
# postgresql.auto.conf, a file an include names), in one statement that names nothing through the search path, and only
# when planted_objects finds nothing. Every superuser session runs with the search path pinned to the
# system catalog by a startup option, which no setting of the database owner overrides, and the restore mode functions are
# named with their schema (ARV-183 review). grant-settings is one more (ARV-290): one constant GRANT SET ON PARAMETER of
# temp_file_limit to ariva_migration, the group role the migration login holds, which only a superuser may give. A restore of
# a dump taken under an older TimescaleDB (ARV-244) adds two in the empty database, before restore mode: DROP EXTENSION
# timescaledb, the extension the image created, and CREATE EXTENSION timescaledb in schema public (where the image has it)
# at the dump's version, each the first and
# only statement of a session of its own (a session that loaded one version's library cannot create another); the version
# reaches it only as a psql variable (:'version'), after it passed valid_version and was found among the versions the image
# ships, with its own install script and library. After the restore the update is update-extensions' own. Nothing
# else runs as the superuser. The runtime login is never in a backup: the
# migration job
# (api-main --migrate) creates it after a restore with the deployment's own password.
#
# What may be restored (CWE-345). The superuser runs the dump's SQL, so its origin is the gate, and that gate is outside
# this script: ariva-ops.mjs restores only a file whose private copy has the SHA-256 of a manifest signed by an allowed
# signer (or a SHA-256 the operator gives from their own log). check-dump reads the table of contents only, never the SQL
# of each entry, and check after the restore looks for what a forged dump would leave; both are defence in depth. Since
# ARV-263's review check-dump takes TimescaleDB's data only for the tables a dump of it carries (TIMESCALE_CATALOG_DATA) and
# for chunks and TimescaleDB's own hypertables, and restore judges the jobs it brought while TimescaleDB's restore mode still
# keeps the scheduler away (restore_job_gate): a job that would run otherwise than this release makes it keeps the database
# in restore mode and stops the restore, since a job runs as its owner as soon as that mode ends. The TimescaleDB version a
# restore creates the extension at (ARV-244) comes from the same places: the signed manifest's, which check-dump compares with
# the version the dump itself records, or with --sha256 the dump's own; never from an unsigned input. After pg_restore the
# restored catalog must record that version too, or the database stays in restore mode.
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
# ARV-263 review: the tables of _timescaledb_catalog whose rows a dump of TimescaleDB carries (the extension's configuration
# tables, pg_extension.extconfig); BackupRestoreTests holds the list equal to the image's. check_toc refuses data for any
# other TimescaleDB table (a job's run state, for one, which pg_dump never writes: a forged next_start would keep a pinned
# retention job from running), and in _timescaledb_internal it takes only the data of chunks, compressed chunks (TimescaleDB
# 2.31 names one after its chunk with _compressed; older versions compress_hyper_N_N_chunk) and the hypertables behind
# continuous aggregates and compression, owned by the migration login.
readonly TIMESCALE_CATALOG_DATA=(bgw_job bgw_job_id_seq chunk chunk_column_stats chunk_column_stats_id_seq chunk_id_seq compression_chunk_size
  compression_settings continuous_agg continuous_aggs_bucket_function continuous_aggs_hypertable_invalidation_log continuous_aggs_invalidation_threshold
  continuous_aggs_jobs_refresh_ranges continuous_aggs_materialization_invalidation_log continuous_aggs_materialization_ranges continuous_aggs_tenant_tracking
  continuous_aggs_watermark dimension dimension_id_seq dimension_slice dimension_slice_id_seq hypertable hypertable_cagg_settings hypertable_id_seq metadata
  tablespace)

# ARV-183: every function body Ariva's scripts ship, as name:SHA-256 of the text between its dollar quotes (what PostgreSQL
# stores as its source). OpsScriptTests holds this list equal to every CREATE FUNCTION of Ariva.Infra/Timescale/Scripts, the
# bodies a later script replaced included, since a backup taken before that script holds the older one. check reports
# any other function outside the extensions, in any schema, and one of these names with another body.
readonly ARIVA_FUNCTIONS="advertising_media_guard:0ab8da7a9d52b3ddaa476898c7585a7b9d0ce0dceec2c7daf34cb25f4dfd164d,\
advertising_media_guard:2b454da6258f0a91d18a87b3978039af67d57f6475cffe9a5d6c607581a632e3,\
advertising_playlist_guard:2e0ba89e6033c9331e127d4556458e0f0fb149a54ad0d81070b5ad732b440a60,\
advertising_policy_stamped:e18c313379de767e5501bc79f0a4db278fa57ecc3ef421f45d3d0b66d166f657,\
advertising_schedule_guard:4db8de8d13bee5fdb96ff795f78e5f1c3777c11d21ff179ef4cc21aeb28e6652,\
alert_keep_raise:52af781790c93f2eab47e7cc2b99b6e592da013f16804c725a0dc1b20b5f4805,\
alert_keep_raise:7da479242dafa3d0ffae451c6f31cd9e9301a2be48720b166dc10ef985dc4190,\
alert_rule_identity:b1bbde2c3162fc34a948fb06dca87e4334b5dcec9c474a7f387f52dfdf9efee1,\
ariva_data_protection_key_purge:9d886614f2343b230d50749c2f10b04df075e698c8758c163f80ca9ff15d975b,\
ariva_ensure_development_login:75fb405a2a74092112033e88ffa56a8cdcf5a0123c3bcde5621e050df6af49a6,\
ariva_ensure_host_login:80ca058fb83a64f7a7bcaeb9c8647ad376e2e332ecac3a67a624eb693ccfe107,\
ariva_ensure_runtime_login:6864027ae91766a330f29144c95cdaafbc7d7d277c67b93f1e0fd38587aadd03,\
ariva_ensure_validation_reader_login:80844ccdbfee88483d1b2b8f3eb810b19c9886998d9ab90174ebfcaef9317acb,\
ariva_retention_purge:535ad38b539587c302aa4ea85e1c3508d55e5d5df6bf4f0675bbadf0076f78d7,\
ariva_set_runtime_session_limits:c61f9b5ed6c6352f1bd8d8c83fe0f608d86ba942e25be86b0fcc174683ee5023,\
ariva_set_runtime_statement_timeout:5a0d58d71b054d30960d5d9be54b37eca1f2c381bb2d46ffe1eaf580b762f3e7,\
ariva_set_runtime_temp_file_limit:9ddecaa76797da4a42f181d899ba813a4e7f62e8a9c71b4573619f3cd5e9d259,\
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

# ARV-189: the runtime role's privileges on every relation of public, with its column grants, as
# <relation>=<privileges>[,<privilege>(<column> ...)] entries separated by ";"; a relation the list does not name grants it
# nothing. Since ARV-083a (script 0074) ariva_runtime is the base every host login holds, and ARIVA_HOST_PRIVILEGES below
# holds each host role's own grants, as <role>.<relation>=<privileges> entries: both are Ariva.Infra's HostDatabaseAccess
# manifest (OpsScriptTests holds them equal to it, and HostDatabaseAccessTests holds a migrated database to it). check compares
# them with the database once it is at ARIVA_RUNTIME_PRIVILEGES_AT or later (before that the migration job has not applied the
# scripts the lists reflect), so a restore that lost a revoke, or gained a grant, is reported. OpsScriptTests refuses a later
# script that grants to or revokes from ariva_runtime or a host role, or creates a view, until these lists (or ARIVA_VIEWS) and
# their script move with it.
readonly ARIVA_RUNTIME_PRIVILEGES_AT="0074_host_database_roles.sql"
readonly ARIVA_RUNTIME_PRIVILEGES="data_protection_key=SELECT,INSERT;\
database_environment=SELECT;\
outbox_message=SELECT,INSERT,UPDATE,DELETE;\
processed_event=SELECT,DELETE;\
schema_version=SELECT;\
user=SELECT(all_sites created_by created_by_id created_on display_name email failed_login_count id is_break_glass is_disabled last_login_on locked_until modified_by modified_by_id modified_on must_change_password totp_enrolled totp_last_step user_name);\
user_role=SELECT;\
user_session=SELECT;\
user_site=SELECT"
readonly ARIVA_HOST_ROLES="ariva_main,ariva_ingest,ariva_stream,ariva_cronz,ariva_integration,ariva_availability"
readonly ARIVA_HOST_PRIVILEGES="ariva_main.advertising_media=SELECT,INSERT,UPDATE;\
ariva_main.advertising_play=SELECT,INSERT;\
ariva_main.advertising_playlist=SELECT,INSERT,UPDATE;\
ariva_main.advertising_policy=SELECT,INSERT,UPDATE;\
ariva_main.advertising_schedule=SELECT,INSERT,UPDATE;\
ariva_main.airport=SELECT,INSERT,UPDATE;\
ariva_main.alert=SELECT,UPDATE;\
ariva_main.alert_rule=SELECT,INSERT,UPDATE;\
ariva_main.audit_entry=SELECT,INSERT;\
ariva_main.availability_minute=SELECT;\
ariva_main.border_desk_interval=SELECT;\
ariva_main.border_desk_session=SELECT;\
ariva_main.border_egate_interval=SELECT;\
ariva_main.checkpoint=SELECT,INSERT,UPDATE;\
ariva_main.counter_allocation=SELECT;\
ariva_main.desk=SELECT,INSERT,UPDATE;\
ariva_main.desk_code_mapping=SELECT,INSERT,UPDATE;\
ariva_main.desk_minute=SELECT;\
ariva_main.desk_observation=SELECT,INSERT;\
ariva_main.desk_observation_batch=SELECT,INSERT;\
ariva_main.device=SELECT,INSERT,UPDATE;\
ariva_main.device_calibration=SELECT,INSERT;\
ariva_main.device_heartbeat=SELECT,INSERT,UPDATE;\
ariva_main.display=SELECT,INSERT,UPDATE;\
ariva_main.email_message=SELECT,INSERT;\
ariva_main.feed_freshness=SELECT,INSERT,UPDATE;\
ariva_main.flight_leg=SELECT,INSERT,UPDATE;\
ariva_main.floor_plan=SELECT,INSERT,UPDATE;\
ariva_main.inbound_lane_demand=SELECT;\
ariva_main.instruction_slide=SELECT,INSERT,UPDATE;\
ariva_main.integration_client=SELECT,INSERT,UPDATE;\
ariva_main.lane_staffing=SELECT;\
ariva_main.level=SELECT,INSERT,UPDATE;\
ariva_main.line=SELECT,INSERT,UPDATE,DELETE;\
ariva_main.line_minute=SELECT;\
ariva_main.maintenance_window=SELECT,INSERT,UPDATE;\
ariva_main.manual_count=SELECT,INSERT;\
ariva_main.operating_day_exception=SELECT,INSERT,UPDATE;\
ariva_main.operating_week=SELECT,INSERT,UPDATE;\
ariva_main.outbound_endpoint=SELECT,INSERT,UPDATE;\
ariva_main.overflow_minute=SELECT;\
ariva_main.processed_event=INSERT;\
ariva_main.queue_bin=SELECT;\
ariva_main.queue_minute=SELECT;\
ariva_main.recovery_code=SELECT,INSERT,UPDATE;\
ariva_main.refresh_token=SELECT,INSERT,UPDATE;\
ariva_main.report_delivery=SELECT;\
ariva_main.report_schedule=SELECT,INSERT,UPDATE;\
ariva_main.report_schedule_recipient=SELECT,INSERT,DELETE;\
ariva_main.site=SELECT,INSERT,UPDATE(modified_by modified_by_id modified_on name);\
ariva_main.terminal=SELECT,INSERT,UPDATE;\
ariva_main.tracer_batch=SELECT,INSERT;\
ariva_main.tracer_run=SELECT,INSERT;\
ariva_main.user=SELECT,INSERT,UPDATE;\
ariva_main.user_role=INSERT,DELETE;\
ariva_main.user_session=INSERT,UPDATE;\
ariva_main.user_site=INSERT,DELETE;\
ariva_main.validation_campaign=SELECT,INSERT,UPDATE(closed_by_id closed_utc started_by_id started_utc status);\
ariva_main.validation_campaign_desk=SELECT,INSERT;\
ariva_main.validation_campaign_line=SELECT,INSERT;\
ariva_main.validation_campaign_zone=SELECT,INSERT;\
ariva_main.validation_result_revision=SELECT,INSERT;\
ariva_main.zone=SELECT,INSERT,UPDATE,DELETE;\
ariva_main.zone_health=SELECT,INSERT,UPDATE;\
ariva_main.zone_health_bin=SELECT;\
ariva_main.zone_outage=SELECT;\
ariva_main.zone_profile=SELECT,INSERT,UPDATE,DELETE;\
ariva_ingest.airport=SELECT;\
ariva_ingest.device=SELECT;\
ariva_ingest.level=SELECT;\
ariva_ingest.line=SELECT;\
ariva_ingest.terminal=SELECT;\
ariva_ingest.zone=SELECT;\
ariva_ingest.zone_profile=SELECT;\
ariva_stream.alert=SELECT,INSERT,UPDATE;\
ariva_stream.alert_rule=SELECT;\
ariva_stream.alert_rule_state=SELECT,INSERT,UPDATE,DELETE;\
ariva_stream.audit_entry=INSERT;\
ariva_stream.border_desk_interval=SELECT;\
ariva_stream.border_desk_session=SELECT;\
ariva_stream.border_egate_interval=SELECT;\
ariva_stream.checkpoint=SELECT;\
ariva_stream.desk=SELECT;\
ariva_stream.desk_code_mapping=SELECT;\
ariva_stream.desk_feed_state=SELECT,INSERT,UPDATE;\
ariva_stream.desk_minute=SELECT,INSERT,UPDATE;\
ariva_stream.desk_sensor_minute=SELECT,INSERT,UPDATE;\
ariva_stream.desk_zone_reading=SELECT,INSERT;\
ariva_stream.device=SELECT;\
ariva_stream.device_health_event=SELECT,INSERT;\
ariva_stream.device_heartbeat=SELECT;\
ariva_stream.egate_minute=SELECT,INSERT,UPDATE;\
ariva_stream.email_message=SELECT,INSERT;\
ariva_stream.flight_leg=SELECT;\
ariva_stream.inbound_lane_demand=SELECT;\
ariva_stream.lane_staffing=SELECT;\
ariva_stream.line=SELECT;\
ariva_stream.line_minute=SELECT,INSERT,UPDATE;\
ariva_stream.overflow_minute=SELECT,INSERT,UPDATE;\
ariva_stream.processed_event=INSERT;\
ariva_stream.queue_bin=SELECT,INSERT,UPDATE;\
ariva_stream.queue_minute=SELECT,INSERT,UPDATE;\
ariva_stream.queue_minute_shadow=INSERT,SELECT(minute_utc zone_key),UPDATE(no_service nowcast_degraded nowcast_minutes sensor_cycle_minutes updated_on);\
ariva_stream.replay_run=SELECT,INSERT;\
ariva_stream.sensing_batch=SELECT,INSERT;\
ariva_stream.sensing_day_key=SELECT,INSERT,DELETE;\
ariva_stream.sensing_event=SELECT,INSERT;\
ariva_stream.stream_offset=SELECT,INSERT,UPDATE;\
ariva_stream.stream_partition_count=SELECT,INSERT;\
ariva_stream.stream_zone_state=SELECT,INSERT,UPDATE;\
ariva_stream.zone=SELECT;\
ariva_stream.zone_health_bin=SELECT,INSERT,UPDATE;\
ariva_stream.zone_outage=SELECT,INSERT,UPDATE(closed recorded_on to_utc);\
ariva_stream.zone_profile=SELECT;\
ariva_cronz.airport=SELECT;\
ariva_cronz.alert=SELECT;\
ariva_cronz.border_egate_interval=SELECT;\
ariva_cronz.checkpoint=SELECT;\
ariva_cronz.desk=SELECT;\
ariva_cronz.desk_minute=SELECT;\
ariva_cronz.device=SELECT;\
ariva_cronz.device_heartbeat=SELECT;\
ariva_cronz.display=SELECT(credential_hash credential_issued_on deleted_on);\
ariva_cronz.flight_leg=SELECT;\
ariva_cronz.inbound_lane_demand=SELECT;\
ariva_cronz.integration_client=SELECT(id secret_changed_utc status totp_changed_utc totp_secret_protected),UPDATE(totp_secret_protected);\
ariva_cronz.lane_staffing=SELECT,INSERT;\
ariva_cronz.outbound_endpoint=SELECT(id secret_changed_utc secret_protected status),UPDATE(secret_protected);\
ariva_cronz.queue_minute=SELECT;\
ariva_cronz.report_delivery=SELECT,INSERT,UPDATE(attempts reason sent_utc status);\
ariva_cronz.report_schedule=SELECT;\
ariva_cronz.report_schedule_recipient=SELECT;\
ariva_cronz.retention_period=SELECT;\
ariva_cronz.site=SELECT;\
ariva_cronz.terminal=SELECT;\
ariva_cronz.user=SELECT(totp_secret_protected),UPDATE(totp_secret_protected);\
ariva_cronz.zone=SELECT;\
ariva_cronz.zone_outage=SELECT;\
ariva_cronz.zone_profile=SELECT;\
ariva_integration.airport=SELECT;\
ariva_integration.alert=SELECT;\
ariva_integration.aman_pull_cursor=SELECT,INSERT,UPDATE;\
ariva_integration.audit_entry=INSERT;\
ariva_integration.border_desk_interval=SELECT,INSERT;\
ariva_integration.border_desk_session=SELECT,INSERT;\
ariva_integration.border_egate_interval=SELECT,INSERT;\
ariva_integration.checkpoint=SELECT;\
ariva_integration.counter_allocation=SELECT,INSERT,UPDATE;\
ariva_integration.desk=SELECT;\
ariva_integration.desk_code_mapping=SELECT;\
ariva_integration.email_message=SELECT,UPDATE(attempts next_attempt_utc reason sent_utc status);\
ariva_integration.feed_freshness=SELECT,INSERT,UPDATE;\
ariva_integration.flight_event=INSERT;\
ariva_integration.flight_leg=SELECT,INSERT,UPDATE;\
ariva_integration.inbound_lane_demand=SELECT,INSERT,UPDATE;\
ariva_integration.integration_call=INSERT;\
ariva_integration.integration_client=SELECT,UPDATE(attempt_window_utc attempts_in_window failed_attempts last_token_utc locked_until_utc totp_last_step);\
ariva_integration.integration_idempotency=SELECT,INSERT,DELETE,UPDATE(response_body status_code);\
ariva_integration.outbound_endpoint=SELECT,UPDATE(consecutive_failures last_modified last_poll_utc last_status);\
ariva_integration.processed_event=INSERT;\
ariva_integration.site=SELECT;\
ariva_integration.terminal=SELECT;\
ariva_availability.airport=SELECT;\
ariva_availability.availability_cursor=SELECT,INSERT,UPDATE;\
ariva_availability.availability_minute=SELECT,INSERT;\
ariva_availability.maintenance_window=SELECT;\
ariva_availability.operating_day_exception=SELECT;\
ariva_availability.operating_week=SELECT;\
ariva_availability.queue_minute=SELECT;\
ariva_availability.terminal=SELECT;\
ariva_availability.zone=SELECT;\
ariva_availability.zone_profile=SELECT"

# ARV-290 review: the bounds the hosts and the migration job hold the runtime login's caps to (DatabaseSettings'
# MinStatementTimeoutSeconds to MaxStatementTimeoutSeconds and MinTempFileLimitMegabytes to MaxTempFileLimitMegabytes;
# OpsScriptTests holds them equal), so check's "runtime caps" fails on a value the migration job would refuse, not only on
# one it never writes.
readonly RUNTIME_TIMEOUT_MIN_SECONDS=301
readonly RUNTIME_TIMEOUT_MAX_SECONDS=3600
readonly RUNTIME_TEMP_FILE_MIN_MEGABYTES=64
readonly RUNTIME_TEMP_FILE_MAX_MEGABYTES=102400
# ARV-292: and its idle transaction limit (MinIdleInTransactionTimeoutSeconds to MaxIdleInTransactionTimeoutSeconds) and TCP
# keepalives, which the migration job sets to DatabaseSettings' fixed values (KeepaliveIdleSeconds, KeepaliveIntervalSeconds,
# KeepaliveCount); OpsScriptTests holds these equal too.
readonly RUNTIME_IDLE_MIN_SECONDS=300
readonly RUNTIME_IDLE_MAX_SECONDS=7200
readonly RUNTIME_KEEPALIVE_IDLE_SECONDS=60
readonly RUNTIME_KEEPALIVE_INTERVAL_SECONDS=10
readonly RUNTIME_KEEPALIVE_COUNT=6

# ARV-263: the retention period each hypertable's TimescaleDB retention policy may have, in days, as <hypertable>=<min>-<max>
# entries separated by ";": the bounds RetentionSettings holds each configured period to (the migration job refuses any
# other; OpsScriptTests holds these equal). A retention job's drop_after is pinned as "within the bounds" (each deployment
# configures its own period), so schema's hash of the job stays the same, and a period outside them is reported as a
# difference.
readonly ARIVA_RETENTION_DAYS="advertising_play=30-3650;desk_zone_reading=2-90;device_health_event=7-3650;lane_staffing=90-3650;\
sensing_batch=7-3650;sensing_event=7-3650;zone_outage=7-3650"

# ARV-189 review: the views of public as the scripts create them, as <view>=<what it reads> entries separated by ";": a
# continuous aggregate its hypertable, a plain view the relations it names (separated by spaces). check compares them, as
# it does the privileges, once the database is at ARIVA_RUNTIME_PRIVILEGES_AT: a view a forged dump adds takes the default
# DML of script 0001 and reads with its owner's rights. OpsScriptTests holds the names equal to the scripts' views.
readonly ARIVA_VIEWS="line_minute_15m=line_minute;overflow_bin_15m=overflow_minute;queue_minute_15m=queue_minute"

# ARV-262 (CWE-269, CWE-345): every object of Ariva's in public as a database the scripts migrated holds it, as
# <object>=<hash> entries separated by ";" (schema_report says what each hash covers: a relation's columns, constraints,
# indexes, triggers and grants, a function's definition and grants). check compares them once the database is at
# ARIVA_SCHEMA_AT, the latest script (any script may change them), so a trigger, constraint, grant or function a forged dump
# left out, changed or added is reported even where no other line looks. OpsScriptTests holds ARIVA_SCHEMA_AT at the
# latest script; BackupRestoreTests holds the list equal to a database the release's migration job migrated (and prints it).
readonly ARIVA_SCHEMA_AT="0074_host_database_roles.sql"
readonly ARIVA_SCHEMA="advertising_media=efa1f55d8551acc26956c6a23987ee5c;\
advertising_media_guard()=0c14e39356e12f186a82b88895cc130a;\
advertising_play=273d01cb52fbcaca594c2bce19e6edf0;\
advertising_playlist=b5b78c8539b41c0ecdf69e8db1776d7d;\
advertising_playlist_guard()=633d55f8a6c27f13b7b7a1e94d2b9da1;\
advertising_policy=511c705797e5bbda07ac44ef1c8fade6;\
advertising_policy_stamped()=5c0aa1c10b5c5c2a24037be6464daf6e;\
advertising_schedule=85b92a00c2798b05b848c0779b2340fa;\
advertising_schedule_guard()=bf75f116d53a7bf6e874609ff14775f4;\
airport=3521f76004e77098a8b58c31bb863824;\
alert=76dde94442cb22504b9ec8cfe8e9e34b;\
alert_keep_raise()=85dd9b3f8edd77b7ada96d91567edd5e;\
alert_rule=69ab50417eb542cdefbf9be442a8330a;\
alert_rule_identity()=895b6cc23a25603fadff715c72460978;\
alert_rule_state=944aadd8e543d6f301de7fdabbf9755b;\
aman_pull_cursor=ea251994d68bde67af782d8d6f314f4d;\
ariva_data_protection_key_purge(p_after_days integer, p_limit integer)=9b8f72595b8d5b84e4d087f366b487c5;\
ariva_ensure_development_login(login_name text, login_verifier text)=fec2f431c1d59d2ab46a22b5cbabb0db;\
ariva_ensure_host_login(login_name text, login_verifier text, host_role text)=fa60aa4b4cc12da4d0f67602044fa2b8;\
ariva_ensure_validation_reader_login(login_name text, login_verifier text)=a933282b920424704f9a04912201735e;\
ariva_retention_purge(p_data_set text, p_limit integer)=667cc4b997e5dde63f94f3e048f110e7;\
ariva_set_runtime_session_limits(login_name text, idle_in_transaction_seconds integer, keepalive_idle_seconds integer, keepalive_interval_seconds integer, keepalive_count integer)=42767b94e5f2e71b2eb0ebc49a7ff9a0;\
ariva_set_runtime_statement_timeout(login_name text, timeout_seconds integer)=9e7119ef6c672f24c7f58833abdac5f9;\
ariva_set_runtime_temp_file_limit(login_name text, limit_megabytes integer)=7bd37ec1510e5736eb101d351a794abc;\
audit_entry=acaaeb5d0eba12ebc9f117edcc99f31e;\
availability_cursor=f1b3496fc30991bbfc8d6e5ee0359177;\
availability_minute=5046c63c93132ea430e4a50b7b88e4a9;\
border_desk_interval=c608272e0e38abf35832e50a71d64e87;\
border_desk_session=fb919bd0c662d17d12feb1d06ff6e12e;\
border_egate_interval=20990bf3303bde380dcd2d2b0b6fef6c;\
checkpoint=14c06b1c6bf1b395ef7016df49e1ef04;\
counter_allocation=3c80f5ba079259bba3c9472bc00f331c;\
data_protection_key=1bdb6d157bcbf9c56ef37a12b00b98cf;\
database=b90a9d25f2cafa4afcdf86348a4884ea;\
database_environment=e52adb8c3538e5988c62134485224f03;\
desk=79f14d06be46a13b55d94fe4fcc76970;\
desk_code_mapping=638faafef308c8c20acca131929f3d54;\
desk_feed_state=1f4947f9f11cc25da175971112ca1e60;\
desk_minute=c2a0c2faf71c5845cdb9ab41295aad61;\
desk_observation=c262e94305a41a99eed9b9670937267d;\
desk_observation_batch=41717c0ac0c736b78df672e035634873;\
desk_observation_insert_check()=825ab6784dec0fbdbc3538ef0a990d2f;\
desk_sensor_minute=483da35db29b2a8de7f9a7cedf9cfd6c;\
desk_zone_reading=b4b1bc8a4420ce49f3ca362cdddc3165;\
device=2807c34022015792d0ac0b8b58ccb9fb;\
device_calibration=a60e63b9883da3bc75711a6008f8f646;\
device_calibration_immutable()=898d61ed2d4d8bed0d1c463c86f4abe9;\
device_health_event=098a47a36118c584296f0e3126dff26a;\
device_heartbeat=40043b90d1ffec64e8d8393685e7d642;\
display=2dbdcac9c50f0eeb35d167dac3afbc8e;\
display_advertising_allowed()=5420d3348838bb6c035cad53f5684d8c;\
egate_minute=56950de3a3438f05b4e71cd1408dbe38;\
email_message=b9ee18c9a3284939336685e716471e84;\
email_message_keep()=d91278075b584d4e3e4b5905e6770ee6;\
feed_freshness=7103bec594ffca8f14b6e7e132734af7;\
flight_event=7998f95b435f5152fa1a00a2e1729834;\
flight_leg=8424fca173691ec7c6ae8fb88e172092;\
floor_plan=b3410bfd693f141f44f483edf3b6299c;\
inbound_lane_demand=7d127dd67d10fe2b5fae06d6a7d26415;\
instruction_slide=0bb488ce13fe92568cc889e07a0dc6ef;\
integration_call=66db811725f2becb638d9e7e47218a5e;\
integration_client=32a34d2592c2a70c2191d52d3b88a5a9;\
integration_idempotency=de521b2fd8d56190dfe78d6ab1ac6a95;\
integration_idempotency_keep()=b3b527e2df0c9358c30ac3e1d26d786f;\
job _timescaledb_functions.policy_compression on public.device_health_event=89b38544bfc656b1c3fd4271615bc972;\
job _timescaledb_functions.policy_compression on public.sensing_event=ae0ad0fbd8f7bfca70da5aa0f18de182;\
job _timescaledb_functions.policy_job_stat_history_retention=f9f6e8a98aae7855fc8b2fc6d0d11712;\
job _timescaledb_functions.policy_refresh_continuous_aggregate on public.line_minute_15m=818f3058b83c76b8289b1a356e442f4b;\
job _timescaledb_functions.policy_refresh_continuous_aggregate on public.queue_minute_15m=52094f391c0352044bda48078e0e673e;\
job _timescaledb_functions.policy_retention on public.advertising_play=ad58ef1bb606639f073579bc3076248f;\
job _timescaledb_functions.policy_retention on public.desk_zone_reading=e16c4e0e02e3a2ec237efd63912e1b14;\
job _timescaledb_functions.policy_retention on public.device_health_event=7295264071aa07fd1c00c5ef47162393;\
job _timescaledb_functions.policy_retention on public.lane_staffing=41130943d66f17fe5d1af29c6f7b230d;\
job _timescaledb_functions.policy_retention on public.sensing_batch=41989bc3ae244f955bab1b04f099690e;\
job _timescaledb_functions.policy_retention on public.sensing_event=13fc8b41922715a182ff842c2988f340;\
job _timescaledb_functions.policy_retention on public.zone_outage=8ecdc4df16b85f013fce50266318a530;\
job _timescaledb_functions.policy_telemetry=391d9deb8604ef06497fe1c4d49a4936;\
lane_staffing=1aef2e44c206eceae0f92dc04c497817;\
level=dad6a94db0e22b8c3d9b251fc5d6134e;\
line=99dc21870cb5df369c45f4e2fa02b431;\
line_minute=adbf08c9afc67211330485b13ad0f684;\
line_minute_15m=dd892980cfe6e5544461034eb00be317;\
maintenance_window=63bb5572083b570937341557340e85a9;\
manual_count=63c4065743f9c5c874443006b5bd5898;\
manual_count_insert_check()=440139ea9b09c91a26a5d0d497faae00;\
operating_day_exception=990ba3243eeaaefbd32c9d07d8c3c8bd;\
operating_week=bc180eefb3cb9eede700f550e3d67bfd;\
outbound_endpoint=b7c0f4fe02360628e854d555c9e7a0a8;\
outbox_message=bbd7564a3197af6eab375a3b02976a3f;\
outbox_message_seq_seq=d7d6baf610a6890e7fa06eb6c3be767d;\
overflow_bin_15m=0631512be8adc43aa49d188fe1e0aa95;\
overflow_minute=c8353984cc798b6ab5bdb76d34ecb2fb;\
processed_event=f3063145e7c18f51a1bef9f029c37131;\
queue_bin=aedc281a35db1300b3a77cf4a4f981d5;\
queue_bin_final_is_immutable()=a8f0a0b610e7430cc97d338b164571aa;\
queue_minute=8656bce695c7e60f6f7ebf43074d648e;\
queue_minute_15m=45ffc689720d520b2212d0ab423e424c;\
queue_minute_shadow=4ad28954f236a1752692d7fc31b61c86;\
recovery_code=16db38569318de154b9898a5d46fc4c6;\
refresh_token=ddc5d0b811dd08a8debc854fee998666;\
replay_run=32b01e00dc61157c8078d77e388f6809;\
replay_run_anchor()=0787fcfcc3f351eb37e3f469e36ffbd5;\
report_delivery=0d73a2521cf718247e06069928a104da;\
report_schedule=21b60b30c0a3c266b062d25ff4020c8a;\
report_schedule_recipient=8b2e385ae67ea0e14c35146dc4544432;\
retention_period=0c560d7fa806c4b99a1e0cad33cfa30c;\
schema _timescaledb_cache=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_catalog=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_config=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_functions=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_internal=01b9f62992baa1842166fe29ac004a7a;\
schema information_schema=01b9f62992baa1842166fe29ac004a7a;\
schema pg_catalog=01b9f62992baa1842166fe29ac004a7a;\
schema pg_toast=b674f4d680283437e56727831c38bbf3;\
schema public=c5502e0e18b2371527559f5bff156f6c;\
schema timescaledb_experimental=01b9f62992baa1842166fe29ac004a7a;\
schema timescaledb_information=01b9f62992baa1842166fe29ac004a7a;\
schema toolkit_experimental=01b9f62992baa1842166fe29ac004a7a;\
schema_version=b28ff4f835f1e99b965b23ee311bb0af;\
sensing_batch=f1e4c09c6e7c6c5f5a2743c771d2c150;\
sensing_day_key=3ec7b4c4b245a21310efe1c81d3a9fb6;\
sensing_event=1b1485b9700d98ad44bcfd8a710ec7b5;\
site=86c6a3119775eb5d7cf3a1048667d126;\
stream_offset=68101bd0fcc12f775ad93d87ba65d708;\
stream_partition_count=a1e51296a4b5fc1eb39cabff942af976;\
stream_zone_state=300ca1c9c2191dad9af1c7a9d7af9bde;\
terminal=8f8e8c7629ab5c892f9ba80fdb7f7c98;\
tracer_batch=a05e1e9bb968eb60f8b90a2009a22be7;\
tracer_run=9ed548503963e020a1312d128c36e176;\
user=9e7fb747afed0755779eccae5e3fb72d;\
user_role=63490d89117cf7065a3b458cda569d8f;\
user_session=a13d591e150e76f223c7b5ee2419ddce;\
user_site=be6e3d64c45b3195628591e139a1b134;\
validation_campaign=9cdf5378be1c436c2776c148a76aeae4;\
validation_campaign_desk=a0e90d385f03a957ba2b6d20790b04cf;\
validation_campaign_desk_check()=104ea7afcc154308d02495c5c62a6cb3;\
validation_campaign_insert_check()=54d97900461e4a0900d00a9e1b2ca38c;\
validation_campaign_line=915d35d5da0524d8fc6272a948bfa837;\
validation_campaign_line_check()=660ada511c8ea7f4436835940bb6348f;\
validation_campaign_transition()=1b4cd420c3805b5b6125015cd8456ff3;\
validation_campaign_zone=ff07d1c941d3fe75efd2f5a1cc2932df;\
validation_campaign_zone_check()=6ff7893065cd5560d80c53b1b8d44af5;\
validation_capture_insert_check()=75dff80d9f1c8e8e64d54c9f222fb0d1;\
validation_result_revision=025fe05d2c0d8f1bd715bfc1619a62de;\
validation_result_revision_insert_check()=bcdfa3eee48504be818dddb7738ef6fb;\
validation_row_immutable()=e77b1c7f83acb7761ff4cd2ff8877653;\
zone=8a845285dd6ad59a486cf1a9f214ae7e;\
zone_health=14e685bdcd32c9c80d6f45131ed2d03f;\
zone_health_bin=fd04ffafc63f78568fee780f590f2a76;\
zone_health_bin_final_is_immutable()=f6f65edacdd24324741915722bdcfa7b;\
zone_outage=c98b2173c06d9021128510126dff48ee;\
zone_profile=dae06e9239058daa7be2d6a256e81f27;\
zone_profile_geometry_immutable()=6328ab8391d54f21a060ba94d04bd158;\
zone_profile_immutable()=ffc3bff3f5370bf70f8ae9d8c0702f41"

# ARV-262: the members of TimescaleDB's extensions as the image ships them, as <extension> <version>=<SHA-256> entries
# separated by ";" (extension_members' digest). An object a forged dump adds to an extension, or one it replaces in place,
# changes it. BackupRestoreTests holds it equal to the pinned image's; a new image moves it.
readonly ARIVA_EXTENSION_MEMBERS="timescaledb 2.31.0=f98feb4c3760ffd0239707ee37978915e8a4d82be2ce78a370a61a0c137e0b12;timescaledb_toolkit 1.26.0=f952f9b5b44c9746652ee0d4ced8b00c4002198e8afb9b27f5c41eda6a5ed52a"

# ARV-262: the system catalog's own objects (system_catalog's rows, sorted, as a SHA-256 taken here, outside the database)
# per PostgreSQL major version, as <major>=<SHA-256> entries separated by ";". template1 stays the reference for naming what
# differs, but no longer the one that decides: a forged dump run by the superuser could change template1 as well.
# BackupRestoreTests holds it equal to the pinned image's. What these pins prove, and what not: check finds what a careless
# forged dump leaves; it cannot prove a careful one absent, since the superuser that runs the dump can also change what
# check reads (a catalog row under allow_system_table_mods, a library written to the server's disk, the server's own
# configuration files); the other digests are computed by the database's functions, which this one vouches for only as
# far as that. The signed manifest stays the only gate. The digest assumes a cluster initialised by this image's
# PostgreSQL and settings that leave the printed text alone (extra_float_digits changes how a cost prints): either
# mismatch fails, never passes.
readonly ARIVA_SYSTEM_FUNCTIONS="17=a077d5c8f23e6197ffa3ba07f2e8cd7931441f7fe639efe6ccde08b97bb5659f"

# Set by restore only: a database behind ARIVA_RUNTIME_PRIVILEGES_AT is then the expected state (the migration job follows),
# not a failure. check sets check_not_compared when it reported such a database, so that restore's last line says what to do.
check_after_restore=0
check_not_compared=0
# ARV-244 review: set while a restore checks the restored database before the extensions' update (cmd_restore).
check_before_update=0

# The migration password stays in this shell; children get it only as PGPASSWORD of the migration login's connections.
MIGRATION_PASSWORD="${ARIVA_MIGRATION_PASSWORD:-}"
export -n ARIVA_MIGRATION_PASSWORD 2>/dev/null || true
unset ARIVA_MIGRATION_PASSWORD POSTGRES_PASSWORD PGPASSWORD

DATABASE="${POSTGRES_DB:-ariva}"
LOGIN="${ARIVA_MIGRATION_LOGIN:-}"
SUPERUSER="${POSTGRES_USER:-postgres}"
HOST=127.0.0.1
PORT=5432
SOCKET_DIR=/var/run/postgresql

say() { printf 'ariva-db: %s\n' "$*" >&2; }
die() { say "error: $*"; exit 1; }
usage() { say "usage: ariva-db.sh <backup|manifest|check-dump|restore|check|schema|keyring|after-kafka-loss|update-extensions|grant-settings> [options]; see the header of this file"; exit 2; }

valid_identifier() { [[ "$1" =~ ^[a-z_][a-z0-9_]{0,62}$ ]]; }
valid_file() { [[ "$1" == "-" ]] || { [[ "$1" =~ ^/[A-Za-z0-9._/-]{1,255}$ ]] && [[ "$1" != *..* ]]; }; }
valid_sha256() { [[ "$1" =~ ^[0-9A-Fa-f]{64}$ ]]; }
valid_setting() { [[ "$1" =~ ^[a-z_][a-z0-9_]{0,62}(\.[a-z_][a-z0-9_]{0,62})?$ ]]; }
# ARV-244: a TimescaleDB version as its releases are named, a plain dotted number (two to four parts), nothing else.
valid_version() { [[ "$1" =~ ^[0-9]{1,6}(\.[0-9]{1,6}){1,3}$ ]]; }

check_settings() {
  valid_identifier "$DATABASE" || die "the database name is not a plain lower-case identifier"
  valid_identifier "$LOGIN" || die "set ARIVA_MIGRATION_LOGIN (or --login) to the migration login, a plain lower-case identifier"
  valid_identifier "$SUPERUSER" || die "the superuser name is not a plain lower-case identifier"
  [[ "$HOST" =~ ^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?$ ]] || die "the host is not a lower-case host name or IPv4 address"
  [[ "$PORT" =~ ^[0-9]{1,5}$ ]] || die "the port is not a number"
  [[ "$SOCKET_DIR" =~ ^/[A-Za-z0-9._/-]{1,200}$ && "$SOCKET_DIR" != *..* ]] || die "the socket directory is not a plain absolute path"
  [[ -n "$MIGRATION_PASSWORD" ]] || die "ARIVA_MIGRATION_PASSWORD is not set (the TimescaleDB pod has it from the timescaledb-credentials secret)"
}

# psql as the migration login over TCP; SQL comes on stdin, values as psql variables. ON_DATABASE names another database of
# the server for one call (a constant: check reads template1's system functions, ARV-183).
as_migration() {
  PGPASSWORD="$MIGRATION_PASSWORD" PGAPPNAME=ariva-ops PGCONNECT_TIMEOUT=15 psql --no-psqlrc --quiet --no-password \
    --set=ON_ERROR_STOP=1 --host="$HOST" --port="$PORT" --username="$LOGIN" --dbname="${ON_DATABASE:-$DATABASE}" "$@"
}

# psql as the database superuser over the local socket (peer authentication; the restore's TimescaleDB steps, the
# extensions' update, check's read of the ALTER SYSTEM settings and grant-settings' grant only: OpsScriptTests holds every
# call to one of eight fixed statements, the only psql variable among them the restore's validated TimescaleDB version,
# ARV-244). The search path is the system catalog's for the whole session, a startup option that wins over any
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
)" || die "cannot sign in as the migration login $LOGIN to database $DATABASE over $HOST"
  [[ "$row" == "f|t|f|f" ]] || die "the migration login must own database $DATABASE and be neither superuser, replication nor bypassrls (found superuser|owner|replication|bypassrls = $row)"
}

# Members of Ariva's group roles other than the migration login and the named runtime and reader logins (since ARV-083a the
# host roles' too, so a server whose host roles already have members is refused as one whose runtime role has).
extra_members() {
  as_migration_catalog --tuples-only --no-align --set=runtime="$1" --set=reader="$2" <<'SQL'
SELECT coalesce(string_agg(g.rolname || ' has member ' || r.rolname, ', ' ORDER BY g.rolname, r.rolname), '')
  FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles r ON r.oid = m.member
 WHERE g.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability') AND r.rolname <> current_user
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
  local args=(--format=custom --no-password --host="$HOST" --port="$PORT" --username="$LOGIN" --dbname="$DATABASE")
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
        schema="${rest#"$kind" }"; name="${schema#* }"; schema="${schema%% *}"; name="${name%% *}"
        if [[ "$schema" == _timescaledb_catalog ]]; then
          ext=0
          for e in "${TIMESCALE_CATALOG_DATA[@]}"; do [[ "$name" == "$e" ]] && ext=1; done
          ((ext == 1)) || { say "refused: data of $schema.$name, which a dump of TimescaleDB does not carry"; problems=$((problems + 1)); }
          continue
        fi
        if [[ "$schema" == _timescaledb_config || "$schema" == _timescaledb_cache || "$schema" == _timescaledb_functions ]]; then
          say "refused: data of $schema.$name, which a dump of TimescaleDB does not carry"; problems=$((problems + 1)); continue
        fi
        if [[ "$schema" == _timescaledb_internal ]] \
          && ! [[ "$kind" == "TABLE DATA" && "$name" =~ ^(_hyper_[0-9]+_[0-9]+_chunk(_compressed)?|compress_hyper_[0-9]+_[0-9]+_chunk|_materialized_hypertable_[0-9]+|_compressed_hypertable_[0-9]+)$ ]]; then
          say "refused: data of $schema.$name, which is not a chunk or a hypertable of TimescaleDB's"; problems=$((problems + 1)); continue
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

# ARV-244: a dump's table of contents (DUMP_TOC) and the TimescaleDB version it records (DUMP_TIMESCALEDB, empty when it
# records none in the form read here). The version is the timescaledb_version row of _timescaledb_catalog.metadata, one of the
# extension's configuration tables, whose rows pg_dump carries (all but uuid): the table of contents and the dump's EXTENSION
# entry name no version (CREATE EXTENSION IF NOT EXISTS timescaledb WITH SCHEMA public). From stdin it is one pass: tee hands
# the stream to a second pg_restore through a FIFO, and cat reads what is left, so the stream's writer (the decryption)
# authenticates all of it before the restore pass. pg_restore reads the FIFO as its stdin, which it reads in order (given as a
# file name it would seek). Its review: what both reads write goes through head into a private folder (mktemp, 0700, under
# the pod's /tmp, an emptyDir of 64Mi whose overrun evicts the pod) and is refused past its bound, so a forged dump fills
# neither /tmp nor the shell's memory. The metadata table's rows are about 1 KiB (64 KiB bound). The table of contents grows
# with the chunks, measured in the review at about 400 bytes an uncompressed chunk and 750 a compressed one: at the retention
# bounds' most (3650 days), sensing_event and device_health_event (compressed) and sensing_batch and lane_staffing (not) hold
# 14,600 daily chunks, about 8.4 MB, the other hypertables about 0.3 MB, and each of the seven weekly ones without retention
# adds about 21 KB a year, hence 16 MiB. The folder is removed before check_toc runs, so its here-strings (bash writes one
# past a pipe's size to a temporary file under /tmp, one at a time) never sit beside it. Each reader is a background process
# whose PID is kept, and the script waits for them, so TERM, HUP or INT stops every one of them and removes the folder
# (stop_read_dump), from a file as from stdin.
METADATA_MAX_BYTES=65536
TOC_MAX_BYTES=16777216
DUMP_TOC=""
DUMP_TIMESCALEDB=""
READ_FOLDER=""
READ_PIDS=()
stop_readers() {
  local pid
  for pid in "${READ_PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  READ_PIDS=()
}

# Also on an exit while readers run (a die, a failed command): none outlives the script, none is left on a FIFO.
remove_read_folder() {
  stop_readers
  if [[ -n "$READ_FOLDER" ]]; then
    rm -f "$READ_FOLDER/dump" "$READ_FOLDER/list" "$READ_FOLDER/rows" "$READ_FOLDER/entries" "$READ_FOLDER/toc" "$READ_FOLDER/metadata"
    rmdir "$READ_FOLDER" 2>/dev/null || true
    READ_FOLDER=""
  fi
}

# A signal while the dump is read: every reader stopped, the folder removed, and the script ends as the signal would.
stop_read_dump() {
  trap - HUP INT TERM
  remove_read_folder
  say "stopped while the dump was read: its readers were stopped and their folder removed; nothing was restored"
  exit "$1"
}

read_dump() {
  local input="$1" status=0 folder pid tee_pid=""
  [[ "$input" == "-" || -f "$input" ]] || die "no dump at $input"
  trap 'stop_read_dump 129' HUP
  trap 'stop_read_dump 130' INT
  trap 'stop_read_dump 143' TERM
  READ_FOLDER="$(mktemp -d)" || die "cannot create a private folder under ${TMPDIR:-/tmp} for what the dump's reads write"
  folder="$READ_FOLDER"
  trap remove_read_folder EXIT
  : >"$folder/toc"
  : >"$folder/metadata"
  # Every FIFO before any reader, so that no reader waits on one that a refusal never made.
  mkfifo "$folder/rows" "$folder/entries" || die "cannot create the FIFOs the dump is read through in $folder"
  [[ "$input" != "-" ]] || mkfifo "$folder/dump" "$folder/list" || die "cannot create the FIFOs the dump's stream is read through in $folder"
  # Each opens its FIFOs itself (in the background), so none blocks the script; the explicit <&0 keeps tee on the stream.
  head -c "$((METADATA_MAX_BYTES + 1))" <"$folder/rows" >"$folder/metadata" &
  READ_PIDS+=("$!")
  head -c "$((TOC_MAX_BYTES + 1))" <"$folder/entries" >"$folder/toc" &
  READ_PIDS+=("$!")
  if [[ "$input" == "-" ]]; then
    pg_restore --data-only --schema=_timescaledb_catalog --table=metadata --file=- <"$folder/dump" >"$folder/rows" &
    READ_PIDS+=("$!")
    pg_restore --list <"$folder/list" >"$folder/entries" &
    READ_PIDS+=("$!")
    tee --output-error=warn-nopipe "$folder/dump" <&0 >"$folder/list" &
    tee_pid="$!"
    READ_PIDS+=("$tee_pid")
    wait "$tee_pid" || status=1
    cat <&0 >/dev/null &
    READ_PIDS+=("$!")
  else
    pg_restore --data-only --schema=_timescaledb_catalog --table=metadata --file=- "$input" >"$folder/rows" &
    READ_PIDS+=("$!")
    pg_restore --list "$input" >"$folder/entries" &
    READ_PIDS+=("$!")
  fi
  for pid in "${READ_PIDS[@]}"; do
    [[ "$pid" != "$tee_pid" ]] || continue
    wait "$pid" || status=1
  done
  READ_PIDS=()
  # The signal traps stay until the folder is removed: a TERM taken by default between the two left the folder behind in the
  # review's second round (the EXIT trap did not run in those runs).
  (($(wc -c <"$folder/toc") <= TOC_MAX_BYTES)) \
    || die "the dump's table of contents is larger than 16 MiB, more than this release's retention bounds produce at their most (about 9 MB): refused; nothing was restored"
  (($(wc -c <"$folder/metadata") <= METADATA_MAX_BYTES)) \
    || die "the dump's TimescaleDB catalog metadata (_timescaledb_catalog.metadata) is larger than 64 KiB, which TimescaleDB's is not: refused; nothing was restored"
  if ((status != 0)); then
    [[ "$input" == "-" ]] && die "the input is not a custom-format dump"
    die "$input is not a custom-format dump"
  fi
  DUMP_TOC="$(<"$folder/toc")"
  local metadata
  metadata="$(<"$folder/metadata")"
  remove_read_folder
  trap - EXIT
  trap - HUP INT TERM
  DUMP_TIMESCALEDB="$(metadata_version "$metadata")"
}

# The value of the timescaledb_version row in the plain SQL pg_restore writes for _timescaledb_catalog.metadata's data (COPY
# text: key, value and include_in_telemetry, tab-separated), across every COPY block of that table; empty unless exactly one
# such row is there.
metadata_version() {
  local line copy=0 key value rest found="" rows=0
  while IFS= read -r line; do
    if ((copy == 0)); then
      [[ "$line" == "COPY _timescaledb_catalog.metadata (key, value, "*") FROM stdin;" ]] && copy=1
    elif [[ "$line" == '\.' ]]; then
      copy=0
    else
      IFS=$'\t' read -r key value rest <<<"$line"
      if [[ "$key" == timescaledb_version ]]; then
        found="$value"
        rows=$((rows + 1))
      fi
    fi
  done <<<"$1"
  ((rows == 1)) || found=""
  printf '%s' "$found"
}

# ARV-244 (CWE-345): the dump's own TimescaleDB version, the one a restore creates the extension at; given one (the signed
# manifest's, through ariva-ops.mjs), the two must agree. A value the dump holds is never printed unless it passed
# valid_version.
check_dump_version() {
  local expected="$1"
  [[ -n "$DUMP_TIMESCALEDB" ]] || die "the dump records no TimescaleDB version (no single timescaledb_version row in _timescaledb_catalog.metadata), so it cannot be restored into its own version; nothing was restored"
  valid_version "$DUMP_TIMESCALEDB" || die "the TimescaleDB version the dump records is not a plain dotted number: refused; nothing was restored"
  [[ -z "$expected" || "$expected" == "$DUMP_TIMESCALEDB" ]] \
    || die "the dump records TimescaleDB $DUMP_TIMESCALEDB, not the $expected given (the signed manifest's): refused; nothing was restored"
  say "the dump records TimescaleDB $DUMP_TIMESCALEDB"
}

cmd_check_dump() {
  local input="-" extensions=() expected=""
  while (($#)); do
    case "$1" in
      --input) (($# >= 2)) || usage; input="$2"; shift 2 ;;
      --allow-extension) (($# >= 2)) || usage; valid_identifier "$2" || die "--allow-extension must be a plain lower-case identifier"; extensions+=("$2"); shift 2 ;;
      --timescaledb-version) (($# >= 2)) || usage; valid_version "$2" || die "--timescaledb-version must be a plain dotted number (for example 2.31.0)"
        expected="$2"; shift 2 ;;
      *) usage ;;
    esac
  done
  valid_file "$input" || die "--input must be - or a plain absolute path"
  valid_identifier "$LOGIN" || die "set ARIVA_MIGRATION_LOGIN (or --login) to the migration login"
  read_dump "$input"
  check_toc "$DUMP_TOC" "${extensions[@]}"
  check_dump_version "$expected"
  printf 'timescaledb %s\n' "$DUMP_TIMESCALEDB"
}

# ARV-244: refuses, before anything changes, a dump of a TimescaleDB version this image cannot restore into: one it does not
# ship, that is one missing from pg_available_extension_versions or whose own install script or library the image lacks (an
# older version can be listed there only as the end of a downgrade script, as 2.16.1 is in pg17-ts2.31, and creating it then
# fails once the image's extension is dropped), or one it ships but cannot update to its own (no update path). A dump newer
# than the image is one it does not ship: roll the image forward.
require_shipped_version() {
  local version="$1" shipped="$2" row available path sharedir libdir
  [[ "$version" != "$shipped" ]] || return 0
  row="$(as_migration_catalog --tuples-only --no-align --field-separator='|' --set=version="$version" <<'SQL'
SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_available_extension_versions v WHERE v.name = 'timescaledb' AND v.version = :'version'),
       EXISTS (SELECT 1 FROM pg_catalog.pg_extension_update_paths('timescaledb') u
                 JOIN pg_catalog.pg_available_extensions a ON a.name = 'timescaledb' AND u.target = a.default_version
                WHERE u.source = :'version' AND u.path IS NOT NULL);
SQL
)" || die "the TimescaleDB versions the image ships could not be read as $LOGIN"
  IFS='|' read -r available path <<<"$row"
  sharedir="$(pg_config --sharedir)" || die "pg_config is not on the PATH: run this script in the TimescaleDB pod"
  libdir="$(pg_config --pkglibdir)" || die "pg_config is not on the PATH: run this script in the TimescaleDB pod"
  if [[ "$available" != "t" || ! -f "$sharedir/extension/timescaledb--$version.sql" || ! -f "$libdir/timescaledb-$version.so" ]]; then
    die "the dump is of TimescaleDB $version, which this image does not ship (it ships $shipped and older versions with their own library): restore it on a server whose image ships $version (wiki 04 section 10.4); nothing was changed"
  fi
  [[ "$path" == "t" ]] || die "the dump is of TimescaleDB $version, which this image ships but has no update from to its own $shipped: restore it on a server whose image ships $version (wiki 04 section 10.4); nothing was changed"
}

RESTORING=0
# ARV-263 review: set while a restore has not yet had its jobs passed by restore_job_gate (from restore mode's start), so
# that a restore refused, failed or stopped leaves the database in restore mode and none of the dump's jobs runs.
KEEP_RESTORING=0
# ARV-244 review: returns 1 when timescaledb_post_restore() failed, so a restore stops there, before any update or check.
end_restore_mode() {
  if ((RESTORING == 1)); then
    RESTORING=0
    say "leaving TimescaleDB's restore mode"
    as_superuser --tuples-only --no-align <<'SQL' >/dev/null || { say "timescaledb_post_restore() failed: TimescaleDB may still be in restore mode"; return 1; }
SELECT public.timescaledb_post_restore();
SQL
  fi
}

on_restore_exit() {
  local status=$?
  if ((KEEP_RESTORING == 1)); then
    ((RESTORING == 0)) || say "the database stays in TimescaleDB's restore mode, so that no job of the dump runs before it is judged: drop it, and restore a backup you trust into an empty database (a new volume)."
  else
    end_restore_mode || true
  fi
  if ((status != 0)); then
    say "the restore did not finish: the database is partly restored. Start again from an empty database (a new volume) after fixing the cause."
  fi
}

# ARV-244 review: set from just before the image's TimescaleDB is dropped until the dump's version is created, so that a restore
# that stops between the two (a failure, a signal) says the database may have no TimescaleDB left.
REPLACING_EXTENSION=0
on_replace_exit() {
  local status=$?
  if ((status != 0 && REPLACING_EXTENSION == 1)); then
    say "the restore stopped after the image's TimescaleDB was dropped from $DATABASE and before the dump's version was created: the database may have no TimescaleDB extension, and nothing was restored. Start again from an empty database (a new volume)."
  fi
}

# ARV-244 review (CWE-269): what CREATE EXTENSION timescaledb could collide with once the image's extension is dropped from the
# empty database, so that a collision refuses the restore before the DROP rather than fails the CREATE after it: a function,
# operator, type or schema outside the system's schemas that belongs to no extension (schema public excepted; a relation's
# row type and an array type go with what they belong to, relations being judged by the empty database's own check). In an
# empty database created by the chart there is none. One line per object.
foreign_objects() {
  as_migration_catalog --tuples-only --no-align <<'SQL'
WITH members AS (
    SELECT d.classid, d.objid FROM pg_catalog.pg_depend d
     WHERE d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass AND d.deptype = 'e'),
system AS (
    SELECT n.oid FROM pg_catalog.pg_namespace n
     WHERE n.nspname IN ('pg_catalog', 'information_schema', 'pg_toast') OR n.nspname LIKE 'pg\_temp\_%' OR n.nspname LIKE 'pg\_toast\_temp\_%')
SELECT pg_catalog.regexp_replace(x.line, '[[:cntrl:]]', '?', 'g') FROM (
    SELECT 'function ' || n.nspname || '.' || p.proname AS line
      FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
     WHERE p.pronamespace NOT IN (SELECT oid FROM system)
       AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND m.objid = p.oid)
    UNION
    SELECT 'operator ' || n.nspname || '.' || o.oprname
      FROM pg_catalog.pg_operator o JOIN pg_catalog.pg_namespace n ON n.oid = o.oprnamespace
     WHERE o.oprnamespace NOT IN (SELECT oid FROM system)
       AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_operator'::pg_catalog.regclass AND m.objid = o.oid)
    UNION
    SELECT 'type ' || n.nspname || '.' || t.typname
      FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
     WHERE t.typnamespace NOT IN (SELECT oid FROM system)
       AND (t.typrelid = 0 OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid AND c.relkind = 'c'
                                         AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_class'::pg_catalog.regclass AND m.objid = c.oid)))
       AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type e WHERE e.typarray = t.oid)
       AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_type'::pg_catalog.regclass AND m.objid = t.oid)
    UNION
    SELECT 'schema ' || n.nspname
      FROM pg_catalog.pg_namespace n
     WHERE n.oid NOT IN (SELECT oid FROM system) AND n.nspname <> 'public'
       AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_namespace'::pg_catalog.regclass AND m.objid = n.oid)) x
 ORDER BY 1;
SQL
}

# ARV-244: set while a restore of an older TimescaleDB's dump updates the extensions, after restore mode ended.
RESTORED_TIMESCALEDB=""
on_update_exit() {
  local status=$?
  if ((status != 0)); then
    say "the dump is restored at TimescaleDB $RESTORED_TIMESCALEDB, but the update to the image's versions did not finish and the restore's check did not run. Fix the cause above and run update-extensions (wiki 04 section 6.8), then check; objects or settings it refuses came with the dump, so treat the dump as untrusted (wiki 10 section 4.13)."
  fi
}

# ARV-263 review (CWE-269, CWE-345): the jobs a restore brings, read while TimescaleDB's restore mode still keeps its scheduler
# away from the database, as the migration login; check, after it, compares them with ARIVA_SCHEMA, but by then a job would
# have run. Each job must run one of TimescaleDB's policy procedures (its own two as the extension's owner, every other as
# the migration login), with the procedure's own check or none; a policy acts on its own hypertable, and a retention policy
# only on a hypertable of ARIVA_RETENTION_DAYS within its bounds. Any other job keeps the database in restore mode and
# stops the restore. Prints one line per job refused.
restore_job_gate() {
  as_migration_catalog --tuples-only --no-align --set=retention_days="$ARIVA_RETENTION_DAYS" <<'SQL'
WITH bounds AS (
    SELECT split_part(e, '=', 1) AS name, split_part(split_part(e, '=', 2), '-', 1)::integer AS low, split_part(split_part(e, '=', 2), '-', 2)::integer AS high
      FROM unnest(string_to_array(:'retention_days', ';')) e),
jobs AS (
    SELECT j.*, h.schema_name, h.table_name, a.user_view_schema, a.user_view_name,
           (SELECT x.extowner FROM pg_extension x WHERE x.extname = 'timescaledb') AS extension_owner
      FROM _timescaledb_catalog.bgw_job j
      LEFT JOIN _timescaledb_catalog.hypertable h ON h.id = j.hypertable_id
      LEFT JOIN _timescaledb_catalog.continuous_agg a ON a.mat_hypertable_id = j.hypertable_id)
SELECT regexp_replace('job ' || j.id || ' (' || coalesce(j.proc_schema || '.' || j.proc_name, 'no procedure') || ', owner '
                      || coalesce(j.owner::text, 'none') || ')', '[[:cntrl:]]', '?', 'g')
  FROM jobs j
 -- A condition that is unknown (a NULL anywhere in it) refuses the job, as false does.
 WHERE NOT coalesce((j.proc_schema = '_timescaledb_functions'
            AND (j.check_name IS NULL OR (j.check_schema = '_timescaledb_functions' AND j.check_name = j.proc_name || '_check'))
            AND CASE
                WHEN j.proc_name IN ('policy_telemetry', 'policy_job_stat_history_retention')
                    THEN j.owner::oid = j.extension_owner AND j.hypertable_id IS NULL
                WHEN j.proc_name IN ('policy_compression', 'policy_retention')
                    THEN j.owner::oid = (SELECT r.oid FROM pg_roles r WHERE r.rolname = current_user) AND j.hypertable_id IS NOT NULL
                         AND j.schema_name = 'public' AND j.user_view_name IS NULL AND jsonb_typeof(j.config) = 'object'
                         AND (j.config -> 'hypertable_id')::text = j.hypertable_id::text
                         AND (j.proc_name = 'policy_compression'
                              OR EXISTS (SELECT 1 FROM bounds b WHERE b.name = j.table_name
                                           AND substring((j.config -> 'drop_after')::text FROM '^"([1-9][0-9]{0,4}) days"$')::integer BETWEEN b.low AND b.high))
                WHEN j.proc_name = 'policy_refresh_continuous_aggregate'
                    THEN j.owner::oid = (SELECT r.oid FROM pg_roles r WHERE r.rolname = current_user) AND j.hypertable_id IS NOT NULL
                         AND j.user_view_schema = 'public' AND jsonb_typeof(j.config) = 'object'
                         AND (j.config -> 'mat_hypertable_id')::text = j.hypertable_id::text
                ELSE false END), false)
 ORDER BY j.id;
SQL
}

cmd_restore() {
  local input="" checked=0 runtime="" reader="" extensions=() allowed=() version=""
  while (($#)); do
    case "$1" in
      --input) (($# >= 2)) || usage; input="$2"; shift 2 ;;
      --dump-checked) checked=1; shift ;;
      --timescaledb-version) (($# >= 2)) || usage; valid_version "$2" || die "--timescaledb-version must be a plain dotted number (for example 2.31.0)"
        version="$2"; shift 2 ;;
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
    [[ -n "$version" ]] || die "a restore from stdin needs --timescaledb-version, the dump's TimescaleDB version: the signed manifest's, or the one check-dump printed for the same stream (ariva-ops.mjs passes it)"
  else
    read_dump "$input"
    check_toc "$DUMP_TOC" "${extensions[@]}"
    check_dump_version "$version"
    version="$DUMP_TIMESCALEDB"
  fi

  require_migration_login
  local state
  state="$(as_migration_catalog --tuples-only --no-align --field-separator='|' <<'SQL'
SELECT to_regclass('public.schema_version') IS NULL,
       (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
           AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')),
       (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'),
       (SELECT default_version FROM pg_available_extensions WHERE name = 'timescaledb');
SQL
)"
  local empty relations installed shipped
  IFS='|' read -r empty relations installed shipped <<<"$state"
  [[ "$empty" == "t" && "$relations" == "0" ]] || die "database $DATABASE is not empty ($relations relations in public); a restore goes into an empty database only"
  [[ -n "$installed" ]] || die "the timescaledb extension is not installed in $DATABASE; create it first (the chart's image does), or, after a restore that stopped between dropping the image's TimescaleDB and creating the dump's version, start again from an empty database (a new volume)"
  # ARV-244: TimescaleDB restores a dump only into the extension version it was taken at (its catalog travels as data).
  require_shipped_version "$version" "$shipped"
  local origin="the dump's"
  [[ "$input" != "-" ]] || origin="the version given for the dump"
  if [[ "$installed" == "$version" ]]; then
    say "restoring into $DATABASE (TimescaleDB $version, $origin), objects owned by $LOGIN"
  else
    # ARV-244 review: before the image's extension is dropped, what would stop the restore later refuses it now, with nothing
    # changed: what the extensions' update after the restore refuses (planted_objects, with no setting allowed: on a server
    # restored with --allow-extension or --allow-setting the server's own extension or setting is refused here too), and any
    # function, operator, type or schema that belongs to no extension, which CREATE EXTENSION could collide with.
    local planted foreign
    planted="$(planted_objects "")" || die "the database could not be read as $LOGIN before the image's TimescaleDB is replaced"
    foreign="$(foreign_objects)" || die "the database could not be read as $LOGIN before the image's TimescaleDB is replaced"
    if [[ -n "$planted$foreign" ]]; then
      planted="$(printf '%s\n%s' "$planted" "$foreign" | LC_ALL=C sort -u | grep -v '^$' || true)"
      die "refused before anything changes: ${planted//$'\n'/, }. A restore of a dump of another TimescaleDB version replaces the image's extension in the empty database and then updates it as the superuser, so the database holds nothing but the extensions' objects first (an extension or setting a DBA-provided server keeps on purpose included: restore such a backup on a server of the image of its own version, wiki 04 section 10.4). Give the restore an empty database (a new volume), and find out who created these (wiki 10 section 4.13)"
    fi
    say "restoring a dump of TimescaleDB $version into $DATABASE, which has $installed: the extension is created at $version first and updated to $shipped afterwards; objects owned by $LOGIN"
  fi

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
    -- ARV-083a (script 0074): the host roles, which the dump's grants name.
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_main') THEN
        CREATE ROLE ariva_main NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_ingest') THEN
        CREATE ROLE ariva_ingest NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_stream') THEN
        CREATE ROLE ariva_stream NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_cronz') THEN
        CREATE ROLE ariva_cronz NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_integration') THEN
        CREATE ROLE ariva_integration NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_availability') THEN
        CREATE ROLE ariva_availability NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
END
$$;
GRANT ariva_migration TO CURRENT_USER;
SELECT string_agg(rolname, ', ') FROM pg_roles
 WHERE rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz',
                   'ariva_integration', 'ariva_availability')
   AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolcanlogin OR rolreplication OR rolbypassrls);
SQL
)"
  [[ -z "$privileged" ]] || die "these Ariva group roles exist with privileges they must not have: $privileged"
  local members
  members="$(extra_members "$runtime" "$reader")"
  [[ -z "$members" ]] || die "Ariva's group roles already have other members ($members); name this deployment's logins with --runtime-login and --reader-login, or restore into a server of its own"

  # ARV-244: the empty database's extension at the dump's version. DROP refuses when anything depends on it (so the database
  # was not empty); CREATE is the first and only statement of its own session, since a session that loaded one version's
  # library cannot create another, and the version reaches it as a psql variable only, after valid_version and
  # require_shipped_version. It names schema public, where the image creates it and the dump's entry has it: the superuser's
  # session has pg_catalog as its search path, which an extension without a schema would be installed in. The toolkit is left
  # at the image's version: a dump carries none of its rows and no Ariva object uses it.
  if [[ "$installed" != "$version" ]]; then
    # ARV-244 review: no other session of an Ariva login while the extension is replaced. The migration login owns the
    # database, so a session of it could create, between the DROP and the CREATE, an object the CREATE collides with (a schema
    # of TimescaleDB's name), and leave the database without TimescaleDB. Residual: a session opened after this check, in the
    # moment between the two statements, which could still fail the CREATE (it says to start again from a new volume) or plant
    # an object its install script might resolve (ARV-236's residual, narrowed to that moment).
    require_no_ariva_sessions "nothing was dropped or restored"
    REPLACING_EXTENSION=1
    trap on_replace_exit EXIT
    say "dropping the image's TimescaleDB $installed from the empty $DATABASE (superuser over the local socket, the session's first statement)"
    PGOPTIONS='-c lock_timeout=30s' as_superuser <<'SQL' >/dev/null || { REPLACING_EXTENSION=0; die "the image's TimescaleDB could not be dropped from $DATABASE (an object depends on it, so the database is not empty, or its lock was not granted within 30 seconds, so a session holds it): nothing was restored; give the restore an empty database (a new volume)"; }
DROP EXTENSION timescaledb;
SQL
    say "creating TimescaleDB $version in $DATABASE (superuser over the local socket, the session's first statement)"
    PGOPTIONS='-c lock_timeout=30s' as_superuser --set=version="$version" <<'SQL' >/dev/null || die "TimescaleDB $version could not be created in $DATABASE after the image's extension was dropped: nothing was restored; start again from an empty database (a new volume)"
CREATE EXTENSION timescaledb SCHEMA public VERSION :'version';
SQL
    local created
    created="$(as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT extversion FROM pg_extension WHERE extname = 'timescaledb';
SQL
)"
    # Its review: at another version the database has a TimescaleDB, so the exit message for a missing one is not said.
    [[ "$created" == "$version" ]] || { [[ -z "$created" ]] || REPLACING_EXTENSION=0; die "TimescaleDB is not at $version in $DATABASE after its creation (another version, or none): nothing was restored; start again from an empty database (a new volume)"; }
    REPLACING_EXTENSION=0
    trap - EXIT
  fi

  # ARV-263 review: from here on the database leaves restore mode only once restore_job_gate passed its jobs, so a restore
  # that fails or is stopped (an error in the dump's data, a signal) never starts a job nothing has judged.
  KEEP_RESTORING=1
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
  # ARV-244: the restored catalog records the version the extension was created at (from stdin, the version given is the only
  # one this pass knew); otherwise the database stays in restore mode, as for a refused job.
  local restored
  restored="$(as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT coalesce(regexp_replace(string_agg(m.value, ','), '[[:cntrl:]]', '?', 'g'), 'no version')
  FROM _timescaledb_catalog.metadata m WHERE m.key = 'timescaledb_version';
SQL
)" || die "the restored TimescaleDB catalog could not be read as $LOGIN"
  [[ "$restored" == "$version" ]] || die "the restored dump records TimescaleDB ${restored:0:80}, not $version, the version it was restored into (treat the dump or the version given as wrong)"
  local refused_jobs
  refused_jobs="$(restore_job_gate)" || die "the restored jobs could not be read as $LOGIN before TimescaleDB's scheduler starts"
  if [[ -n "$refused_jobs" ]]; then
    die "the dump holds TimescaleDB jobs this release does not create, which would run once restore mode ends: ${refused_jobs//$'\n'/, } (treat the dump as untrusted, wiki 10 section 4.13)"
  fi
  say "TimescaleDB's jobs checked before its scheduler starts: each runs one of TimescaleDB's policies as the role this release gives it"
  KEEP_RESTORING=0
  end_restore_mode || die "TimescaleDB's restore mode did not end (timescaledb_post_restore() failed above): the restore stops before the extensions' update and its check"
  trap - EXIT

  local logins=()
  [[ -z "$runtime" ]] || logins+=(--runtime-login "$runtime")
  [[ -z "$reader" ]] || logins+=(--reader-login "$reader")
  check_after_restore=1
  # ARV-244: a dump of an older TimescaleDB, now restored at its own version, is updated to the image's as update-extensions
  # updates a live database (its refusals first, each ALTER the first statement of its session), before the check, whose
  # extension versions line would fail otherwise. A dump of the image's version skips this. Its review (ARV-263's rule, judge
  # what the dump brought before it can run): the update runs TimescaleDB's update scripts as the superuser on the restored
  # database, so check's lines judge it first and any failure stops the restore before the update, but the two lines only the
  # update can make pass: extension versions, and extension members, which this release pins for the image's versions (before
  # the update they are those of the dump's version, which the restore created from the image's own install script; a dump
  # carries no member, only the extension's configuration rows, which the table of contents check has limited).
  if [[ -n "$(extensions_behind)" ]]; then
    RESTORED_TIMESCALEDB="$version"
    say "checking the restored database before the extensions' update (every line of check but extension versions and extension members)"
    check_before_update=1
    cmd_check "${logins[@]}" "${allowed[@]}"
    check_before_update=0
    trap on_update_exit EXIT
    update_extensions
    trap - EXIT
  fi

  say "updating planner statistics as $LOGIN"
  as_migration <<'SQL'
SET client_min_messages = error;
ANALYZE;
SQL
  cmd_check "${logins[@]}" "${allowed[@]}"
  if ((check_not_compared)); then
    say "restore finished, but the backup is older than this release's scripts: the runtime role's privileges, the views and the schema are not compared yet. Next: run the migration job with api-main --migrate (the platform stays stopped), then check with --runtime-login, and start the hosts only when it passes; then restore the files and run keyring with the Data Protection certificate."
  else
    say "restore finished. Next: run the migration job (api-main --migrate) so the runtime and validation reader logins exist with this deployment's passwords, restore the files, start the hosts, then run keyring with the Data Protection certificate."
  fi
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
  # ARV-263 review: --allow-login is for a DBA's own login; Ariva's roles and logins are never let pass by it, nor (ARV-263b
  # review) one of PostgreSQL's predefined roles (pg_ names are reserved for them), which would hide a grant made to the role.
  local allowed_name
  for allowed_name in ${logins_allowed//,/ }; do
    if [[ "$allowed_name" == ariva_* || "$allowed_name" == pg_* || "$allowed_name" == "$LOGIN" || "$allowed_name" == "$runtime" || "$allowed_name" == "$reader" ]]; then
      die "--allow-login must name a DBA's own login, not one of Ariva's roles or logins or PostgreSQL's predefined roles: $allowed_name"
    fi
  done
  require_migration_login
  # The report runs as the migration login with the catalog's search path for the session (ARV-183 review): a setting of the
  # database or an object the login created cannot change what a name in it means, or what it reports.
  local report
  report="$(as_migration_catalog --tuples-only --no-align --set=runtime="$runtime" --set=reader="$reader" --set=allowed_extensions="$extensions" \
    --set=allowed_logins="$logins_allowed" --set=allowed_settings="$settings_allowed" --set=ariva_functions="$ARIVA_FUNCTIONS" \
    --set=runtime_privileges="$ARIVA_RUNTIME_PRIVILEGES" --set=runtime_privileges_at="$ARIVA_RUNTIME_PRIVILEGES_AT" \
    --set=host_roles="$ARIVA_HOST_ROLES" --set=host_privileges="$ARIVA_HOST_PRIVILEGES" \
    --set=ariva_views="$ARIVA_VIEWS" --set=after_restore="$check_after_restore" \
    --set=timeout_min="$RUNTIME_TIMEOUT_MIN_SECONDS" --set=timeout_max="$RUNTIME_TIMEOUT_MAX_SECONDS" \
    --set=temp_min="$RUNTIME_TEMP_FILE_MIN_MEGABYTES" --set=temp_max="$RUNTIME_TEMP_FILE_MAX_MEGABYTES" \
    --set=idle_min="$RUNTIME_IDLE_MIN_SECONDS" --set=idle_max="$RUNTIME_IDLE_MAX_SECONDS" \
    --set=keepalive_idle="$RUNTIME_KEEPALIVE_IDLE_SECONDS" --set=keepalive_interval="$RUNTIME_KEEPALIVE_INTERVAL_SECONDS" \
    --set=keepalive_count="$RUNTIME_KEEPALIVE_COUNT" <<'SQL'
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
-- ARV-189: the script the database is at and whether it is behind the lists (compared byte by byte: the names are ASCII and
-- the order is the scripts' own, whatever the database's collation).
pinned_at AS (
    SELECT coalesce(max(script_name), 'no script') AS latest,
           coalesce(max(script_name), '') COLLATE "C" < :'runtime_privileges_at' COLLATE "C" AS behind
      FROM public.schema_version),
-- ARV-189: the privileges compared, in this order: every table privilege of this server (MAINTAIN since PostgreSQL 17) and
-- those a column can carry.
table_privileges(p, i) AS (
    SELECT t.p, t.i FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']
           || CASE WHEN current_setting('server_version_num')::integer >= 170000 THEN ARRAY['MAINTAIN'] ELSE ARRAY[]::text[] END) WITH ORDINALITY t(p, i)),
column_privileges(p, i) AS (
    SELECT t.p, t.i FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES']) WITH ORDINALITY t(p, i)),
-- ARV-189: the runtime role's privileges on every relation of public outside the extensions, as ARIVA_RUNTIME_PRIVILEGES writes
-- them: its table-wide privileges in a fixed order, then each privilege it holds on some columns only, with those columns. Any
-- grant counts, a PUBLIC one included (has_table_privilege, has_column_privilege).
runtime_privileges AS (
    SELECT c.relname::text AS name,
           concat_ws(',', VARIADIC ARRAY(
               SELECT t.p FROM table_privileges t WHERE has_table_privilege('ariva_runtime', c.oid, t.p) ORDER BY t.i)
             || ARRAY(
               SELECT t.p || '(' || string_agg(a.attname::text, ' ' ORDER BY a.attname) || ')'
                 FROM column_privileges t
                 JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                WHERE NOT has_table_privilege('ariva_runtime', c.oid, t.p) AND has_column_privilege('ariva_runtime', c.oid, a.attnum, t.p)
                GROUP BY t.p, t.i ORDER BY t.i)) AS privileges
      FROM pg_class c
     WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')),
pinned_privileges AS (
    SELECT split_part(e, '=', 1) AS name, split_part(e, '=', 2) AS privileges FROM unnest(string_to_array(:'runtime_privileges', ';')) e),
-- A relation the list does not name grants nothing (ARV-083a: script 0074 left ariva_runtime the base only and took away
-- script 0001's default privileges); one it names that the database lacks is reported too.
privilege_drift AS (
    SELECT coalesce(a.name, p.name) || ' (the scripts: ' || coalesce(nullif(p.privileges, ''), 'none') || '; found: '
           || CASE WHEN a.name IS NULL THEN 'no such relation' ELSE coalesce(nullif(a.privileges, ''), 'none') END || ')' AS name
      FROM runtime_privileges a FULL JOIN pinned_privileges p ON p.name = a.name
     WHERE a.privileges IS DISTINCT FROM coalesce(p.privileges, '')),
-- ARV-083a: each host role's privileges, as ARIVA_HOST_PRIVILEGES writes them (<role>.<relation>=<privileges>); a relation
-- the list does not name for a role grants that role nothing. A host role missing from the server shows no privileges.
host_role_names(name) AS (
    SELECT unnest(string_to_array(:'host_roles', ','))),
host_privileges AS (
    SELECT h.name || '.' || c.relname::text AS name,
           concat_ws(',', VARIADIC ARRAY(
               SELECT t.p FROM table_privileges t WHERE has_table_privilege(r.oid, c.oid, t.p) ORDER BY t.i)
             || ARRAY(
               SELECT t.p || '(' || string_agg(a.attname::text, ' ' ORDER BY a.attname) || ')'
                 FROM column_privileges t
                 JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                WHERE NOT has_table_privilege(r.oid, c.oid, t.p) AND has_column_privilege(r.oid, c.oid, a.attnum, t.p)
                GROUP BY t.p, t.i ORDER BY t.i)) AS privileges
      FROM host_role_names h JOIN pg_roles r ON r.rolname = h.name
      CROSS JOIN pg_class c
     WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')),
pinned_host_privileges AS (
    SELECT split_part(e, '=', 1) AS name, split_part(e, '=', 2) AS privileges FROM unnest(string_to_array(:'host_privileges', ';')) e),
host_privilege_drift AS (
    SELECT coalesce(a.name, p.name) || ' (the scripts: ' || coalesce(nullif(p.privileges, ''), 'none') || '; found: '
           || CASE WHEN a.name IS NULL THEN 'no such role or relation' ELSE coalesce(nullif(a.privileges, ''), 'none') END || ')' AS name
      FROM host_privileges a FULL JOIN pinned_host_privileges p ON p.name = a.name
     WHERE a.privileges IS DISTINCT FROM coalesce(p.privileges, '')),
-- ARV-189 review: the views of public (and any materialized view or foreign table) are the scripts' own, each reading what its
-- script reads: a continuous aggregate its hypertable, a plain view the relations it names. A view the scripts do not create
-- would take the default DML and act with its owner's rights on whatever it reads.
public_views AS (
    SELECT v.relname::text AS name,
           coalesce((SELECT ca.hypertable_name::text FROM timescaledb_information.continuous_aggregates ca
                      WHERE ca.view_schema = 'public' AND ca.view_name = v.relname),
                    (SELECT string_agg(s.x, ' ' ORDER BY s.x)
                       FROM (SELECT DISTINCT CASE WHEN t.relnamespace = 'public'::regnamespace THEN t.relname::text ELSE t.oid::regclass::text END AS x
                               FROM pg_rewrite r
                               JOIN pg_depend d ON d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid AND d.refclassid = 'pg_class'::regclass
                               JOIN pg_class t ON t.oid = d.refobjid
                              WHERE r.ev_class = v.oid AND d.refobjid <> v.oid) s), '') AS reads
      FROM pg_class v
     WHERE v.relnamespace = 'public'::regnamespace AND v.relkind IN ('v', 'm', 'f')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = v.oid AND d.deptype = 'e')),
pinned_views AS (
    SELECT split_part(e, '=', 1) AS name, split_part(e, '=', 2) AS reads FROM unnest(string_to_array(:'ariva_views', ';')) e),
view_drift AS (
    SELECT coalesce(a.name, p.name) || ' (the scripts: ' || coalesce('reads ' || p.reads, 'no such view') || '; found: '
           || CASE WHEN a.name IS NULL THEN 'no such view' ELSE 'reads ' || coalesce(nullif(a.reads, ''), 'nothing') END || ')' AS name
      FROM public_views a FULL JOIN pinned_views p ON p.name = a.name
     WHERE a.reads IS DISTINCT FROM p.reads),
-- ARV-189: outside public, the runtime role holds privileges only where TimescaleDB keeps a hypertable's rows or a continuous
-- aggregate's, and each relation is judged by what it is, never by its name or by catalog rows alone, which a dump carries
-- (its review): an inheritance child grants no more than its parent (a chunk reaches its hypertable's rows), and only a table
-- the catalog lists as a chunk of the hypertable it inherits from is one; a chunk's compressed form (a table TimescaleDB 2.31
-- names after the chunk with _compressed) grants no more than the chunk's hypertable; a continuous aggregate's materialization
-- table and its partial and direct views (as the catalog names them) at most SELECT; no view, here or in public, grants a
-- privilege the runtime role lacks on a relation it reads (a view reads with its owner's rights); any other relation created
-- after initdb (oid 16384 and above, information_schema and pg_catalog included) outside public and the extensions grants it
-- nothing; no sequence lets it set a value; no extension table grants PUBLIC, the role or a login of it more than the
-- extension did (pg_init_privs), table-wide or on a column; and no table of the server's own grants the role or a login of it
-- anything, or PUBLIC more than initdb did (pg_init_privs records pg_catalog's; information_schema's views, which it does not
-- record, may give PUBLIC SELECT and nothing else, but its _pg_ views, which initdb leaves to their owner: _pg_user_mappings
-- shows every user mapping's options, passwords included).
runtime_members(oid) AS (
    SELECT m.member FROM pg_auth_members m WHERE m.roleid = 'ariva_runtime'::regrole AND m.member <> (SELECT oid FROM me)
    UNION
    SELECT m.member FROM pg_auth_members m JOIN runtime_members r ON m.roleid = r.oid WHERE m.member <> (SELECT oid FROM me)),
runtime_grantees(oid) AS (
    SELECT 'ariva_runtime'::regrole::oid
    UNION
    SELECT r.oid FROM runtime_members r),
-- ARV-189 review: the runtime role and the logins that hold it create nothing, in any schema or in the database.
runtime_create AS (
    SELECT pg_get_userbyid(r.oid) || ' in schema ' || n.nspname AS name
      FROM runtime_grantees r CROSS JOIN pg_namespace n WHERE has_schema_privilege(r.oid, n.oid, 'CREATE')
    UNION ALL
    SELECT pg_get_userbyid(r.oid) || ' in the database' FROM runtime_grantees r
     WHERE has_database_privilege(r.oid, (SELECT oid FROM pg_database WHERE datname = current_database()), 'CREATE')),
extension_members AS (
    SELECT d.objid AS relation FROM pg_depend d
     WHERE d.classid = 'pg_class'::regclass AND d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e'),
inherit_pairs AS (
    SELECT i.inhrelid AS relation, i.inhparent AS parent FROM pg_inherits i
     WHERE NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.relation = i.inhrelid)),
chunks AS (
    SELECT p.relation, p.parent FROM inherit_pairs p JOIN pg_class c ON c.oid = p.relation AND c.relkind = 'r'
     WHERE EXISTS (SELECT 1 FROM timescaledb_information.chunks ch
                    WHERE to_regclass(format('%I.%I', ch.chunk_schema, ch.chunk_name)) = p.relation
                      AND to_regclass(format('%I.%I', ch.hypertable_schema, ch.hypertable_name)) = p.parent)),
compressed AS (
    SELECT c.oid AS relation, k.parent
      FROM chunks k JOIN pg_class h ON h.oid = k.relation
      JOIN pg_class c ON c.relnamespace = h.relnamespace AND c.relname = h.relname || '_compressed' AND c.relkind = 'r'),
read_only AS (
    SELECT c.oid AS relation
      FROM timescaledb_information.continuous_aggregates m
      JOIN pg_class c ON c.oid = to_regclass(format('%I.%I', m.materialization_hypertable_schema, m.materialization_hypertable_name)) AND c.relkind = 'r'
    UNION
    SELECT c.oid
      FROM _timescaledb_catalog.continuous_agg a
      JOIN pg_class c ON c.relkind = 'v' AND c.oid IN (to_regclass(format('%I.%I', a.partial_view_schema, a.partial_view_name)),
                                                       to_regclass(format('%I.%I', a.direct_view_schema, a.direct_view_name)))),
-- What each view of Ariva's reads: the relations its rewrite rule depends on (a toast table and a sequence too, its review's
-- third round: a view over a toast table reads the rows behind a table the runtime role may not read).
view_reads AS (
    SELECT DISTINCT v.oid AS view, d.refobjid AS reads
      FROM pg_class v JOIN pg_rewrite r ON r.ev_class = v.oid
      JOIN pg_depend d ON d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid AND d.refclassid = 'pg_class'::regclass AND d.refobjid <> v.oid
      JOIN pg_class t ON t.oid = d.refobjid AND t.relkind IN ('r', 'p', 'v', 'm', 'f', 't', 'S')
     WHERE v.relkind IN ('v', 'm') AND v.oid >= 16384 AND NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.relation = v.oid)),
internal_drift AS (
    SELECT x.relation::regclass::text || ' (wider than ' || x.parent::regclass::text || ')' AS name
      FROM (SELECT p.relation, p.parent FROM inherit_pairs p UNION ALL SELECT k.relation, k.parent FROM compressed k) x
     WHERE EXISTS (SELECT 1 FROM table_privileges t WHERE has_table_privilege('ariva_runtime', x.relation, t.p) AND NOT has_table_privilege('ariva_runtime', x.parent, t.p))
        OR EXISTS (SELECT 1 FROM column_privileges t
                     JOIN pg_attribute a ON a.attrelid = x.relation AND a.attnum > 0 AND NOT a.attisdropped
                    WHERE has_column_privilege('ariva_runtime', x.relation, a.attnum, t.p) AND NOT has_table_privilege('ariva_runtime', x.parent, t.p)
                      AND NOT EXISTS (SELECT 1 FROM pg_attribute h WHERE h.attrelid = x.parent AND h.attname = a.attname AND NOT h.attisdropped
                                                                      AND has_column_privilege('ariva_runtime', x.parent, h.attnum, t.p)))
    UNION ALL
    SELECT p.relation::regclass::text || ' (inherits ' || p.parent::regclass::text || ')'
      FROM inherit_pairs p WHERE NOT EXISTS (SELECT 1 FROM chunks k WHERE k.relation = p.relation)
    UNION ALL
    SELECT r.relation::regclass::text || ' (more than SELECT)' FROM read_only r
     WHERE EXISTS (SELECT 1 FROM table_privileges t WHERE t.p <> 'SELECT' AND has_table_privilege('ariva_runtime', r.relation, t.p))
        OR EXISTS (SELECT 1 FROM column_privileges t JOIN pg_attribute a ON a.attrelid = r.relation AND a.attnum > 0 AND NOT a.attisdropped
                    WHERE t.p <> 'SELECT' AND has_column_privilege('ariva_runtime', r.relation, a.attnum, t.p))
    UNION ALL
    SELECT DISTINCT v.view::regclass::text || ' (reads ' || v.reads::regclass::text || ')'
      FROM view_reads v CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE')) t(p)
     WHERE (has_table_privilege('ariva_runtime', v.view, t.p)
            OR CASE WHEN t.p = 'DELETE' THEN false
                    ELSE EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = v.view AND a.attnum > 0 AND NOT a.attisdropped
                                                               AND has_column_privilege('ariva_runtime', v.view, a.attnum, t.p)) END)
       AND NOT has_table_privilege('ariva_runtime', v.reads, t.p)
    UNION ALL
    SELECT c.oid::regclass::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE c.oid >= 16384 AND n.nspname <> 'public' AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'
       AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
       AND NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.relation = c.oid)
       AND NOT EXISTS (SELECT 1 FROM chunks k WHERE k.relation = c.oid)
       AND NOT EXISTS (SELECT 1 FROM compressed k WHERE k.relation = c.oid)
       AND NOT EXISTS (SELECT 1 FROM read_only r WHERE r.relation = c.oid)
       AND (EXISTS (SELECT 1 FROM table_privileges t WHERE has_table_privilege('ariva_runtime', c.oid, t.p))
            OR EXISTS (SELECT 1 FROM column_privileges t JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                        WHERE has_column_privilege('ariva_runtime', c.oid, a.attnum, t.p)))
    UNION ALL
    SELECT c.oid::regclass::text || ' (sets values)' FROM pg_class c
     WHERE c.relkind = 'S' AND c.oid >= 16384
       AND CASE WHEN c.relkind = 'S' THEN has_sequence_privilege('ariva_runtime', c.oid, 'UPDATE') ELSE false END
    UNION ALL
    SELECT DISTINCT c.oid::regclass::text || CASE WHEN g.attname IS NULL THEN '' ELSE ' (' || g.attname || ')' END
           || CASE WHEN c.oid < 16384 THEN ' (system table)' ELSE ' (extension table)' END
      FROM pg_class c
      CROSS JOIN LATERAL (SELECT 0 AS objsubid, NULL::name AS attname, x.grantee, x.privilege_type FROM aclexplode(c.relacl) x
                          UNION ALL
                          SELECT t.attnum, t.attname, x.grantee, x.privilege_type FROM pg_attribute t CROSS JOIN LATERAL aclexplode(t.attacl) x
                           WHERE t.attrelid = c.oid AND t.attnum > 0 AND t.attacl IS NOT NULL) g
     WHERE (c.oid < 16384 OR EXISTS (SELECT 1 FROM extension_members e WHERE e.relation = c.oid))
       AND (g.grantee IN (SELECT r.oid FROM runtime_grantees r)
            OR (g.grantee = 0 AND (c.oid >= 16384 OR g.objsubid <> 0 OR g.privilege_type <> 'SELECT'
                                   OR EXISTS (SELECT 1 FROM pg_init_privs ip WHERE ip.objoid = c.oid AND ip.classoid = 'pg_class'::regclass)
                                   OR (c.relnamespace = 'information_schema'::regnamespace AND c.relname LIKE '\_pg\_%'))))
       AND NOT EXISTS (SELECT 1 FROM pg_init_privs ip CROSS JOIN LATERAL aclexplode(ip.initprivs) b
                        WHERE ip.objoid = c.oid AND ip.classoid = 'pg_class'::regclass AND ip.objsubid = g.objsubid
                          AND b.grantee = g.grantee AND b.privilege_type = g.privilege_type)),
-- ARV-189 review: every grant to the runtime role goes to ariva_runtime, without the right to pass it on: a table or column
-- granted to a login that holds it (the runtime login) reaches the hosts the same way and no comparison above would see it,
-- and a grant option would let a host widen what other logins read. Nor does a relation of Ariva's (outside the extensions,
-- created after initdb) grant PUBLIC anything: a migrated database has no such grant, and every login of the server holds
-- PUBLIC. The migration login, which holds ariva_runtime as the role's creator (PostgreSQL 16 and later) and owns every
-- relation, is not one of the role's members here.
stray_grants AS (
    SELECT DISTINCT c.oid::regclass::text || CASE WHEN g.attname IS NULL THEN '' ELSE ' (' || g.attname || ')' END
           || CASE WHEN g.grantee = 0 THEN ' to PUBLIC' ELSE ' to ' || pg_get_userbyid(g.grantee) END
           || CASE WHEN g.is_grantable THEN ' with grant option' ELSE '' END AS name
      FROM pg_class c
      CROSS JOIN LATERAL (SELECT NULL::name AS attname, x.grantee, x.is_grantable FROM aclexplode(c.relacl) x
                          UNION ALL
                          SELECT t.attname, x.grantee, x.is_grantable FROM pg_attribute t CROSS JOIN LATERAL aclexplode(t.attacl) x
                           WHERE t.attrelid = c.oid AND t.attnum > 0 AND t.attacl IS NOT NULL) g
     WHERE g.grantee IN (SELECT r.oid FROM runtime_members r)
        OR (g.grantee = 'ariva_runtime'::regrole AND g.is_grantable)
        OR (g.grantee = 0 AND c.oid >= 16384 AND NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.relation = c.oid))),
-- ARV-189 review (its third round): the default privileges are the scripts' and nothing else. Since ARV-083a (script 0074)
-- there are none: a table or sequence a later script creates gives no role anything until its script grants it. A database
-- behind script 0074 has script 0001's: the tables the migration login creates in public give ariva_runtime SELECT, INSERT,
-- UPDATE and DELETE, its sequences there SELECT and USAGE, without grant option. A dump carries default privileges (the
-- migration login's DEFAULT ACL entries) and any role may change its own, so another entry would widen what the tables and
-- views of a later release give, past every check of this one.
default_privileges AS (
    SELECT d.defaclrole, d.defaclnamespace, d.defaclobjtype,
           coalesce((SELECT string_agg(x.e, ',' ORDER BY x.e COLLATE "C")
                       FROM (SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END || '=' || a.privilege_type
                                    || CASE WHEN a.is_grantable THEN ' with grant option' ELSE '' END AS e
                               FROM aclexplode(d.defaclacl) a) x), '') AS entries
      FROM pg_default_acl d),
default_drift AS (
    SELECT pg_get_userbyid(p.defaclrole) || ' in '
           || CASE WHEN p.defaclnamespace = 0 THEN 'every schema' ELSE p.defaclnamespace::regnamespace::text END || ' on '
           || CASE p.defaclobjtype WHEN 'r' THEN 'tables' WHEN 'S' THEN 'sequences' WHEN 'f' THEN 'functions' WHEN 'T' THEN 'types'
                                   WHEN 'n' THEN 'schemas' WHEN 'L' THEN 'large objects' ELSE p.defaclobjtype::text END
           || ' gives ' || coalesce(nullif(p.entries, ''), 'nothing') AS name
      FROM default_privileges p
     WHERE NOT ((SELECT behind FROM pinned_at) AND p.defaclrole = (SELECT oid FROM me) AND p.defaclnamespace = 'public'::regnamespace
                AND ((p.defaclobjtype = 'r' AND p.entries = 'ariva_runtime=DELETE,ariva_runtime=INSERT,ariva_runtime=SELECT,ariva_runtime=UPDATE')
                     OR (p.defaclobjtype = 'S' AND p.entries = 'ariva_runtime=SELECT,ariva_runtime=USAGE')))
    UNION ALL
    SELECT 'script 0001''s for ' || x.what || ' missing' FROM (VALUES ('r', 'tables'), ('S', 'sequences')) x(t, what)
     WHERE (SELECT behind FROM pinned_at)
       AND NOT EXISTS (SELECT 1 FROM default_privileges p
                        WHERE p.defaclrole = (SELECT oid FROM me) AND p.defaclnamespace = 'public'::regnamespace AND p.defaclobjtype::text = x.t)),
-- What a forged dump would leave behind (defence in depth; the signed manifest is the gate): code the superuser could run.
risky_functions AS (
    SELECT n.nspname || '.' || p.proname || ' (' || l.lanname || CASE WHEN p.prosecdef THEN ', SECURITY DEFINER' ELSE '' END || ')' AS name
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
     WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
       AND (p.prosecdef OR NOT l.lanpltrusted OR l.lanname NOT IN ('sql', 'plpgsql'))
       -- ARV-111a: Ariva's SECURITY DEFINER functions (script 0054, the retention purge the Cronz job calls; script 0072,
       -- ARV-207, the key ring purge the Cronz key lifecycle job calls), each exactly as shipped: its name and arguments, the
       -- migration login as owner, plpgsql, its settings (search path and time zone), the SHA-256 of its body
       -- (OpsScriptTests keeps these hashes equal to the scripts) and its grants: EXECUTE for the owner and ariva_cronz only
       -- (ARV-083a, the role of the Cronz jobs that call them; ariva_runtime on a database behind script 0074), without grant
       -- option (a NULL ACL would mean PUBLIC may run it). Anything else under their names is reported.
       AND NOT (p.oid = to_regprocedure('public.ariva_retention_purge(text, integer)') AND p.proowner = (SELECT oid FROM me)
                AND l.lanname = 'plpgsql' AND p.proconfig = ARRAY['search_path=pg_catalog, pg_temp', 'TimeZone=UTC']
                AND encode(sha256(convert_to(p.prosrc, 'UTF8')), 'hex') = '535ad38b539587c302aa4ea85e1c3508d55e5d5df6bf4f0675bbadf0076f78d7'
                AND p.proacl IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) a
                                 WHERE a.grantee NOT IN ((SELECT oid FROM me), (SELECT CASE WHEN behind THEN 'ariva_runtime'::regrole::oid
                                                                                           ELSE coalesce(to_regrole('ariva_cronz')::oid, 0::oid) END FROM pinned_at))
                                    OR a.is_grantable))
       AND NOT (p.oid = to_regprocedure('public.ariva_data_protection_key_purge(integer, integer)') AND p.proowner = (SELECT oid FROM me)
                AND l.lanname = 'plpgsql' AND p.proconfig = ARRAY['search_path=pg_catalog, pg_temp', 'TimeZone=UTC']
                AND encode(sha256(convert_to(p.prosrc, 'UTF8')), 'hex') = '9d886614f2343b230d50749c2f10b04df075e698c8758c163f80ca9ff15d975b'
                AND p.proacl IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) a
                                 WHERE a.grantee NOT IN ((SELECT oid FROM me), (SELECT CASE WHEN behind THEN 'ariva_runtime'::regrole::oid
                                                                                           ELSE coalesce(to_regrole('ariva_cronz')::oid, 0::oid) END FROM pinned_at))
                                    OR a.is_grantable))),
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
-- create subscriptions; and (ARV-263) pg_maintain, PostgreSQL 17's VACUUM, ANALYZE, CLUSTER, REINDEX, REFRESH and LOCK TABLE
-- on every relation, with which its holder can lock the hosts out of any table; and (ARV-263b) pg_read_all_stats and
-- pg_monitor (which holds it), with which its holder reads every session's statement text, literals included, in
-- pg_stat_activity and pg_stat_statements, and every relation's statistics, and pg_read_all_settings (also in pg_monitor),
-- which reads the superuser-only settings too (primary_conninfo can hold a replication password, archive_command and
-- restore_command a program to run, and the server's file paths). A DBA's own monitoring login passes when named with
-- --allow-login.
powerful AS (
    SELECT oid, rolname FROM pg_roles
     WHERE rolsuper OR rolcreaterole OR rolreplication OR rolbypassrls
        OR rolname IN ('pg_execute_server_program', 'pg_read_server_files', 'pg_write_server_files', 'pg_read_all_data',
                       'pg_write_all_data', 'pg_signal_backend', 'pg_create_subscription', 'pg_maintain', 'pg_read_all_stats',
                       'pg_monitor', 'pg_read_all_settings')),
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
       AND (r.oid IN (SELECT g.oid FROM runtime_grantees g) OR NOT r.rolname = ANY (string_to_array(:'allowed_logins', ',')))
    UNION
    SELECT r.rolname || ' (holds ' || p.rolname || ')'
      FROM held h JOIN pg_roles r ON r.oid = h.member JOIN powerful p ON p.oid = h.roleid
     WHERE h.member <> 10 AND (r.oid IN (SELECT g.oid FROM runtime_grantees g) OR NOT r.rolname = ANY (string_to_array(:'allowed_logins', ',')))
       -- ARV-263b: initdb's own memberships among PostgreSQL's predefined roles (pg_monitor holds pg_read_all_settings,
       -- pg_read_all_stats and pg_stat_scan_tables) are the server's: a pair whose rows are all initdb's as it made them
       -- (granted by the bootstrap superuser, without the admin option) passes. One granted since (to pg_monitor too), a
       -- second grant of the same pair or initdb's row given the admin option is reported, and so is a login that reaches a
       -- powerful role through initdb's memberships (no row of its own).
       AND NOT (EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = h.member AND m.roleid = h.roleid)
                AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = h.member AND m.roleid = h.roleid
                                                                 AND (m.oid >= 16384 OR m.admin_option OR m.grantor <> 10)))),
-- ARV-183 review: Ariva's group roles hold no role, and the named reader login only the reader role (the runtime login's
-- own line checks it the same way).
held_by_ariva AS (
    SELECT r.rolname || ' holds ' || g.rolname AS name
      FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member JOIN pg_roles g ON g.oid = m.roleid
     WHERE r.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability')
        OR (:'reader' <> '' AND r.rolname = :'reader' AND g.rolname <> 'ariva_validation_reader')),
-- ARV-183 review: settings made for a role's sessions (ALTER ROLE ... SET), in this database or in every one, but the
-- runtime login's statement timeout (script 0063), temporary file cap (script 0069, ARV-290), idle transaction limit and TCP
-- keepalives (script 0070, ARV-292) in this database and the
-- names --allow-setting gives; settings for every
-- session of the database are planted_objects' line.
role_settings AS (
    SELECT coalesce(r.rolname, 'role ' || s.setrole) || ' ' || split_part(c, '=', 1) AS name
      FROM pg_db_role_setting s LEFT JOIN pg_roles r ON r.oid = s.setrole CROSS JOIN LATERAL unnest(s.setconfig) c
     WHERE s.setrole <> 0 AND s.setdatabase IN (0, (SELECT oid FROM pg_database WHERE datname = current_database()))
       AND NOT split_part(c, '=', 1) = ANY (string_to_array(:'allowed_settings', ','))
       AND NOT (split_part(c, '=', 1) IN ('statement_timeout', 'temp_file_limit', 'idle_in_transaction_session_timeout', 'tcp_keepalives_idle',
                                          'tcp_keepalives_interval', 'tcp_keepalives_count') AND s.setdatabase <> 0
                AND s.setrole IN (SELECT m.member FROM pg_auth_members m WHERE m.roleid = 'ariva_runtime'::regrole))),
-- ARV-290 review: privileges on parameters (GRANT SET or ALTER SYSTEM ON PARAMETER), which are the cluster's, not the
-- database's: SET on temp_file_limit, without grant option, to the migration login or ariva_migration (the chart's init script,
-- grant-settings), the bootstrap superuser's own, and what a login --allow-login names holds. SET on another superuser
-- setting would let its holder change it for other roles' sessions (ALTER ROLE ... SET), so anything else is reported.
parameter_grants AS (
    SELECT p.parname || ' ' || a.privilege_type || CASE WHEN a.is_grantable THEN ' with grant option' ELSE '' END || ' to '
           || CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE coalesce(r.rolname::text, 'role ' || a.grantee) END AS name
      FROM pg_parameter_acl p CROSS JOIN LATERAL aclexplode(p.paracl) a LEFT JOIN pg_roles r ON r.oid = a.grantee
     WHERE a.grantee <> 10
       AND NOT (p.parname = 'temp_file_limit' AND a.privilege_type = 'SET' AND NOT a.is_grantable
                AND r.rolname IN (current_user, 'ariva_migration'))
       AND (a.grantee IN (SELECT g.oid FROM runtime_grantees g) OR NOT coalesce(r.rolname = ANY (string_to_array(:'allowed_logins', ',')), false))),
-- ARV-263 (ARV-262 review, N6): the system catalog's own functions (below oid 16384) grant no role more than initdb did
-- (pg_init_privs, or EXECUTE for PUBLIC and the owner where it recorded none): EXECUTE on lo_export, lo_import or
-- pg_read_binary_file reads or writes the server's files as whoever holds it, and a grant to ariva_runtime gives that to the
-- hosts. A NULL ACL is the default one (EXECUTE for PUBLIC), so one written over initdb's revoke is reported too; a grant
-- option counts as more; a DBA's own login named with --allow-login passes, but never the runtime role or a login of it.
system_function_grants AS (
    SELECT regexp_replace(p.oid::regprocedure::text || ' ' || a.privilege_type || CASE WHEN a.is_grantable THEN ' with grant option' ELSE '' END || ' to '
                          || CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE coalesce(r.rolname::text, 'role ' || a.grantee) END, '[[:cntrl:]]', '?', 'g') AS name
      FROM pg_proc p CROSS JOIN LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a LEFT JOIN pg_roles r ON r.oid = a.grantee
     WHERE p.oid < 16384
       AND NOT EXISTS (SELECT 1 FROM aclexplode(coalesce((SELECT ip.initprivs FROM pg_init_privs ip
                                                          WHERE ip.objoid = p.oid AND ip.classoid = 'pg_proc'::regclass AND ip.objsubid = 0),
                                                         acldefault('f', p.proowner))) b
                        WHERE b.grantee = a.grantee AND b.privilege_type = a.privilege_type AND (b.is_grantable OR NOT a.is_grantable))
       AND (a.grantee IN (SELECT g.oid FROM runtime_grantees g) OR NOT coalesce(r.rolname = ANY (string_to_array(:'allowed_logins', ',')), false))),
-- ARV-263 (ARV-189 review, round 3): no toast table grants the runtime role or a login that holds it anything, table-wide or
-- on a column, PUBLIC's grants included (a migrated database's toast tables have no grant: their owner's privileges only),
-- and none of them may use pg_toast, or a temporary toast schema: a grant on the toast table behind a table it may not read
-- hands it that table's long values. Only a toast table with grants of its own is looked at, so a server with many chunks
-- costs little.
toast_grants AS (
    SELECT DISTINCT regexp_replace(c.oid::regclass::text || ' to ' || pg_get_userbyid(r.oid), '[[:cntrl:]]', '?', 'g') AS name
      FROM pg_class c CROSS JOIN runtime_grantees r
     WHERE c.relkind = 't'
       AND (c.relacl IS NOT NULL OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 AND a.attacl IS NOT NULL))
       AND (EXISTS (SELECT 1 FROM table_privileges t WHERE has_table_privilege(r.oid, c.oid, t.p))
            OR EXISTS (SELECT 1 FROM column_privileges t WHERE has_any_column_privilege(r.oid, c.oid, t.p)))
    UNION ALL
    SELECT regexp_replace('USAGE on schema ' || n.nspname || ' to ' || pg_get_userbyid(r.oid), '[[:cntrl:]]', '?', 'g')
      FROM pg_namespace n CROSS JOIN runtime_grantees r
     WHERE n.nspname ~ '^pg_toast(_temp_[0-9]+)?$' AND has_schema_privilege(r.oid, n.oid, 'USAGE')),
-- ARV-290 review (L3): the runtime login's own settings in this database, which role_settings allows by name: the migration job
-- sets both on every release, and the migration login may change them in between (it may set temp_file_limit, to -1 or to
-- terabytes too). Each is judged by its value, in the form the job writes and within the bounds the job holds it to; at most
-- seven digits are read, so no text makes the cast fail.
runtime_caps AS (
    SELECT statement_timeout, temp_file_limit, idle_limit, keepalives,
           coalesce(substring(statement_timeout FROM '^([1-9][0-9]{0,6})s$')::int BETWEEN :'timeout_min'::int AND :'timeout_max'::int, false) AS timeout_ok,
           coalesce(substring(temp_file_limit FROM '^([1-9][0-9]{0,6})MB$')::int BETWEEN :'temp_min'::int AND :'temp_max'::int, false) AS temp_ok,
           coalesce(substring(idle_limit FROM '^([1-9][0-9]{0,6})s$')::int BETWEEN :'idle_min'::int AND :'idle_max'::int, false) AS idle_ok,
           keepalives IS NOT DISTINCT FROM (:'keepalive_idle' || '/' || :'keepalive_interval' || '/' || :'keepalive_count') AS keepalives_ok
      FROM (SELECT max(substr(c, length('statement_timeout=') + 1)) FILTER (WHERE split_part(c, '=', 1) = 'statement_timeout') AS statement_timeout,
                   max(substr(c, length('temp_file_limit=') + 1)) FILTER (WHERE split_part(c, '=', 1) = 'temp_file_limit') AS temp_file_limit,
                   max(substr(c, length('idle_in_transaction_session_timeout=') + 1)) FILTER (WHERE split_part(c, '=', 1) = 'idle_in_transaction_session_timeout') AS idle_limit,
                   concat_ws('/', max(substr(c, length('tcp_keepalives_idle=') + 1)) FILTER (WHERE split_part(c, '=', 1) = 'tcp_keepalives_idle'),
                                  max(substr(c, length('tcp_keepalives_interval=') + 1)) FILTER (WHERE split_part(c, '=', 1) = 'tcp_keepalives_interval'),
                                  max(substr(c, length('tcp_keepalives_count=') + 1)) FILTER (WHERE split_part(c, '=', 1) = 'tcp_keepalives_count')) AS keepalives
              FROM pg_db_role_setting s CROSS JOIN LATERAL unnest(s.setconfig) c
             WHERE s.setrole = (SELECT oid FROM pg_roles WHERE rolname = :'runtime')
               AND s.setdatabase = (SELECT oid FROM pg_database WHERE datname = current_database())) raw),
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
-- ARV-083a: a host login (it signs in and holds ariva_runtime and exactly one host role, nothing else; the migration job's
-- ariva_ensure_host_login) is one of the deployment's logins; any other member of Ariva's roles but the named ones is reported.
extra_members AS (
    SELECT g.rolname || ' has member ' || r.rolname AS name
      FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles r ON r.oid = m.member
     WHERE g.rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability') AND r.rolname <> current_user
       AND NOT (g.rolname = 'ariva_runtime' AND r.rolname = :'runtime')
       AND NOT (g.rolname = 'ariva_validation_reader' AND r.rolname = :'reader')
       AND NOT (r.rolcanlogin AND g.rolname IN ('ariva_runtime', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability')
                AND (SELECT count(*) FROM pg_auth_members x WHERE x.member = r.oid) = 2
                AND EXISTS (SELECT 1 FROM pg_auth_members x JOIN pg_roles h ON h.oid = x.roleid WHERE x.member = r.oid AND h.rolname = 'ariva_runtime')
                AND EXISTS (SELECT 1 FROM pg_auth_members x JOIN pg_roles h ON h.oid = x.roleid WHERE x.member = r.oid
                                                                                                AND h.rolname IN ('ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability'))))
SELECT 'schema_version|' || (SELECT count(*) FROM public.schema_version) || ' scripts, latest ' || coalesce((SELECT max(script_name) FROM public.schema_version), 'none')
UNION ALL SELECT 'hypertables|' || (SELECT count(*) FROM timescaledb_information.hypertables) || ' hypertables, '
    || (SELECT count(*) FROM timescaledb_information.jobs WHERE application_name NOT LIKE 'Telemetry%') || ' TimescaleDB jobs'
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'owners|every object belongs to ' || current_user
                      ELSE 'FAIL owners|' || count(*) || ' objects belong to another role, for example ' || min(name) END FROM foreign_owned
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'runtime role|ariva_runtime and the logins that hold it have no CREATE on public, on any other schema or on the database'
                      ELSE 'FAIL runtime role|may create objects: ' || string_agg(name, ', ' ORDER BY name) END FROM runtime_create
UNION ALL SELECT CASE WHEN (SELECT n FROM runtime_owned) = 0 THEN 'runtime role|owns nothing' ELSE 'FAIL runtime role|owns ' || (SELECT n FROM runtime_owned) || ' relations' END
UNION ALL SELECT CASE
        WHEN (SELECT behind FROM pinned_at) AND :'after_restore' = '1'
            THEN 'runtime privileges|not compared: the database is at ' || (SELECT latest FROM pinned_at) || ' and the list at ' || :'runtime_privileges_at'
                 || ' (run the migration job, then check again before starting the hosts)'
        WHEN (SELECT behind FROM pinned_at)
            THEN 'FAIL runtime privileges|not compared: the database is at ' || (SELECT latest FROM pinned_at) || ' and the list at ' || :'runtime_privileges_at'
                 || ' (run the migration job, then check again)'
        WHEN count(*) = 0 THEN 'runtime privileges|ariva_runtime''s grants on public as the scripts leave them (' || (SELECT count(*) FROM pinned_privileges)
                 || ' relations, the base every host login holds; nothing on any other)'
        ELSE 'FAIL runtime privileges|' || count(*) || ' relations differ from the scripts: '
             || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM privilege_drift ORDER BY name LIMIT 10) first_ten)
             || CASE WHEN count(*) > 10 THEN ', and ' || (count(*) - 10) || ' more' ELSE '' END END FROM privilege_drift
UNION ALL SELECT CASE
        WHEN (SELECT behind FROM pinned_at) AND :'after_restore' = '1' THEN 'host privileges|not compared: the database is behind the list (as runtime privileges)'
        WHEN (SELECT behind FROM pinned_at) THEN 'FAIL host privileges|not compared: the database is behind the list (as runtime privileges)'
        WHEN count(*) = 0 THEN 'host privileges|each host role''s grants on public as the scripts leave them (' || (SELECT count(*) FROM pinned_host_privileges)
                 || ' grants of ' || replace(:'host_roles', ',', ', ') || '; nothing on any other relation)'
        ELSE 'FAIL host privileges|' || count(*) || ' grants differ from the scripts: '
             || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM host_privilege_drift ORDER BY name LIMIT 10) first_ten)
             || CASE WHEN count(*) > 10 THEN ', and ' || (count(*) - 10) || ' more' ELSE '' END END FROM host_privilege_drift
UNION ALL SELECT CASE
        WHEN (SELECT behind FROM pinned_at) AND :'after_restore' = '1' THEN 'views|not compared: the database is behind the list (as runtime privileges)'
        WHEN (SELECT behind FROM pinned_at) THEN 'FAIL views|not compared: the database is behind the list (as runtime privileges)'
        WHEN count(*) = 0 THEN 'views|the views of public are the scripts'' ' || (SELECT count(*) FROM pinned_views) || ', each reading what its script reads'
        ELSE 'FAIL views|' || count(*) || ' views differ from the scripts: '
             || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM view_drift ORDER BY name LIMIT 10) first_ten)
             || CASE WHEN count(*) > 10 THEN ', and ' || (count(*) - 10) || ' more' ELSE '' END END FROM view_drift
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'internal privileges|ariva_runtime: each inheritance child no wider than its parent and only TimescaleDB''s chunks inheriting, each compressed chunk no wider than its hypertable, each continuous aggregate''s table and views at most SELECT, no view wider than what it reads, nothing on any other relation created after initdb outside public and the extensions, no sequence it may set, no extension or system table granting more than it was created with'
                      ELSE 'FAIL internal privileges|' || count(*) || ' relations grant ariva_runtime more than these rules allow: '
                           || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM internal_drift ORDER BY name LIMIT 50) first_ones)
                           || CASE WHEN count(*) > 50 THEN ', and ' || (count(*) - 50) || ' more' ELSE '' END END FROM internal_drift
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'runtime grants|every grant of a relation or column to the runtime role goes to ariva_runtime without grant option, none to a role that holds it, and none of Ariva''s relations grants PUBLIC anything'
                      ELSE 'FAIL runtime grants|' || count(*) || ' grants to a role that holds ariva_runtime, with grant option or to PUBLIC: '
                           || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM stray_grants ORDER BY name LIMIT 50) first_ones)
                           || CASE WHEN count(*) > 50 THEN ', and ' || (count(*) - 50) || ' more' ELSE '' END END FROM stray_grants
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'toast|no toast table grants ariva_runtime or a login that holds it anything, and none of them may use pg_toast'
                      ELSE 'FAIL toast|' || count(*) || ' grants on toast tables or their schemas: '
                           || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM toast_grants ORDER BY name LIMIT 50) first_ones)
                           || CASE WHEN count(*) > 50 THEN ', and ' || (count(*) - 50) || ' more' ELSE '' END END FROM toast_grants
UNION ALL SELECT CASE WHEN count(*) = 0 AND NOT (SELECT behind FROM pinned_at) THEN 'default privileges|none: a table or sequence a later script creates gives no role anything until its script grants it (script 0074)'
                      WHEN count(*) = 0 THEN 'default privileges|script 0001''s only: the migration login''s tables in public give ariva_runtime SELECT, INSERT, UPDATE and DELETE, its sequences there SELECT and USAGE'
                      ELSE 'FAIL default privileges|' || count(*) || ' entries differ from the scripts'': ' || string_agg(name, '; ' ORDER BY name) END FROM default_drift
UNION ALL SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability')
                                     AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolcanlogin OR rolreplication OR rolbypassrls))
                      THEN 'FAIL group roles|an Ariva group role is privileged or can sign in' ELSE 'group roles|NOLOGIN, unprivileged' END
UNION ALL SELECT 'migration login|' || current_user || ' owns the database; not superuser, replication or bypassrls'
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'group members|only the migration login and the named logins'
                      ELSE 'FAIL group members|' || string_agg(name, ', ' ORDER BY name) || ' (name this deployment''s logins with --runtime-login and --reader-login)' END
                 FROM extra_members
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'held roles|Ariva''s group roles hold no role' || CASE WHEN :'reader' = '' THEN '' ELSE ', ' || :'reader' || ' only ariva_validation_reader' END
                      ELSE 'FAIL held roles|' || string_agg(name, ', ' ORDER BY name) END FROM held_by_ariva
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'functions|no C, untrusted-language or SECURITY DEFINER function outside the extensions, but the retention purges as scripts 0054 and 0072 ship them'
                      ELSE 'FAIL functions|' || count(*) || ' outside the extensions: ' || string_agg(name, ', ' ORDER BY name) END FROM risky_functions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'languages|plpgsql only'
                      ELSE 'FAIL languages|' || string_agg(name, ', ' ORDER BY name) END FROM other_languages
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'extensions|plpgsql, timescaledb and timescaledb_toolkit'
                                                || CASE WHEN :'allowed_extensions' = '' THEN ' only' ELSE ', and allowed: ' || replace(:'allowed_extensions', ',', ', ') END
                      ELSE 'FAIL extensions|' || string_agg(name, ', ' ORDER BY name) || ' (a DBA-provided server''s own: name it with --allow-extension)' END FROM other_extensions
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'plpgsql|its three handler functions and its language only'
                      ELSE 'FAIL plpgsql|also holds ' || string_agg(name, ', ' ORDER BY name) || ' (added to the extension, where the function checks do not look)' END FROM plpgsql_members
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'logins|no role but the bootstrap superuser and the migration login is a superuser or has createrole, replication or bypassrls, and none holds such a role or one of the server, data, signal, maintenance or statistics roles'
                                                || CASE WHEN :'allowed_logins' = '' THEN '' ELSE ', but allowed: ' || replace(:'allowed_logins', ',', ', ') END
                      ELSE 'FAIL logins|' || string_agg(name, ', ' ORDER BY name) || ' (a DBA''s own login: name it with --allow-login)' END FROM privileged_roles
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'role settings|none but the runtime login''s statement timeout, temporary file cap, idle transaction limit and keepalives'
                                                || CASE WHEN :'allowed_settings' = '' THEN '' ELSE ', and allowed: ' || replace(:'allowed_settings', ',', ', ') END
                      ELSE 'FAIL role settings|' || string_agg(name, ', ' ORDER BY name) || ' (ALTER ROLE ... SET; a DBA''s own: name it with --allow-setting)' END FROM role_settings
UNION ALL SELECT CASE WHEN has_parameter_privilege('temp_file_limit', 'SET') THEN 'temp file cap|the migration login may set temp_file_limit on the runtime login (ARV-290)'
                      ELSE 'FAIL temp file cap|the migration login may not set temp_file_limit, so the migration job stops (ARV-290): run grant-settings' END
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'parameter privileges|SET on temp_file_limit to the migration login or ariva_migration only'
                                                || CASE WHEN :'allowed_logins' = '' THEN '' ELSE ', and the allowed logins'' own' END
                      ELSE 'FAIL parameter privileges|' || string_agg(name, ', ' ORDER BY name)
                           || ' (GRANT ... ON PARAMETER, the cluster''s: find out who granted it, wiki 10 section 4.19)' END FROM parameter_grants
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'system function grants|the system catalog''s functions grant what initdb gave and nothing more'
                                                || CASE WHEN :'allowed_logins' = '' THEN '' ELSE ', but the allowed logins'' own' END
                      ELSE 'FAIL system function grants|' || count(*) || ' beyond initdb''s: '
                           || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM system_function_grants ORDER BY name LIMIT 50) first_ones)
                           || CASE WHEN count(*) > 50 THEN ', and ' || (count(*) - 50) || ' more' ELSE '' END
                           || ' (GRANT on a function of pg_catalog, a superuser''s: find out who granted it, then REVOKE it)' END FROM system_function_grants
UNION ALL SELECT CASE WHEN timeout_ok AND temp_ok AND idle_ok AND keepalives_ok
                      THEN 'runtime caps|' || :'runtime' || ' in this database: statement_timeout ' || statement_timeout || ', temp_file_limit ' || temp_file_limit
                           || ', idle_in_transaction_session_timeout ' || idle_limit || ', tcp_keepalives ' || keepalives
                      ELSE 'FAIL runtime caps|' || :'runtime' || ' in this database: '
                           || concat_ws(', ', CASE WHEN timeout_ok THEN NULL ELSE 'statement_timeout ' || coalesce(statement_timeout, 'not set') END,
                                              CASE WHEN temp_ok THEN NULL ELSE 'temp_file_limit ' || coalesce(temp_file_limit, 'not set') END,
                                              CASE WHEN idle_ok THEN NULL ELSE 'idle_in_transaction_session_timeout ' || coalesce(idle_limit, 'not set') END,
                                              CASE WHEN keepalives_ok THEN NULL ELSE 'tcp_keepalives ' || coalesce(nullif(keepalives, ''), 'not set') END)
                           || ' (the migration job sets ' || :'timeout_min' || 's to ' || :'timeout_max' || 's, ' || :'temp_min' || 'MB to '
                           || :'temp_max' || 'MB, ' || :'idle_min' || 's to ' || :'idle_max' || 's and keepalives ' || :'keepalive_idle' || '/'
                           || :'keepalive_interval' || '/' || :'keepalive_count' || '; ARV-096d, ARV-290, ARV-292): run the migration job' END
                 FROM runtime_caps WHERE :'runtime' <> '' AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'runtime')
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
        -- ARV-083a: ariva_runtime and at most one host role (a host's login), nothing else.
        WHEN NOT EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                          WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = :'runtime') AND g.rolname = 'ariva_runtime')
          OR EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                      WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = :'runtime') AND g.rolname <> 'ariva_runtime'
                        AND g.rolname NOT IN ('ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability'))
          OR (SELECT count(*) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
               WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = :'runtime') AND g.rolname IN ('ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability')) > 1
            THEN 'FAIL runtime login|' || :'runtime' || ' holds roles other than ariva_runtime and one host role'
        WHEN has_schema_privilege(:'runtime', 'public', 'CREATE') THEN 'FAIL runtime login|' || :'runtime' || ' may create objects in public'
        ELSE 'runtime login|' || :'runtime' || ' holds ' || (SELECT string_agg(g.rolname::text, ' and ' ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                                                              WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = :'runtime')) || ' (DML only)' END;
SQL
)"
  # ARV-183 review: the system's own functions against template1's (a function replaced in place keeps its oid).
  report+=$'\n'"$(system_functions_line)"
  # ARV-262: Ariva's objects in public against a database the same scripts migrated, and TimescaleDB's members against the image's.
  local schema_line
  schema_line="$(schema_report check)" || schema_line="FAIL schema|could not be read as the migration login"
  report+=$'\n'"$schema_line"
  report+=$'\n'"$(extension_members_line)"
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
  local names name file kept=() passed=()
  if ((read == 0)) || [[ -n "$planted" ]]; then
    report+=$'\n''FAIL settings|the configuration files not read: the superuser reads nothing while the line above fails'
  elif names="$(file_settings)"; then
    while IFS='|' read -r name file; do
      [[ -n "$name$file" ]] || continue
      if [[ ",$settings_allowed," == *",$name,"* ]]; then passed+=("$name ($file)"); else kept+=("$name ($file)"); fi
    done <<<"$names"
    if ((${#kept[@]} == 0)); then
      # ARV-262 review: each allowed setting with the file it comes from, so a name allowed for one file is seen in another.
      local shown=""
      for name in "${passed[@]}"; do shown+="${shown:+, }$name"; done
      report+=$'\n'"settings|no setting from a file but the server's postgresql.conf (postgresql.auto.conf, a file an include names)${shown:+ but the allowed: $shown}"
    else
      local joined=""
      for name in "${kept[@]}"; do joined+="${joined:+, }$name"; done
      report+=$'\n'"FAIL settings|set outside the server's postgresql.conf: $joined: find out who, then ALTER SYSTEM RESET what postgresql.auto.conf holds, take out an include line and the file it names, and restart the server (a DBA's own: name it with --allow-setting)"
    fi
  else
    report+=$'\n''FAIL settings|the configuration files could not be read as the superuser over the local socket (run check in the database pod or on the server)'
  fi
  local line failed=0
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    # ARV-244 review: before a restore's update, only the failures are printed, and the two lines the update makes pass are
    # not counted (cmd_restore).
    if ((check_before_update == 1)); then
      if [[ "$line" == FAIL* && "$line" != "FAIL extension versions|"* && "$line" != "FAIL extension members|"* ]]; then
        failed=1
        printf 'FAIL  %s\n' "${line#FAIL }"
      fi
      continue
    fi
    if [[ "$line" == FAIL* ]]; then
      failed=1
      printf 'FAIL  %s\n' "${line#FAIL }"
    else
      printf 'ok    %s\n' "$line"
    fi
    [[ "$line" != *"|not compared:"* ]] || check_not_compared=1
  done <<<"$report"
  if ((check_before_update == 1)); then
    ((failed == 0)) || die "the restored database fails the check above before the extensions' update, which runs TimescaleDB's update scripts as the superuser on it: nothing was updated, and $DATABASE is restored at TimescaleDB $RESTORED_TIMESCALEDB. What it names came with the dump: treat the dump as untrusted (wiki 10 section 4.13), and restore a backup you trust into an empty database (a new volume)"
    say "the restored database passes the check before the extensions' update (extension versions and members aside)"
    return 0
  fi
  ((failed == 0)) || die "check failed"
}

# The settings any configuration file but the server's own postgresql.conf holds (ARV-183; ARV-262: not only the
# postgresql.auto.conf ALTER SYSTEM writes, but a file an include line names, or one written with COPY TO and included),
# one <name>|<file> line each: what the files say now (pg_file_settings, an entry that does not parse as "an unreadable
# entry") and what the server applied from them (pg_settings, which keeps a value read from a file removed since). Both
# show their files to a superuser only. The statement names nothing through the search path, and the session pins it to the
# catalog anyway; control characters in a value print as "?".
file_settings() {
  as_superuser --tuples-only --no-align <<'SQL'
SELECT pg_catalog.regexp_replace(x.line, '[[:cntrl:]]', '?', 'g') FROM (SELECT coalesce(s.name, 'an unreadable entry') OPERATOR(pg_catalog.||) '|' OPERATOR(pg_catalog.||) s.sourcefile AS line FROM pg_catalog.pg_file_settings s WHERE s.sourcefile OPERATOR(pg_catalog.<>) pg_catalog.current_setting('config_file') UNION SELECT s.name OPERATOR(pg_catalog.||) '|' OPERATOR(pg_catalog.||) s.sourcefile FROM pg_catalog.pg_settings s WHERE s.sourcefile OPERATOR(pg_catalog.<>) pg_catalog.current_setting('config_file')) x ORDER BY 1;
SQL
}

# ARV-262 (CWE-269, CWE-345): Ariva's objects as one <object>|<part> line per part: each relation of public outside the
# extensions (table, view, sequence, foreign table, composite type) with its kind and options, columns (type, not null,
# default, identity, generated, collation), constraints (and whether a trigger that enforces one is disabled), indexes,
# triggers (and whether each is enabled), rules, row security policies, inheritance, what each inheritance child lacks of
# its parent's row triggers, unique indexes and foreign keys (TimescaleDB copies them to every chunk, and an UPDATE fires
# the chunk's trigger) and a trigger, row security or policy it has that the parent does not, grants table-wide and per column (the owner's as "owner", grantors left out), a view's definition,
# a continuous aggregate's materialized-only switch, partial and direct views (TimescaleDB's internal numbers written as N)
# and whether it reads the materialization TimescaleDB keeps for it, a sequence's parameters and a hypertable's dimensions
# and compression switch; each function of public outside the extensions with its language, kind, arguments and their
# defaults, result, markings, support function, settings, the SHA-256 of its body (a SQL-standard body too) and its
# grants; every schema but the temporary ones (the system's, TimescaleDB's and public too) with its owner and grants; the database's
# owner and its grants beyond PostgreSQL's default and the CONNECT the migration job gives this deployment's runtime and
# validation reader logins (a REVOKE narrows it and passes); and each type, operator, collation,
# conversion, text search configuration or dictionary, operator class or family in public and cast created after initdb
# outside the extensions, and each of these or a relation created after initdb in pg_catalog or information_schema (a
# migrated database has none); and (ARV-263) each TimescaleDB job, which a dump carries as a row of TimescaleDB's catalog and
# which runs as its owner, as "job <procedure> on <hypertable>" (a continuous aggregate's by its view) with its owner,
# schedule, retries, whether it is scheduled (but TimescaleDB's telemetry job's), check function and configuration, the
# hypertable ids in it as the hypertables they name and a
# retention policy's period as "within the retention bounds" when ARIVA_RETENTION_DAYS allows it (each deployment
# configures its own). Each part is hashed as PostgreSQL prints it but for the one form a dump and restore changes in a CHECK
# constraint (a varchar IN list: ((ARRAY['a'::character varying])::text[]) migrated, (ARRAY[('a'::character varying)::text])
# restored, both hashed as (ARRAY['a'::character varying])); printed, a backslash doubles, a newline is \n and any other
# control character "?" (display only). Modes: full (those lines), list (<object>=<hash>, the first 32 hex digits of the
# SHA-256 of an object's parts in byte order: ARIVA_SCHEMA's entries) and check (the report line, compared once the
# database is at ARIVA_SCHEMA_AT).
schema_report() {
  as_migration_catalog --tuples-only --no-align --set=mode="$1" --set=pinned="$ARIVA_SCHEMA" --set=schema_at="$ARIVA_SCHEMA_AT" \
    --set=after_restore="$check_after_restore" --set=retention_days="$ARIVA_RETENTION_DAYS" <<'SQL'
WITH extension_members AS (
    SELECT d.classid, d.objid FROM pg_depend d WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e'),
relations AS (
    SELECT c.* FROM pg_class c
     WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f', 'c')
       AND NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.classid = 'pg_class'::regclass AND e.objid = c.oid)),
functions AS (
    SELECT p.*, p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' AS object FROM pg_proc p
     WHERE p.pronamespace = 'public'::regnamespace
       AND NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.classid = 'pg_proc'::regclass AND e.objid = p.oid)),
children AS (
    SELECT c.oid AS parent, i.inhrelid AS child FROM relations c JOIN pg_inherits i ON i.inhparent = c.oid),
namespaces AS (
    SELECT n.* FROM pg_namespace n WHERE n.nspname !~ '^pg_(toast_)?temp_'),
acl_entries AS (
    SELECT c.oid AS relation, NULL::name AS attname,
           CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = c.relowner THEN 'owner' ELSE pg_get_userbyid(a.grantee) END AS grantee,
           a.privilege_type, a.is_grantable
      FROM relations c CROSS JOIN LATERAL aclexplode(coalesce(c.relacl, acldefault(CASE WHEN c.relkind = 'S' THEN 's' ELSE 'r' END::"char", c.relowner))) a
    UNION ALL
    SELECT c.oid, t.attname, CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = c.relowner THEN 'owner' ELSE pg_get_userbyid(a.grantee) END,
           a.privilege_type, a.is_grantable
      FROM relations c JOIN pg_attribute t ON t.attrelid = c.oid AND t.attnum > 0 AND NOT t.attisdropped AND t.attacl IS NOT NULL
      CROSS JOIN LATERAL aclexplode(t.attacl) a),
cagg AS (
    SELECT to_regclass(format('%I.%I', a.user_view_schema, a.user_view_name)) AS relation, a.materialized_only,
           to_regclass(format('%I.%I', a.partial_view_schema, a.partial_view_name)) AS partial_view,
           to_regclass(format('%I.%I', a.direct_view_schema, a.direct_view_name)) AS direct_view,
           to_regclass(format('%I.%I', h.schema_name, h.table_name)) AS materialization
      FROM _timescaledb_catalog.continuous_agg a LEFT JOIN _timescaledb_catalog.hypertable h ON h.id = a.mat_hypertable_id
     WHERE a.user_view_schema = 'public'),
-- ARV-263: TimescaleDB's jobs, each named by its procedure and the hypertable it acts on (a continuous aggregate's
-- materialization by the aggregate's view), and the retention bounds each hypertable of ARIVA_RETENTION_DAYS has.
job_targets AS (
    SELECT h.id, CASE WHEN a.mat_hypertable_id IS NULL THEN format('%I.%I', h.schema_name, h.table_name)
                      ELSE format('%I.%I', a.user_view_schema, a.user_view_name) END AS name
      FROM _timescaledb_catalog.hypertable h LEFT JOIN _timescaledb_catalog.continuous_agg a ON a.mat_hypertable_id = h.id),
job_retention_days AS (
    SELECT format('%I.%I', 'public', split_part(e, '=', 1)) AS name, split_part(split_part(e, '=', 2), '-', 1)::integer AS low,
           split_part(split_part(e, '=', 2), '-', 2)::integer AS high
      FROM unnest(string_to_array(:'retention_days', ';')) e),
jobs AS (
    SELECT j.*, 'job ' || format('%I.%I', j.proc_schema, j.proc_name)
           || CASE WHEN j.hypertable_id IS NULL THEN ''
                   ELSE ' on ' || coalesce((SELECT t.name FROM job_targets t WHERE t.id = j.hypertable_id), 'hypertable ' || j.hypertable_id || ' (no such hypertable)') END AS object
      FROM _timescaledb_catalog.bgw_job j),
-- public's, and those created after initdb in the system's schemas (ARV-262 review: an overload or an operator there is
-- picked up by any statement that names it, a superuser's included).
object_schemas AS (
    SELECT n.oid, n.nspname = 'public' AS all_of_it FROM pg_namespace n WHERE n.nspname IN ('public', 'pg_catalog', 'information_schema')),
other_objects AS (
    SELECT 'pg_type'::regclass AS classid, t.oid FROM pg_type t JOIN object_schemas s ON s.oid = t.typnamespace AND (s.all_of_it OR t.oid >= 16384)
     WHERE t.typrelid = 0 AND NOT EXISTS (SELECT 1 FROM pg_type a WHERE a.typarray = t.oid)
    UNION ALL SELECT 'pg_operator'::regclass, o.oid FROM pg_operator o JOIN object_schemas s ON s.oid = o.oprnamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_collation'::regclass, o.oid FROM pg_collation o JOIN object_schemas s ON s.oid = o.collnamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_conversion'::regclass, o.oid FROM pg_conversion o JOIN object_schemas s ON s.oid = o.connamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_ts_config'::regclass, o.oid FROM pg_ts_config o JOIN object_schemas s ON s.oid = o.cfgnamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_ts_dict'::regclass, o.oid FROM pg_ts_dict o JOIN object_schemas s ON s.oid = o.dictnamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_opclass'::regclass, o.oid FROM pg_opclass o JOIN object_schemas s ON s.oid = o.opcnamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_opfamily'::regclass, o.oid FROM pg_opfamily o JOIN object_schemas s ON s.oid = o.opfnamespace AND (s.all_of_it OR o.oid >= 16384)
    UNION ALL SELECT 'pg_class'::regclass, c.oid FROM pg_class c JOIN object_schemas s ON s.oid = c.relnamespace AND NOT s.all_of_it AND c.oid >= 16384
    UNION ALL SELECT 'pg_cast'::regclass, o.oid FROM pg_cast o WHERE o.oid >= 16384),
parts (object, part) AS (
    SELECT c.relname::text, 'kind ' || c.relkind::text || ', persistence ' || c.relpersistence::text || ', row security ' || c.relrowsecurity::text
           || ', forced ' || c.relforcerowsecurity::text || ', options ' || coalesce(array_to_string(c.reloptions, ','), 'none')
      FROM relations c
    UNION ALL
    SELECT c.relname, 'column ' || t.attname || ' ' || format_type(t.atttypid, t.atttypmod) || CASE WHEN t.attnotnull THEN ' not null' ELSE '' END
           || coalesce(' default ' || pg_get_expr(d.adbin, d.adrelid), '') || CASE WHEN t.attidentity <> '' THEN ' identity ' || t.attidentity::text ELSE '' END
           || CASE WHEN t.attgenerated <> '' THEN ' generated ' || t.attgenerated::text ELSE '' END
           || CASE WHEN t.attcollation <> 0 AND t.attcollation <> (SELECT y.typcollation FROM pg_type y WHERE y.oid = t.atttypid)
                   THEN ' collate ' || t.attcollation::regcollation::text ELSE '' END
      FROM relations c JOIN pg_attribute t ON t.attrelid = c.oid AND t.attnum > 0 AND NOT t.attisdropped
      LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = t.attnum
    UNION ALL
    SELECT c.relname, 'constraint ' || o.conname || ' ' || pg_get_constraintdef(o.oid) || CASE WHEN o.convalidated THEN '' ELSE ' (not validated)' END
           || CASE WHEN EXISTS (SELECT 1 FROM pg_trigger g WHERE g.tgconstraint = o.oid AND g.tgenabled <> 'O') THEN ' (a trigger of it disabled)' ELSE '' END
      FROM relations c JOIN pg_constraint o ON o.conrelid = c.oid
    UNION ALL
    SELECT c.relname, 'index ' || pg_get_indexdef(i.indexrelid) || CASE WHEN i.indisvalid THEN '' ELSE ' (invalid)' END
      FROM relations c JOIN pg_index i ON i.indrelid = c.oid
    UNION ALL
    SELECT c.relname, 'trigger ' || pg_get_triggerdef(t.oid) || ' (enabled ' || t.tgenabled::text || ')'
      FROM relations c JOIN pg_trigger t ON t.tgrelid = c.oid AND NOT t.tgisinternal
    UNION ALL
    SELECT c.relname, 'rule ' || pg_get_ruledef(r.oid)
      FROM relations c JOIN pg_rewrite r ON r.ev_class = c.oid AND r.rulename <> '_RETURN'
    UNION ALL
    SELECT c.relname, 'policy ' || p.polname || ' ' || p.polcmd::text || CASE WHEN p.polpermissive THEN ' permissive' ELSE ' restrictive' END
           || ' to ' || coalesce((SELECT string_agg(CASE WHEN r = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(r) END, ',' ORDER BY 1) FROM unnest(p.polroles) r), 'none')
           || coalesce(' using ' || pg_get_expr(p.polqual, p.polrelid), '') || coalesce(' check ' || pg_get_expr(p.polwithcheck, p.polrelid), '')
      FROM relations c JOIN pg_policy p ON p.polrelid = c.oid
    UNION ALL
    SELECT c.relname, 'inherits ' || i.inhparent::regclass::text
      FROM relations c JOIN pg_inherits i ON i.inhrelid = c.oid
    UNION ALL
    SELECT c.relname, 'child ' || k.child::regclass::text || ' lacks trigger ' || t.tgname
      FROM relations c JOIN children k ON k.parent = c.oid
      JOIN pg_trigger t ON t.tgrelid = c.oid AND NOT t.tgisinternal AND (t.tgtype::integer & 1) = 1
     WHERE NOT EXISTS (SELECT 1 FROM pg_trigger x
                        WHERE x.tgrelid = k.child AND x.tgname = t.tgname AND x.tgenabled = t.tgenabled
                          AND replace(pg_get_triggerdef(x.oid), ' ON ' || k.child::regclass::text || ' ', ' ON ' || c.oid::regclass::text || ' ')
                              = pg_get_triggerdef(t.oid))
    UNION ALL
    SELECT c.relname, 'child ' || k.child::regclass::text || ' lacks unique index '
           || regexp_replace(pg_get_indexdef(i.indexrelid), '^CREATE (UNIQUE )?INDEX .* ON (ONLY )?\S+ ', '')
      FROM relations c JOIN children k ON k.parent = c.oid JOIN pg_index i ON i.indrelid = c.oid AND i.indisunique
     WHERE NOT EXISTS (SELECT 1 FROM pg_index x
                        WHERE x.indrelid = k.child AND x.indisunique AND x.indisvalid AND x.indisready
                          AND regexp_replace(pg_get_indexdef(x.indexrelid), '^CREATE (UNIQUE )?INDEX .* ON (ONLY )?\S+ ', '')
                              = regexp_replace(pg_get_indexdef(i.indexrelid), '^CREATE (UNIQUE )?INDEX .* ON (ONLY )?\S+ ', ''))
    UNION ALL
    SELECT c.relname, 'child ' || k.child::regclass::text || ' has trigger ' || x.tgname || ' its parent lacks'
      FROM relations c JOIN children k ON k.parent = c.oid JOIN pg_trigger x ON x.tgrelid = k.child AND NOT x.tgisinternal
     WHERE NOT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = x.tgname AND NOT t.tgisinternal)
    UNION ALL
    SELECT c.relname, 'child ' || k.child::regclass::text || ' has row security or a policy'
      FROM relations c JOIN children k ON k.parent = c.oid JOIN pg_class r ON r.oid = k.child
     WHERE r.relrowsecurity OR r.relforcerowsecurity OR EXISTS (SELECT 1 FROM pg_policy y WHERE y.polrelid = k.child)
    UNION ALL
    SELECT c.relname, 'child ' || k.child::regclass::text || ' lacks foreign key ' || pg_get_constraintdef(o.oid)
      FROM relations c JOIN children k ON k.parent = c.oid JOIN pg_constraint o ON o.conrelid = c.oid AND o.contype = 'f'
     WHERE NOT EXISTS (SELECT 1 FROM pg_constraint x
                        WHERE x.conrelid = k.child AND x.contype = 'f' AND pg_get_constraintdef(x.oid) = pg_get_constraintdef(o.oid)
                          AND NOT EXISTS (SELECT 1 FROM pg_trigger g WHERE g.tgconstraint = x.oid AND g.tgenabled <> 'O'))
    UNION ALL
    SELECT c.relname, CASE WHEN e.attname IS NULL THEN 'grant ' ELSE 'column grant ' || e.attname || ' ' END || e.grantee || ' ' || e.privilege_type
           || CASE WHEN e.is_grantable THEN ' with grant option' ELSE '' END
      FROM relations c JOIN acl_entries e ON e.relation = c.oid
    UNION ALL
    SELECT c.relname, 'definition ' || regexp_replace(pg_get_viewdef(c.oid), '(_materialized_hypertable_|cagg_watermark\()[0-9]+', '\1N', 'g')
      FROM relations c WHERE c.relkind IN ('v', 'm')
    UNION ALL
    SELECT c.relname, 'continuous aggregate, materialized only ' || a.materialized_only::text
           || ', partial view ' || coalesce(regexp_replace(pg_get_viewdef(a.partial_view), '(_materialized_hypertable_|cagg_watermark\()[0-9]+', '\1N', 'g'), 'missing')
           || ', direct view ' || coalesce(regexp_replace(pg_get_viewdef(a.direct_view), '(_materialized_hypertable_|cagg_watermark\()[0-9]+', '\1N', 'g'), 'missing')
           || ', reads its own materialization ' || EXISTS (SELECT 1 FROM pg_rewrite r
                                                             JOIN pg_depend d ON d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid AND d.refclassid = 'pg_class'::regclass
                                                            WHERE r.ev_class = c.oid AND d.refobjid = a.materialization)::text
      FROM relations c JOIN cagg a ON a.relation = c.oid
    UNION ALL
    SELECT c.relname, 'sequence ' || format_type(s.seqtypid, NULL) || ' start ' || s.seqstart || ' increment ' || s.seqincrement || ' min ' || s.seqmin
           || ' max ' || s.seqmax || ' cache ' || s.seqcache || ' cycle ' || s.seqcycle::text
      FROM relations c JOIN pg_sequence s ON s.seqrelid = c.oid
    UNION ALL
    SELECT h.hypertable_name::text, 'hypertable dimension ' || d.column_name || ' ' || d.dimension_type || coalesce(' interval ' || d.time_interval::text, '')
           || coalesce(' interval ' || d.integer_interval::text, '') || coalesce(' partitions ' || d.num_partitions::text, '')
      FROM timescaledb_information.hypertables h JOIN timescaledb_information.dimensions d USING (hypertable_schema, hypertable_name)
     WHERE h.hypertable_schema = 'public'
    UNION ALL
    SELECT h.hypertable_name::text, 'hypertable compression ' || h.compression_enabled::text
      FROM timescaledb_information.hypertables h WHERE h.hypertable_schema = 'public'
    UNION ALL
    SELECT p.object, 'function ' || l.lanname || ', kind ' || p.prokind::text || ', arguments ' || pg_get_function_arguments(p.oid)
           || ', returns ' || coalesce(pg_get_function_result(p.oid), 'nothing')
           || ', volatility ' || p.provolatile::text || ', strict ' || p.proisstrict::text || ', security definer ' || p.prosecdef::text
           || ', leakproof ' || p.proleakproof::text || ', parallel ' || p.proparallel::text || ', support ' || p.prosupport::text
           || ', settings ' || coalesce(array_to_string(p.proconfig, ','), 'none')
           || ', body sha256 ' || encode(sha256(convert_to(coalesce(p.prosrc, '') || '|' || coalesce(p.probin, '') || '|'
                                                           || coalesce(pg_get_function_sqlbody(p.oid), ''), 'UTF8')), 'hex')
      FROM functions p JOIN pg_language l ON l.oid = p.prolang
    UNION ALL
    SELECT p.object, 'grant ' || CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = p.proowner THEN 'owner' ELSE pg_get_userbyid(a.grantee) END
           || ' ' || a.privilege_type || CASE WHEN a.is_grantable THEN ' with grant option' ELSE '' END
      FROM functions p CROSS JOIN LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
    UNION ALL
    SELECT o.type || ' ' || o.identity, 'exists'
      FROM other_objects x CROSS JOIN LATERAL pg_identify_object(x.classid, x.oid, 0) o
     WHERE NOT EXISTS (SELECT 1 FROM extension_members e WHERE e.classid = x.classid AND e.objid = x.oid)
    UNION ALL
    SELECT 'schema ' || n.nspname, 'owner ' || CASE WHEN n.nspowner = 10 THEN 'the bootstrap superuser'
                                                     WHEN n.nspowner = (SELECT r.oid FROM pg_roles r WHERE r.rolname = current_user) THEN 'the migration login'
                                                     ELSE pg_get_userbyid(n.nspowner) END
      FROM namespaces n
    UNION ALL
    SELECT 'schema ' || n.nspname, 'grant ' || CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = n.nspowner THEN 'owner' ELSE pg_get_userbyid(a.grantee) END
           || ' ' || a.privilege_type || CASE WHEN a.is_grantable THEN ' with grant option' ELSE '' END
      FROM namespaces n CROSS JOIN LATERAL aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) a
    UNION ALL
    SELECT j.object, 'job owner ' || CASE WHEN j.owner::oid = 10 THEN 'the bootstrap superuser'
                                          WHEN j.owner::oid = (SELECT r.oid FROM pg_roles r WHERE r.rolname = current_user) THEN 'the migration login'
                                          ELSE j.owner::text END
           || ', application ' || regexp_replace(j.application_name, '\[[0-9]+\]$', '[N]') || ', schedule ' || j.schedule_interval::text
           || ', fixed ' || j.fixed_schedule::text || ', initial start ' || coalesce(extract(epoch FROM j.initial_start)::text, 'none')
           || ', time zone ' || coalesce(j.timezone, 'none') || ', max runtime ' || j.max_runtime::text || ', max retries ' || j.max_retries::text
           || ', retry period ' || j.retry_period::text
           -- Whether TimescaleDB's telemetry job is scheduled is left out: the image's entrypoint unschedules it in the database it
           -- creates when telemetry is off, a database whose extension was created otherwise keeps it scheduled, and what it sends
           -- is decided by timescaledb.telemetry_level, which the chart sets off (any other job's switch is compared).
           || CASE WHEN j.proc_schema = '_timescaledb_functions' AND j.proc_name = 'policy_telemetry' THEN '' ELSE ', scheduled ' || j.scheduled::text END
           || ', check ' || CASE WHEN j.check_name IS NULL THEN 'none' ELSE format('%I.%I', j.check_schema, j.check_name) END
           || ', config ' || CASE
                  WHEN j.config IS NULL THEN 'none'
                  WHEN jsonb_typeof(j.config) <> 'object' THEN j.config::text
                  ELSE coalesce((SELECT string_agg(k.key || '=' || CASE
                           WHEN k.key IN ('hypertable_id', 'mat_hypertable_id')
                               THEN coalesce((SELECT t.name FROM job_targets t WHERE t.id::text = k.value::text), 'no hypertable ' || k.value::text)
                           WHEN k.key = 'drop_after' AND j.proc_schema = '_timescaledb_functions' AND j.proc_name = 'policy_retention'
                                AND EXISTS (SELECT 1 FROM job_retention_days d JOIN job_targets t ON t.name = d.name
                                             WHERE t.id::text = (j.config -> 'hypertable_id')::text
                                               AND substring(k.value::text FROM '^"([1-9][0-9]{0,4}) days"$')::integer BETWEEN d.low AND d.high)
                               THEN 'within the retention bounds'
                           ELSE k.value::text END, ',' ORDER BY k.key COLLATE "C")
                                   FROM jsonb_each(j.config) k), 'empty') END
      FROM jobs j
    UNION ALL
    SELECT 'database', 'owner ' || CASE WHEN d.datdba = (SELECT r.oid FROM pg_roles r WHERE r.rolname = current_user) THEN 'the migration login'
                                        ELSE pg_get_userbyid(d.datdba) END
      FROM pg_database d WHERE d.datname = current_database()
    UNION ALL
    SELECT 'database', 'grant ' || CASE WHEN a.grantee = 0 THEN 'PUBLIC' WHEN a.grantee = d.datdba THEN 'owner' ELSE pg_get_userbyid(a.grantee) END
           || ' ' || a.privilege_type || CASE WHEN a.is_grantable THEN ' with grant option' ELSE '' END
      FROM pg_database d CROSS JOIN LATERAL aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) a
     WHERE d.datname = current_database()
       AND NOT EXISTS (SELECT 1 FROM aclexplode(acldefault('d', d.datdba)) x
                        WHERE x.grantee = a.grantee AND x.privilege_type = a.privilege_type AND x.is_grantable = a.is_grantable)
       -- The migration job grants CONNECT to this deployment's runtime and validation reader logins, by their own names.
       AND NOT (a.privilege_type = 'CONNECT' AND NOT a.is_grantable
                AND EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                             WHERE m.member = a.grantee AND g.rolname IN ('ariva_runtime', 'ariva_validation_reader')))),
normalized AS (
    SELECT object, regexp_replace(regexp_replace(part,
               '\(''((?:[^'']|'''')*)''::character varying\)::text', '''\1''::character varying', 'g'),
               '\(\(ARRAY\[((?:[^]'']|''(?:[^'']|'''')*'')*)\]\)::text\[\]\)', '(ARRAY[\1])', 'g') AS part
      FROM parts),
objects AS (
    SELECT n.object, left(encode(sha256(convert_to(string_agg(n.part, E'\n' ORDER BY n.part COLLATE "C"), 'UTF8')), 'hex'), 32) AS hash
      FROM normalized n GROUP BY n.object),
pinned AS (
    SELECT split_part(e, '=', 1) AS object, split_part(e, '=', 2) AS hash FROM unnest(string_to_array(:'pinned', ';')) e),
latest AS (
    SELECT coalesce(max(script_name), 'no script') AS script, coalesce(max(script_name), '') COLLATE "C" AS at FROM public.schema_version),
drift AS (
    SELECT coalesce(o.object, p.object) || CASE WHEN o.object IS NULL THEN ' (missing)' WHEN p.object IS NULL THEN ' (not in the scripts)' ELSE ' (differs)' END AS name
      FROM objects o FULL JOIN pinned p ON p.object = o.object
     WHERE o.hash IS DISTINCT FROM p.hash)
SELECT line FROM (
    SELECT regexp_replace(replace(replace(n.object || '|' || n.part, '\', '\\'), E'\n', '\n'), '[[:cntrl:]]', '?', 'g') AS line
      FROM normalized n WHERE :'mode' = 'full'
    UNION ALL
    SELECT regexp_replace(o.object, '[[:cntrl:]]', '?', 'g') || '=' || o.hash FROM objects o WHERE :'mode' = 'list'
    UNION ALL
    SELECT CASE
        WHEN (SELECT at FROM latest) < :'schema_at' COLLATE "C" AND :'after_restore' = '1'
            THEN 'schema|not compared: the database is at ' || (SELECT script FROM latest) || ' and the list at ' || :'schema_at'
                 || ' (run the migration job, then check again before starting the hosts)'
        WHEN (SELECT at FROM latest) < :'schema_at' COLLATE "C"
            THEN 'FAIL schema|not compared: the database is at ' || (SELECT script FROM latest) || ' and the list at ' || :'schema_at'
                 || ' (run the migration job, then check again)'
        WHEN (SELECT at FROM latest) > :'schema_at' COLLATE "C"
            THEN 'FAIL schema|not compared: the database is at ' || (SELECT script FROM latest) || ', past the list at ' || :'schema_at'
                 || ' (run the ariva-db.sh of the release whose migration job ran)'
        WHEN count(*) = 0 THEN 'schema|Ariva''s ' || (SELECT count(*) FROM pinned) || ' objects in public as a database the same scripts migrated'
                 || ' (columns, constraints, indexes, triggers, grants and function definitions), TimescaleDB''s jobs included'
        ELSE 'FAIL schema|' || count(*) || ' objects differ from a database the same scripts migrated: '
             || (SELECT string_agg(regexp_replace(name, '[[:cntrl:]]', '?', 'g'), ', ' ORDER BY name COLLATE "C")
                   FROM (SELECT name FROM drift ORDER BY name COLLATE "C" LIMIT 50) first_ones)
             || CASE WHEN count(*) > 50 THEN ', and ' || (count(*) - 50) || ' more' ELSE '' END
             || ' (ariva-db.sh schema --full here and on a database this release''s migration job migrated shows each difference)' END
      FROM drift HAVING :'mode' = 'check') report
 ORDER BY line COLLATE "C";
SQL
}

# ARV-262: TimescaleDB's extensions' members in database $1 as one <extension> <version>|<member> line each (mode list): its
# type and identity, a function's definition (language, kind, markings, settings, source, library, support function,
# arguments with their defaults and a SQL-standard body) and a view's as a SHA-256 each; or one <extension> <version>=<SHA-256 of its member lines in byte order> line per extension (mode digest,
# ARIVA_EXTENSION_MEMBERS' entries). An object a forged dump adds to an extension (ALTER EXTENSION ... ADD) is skipped by
# every check that leaves the extensions out, and a member replaced in place (CREATE OR REPLACE keeps it a member) changes
# what TimescaleDB, and check itself, run.
extension_members() {
  valid_identifier "$1" || die "not a database name: $1"
  ON_DATABASE="$1" as_migration_catalog --tuples-only --no-align --set=mode="$2" <<'SQL'
WITH members AS (
    SELECT regexp_replace(e.extname || ' ' || e.extversion, '[[:cntrl:]|]', '?', 'g') AS extension,
           regexp_replace(o.type || ' ' || o.identity, '[[:cntrl:]]', '?', 'g')
           || coalesce(' ' || (SELECT encode(sha256(convert_to(concat_ws('|', l.lanname, p.prokind, p.prosecdef, p.proleakproof, p.proisstrict,
                                                              p.provolatile, p.proparallel, p.proconfig, p.prosrc, p.probin, p.prosupport,
                                                              pg_get_function_arguments(p.oid), pg_get_function_sqlbody(p.oid)), 'UTF8')), 'hex')
                                 FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang WHERE d.classid = 'pg_proc'::regclass AND p.oid = d.objid), '')
           || coalesce(' ' || (SELECT encode(sha256(convert_to(pg_get_viewdef(c.oid), 'UTF8')), 'hex')
                                 FROM pg_class c WHERE d.classid = 'pg_class'::regclass AND c.oid = d.objid AND c.relkind IN ('v', 'm')), '') AS member
      FROM pg_depend d JOIN pg_extension e ON e.oid = d.refobjid CROSS JOIN LATERAL pg_identify_object(d.classid, d.objid, d.objsubid) o
     WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e' AND e.extname IN ('timescaledb', 'timescaledb_toolkit'))
SELECT line FROM (
    SELECT m.extension || '|' || m.member AS line FROM members m WHERE :'mode' = 'list'
    UNION ALL
    SELECT m.extension || '=' || encode(sha256(convert_to(string_agg(m.member, E'\n' ORDER BY m.member COLLATE "C"), 'UTF8')), 'hex')
      FROM members m GROUP BY m.extension HAVING :'mode' = 'digest') lines
 ORDER BY line COLLATE "C";
SQL
}


# The lines of $1 (an extension and its version) in the extension_members list on stdin.
lines_of() {
  local line
  while IFS= read -r line; do
    [[ "$line" != "$1|"* ]] || printf '%s\n' "$line"
  done
}

# A member line's object, without the extension and the definition's SHA-256 a function or view carries.
member_name() {
  local name="${1#*|}"
  [[ ! "$name" =~ ^(.*)\ [0-9a-f]{64}$ ]] || name="${BASH_REMATCH[1]}"
  printf '%s' "$name"
}

# ARV-262: check's line for TimescaleDB's members: each installed extension's digest against ARIVA_EXTENSION_MEMBERS (the
# image's, pinned by this release; a digest, never another database, is the reference, since a forged dump run by the
# superuser could reach template1 too), and on a difference what differs from template1's (or postgres's) members of the
# same version, which a restore does not touch: added, missing, or changed (a function or view with another definition).
extension_members_line() {
  local digests extension digest pin found installed="" known="" pins=() missing=() differing=() items=()
  IFS=';' read -ra pins <<<"$ARIVA_EXTENSION_MEMBERS"
  for pin in "${pins[@]}"; do known+="${known:+, }${pin%%=*}"; done
  digests="$(extension_members "$DATABASE" digest)" || { printf '%s' "FAIL extension members|$DATABASE's could not be read as the migration login"; return; }
  while IFS='=' read -r extension digest; do
    [[ -n "$extension" ]] || continue
    installed+="${installed:+ and }$extension"
    found=""
    for pin in "${pins[@]}"; do
      [[ "${pin%%=*}" != "$extension" ]] || found="${pin#*=}"
    done
    if [[ -z "$found" ]]; then
      missing+=("$extension")
    elif [[ "$found" != "$digest" ]]; then
      differing+=("$extension")
    fi
  done <<<"$digests"
  if [[ -z "$installed" ]]; then
    printf '%s' "FAIL extension members|neither timescaledb nor timescaledb_toolkit is installed in $DATABASE"
    return
  fi
  if ((${#missing[@]} == 0 && ${#differing[@]} == 0)); then
    printf '%s' "extension members|$installed: exactly the members this release pins for the image, each function and view by its definition"
    return
  fi
  for extension in "${missing[@]}"; do
    items+=("$extension has no members pinned here (this release pins $known: run the ariva-db.sh of the release that ships this image)")
  done
  local ours theirs reference line name shown i named
  local -A added=() lacking=()
  local differences=() sorted=()
  ours="$(extension_members "$DATABASE" list)" || ours=""
  for extension in "${differing[@]}"; do
    named=0
    for reference in template1 postgres; do
      theirs="$(extension_members "$reference" list 2>/dev/null | lines_of "$extension")" || theirs=""
      [[ -n "$theirs" ]] || continue
      added=()
      lacking=()
      while IFS= read -r line; do
        case "$line" in
          $'\t'*) lacking["$(member_name "${line#$'\t'}")"]=1 ;;
          ?*) added["$(member_name "$line")"]=1 ;;
        esac
      done < <(comm -3 <(lines_of "$extension" <<<"$ours" | sort) <(sort <<<"$theirs"))
      differences=()
      for name in "${!added[@]}"; do
        if [[ -n "${lacking[$name]:-}" ]]; then differences+=("changed $name"); else differences+=("added $name"); fi
      done
      for name in "${!lacking[@]}"; do
        [[ -n "${added[$name]:-}" ]] || differences+=("missing $name")
      done
      mapfile -t sorted < <(((${#differences[@]} == 0)) || printf '%s\n' "${differences[@]}" | sort)
      shown=""
      for ((i = 0; i < ${#sorted[@]} && i < 10; i++)); do shown+="${shown:+; }${sorted[i]}"; done
      ((${#sorted[@]} <= 10)) || shown+="; and $((${#sorted[@]} - 10)) more"
      if [[ -n "$shown" ]]; then
        items+=("$extension differs from the members this release pins (against $reference's: $shown)")
      else
        items+=("$extension differs from the members this release pins, and $reference's are the same (changed there as well, or an image this release does not ship)")
      fi
      named=1
      break
    done
    ((named == 1)) || items+=("$extension differs from the members this release pins (no other database holds that version to name what differs)")
  done
  shown=""
  for line in "${items[@]}"; do shown+="${shown:+, }$line"; done
  printf '%s' "FAIL extension members|$shown (an object added to an extension, or one replaced in place: find out who, wiki 10 section 4.13)"
}

# The system catalog's own objects in a database (oid below 16384; ARV-183 review, widened by ARV-262's): every function,
# operator, type, cast, aggregate, operator class and family with their members and support functions (one an operator
# family of the system's gained since included), language and rewrite rule (the system's views, check's own inputs among
# them), one row each of the catalog's
# columns but its grants, as PostgreSQL's own output functions print them: the kind, the name, then the columns, separated
# by the unit separator, each row ended by NUL. CREATE OR REPLACE keeps a function's oid (and a SQL-standard body lives in
# prosqlbody alone), so a system function a forged dump replaced in place differs from the same one in template1 (or
# postgres), which a restore never touches, and from the digest this release pins. The digest is taken from these bytes
# here (sha256sum), never by a function of the database, which the same superuser could replace: a SQL or PL/pgSQL body
# cannot stand in for an output function (it returns cstring). The statements name nothing but catalog columns.
system_catalog() {
  valid_identifier "$1" || die "not a database name: $1"
  ON_DATABASE="$1" as_migration_catalog --tuples-only --no-align --field-separator=$'\x1f' --record-separator-zero <<'SQL'
SELECT 'function', p.proname, p.oid, p.pronamespace, p.proowner, p.prolang, p.procost, p.prorows, p.provariadic, p.prosupport, p.prokind,
       p.prosecdef, p.proleakproof, p.proisstrict, p.proretset, p.provolatile, p.proparallel, p.pronargs, p.pronargdefaults, p.prorettype,
       p.proargtypes, p.proallargtypes, p.proargmodes, p.proargnames, p.proargdefaults, p.protrftypes, p.prosrc, p.probin, p.prosqlbody, p.proconfig
  FROM pg_proc p WHERE p.oid < 16384;
SELECT 'operator', o.oprname, o.* FROM pg_operator o WHERE o.oid < 16384;
SELECT 'type', t.typname, t.oid, t.typnamespace, t.typowner, t.typlen, t.typbyval, t.typtype, t.typcategory, t.typispreferred, t.typisdefined,
       t.typdelim, t.typrelid, t.typsubscript, t.typelem, t.typarray, t.typinput, t.typoutput, t.typreceive, t.typsend, t.typmodin, t.typmodout,
       t.typanalyze, t.typalign, t.typstorage, t.typnotnull, t.typbasetype, t.typtypmod, t.typndims, t.typcollation, t.typdefaultbin, t.typdefault
  FROM pg_type t WHERE t.oid < 16384;
SELECT 'cast', c.oid, c.* FROM pg_cast c WHERE c.oid < 16384;
SELECT 'aggregate', a.aggfnoid, a.* FROM pg_aggregate a WHERE a.aggfnoid::oid < 16384;
SELECT 'operator class member', o.oid, o.* FROM pg_amop o WHERE o.oid < 16384 OR o.amopfamily < 16384;
SELECT 'operator class function', p.oid, p.* FROM pg_amproc p WHERE p.oid < 16384 OR p.amprocfamily < 16384;
SELECT 'operator class', c.opcname, c.* FROM pg_opclass c WHERE c.oid < 16384;
SELECT 'operator family', f.opfname, f.* FROM pg_opfamily f WHERE f.oid < 16384;
SELECT 'rule', r.rulename, r.* FROM pg_rewrite r WHERE r.oid < 16384;
SELECT 'language', l.lanname, l.oid, l.lanowner, l.lanispl, l.lanpltrusted, l.lanplcallfoid, l.laninline, l.lanvalidator
  FROM pg_language l WHERE l.oid < 16384;
SQL
}

# The server's major version (17 for PostgreSQL 17.x): ARIVA_SYSTEM_FUNCTIONS' key.
server_major() {
  as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT current_setting('server_version_num')::integer / 10000;
SQL
}

# The SHA-256 of system_catalog's rows for the database, sorted byte by byte (ARIVA_SYSTEM_FUNCTIONS' value), taken here.
system_functions_digest() {
  local digest
  digest="$(system_catalog "$DATABASE" | sort -z | sha256sum)" || return 1
  printf '%s' "${digest%% *}"
}

# The objects whose rows differ between this database and $1, by name, comma-separated (a function by its name alone).
system_catalog_changes() {
  local line names=()
  while IFS= read -r line; do
    line="${line#$'\t'}"
    [[ -n "$line" ]] || continue
    [[ "$line" != function$'\x1f'* ]] || line="${line#function$'\x1f'}"
    names+=("${line//$'\x1f'/ }")
  done < <(comm -z -3 <(system_catalog "$DATABASE" | sort -z) <(system_catalog "$1" | sort -z) | cut -z -d $'\x1f' -f1,2 | tr '\0' '\n')
  ((${#names[@]} > 0)) || return 0
  printf '%s\n' "${names[@]}" | sort -u | paste -sd, -
}

# ARV-262: the system functions' digest against the one this release pins for the server's major version decides; template1's
# (or postgres's) functions, which a restore never touches, name what differs.
system_functions_line() {
  local reference changed major digest pin pinned="" known="" pins=()
  digest="$(system_functions_digest)" || { printf '%s' "FAIL system functions|$DATABASE's could not be read"; return; }
  major="$(server_major)" || major="unknown"
  IFS=';' read -ra pins <<<"$ARIVA_SYSTEM_FUNCTIONS"
  for pin in "${pins[@]}"; do
    known+="${known:+, }${pin%%=*}"
    [[ "${pin%%=*}" != "$major" ]] || pinned="${pin#*=}"
  done
  if [[ -n "$pinned" && "$digest" == "$pinned" ]]; then
    printf '%s' "system functions|as PostgreSQL $major ships them, with its operators, types, casts, aggregates, operator classes, languages and views (the digest this release pins, taken outside the database)"
    return
  fi
  local why="differ from what this release pins for PostgreSQL $major"
  [[ -n "$pinned" ]] || why="have no digest pinned here for PostgreSQL $major (this release pins $known: run the ariva-db.sh of the release that ships this image)"
  for reference in template1 postgres; do
    if system_catalog "$reference" >/dev/null 2>&1; then
      changed="$(system_catalog_changes "$reference")" || changed=""
      if [[ -z "$changed" ]]; then
        printf '%s' "FAIL system functions|$DATABASE's $why, and $reference's are the same (changed there as well, or a server build this release does not ship)"
      else
        printf '%s' "FAIL system functions|${changed//,/, } differ from $reference's (replaced in place in $DATABASE, or changed in $reference); $DATABASE's $why"
      fi
      return
    fi
  done
  printf '%s' "FAIL system functions|$DATABASE's $why; neither template1 nor postgres could be read as the migration login to name what differs (CONNECT on template1)"
}

# ARV-262: what check compares a database with, for an operator to compare two databases (a restored one and one this
# release's migration job migrated) and for BackupRestoreTests to read the pins from a migrated one. Read-only.
cmd_schema() {
  local full=0 line
  while (($#)); do
    case "$1" in
      --full) full=1; shift ;;
      *) usage ;;
    esac
  done
  check_settings
  require_migration_login
  if ((full == 1)); then
    schema_report full
    extension_members "$DATABASE" list | while IFS= read -r line; do printf 'extension %s\n' "$line"; done
  else
    local major digest
    major="$(server_major)" || die "the server's version could not be read"
    digest="$(system_functions_digest)" || die "the system catalog could not be read"
    schema_report list
    extension_members "$DATABASE" digest | while IFS= read -r line; do printf 'extension %s\n' "$line"; done
    printf 'system functions %s=%s\n' "$major" "$digest"
  fi
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
           -- ARV-211: the key ring signing certificate that signed the key (none before ARV-211: the wrapping certificate's key).
           upper(substring(xml from '<encryptedKey [^>]*signer="([0-9A-Fa-f]{64})"')) AS signer,
           -- ARV-211 (script 0073): whether the row carries the hosts' row signature (none before ARV-211); checked by the hosts only.
           signature IS NOT NULL AS row_signed,
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
              || ' signer ' || coalesce(signer, 'the certificate (before ARV-211)')
              || CASE WHEN row_signed THEN ' row signed' ELSE ' row not signed (before ARV-211: read only inside DataProtection:Signing:CertificateSignedUntil)' END
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
# pg_catalog or one of theirs (since ARV-262's review also one created after initdb in pg_catalog or information_schema
# under such a name, an overload that an exact match of its argument types would pick), any other extension, and settings made for every session of this database (ALTER DATABASE
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
SELECT 'function ' || n.nspname || '.' || p.proname || ' (created after initdb)'
  FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
 WHERE n.nspname IN ('pg_catalog', 'information_schema') AND p.oid >= 16384
   AND p.proname IN (SELECT q.proname FROM pg_catalog.pg_proc q
                      WHERE q.oid < 16384 OR EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND m.objid = q.oid))
   AND NOT EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND m.objid = p.oid)
UNION
SELECT 'operator ' || n.nspname || '.' || o.oprname || ' (created after initdb)'
  FROM pg_catalog.pg_operator o JOIN pg_catalog.pg_namespace n ON n.oid = o.oprnamespace
 WHERE n.nspname IN ('pg_catalog', 'information_schema') AND o.oid >= 16384
   AND o.oprname IN (SELECT q.oprname FROM pg_catalog.pg_operator q
                      WHERE q.oid < 16384 OR EXISTS (SELECT 1 FROM members m WHERE m.classid = 'pg_catalog.pg_operator'::pg_catalog.regclass AND m.objid = q.oid))
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

# No session of an Ariva login on the database but this one: the hosts scaled to 0, whose pods may take a few seconds to end
# after the scale (up to 10 here, then refused). $1: what the refusal leaves unchanged, for its message. ARV-236's update and,
# since ARV-244's review, a restore before it replaces the image's TimescaleDB.
require_no_ariva_sessions() {
  local sessions attempt
  for attempt in 1 2 3 4 5 6; do
    sessions="$(ariva_sessions)"
    [[ -n "$sessions" && "$attempt" -lt 6 ]] || break
    sleep 2
  done
  [[ -z "$sessions" ]] || die "Ariva's logins still have sessions on $DATABASE: ${sessions//$'\n'/, }. Scale the Ariva deployments to 0 first (kubectl -n <namespace> scale deployment --all --replicas=0) and wait for their pods to end; $1"
}

# ARV-236: a newer image ships newer versions of the extensions, and the database keeps the one it has until the
# extension's owner updates it. It runs with the Ariva deployments scaled to 0 (wiki 04 section 6.8): a connection that
# loaded the old library is closed by TimescaleDB at its next statement (FATAL), and a long transaction would hold the
# update's locks. In order, before any statement as the superuser: refuse an extension the image cannot update (no update
# path), a session of an Ariva login, and anything planted_objects finds; then each ALTER EXTENSION as the first and only statement of a psql
# session of its own, since TimescaleDB loads its library at a session's first statement and refuses the update once the
# old one is loaded ("cannot be updated after the old version has already been loaded"); psql without psqlrc sends nothing
# before it, and lock_timeout (a startup option, not a statement) ends a wait for a lock after 30 seconds with nothing
# changed. The statements are constants: no value reaches them. A restore of an older TimescaleDB's dump runs the same update
# (update_extensions) once restore mode has ended (ARV-244).
cmd_update_extensions() {
  (($# == 0)) || usage
  check_settings
  require_migration_login
  if [[ -z "$(extensions_behind)" ]]; then
    say "TimescaleDB's extensions in $DATABASE are at the versions the image ships: nothing to update"
    return
  fi
  update_extensions
  say "done: TimescaleDB's extensions in $DATABASE are at the versions the image ships; scale the deployments back, run check, and take a new backup (a restore goes through the backup's own TimescaleDB version, which a later image may no longer ship)"
}

# The update itself, for update-extensions and a restore (above): its refusals, then each ALTER as the superuser.
update_extensions() {
  local behind name installed shipped path planted
  behind="$(extensions_behind)"
  [[ -n "$behind" ]] || return 0
  while IFS='|' read -r name installed shipped path; do
    [[ -n "$name" && "$path" != "t" ]] || continue
    die "$name $installed has no update to $shipped, the version the image ships: the image is older than the database's extension (for example a rollback of the database image after update-extensions); roll the image forward (wiki 18)"
  done <<<"$behind"
  require_no_ariva_sessions "nothing was changed"
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
}

# ARV-290: SET on temp_file_limit for the migration login, which the migration job needs to cap the runtime login's temporary
# files (script 0069); the chart's init script grants it to the login on a new volume, this on a server first started before
# ARV-290, to ariva_migration (script 0001), whose privileges the migration login holds: one constant statement as the
# superuser over the local socket, then the migration login's own view of it. Unlike update-extensions it runs without
# planted_objects first: GRANT ON PARAMETER names a server-wide parameter and a role, nothing it resolves through the
# search path, and no event trigger fires for it (its review).
cmd_grant_settings() {
  (($# == 0)) || usage
  check_settings
  require_migration_login
  local group
  group="$(as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ariva_migration');
SQL
)"
  [[ "$group" == "t" ]] || die "ariva_migration does not exist: no migration has run on this server yet. On a new volume the chart's init script grants the migration login SET on temp_file_limit itself; on a server prepared another way, its superuser grants it to the migration login (wiki 04 section 6.10), then the migration job runs"
  say "granting ariva_migration, and so $LOGIN, SET on temp_file_limit (superuser over the local socket)"
  as_superuser <<'SQL' >/dev/null
GRANT SET ON PARAMETER temp_file_limit TO ariva_migration;
SQL
  local may
  may="$(as_migration_catalog --tuples-only --no-align <<'SQL'
SELECT has_parameter_privilege('temp_file_limit', 'SET');
SQL
)"
  [[ "$may" == "t" ]] || die "$LOGIN still may not set temp_file_limit: it does not hold ariva_migration's privileges (it must be a member that inherits them; check names the group's members)"
  say "done: run the migration job again; it sets the runtime login's temporary file cap (ARV-290)"
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
      --host) (($# >= 2)) || usage; HOST="$2"; shift 2 ;;
      --port) (($# >= 2)) || usage; PORT="$2"; shift 2 ;;
      --socket-dir) (($# >= 2)) || usage; SOCKET_DIR="$2"; shift 2 ;;
      *) rest+=("$1"); shift ;;
    esac
  done
  # ARV-182: another server than this pod's only for the two commands that never run as the superuser.
  if [[ "$HOST" != "127.0.0.1" && "$command" != "backup" && "$command" != "manifest" ]]; then
    die "--host applies to backup and manifest only: $command runs in the database pod against its own server"
  fi
  case "$command" in
    backup) cmd_backup "${rest[@]}" ;;
    manifest) cmd_manifest "${rest[@]}" ;;
    check-dump) cmd_check_dump "${rest[@]}" ;;
    restore) cmd_restore "${rest[@]}" ;;
    check) cmd_check "${rest[@]}" ;;
    schema) cmd_schema "${rest[@]}" ;;
    keyring) cmd_keyring "${rest[@]}" ;;
    after-kafka-loss) cmd_after_kafka_loss "${rest[@]}" ;;
    update-extensions) cmd_update_extensions "${rest[@]}" ;;
    grant-settings) cmd_grant_settings "${rest[@]}" ;;
    *) usage ;;
  esac
}

main "$@"
