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
#                                   but the server's postgresql.conf (postgresql.auto.conf or a file an include names). It
#                                   finds what a careless forged dump leaves; it cannot prove a careful one absent (the
#                                   superuser that ran it can change what check reads): the signed manifest is the gate
#   schema [--full]                 what check compares: one <object>=<hash> line per object of Ariva's in public, each
#                                   TimescaleDB extension's members digest and the system functions' digest; with --full
#                                   each object's normalised definition and each extension member, one line each, so that
#                                   two databases' outputs compare line by line (read-only, as the migration login)
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
# did not review. check reads two read-only views as the superuser too (ARV-183, ARV-262): pg_file_settings and pg_settings,
# whose files only a superuser may see, for the settings any file but the server's postgresql.conf holds (ALTER SYSTEM's
# postgresql.auto.conf, a file an include names), in one statement that names nothing through the search path, and only
# when planted_objects finds nothing. Every superuser session runs with the search path pinned to the
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

# ARV-189: the runtime role's privileges on every relation of public that Ariva's scripts leave other than the default
# SELECT, INSERT, UPDATE and DELETE (script 0001's default privileges), with its column grants, as
# <relation>=<privileges>[,<privilege>(<column> ...)] entries separated by ";". check compares them with the database once
# it is at ARIVA_RUNTIME_PRIVILEGES_AT or later (before that the migration job has not applied the scripts the list
# reflects), so a restore that lost a revoke, or gained a grant, is reported. OpsScriptTests refuses a later script that
# grants to or revokes from ariva_runtime, or creates a view, until this list (or ARIVA_VIEWS) and its script move with it;
# BackupRestoreTests holds the list equal to a migrated database.
readonly ARIVA_RUNTIME_PRIVILEGES_AT="0064_database_environment.sql"
readonly ARIVA_RUNTIME_PRIVILEGES="advertising_media=SELECT,INSERT,UPDATE;\
advertising_play=SELECT,INSERT;\
advertising_playlist=SELECT,INSERT,UPDATE;\
advertising_policy=SELECT,INSERT,UPDATE;\
advertising_schedule=SELECT,INSERT,UPDATE;\
airport=SELECT,INSERT,UPDATE;\
alert=SELECT,INSERT,UPDATE;\
alert_rule=SELECT,INSERT,UPDATE;\
aman_pull_cursor=SELECT,INSERT,UPDATE;\
audit_entry=SELECT,INSERT;\
availability_cursor=SELECT,INSERT,UPDATE;\
availability_minute=SELECT,INSERT;\
border_desk_interval=SELECT,INSERT;\
border_desk_session=SELECT,INSERT;\
border_egate_interval=SELECT,INSERT;\
checkpoint=SELECT,INSERT,UPDATE;\
counter_allocation=SELECT,INSERT,UPDATE;\
data_protection_key=SELECT,INSERT;\
database_environment=SELECT;\
desk=SELECT,INSERT,UPDATE;\
desk_code_mapping=SELECT,INSERT,UPDATE;\
desk_feed_state=SELECT,INSERT,UPDATE;\
desk_minute=SELECT,INSERT,UPDATE;\
desk_observation=SELECT,INSERT;\
desk_observation_batch=SELECT,INSERT;\
desk_sensor_minute=SELECT,INSERT,UPDATE;\
desk_zone_reading=SELECT,INSERT;\
device=SELECT,INSERT,UPDATE;\
device_calibration=SELECT,INSERT;\
device_health_event=SELECT,INSERT;\
display=SELECT,INSERT,UPDATE;\
egate_minute=SELECT,INSERT,UPDATE;\
email_message=SELECT,INSERT,UPDATE(attempts next_attempt_utc reason sent_utc status);\
feed_freshness=SELECT,INSERT,UPDATE;\
flight_event=SELECT,INSERT;\
flight_leg=SELECT,INSERT,UPDATE;\
floor_plan=SELECT,INSERT,UPDATE;\
inbound_lane_demand=SELECT,INSERT,UPDATE;\
instruction_slide=SELECT,INSERT,UPDATE;\
integration_call=SELECT,INSERT;\
integration_client=SELECT,INSERT,UPDATE;\
integration_idempotency=SELECT,INSERT,DELETE,UPDATE(response_body status_code);\
lane_staffing=SELECT,INSERT;\
level=SELECT,INSERT,UPDATE;\
line_minute=SELECT,INSERT,UPDATE;\
line_minute_15m=SELECT;\
maintenance_window=SELECT,INSERT,UPDATE;\
manual_count=SELECT,INSERT;\
operating_day_exception=SELECT,INSERT,UPDATE;\
operating_week=SELECT,INSERT,UPDATE;\
outbound_endpoint=SELECT,INSERT,UPDATE;\
overflow_bin_15m=SELECT;\
overflow_minute=SELECT,INSERT,UPDATE;\
queue_bin=SELECT,INSERT,UPDATE;\
queue_minute=SELECT,INSERT,UPDATE;\
queue_minute_15m=SELECT;\
queue_minute_shadow=INSERT,SELECT(minute_utc zone_key),UPDATE(no_service nowcast_degraded nowcast_minutes sensor_cycle_minutes updated_on);\
recovery_code=SELECT,INSERT,UPDATE;\
refresh_token=SELECT,INSERT,UPDATE;\
replay_run=SELECT,INSERT;\
report_delivery=SELECT,INSERT,UPDATE(attempts reason sent_utc status);\
report_schedule=SELECT,INSERT,UPDATE;\
retention_period=SELECT;\
schema_version=SELECT;\
sensing_batch=SELECT,INSERT;\
sensing_day_key=SELECT,INSERT,DELETE;\
sensing_event=SELECT,INSERT;\
site=SELECT,INSERT,UPDATE(modified_by modified_by_id modified_on name);\
stream_offset=SELECT,INSERT,UPDATE;\
stream_partition_count=SELECT,INSERT;\
stream_zone_state=SELECT,INSERT,UPDATE;\
terminal=SELECT,INSERT,UPDATE;\
tracer_batch=SELECT,INSERT;\
tracer_run=SELECT,INSERT;\
user=SELECT,INSERT,UPDATE;\
user_session=SELECT,INSERT,UPDATE;\
validation_campaign=SELECT,INSERT,UPDATE(closed_by_id closed_utc started_by_id started_utc status);\
validation_campaign_desk=SELECT,INSERT;\
validation_campaign_line=SELECT,INSERT;\
validation_campaign_zone=SELECT,INSERT;\
validation_result_revision=SELECT,INSERT;\
zone_health_bin=SELECT,INSERT,UPDATE;\
zone_outage=SELECT,INSERT,UPDATE"

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
readonly ARIVA_SCHEMA_AT="0065_desk_feed_heartbeat.sql"
readonly ARIVA_SCHEMA="advertising_media=2cb17c8c169d81de441d4192fdc21b0c;\
advertising_media_guard()=0c14e39356e12f186a82b88895cc130a;\
advertising_play=d5f1179d8cfaccdcc0d9c7d9c3634258;\
advertising_playlist=4a6c0318760037977918e61300754a06;\
advertising_playlist_guard()=633d55f8a6c27f13b7b7a1e94d2b9da1;\
advertising_policy=d83429f45d805ccf52a04d7a9d403e5e;\
advertising_schedule=b8b25b05cc51068e681cee5e690ef853;\
advertising_schedule_guard()=bf75f116d53a7bf6e874609ff14775f4;\
airport=3c94cfdd409c8390193522cab42771f1;\
alert=24824e4c643233ddbac5ec2d6ab45655;\
alert_keep_raise()=85dd9b3f8edd77b7ada96d91567edd5e;\
alert_rule=667a50186480b9515162c8790ab9d2dc;\
alert_rule_identity()=895b6cc23a25603fadff715c72460978;\
alert_rule_state=33e6047c3886060ebd4fc368a29373be;\
aman_pull_cursor=2b1f0d463807d44bf7e9550223bb1bf9;\
ariva_ensure_runtime_login(login_name text, login_password text)=a34b831ffc5ede64cdd0f70840744ab4;\
ariva_ensure_validation_reader_login(login_name text, login_verifier text)=a933282b920424704f9a04912201735e;\
ariva_retention_purge(p_data_set text, p_limit integer)=a7ff57e7b5404c01e5cb1715f4416c2c;\
ariva_set_runtime_statement_timeout(login_name text, timeout_seconds integer)=9e7119ef6c672f24c7f58833abdac5f9;\
audit_entry=517f91f81aa4f05abee86733509a5cae;\
availability_cursor=8e4b2ef670b3e952fe9b2d7d78c9ad63;\
availability_minute=a0a7f3238150c745a520566b57e17da4;\
border_desk_interval=5f357f5f8810dc45e5d0066550447da3;\
border_desk_session=9f55be74e4a68f69813c1dd90063d42e;\
border_egate_interval=f58e79376e4e7c851af7bb68b5cf1302;\
checkpoint=f39bbc7b8379b08fca1a398490927d55;\
counter_allocation=9160c214b4875f087f813c0fa69702ab;\
data_protection_key=c325c22f8a9977960ad76a2bd20fc808;\
database=b90a9d25f2cafa4afcdf86348a4884ea;\
database_environment=e52adb8c3538e5988c62134485224f03;\
desk=7e2fd78689a8e39df7d7718eb47ff4c2;\
desk_code_mapping=e35f0f7d275e0630066f9fd4e42da56a;\
desk_feed_state=d28b817a20b273a697a452a2e7fd9d50;\
desk_minute=3e875cb29f2515f1ec77676bf22f596e;\
desk_observation=f1b03382ca05ea989e8e7949a437fb9d;\
desk_observation_batch=097cb763e24f900450524f0418de2be4;\
desk_observation_insert_check()=825ab6784dec0fbdbc3538ef0a990d2f;\
desk_sensor_minute=3546d814ef2e6a7e7c18241b43e7a362;\
desk_zone_reading=a204ce21cdfc0d3efb69de85b4b2e2db;\
device=01d58dde100b6d3391b2828a3f696af6;\
device_calibration=8df606c26b6e3ab383f5867afd132b32;\
device_calibration_immutable()=898d61ed2d4d8bed0d1c463c86f4abe9;\
device_health_event=0c163b26b148eac8bd9c460e6a30bc1f;\
device_heartbeat=0cd33743e6e2f39b0940afd5349aa485;\
display=3e2ccabac8fa244989f46e9e8768f3f1;\
display_advertising_allowed()=5420d3348838bb6c035cad53f5684d8c;\
egate_minute=fe0f51641a7af9a738e8e5dcdc48301f;\
email_message=1c531e7cecee02a2b7101e47730a08c8;\
email_message_keep()=d91278075b584d4e3e4b5905e6770ee6;\
feed_freshness=d06d4c8376c322fd09a876991f7735d9;\
flight_event=718de1b8138faa0cb2c5d8d47a852c31;\
flight_leg=995c0e72317cd5713439b4e544bb3b93;\
floor_plan=4cb91c2b99aa2229325665b7547fbe22;\
inbound_lane_demand=4513c5b054cd09dc13a0a5932dfb6ecc;\
instruction_slide=88971ae117e1b10b28506495edb04e7e;\
integration_call=e46b0d368a4916cb0834ecee757ad780;\
integration_client=5900cfdb0d5ca73d3abce7cebf968b94;\
integration_idempotency=7391a46cbb12dc523a9f98ef518d8c77;\
integration_idempotency_keep()=b3b527e2df0c9358c30ac3e1d26d786f;\
lane_staffing=85bfa55d161bfc2a81a3f6efac2592b2;\
level=ef72305459eb8b4ba8140489c8612fcf;\
line=b17d4462e2e004ba9db787d69655149d;\
line_minute=949005495d9badc823b8f406bf0508a4;\
line_minute_15m=c486d2eb00e6f688763ccbfa14f4f49a;\
maintenance_window=30c9e45c92c9f12632648b94e61652cf;\
manual_count=cc73bd7e33a26983b067f4d91e0ce437;\
manual_count_insert_check()=440139ea9b09c91a26a5d0d497faae00;\
operating_day_exception=039d4c83fdcce93a7f70c5689e6dd688;\
operating_week=4bf3eae1c6360300dadde851d38f57c0;\
outbound_endpoint=c22fdee5cf9a5b29d82973abcd2ddbc5;\
outbox_message=bbd7564a3197af6eab375a3b02976a3f;\
outbox_message_seq_seq=1385144055b5137fc429fe2b18c88a02;\
overflow_bin_15m=437c7e5730c99b675118b78e998ca305;\
overflow_minute=5ffa03608bb5598cc7e75b9559c04aee;\
processed_event=e3e043f4a644c70841f27b0067158d27;\
queue_bin=08776c919041f64ad030a3aa7d899eb1;\
queue_bin_final_is_immutable()=a8f0a0b610e7430cc97d338b164571aa;\
queue_minute=6a779edc6e03c01e76844db4c7a8ecfe;\
queue_minute_15m=ab5233a3c5588ab61bbd2a96cc896fb7;\
queue_minute_shadow=99cee8b2a6e0a9df0fad7f8f41fb24fe;\
recovery_code=8505ef0be87bad1390e64df736902e79;\
refresh_token=f1daa29c1e256df7932dac8db5dceae1;\
replay_run=0cd65a5511a133ce3379725c2851d06d;\
replay_run_anchor()=0787fcfcc3f351eb37e3f469e36ffbd5;\
report_delivery=53deee3a4bb2b171b8768ff6ee18b555;\
report_schedule=1b9f664301a76bbf4fd3e8507000c122;\
report_schedule_recipient=a955d379c49e2a89352667344e1f0700;\
retention_period=7392f4079c31935a03633a7a08238725;\
schema _timescaledb_cache=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_catalog=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_config=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_functions=01b9f62992baa1842166fe29ac004a7a;\
schema _timescaledb_internal=01b9f62992baa1842166fe29ac004a7a;\
schema information_schema=01b9f62992baa1842166fe29ac004a7a;\
schema pg_catalog=01b9f62992baa1842166fe29ac004a7a;\
schema pg_toast=b674f4d680283437e56727831c38bbf3;\
schema public=3abf3cd4be4886c9d3aacf2b0b9f3734;\
schema timescaledb_experimental=01b9f62992baa1842166fe29ac004a7a;\
schema timescaledb_information=01b9f62992baa1842166fe29ac004a7a;\
schema toolkit_experimental=01b9f62992baa1842166fe29ac004a7a;\
schema_version=b28ff4f835f1e99b965b23ee311bb0af;\
sensing_batch=e89a84e0d986b4f52711e08243a8ae86;\
sensing_day_key=83545c0e1c076078e6eb00b450a90542;\
sensing_event=d324e9c04c9a19a56db1458b2b351869;\
site=c23fd0ae4abba5729f38703cea18030b;\
stream_offset=7a6dcf84e92b526649085fd79d5fa516;\
stream_partition_count=1ee751f5fda9720aeaa06947656cc370;\
stream_zone_state=d7528ea2a44188105afd889623b93347;\
terminal=df71d62a0b33cfa0a8a270f417784f90;\
tracer_batch=91544f21e175f95d6226eaad7370ecd2;\
tracer_run=ef5e635b2e58841bb8b872082a31dbfe;\
user=98d18078d1cf068518726005d68af86e;\
user_role=b409107c70c5fdc069a1e5d455f5a232;\
user_session=9b9ea08ab46360356d343172edb30f8d;\
user_site=d9ffd9b23069936bfc03888f4a71ef45;\
validation_campaign=835158a659b6cbcb5e7ce1143f22f629;\
validation_campaign_desk=6df792079f53aa13faa2be15ab5eb5f6;\
validation_campaign_desk_check()=104ea7afcc154308d02495c5c62a6cb3;\
validation_campaign_insert_check()=54d97900461e4a0900d00a9e1b2ca38c;\
validation_campaign_line=53c13b9d9179eea8772c51ec24ed9c31;\
validation_campaign_line_check()=660ada511c8ea7f4436835940bb6348f;\
validation_campaign_transition()=1b4cd420c3805b5b6125015cd8456ff3;\
validation_campaign_zone=c2d2c328696d12b45594aadf0f8c2674;\
validation_campaign_zone_check()=6ff7893065cd5560d80c53b1b8d44af5;\
validation_capture_insert_check()=75dff80d9f1c8e8e64d54c9f222fb0d1;\
validation_result_revision=4af93ccdfbc4363e4a52652101add721;\
validation_result_revision_insert_check()=bcdfa3eee48504be818dddb7738ef6fb;\
validation_row_immutable()=e77b1c7f83acb7761ff4cd2ff8877653;\
zone=144daa8f192dce93a13e75a77ec54228;\
zone_health=567ae9a2661ee7a7f18dfb79c3b216c6;\
zone_health_bin=83f6d4fc5a024c2bbae3392fd68df243;\
zone_health_bin_final_is_immutable()=f6f65edacdd24324741915722bdcfa7b;\
zone_outage=9d0dcb545d87fe20fa26779af5ddd1a4;\
zone_profile=ecb5cd2096d36deaa1d51fd176bb2727;\
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
usage() { say "usage: ariva-db.sh <backup|manifest|check-dump|restore|check|schema|keyring|after-kafka-loss|update-extensions> [options]; see the header of this file"; exit 2; }

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
  check_after_restore=1
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
  require_migration_login
  # The report runs as the migration login with the catalog's search path for the session (ARV-183 review): a setting of the
  # database or an object the login created cannot change what a name in it means, or what it reports.
  local report
  report="$(as_migration_catalog --tuples-only --no-align --set=runtime="$runtime" --set=reader="$reader" --set=allowed_extensions="$extensions" \
    --set=allowed_logins="$logins_allowed" --set=allowed_settings="$settings_allowed" --set=ariva_functions="$ARIVA_FUNCTIONS" \
    --set=runtime_privileges="$ARIVA_RUNTIME_PRIVILEGES" --set=runtime_privileges_at="$ARIVA_RUNTIME_PRIVILEGES_AT" \
    --set=ariva_views="$ARIVA_VIEWS" --set=after_restore="$check_after_restore" <<'SQL'
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
-- A relation the list does not name has the default DML; one it names that the database lacks is reported too.
privilege_drift AS (
    SELECT coalesce(a.name, p.name) || ' (the scripts: ' || coalesce(p.privileges, 'SELECT,INSERT,UPDATE,DELETE') || '; found: '
           || CASE WHEN a.name IS NULL THEN 'no such relation' ELSE coalesce(nullif(a.privileges, ''), 'none') END || ')' AS name
      FROM runtime_privileges a FULL JOIN pinned_privileges p ON p.name = a.name
     WHERE a.privileges IS DISTINCT FROM coalesce(p.privileges, 'SELECT,INSERT,UPDATE,DELETE')),
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
-- ARV-189 review (its third round): the default privileges are script 0001's and nothing else: the tables the migration login
-- creates in public give ariva_runtime SELECT, INSERT, UPDATE and DELETE, its sequences there SELECT and USAGE, without grant
-- option. A dump carries default privileges (the migration login's DEFAULT ACL entries) and any role may change its own, so
-- another entry would widen what the tables and views of a later release give, past every check of this one.
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
     WHERE NOT (p.defaclrole = (SELECT oid FROM me) AND p.defaclnamespace = 'public'::regnamespace
                AND ((p.defaclobjtype = 'r' AND p.entries = 'ariva_runtime=DELETE,ariva_runtime=INSERT,ariva_runtime=SELECT,ariva_runtime=UPDATE')
                     OR (p.defaclobjtype = 'S' AND p.entries = 'ariva_runtime=SELECT,ariva_runtime=USAGE')))
    UNION ALL
    SELECT 'script 0001''s for ' || x.what || ' missing' FROM (VALUES ('r', 'tables'), ('S', 'sequences')) x(t, what)
     WHERE NOT EXISTS (SELECT 1 FROM default_privileges p
                        WHERE p.defaclrole = (SELECT oid FROM me) AND p.defaclnamespace = 'public'::regnamespace AND p.defaclobjtype::text = x.t)),
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
                 || ' relations other than the default SELECT, INSERT, UPDATE and DELETE)'
        ELSE 'FAIL runtime privileges|' || count(*) || ' relations differ from the scripts: '
             || (SELECT string_agg(name, ', ' ORDER BY name) FROM (SELECT name FROM privilege_drift ORDER BY name LIMIT 10) first_ten)
             || CASE WHEN count(*) > 10 THEN ', and ' || (count(*) - 10) || ' more' ELSE '' END END FROM privilege_drift
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
UNION ALL SELECT CASE WHEN count(*) = 0 THEN 'default privileges|script 0001''s only: the migration login''s tables in public give ariva_runtime SELECT, INSERT, UPDATE and DELETE, its sequences there SELECT and USAGE'
                      ELSE 'FAIL default privileges|' || count(*) || ' entries differ from script 0001''s: ' || string_agg(name, '; ' ORDER BY name) END FROM default_drift
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
    if [[ "$line" == FAIL* ]]; then
      failed=1
      printf 'FAIL  %s\n' "${line#FAIL }"
    else
      printf 'ok    %s\n' "$line"
    fi
    [[ "$line" != *"|not compared:"* ]] || check_not_compared=1
  done <<<"$report"
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
# migrated database has none). TimescaleDB's jobs are left out (the retention a deployment
# configures). Each part is hashed as PostgreSQL prints it but for the one form a dump and restore changes in a CHECK
# constraint (a varchar IN list: ((ARRAY['a'::character varying])::text[]) migrated, (ARRAY[('a'::character varying)::text])
# restored, both hashed as (ARRAY['a'::character varying])); printed, a backslash doubles, a newline is \n and any other
# control character "?" (display only). Modes: full (those lines), list (<object>=<hash>, the first 32 hex digits of the
# SHA-256 of an object's parts in byte order: ARIVA_SCHEMA's entries) and check (the report line, compared once the
# database is at ARIVA_SCHEMA_AT).
schema_report() {
  as_migration_catalog --tuples-only --no-align --set=mode="$1" --set=pinned="$ARIVA_SCHEMA" --set=schema_at="$ARIVA_SCHEMA_AT" \
    --set=after_restore="$check_after_restore" <<'SQL'
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
                 || ' (columns, constraints, indexes, triggers, grants and function definitions)'
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
    schema) cmd_schema "${rest[@]}" ;;
    keyring) cmd_keyring "${rest[@]}" ;;
    after-kafka-loss) cmd_after_kafka_loss "${rest[@]}" ;;
    update-extensions) cmd_update_extensions "${rest[@]}" ;;
    *) usage ;;
  esac
}

main "$@"
