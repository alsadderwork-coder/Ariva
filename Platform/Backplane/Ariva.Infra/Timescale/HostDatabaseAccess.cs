namespace Ariva.Infra.Timescale;

/// <summary>
/// The database grants of each host (ARV-083a; CWE-269, CWE-285; ASVS 5.0 V13.2.2): one declared manifest that script 0074
/// creates and an integration test holds a migrated database to exactly, no more and no less (HostDatabaseAccessTests).
/// <para>
/// Every host connects with a login of its own, a member of <see cref="RuntimeRole"/> (the base every host needs) and of its
/// host role (what that host's code reads and writes beyond the base) and of nothing else; the migration job creates the
/// login (<c>ariva_ensure_host_login</c>). The availability ledger that Ariva.Api.Cronz runs has a login of its own too
/// (<see cref="AvailabilityRole"/>, Database:Availability), the only one that may record availability minutes or move a
/// site's cursor. On a vm-local database whose hosts share one login (no Database:HostLogins), that login holds every role
/// (<c>ariva_ensure_development_login</c>).
/// </para>
/// <para>
/// Each grant reads as ariva-db.sh's lists do: <c>relation=PRIVILEGE,...</c>, the table privileges in the order SELECT,
/// INSERT, UPDATE, DELETE, then each privilege held on some columns only, <c>PRIVILEGE(column column)</c>, with its columns in
/// byte order. Derived from the services each host registers (its Program.cs and the Ariva.Di extensions it calls) and the
/// tables those read and write: NHibernate entities (a many-to-one is loaded with its owner, so reading a device reads its
/// level, terminal and airport), raw SQL, the binary COPY writers and their staging tables (CREATE TEMP TABLE ... LIKE needs
/// SELECT on the table; TEMP itself comes from PUBLIC on the database), the outbox and the inbox, the Data Protection key
/// ring, the health and start-up checks. A statement with ON CONFLICT on a target or a row lock (FOR UPDATE, FOR SHARE) needs
/// SELECT, and a row lock UPDATE on some column. No role holds a sequence (outbox_message's identity column needs none) or
/// the continuous aggregates, which no code reads.
/// </para>
/// A new table or column a host reads or writes adds its grant here and in its script, in the same change.
/// </summary>
internal static class HostDatabaseAccess
{
    #region Constants

    /// <summary>The base every host login holds (script 0001, narrowed by script 0074).</summary>
    public const string RuntimeRole = "ariva_runtime";

    public const string MainRole = "ariva_main";

    public const string IngestRole = "ariva_ingest";

    public const string StreamRole = "ariva_stream";

    public const string CronzRole = "ariva_cronz";

    public const string IntegrationRole = "ariva_integration";

    /// <summary>The availability ledger's role (ARV-118, owner decision 2026-10-10).</summary>
    public const string AvailabilityRole = "ariva_availability";

    #endregion

    #region Grants

    /// <summary>
    /// Every authenticated request's session, permission and site reads, the schema check, the vm-local environment guard, the
    /// Data Protection key ring (by table, so the row signature of script 0073 is read and written with each row), the outbox
    /// with its relay and the relay's purge of the inbox (the inbox's own insert is the consuming hosts'). Of an account the base reads every column but its credentials (the password hash, salt, algorithm and
    /// iterations, and the protected TOTP seed): no host but Ariva.Api.Main loads a whole account, and the others read the
    /// columns their checks and their email recipients need (the session, permission and site checks, Stream's and
    /// Integration's alert emails, Cronz's report deliveries), so a host that is not Main cannot read a credential even though
    /// every pod mounts the Data Protection certificate. Ariva.Api.Main reads the whole account; Cronz the protected seed it
    /// re-protects.
    /// </summary>
    private static readonly string[] RuntimeGrants =
    [
        "data_protection_key=SELECT,INSERT",
        "database_environment=SELECT",
        "outbox_message=SELECT,INSERT,UPDATE,DELETE",
        "processed_event=SELECT,DELETE",
        "schema_version=SELECT",
        "user=SELECT(all_sites created_by created_by_id created_on display_name email failed_login_count id is_break_glass is_disabled last_login_on locked_until modified_by modified_by_id modified_on must_change_password totp_enrolled totp_last_step user_name)",
        "user_role=SELECT",
        "user_session=SELECT",
        "user_site=SELECT"
    ];

    /// <summary>
    /// Ariva.Api.Main: the administration and operations API (accounts, sites, topology, devices, displays and advertising,
    /// alert rules and alerts, reports and schedules, calendars, integration clients and outbound endpoints, validation), the
    /// sign-in and its sessions, the live hub, the device health consumer and sweep, the display staleness sweep, the
    /// validation results and their freeze (the shadow nowcast through the validation reader's login, never this one), the
    /// flight schedule uploads, the demo seeds, the break-glass and stored name commands.
    /// </summary>
    private static readonly string[] MainGrants =
    [
        "advertising_media=SELECT,INSERT,UPDATE",
        "advertising_play=SELECT,INSERT",
        "advertising_playlist=SELECT,INSERT,UPDATE",
        "advertising_policy=SELECT,INSERT,UPDATE",
        "advertising_schedule=SELECT,INSERT,UPDATE",
        "airport=SELECT,INSERT,UPDATE",
        "alert=SELECT,UPDATE",
        "alert_rule=SELECT,INSERT,UPDATE",
        "audit_entry=SELECT,INSERT",
        "availability_minute=SELECT",
        "border_desk_interval=SELECT",
        "border_desk_session=SELECT",
        "border_egate_interval=SELECT",
        "checkpoint=SELECT,INSERT,UPDATE",
        "counter_allocation=SELECT",
        "desk=SELECT,INSERT,UPDATE",
        "desk_code_mapping=SELECT,INSERT,UPDATE",
        "desk_minute=SELECT",
        "desk_observation=SELECT,INSERT",
        "desk_observation_batch=SELECT,INSERT",
        "device=SELECT,INSERT,UPDATE",
        "device_calibration=SELECT,INSERT",
        "device_heartbeat=SELECT,INSERT,UPDATE",
        "display=SELECT,INSERT,UPDATE",
        "email_message=SELECT,INSERT",
        "feed_freshness=SELECT,INSERT,UPDATE",
        "flight_leg=SELECT,INSERT,UPDATE",
        "floor_plan=SELECT,INSERT,UPDATE",
        "inbound_lane_demand=SELECT",
        "instruction_slide=SELECT,INSERT,UPDATE",
        "integration_client=SELECT,INSERT,UPDATE",
        "lane_staffing=SELECT",
        "level=SELECT,INSERT,UPDATE",
        "line=SELECT,INSERT,UPDATE,DELETE",
        "line_minute=SELECT",
        "maintenance_window=SELECT,INSERT,UPDATE",
        "manual_count=SELECT,INSERT",
        "operating_day_exception=SELECT,INSERT,UPDATE",
        "operating_week=SELECT,INSERT,UPDATE",
        "outbound_endpoint=SELECT,INSERT,UPDATE",
        "overflow_minute=SELECT",
        "processed_event=INSERT",
        "queue_bin=SELECT",
        "queue_minute=SELECT",
        "recovery_code=SELECT,INSERT,UPDATE",
        "refresh_token=SELECT,INSERT,UPDATE",
        "report_delivery=SELECT",
        "report_schedule=SELECT,INSERT,UPDATE",
        "report_schedule_recipient=SELECT,INSERT,DELETE",
        "site=SELECT,INSERT,UPDATE(modified_by modified_by_id modified_on name)",
        "terminal=SELECT,INSERT,UPDATE",
        "tracer_batch=SELECT,INSERT",
        "tracer_run=SELECT,INSERT",
        "user=SELECT,INSERT,UPDATE",
        "user_role=INSERT,DELETE",
        "user_session=INSERT,UPDATE",
        "user_site=INSERT,DELETE",
        "validation_campaign=SELECT,INSERT,UPDATE(closed_by_id closed_utc started_by_id started_utc status)",
        "validation_campaign_desk=SELECT,INSERT",
        "validation_campaign_line=SELECT,INSERT",
        "validation_campaign_zone=SELECT,INSERT",
        "validation_result_revision=SELECT,INSERT",
        "zone=SELECT,INSERT,UPDATE,DELETE",
        "zone_health=SELECT,INSERT,UPDATE",
        "zone_health_bin=SELECT",
        "zone_outage=SELECT",
        "zone_profile=SELECT,INSERT,UPDATE,DELETE"
    ];

    /// <summary>Ariva.Api.Ingest: a device's credential and its published zone (SvcDeviceGateway). It writes nothing but the base.</summary>
    private static readonly string[] IngestGrants =
    [
        "airport=SELECT",
        "device=SELECT",
        "level=SELECT",
        "line=SELECT",
        "terminal=SELECT",
        "zone=SELECT",
        "zone_profile=SELECT"
    ];

    /// <summary>
    /// Ariva.Api.Stream: the queue engine's outputs and checkpoints (StreamStore, ZoneGeometrySource, DeskTermSource), the sensing
    /// and device health archives, the desk feed, the alert evaluation with its inputs, emails and audit entries, the arrival
    /// wave it projects, and replays. It writes no account.
    /// </summary>
    private static readonly string[] StreamGrants =
    [
        "alert=SELECT,INSERT,UPDATE",
        "alert_rule=SELECT",
        "alert_rule_state=SELECT,INSERT,UPDATE,DELETE",
        "audit_entry=INSERT",
        "border_desk_interval=SELECT",
        "border_desk_session=SELECT",
        "border_egate_interval=SELECT",
        "checkpoint=SELECT",
        "desk=SELECT",
        "desk_code_mapping=SELECT",
        "desk_feed_state=SELECT,INSERT,UPDATE",
        "desk_minute=SELECT,INSERT,UPDATE",
        "desk_sensor_minute=SELECT,INSERT,UPDATE",
        "desk_zone_reading=SELECT,INSERT",
        "device=SELECT",
        "device_health_event=SELECT,INSERT",
        "device_heartbeat=SELECT",
        "egate_minute=SELECT,INSERT,UPDATE",
        "email_message=SELECT,INSERT",
        "flight_leg=SELECT",
        "inbound_lane_demand=SELECT",
        "lane_staffing=SELECT",
        "line=SELECT",
        "line_minute=SELECT,INSERT,UPDATE",
        "overflow_minute=SELECT,INSERT,UPDATE",
        "processed_event=INSERT",
        "queue_bin=SELECT,INSERT,UPDATE",
        "queue_minute=SELECT,INSERT,UPDATE",
        "queue_minute_shadow=INSERT,SELECT(minute_utc zone_key),UPDATE(no_service nowcast_degraded nowcast_minutes sensor_cycle_minutes updated_on)",
        "replay_run=SELECT,INSERT",
        "sensing_batch=SELECT,INSERT",
        "sensing_day_key=SELECT,INSERT,DELETE",
        "sensing_event=SELECT,INSERT",
        "stream_offset=SELECT,INSERT,UPDATE",
        "stream_partition_count=SELECT,INSERT",
        "stream_zone_state=SELECT,INSERT,UPDATE",
        "zone=SELECT",
        "zone_health_bin=SELECT,INSERT,UPDATE",
        "zone_outage=SELECT,INSERT,UPDATE(closed recorded_on to_utc)",
        "zone_profile=SELECT"
    ];

    /// <summary>
    /// Ariva.Api.Cronz: the report deliveries and the reports they render, the staffing recommendation, the retention purge
    /// (<see cref="Functions"/>), the re-protection of stored secrets (one column of each of three tables), the key ring upgrade
    /// check at start (those three columns, <see cref="KeyRingUpgradeCheckRoles"/>) and the key lifecycle job (ARV-207,
    /// KeyLifecycleRound): the key ring purge (<see cref="Functions"/>) and the dates of the credentials in use, read with their
    /// status and whether each is set (an integration client's secret and seed, a device's and a display's credential, an
    /// outbound endpoint's secret), never a secret; a column grant cannot tell "is set" from the value, so a display's credential
    /// hash is readable to Cronz, as a device's already was for the reports. The availability ledger runs under its own login.
    /// </summary>
    private static readonly string[] CronzGrants =
    [
        "airport=SELECT",
        "alert=SELECT",
        "border_egate_interval=SELECT",
        "checkpoint=SELECT",
        "desk=SELECT",
        "desk_minute=SELECT",
        "device=SELECT",
        "device_heartbeat=SELECT",
        "display=SELECT(credential_hash credential_issued_on deleted_on)",
        "flight_leg=SELECT",
        "inbound_lane_demand=SELECT",
        "integration_client=SELECT(id secret_changed_utc status totp_changed_utc totp_secret_protected),UPDATE(totp_secret_protected)",
        "lane_staffing=SELECT,INSERT",
        "outbound_endpoint=SELECT(id secret_changed_utc secret_protected status),UPDATE(secret_protected)",
        "queue_minute=SELECT",
        "report_delivery=SELECT,INSERT,UPDATE(attempts reason sent_utc status)",
        "report_schedule=SELECT",
        "report_schedule_recipient=SELECT",
        "retention_period=SELECT",
        "site=SELECT",
        "terminal=SELECT",
        "user=SELECT(totp_secret_protected),UPDATE(totp_secret_protected)",
        "zone=SELECT",
        "zone_outage=SELECT",
        "zone_profile=SELECT"
    ];

    /// <summary>
    /// Ariva.Api.Integration: the AODB and immigration intake, the AMAN feed consumers and pull, the ACRIS pull, the Integration
    /// API's client sign-in (its attempt, lockout and replay columns only), call record and idempotency keys, and the alert email
    /// sender (an email's delivery columns only).
    /// </summary>
    private static readonly string[] IntegrationGrants =
    [
        "airport=SELECT",
        "alert=SELECT",
        "aman_pull_cursor=SELECT,INSERT,UPDATE",
        "audit_entry=INSERT",
        "border_desk_interval=SELECT,INSERT",
        "border_desk_session=SELECT,INSERT",
        "border_egate_interval=SELECT,INSERT",
        "checkpoint=SELECT",
        "counter_allocation=SELECT,INSERT,UPDATE",
        "desk=SELECT",
        "desk_code_mapping=SELECT",
        "email_message=SELECT,UPDATE(attempts next_attempt_utc reason sent_utc status)",
        "feed_freshness=SELECT,INSERT,UPDATE",
        "flight_event=INSERT",
        "flight_leg=SELECT,INSERT,UPDATE",
        "inbound_lane_demand=SELECT,INSERT,UPDATE",
        "integration_call=INSERT",
        "integration_client=SELECT,UPDATE(attempt_window_utc attempts_in_window failed_attempts last_token_utc locked_until_utc totp_last_step)",
        "integration_idempotency=SELECT,INSERT,DELETE,UPDATE(response_body status_code)",
        "outbound_endpoint=SELECT,UPDATE(consecutive_failures last_modified last_poll_utc last_status)",
        "processed_event=INSERT",
        "site=SELECT",
        "terminal=SELECT"
    ];

    /// <summary>The availability ledger (AvailabilityLedger): what it reads to decide a site's minutes, and their only writer.</summary>
    private static readonly string[] AvailabilityGrants =
    [
        "airport=SELECT",
        "availability_cursor=SELECT,INSERT,UPDATE",
        "availability_minute=SELECT,INSERT",
        "maintenance_window=SELECT",
        "operating_day_exception=SELECT",
        "operating_week=SELECT",
        "queue_minute=SELECT",
        "terminal=SELECT",
        "zone=SELECT",
        "zone_profile=SELECT"
    ];

    #endregion

    #region Properties

    /// <summary>The roles a host login holds one of, besides <see cref="RuntimeRole"/>, in the order the scripts name them.</summary>
    public static IReadOnlyList<string> HostRoles { get; } = [MainRole, IngestRole, StreamRole, CronzRole, IntegrationRole, AvailabilityRole];

    /// <summary>The base and the host roles.</summary>
    public static IReadOnlyList<string> Roles { get; } = [RuntimeRole, .. HostRoles];

    /// <summary>Each role's grants on the relations of public, as described above; a relation not named grants the role nothing.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Grants { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [RuntimeRole] = RuntimeGrants,
        [MainRole] = MainGrants,
        [IngestRole] = IngestGrants,
        [StreamRole] = StreamGrants,
        [CronzRole] = CronzGrants,
        [IntegrationRole] = IntegrationGrants,
        [AvailabilityRole] = AvailabilityGrants
    };

    /// <summary>
    /// The functions each role may run (EXECUTE), by their identity as PostgreSQL prints it (regprocedure), in byte order:
    /// Cronz's key lifecycle job calls script 0072's key ring purge and its retention job script 0054's purge. No other role
    /// runs any of Ariva's functions.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Functions { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [CronzRole] = ["ariva_data_protection_key_purge(integer,integer)", "ariva_retention_purge(text,integer)"]
    };

    /// <summary>
    /// The host roles that may read every stored Data Protection payload: the protected TOTP seed of "user" and of
    /// integration_client, and outbound_endpoint's protected secret (Ariva.Api.Main the whole tables, Cronz those columns, which
    /// it re-protects). Only these hosts run the key ring upgrade check at start (KeyRingUpgradeGate, ARV-211), which reads all
    /// three. Ingest and Stream read none of those values, and Integration two of them (a client's seed, an endpoint's secret)
    /// but no account's, so their logins are refused the check's read and they do not run it. An upgrade or a restore that would
    /// leave the values under keys the hosts refuse is stopped by the migration job at release, as its own login, and by Main and
    /// Cronz at start. A unit test holds this list to the roles whose grants above read all three columns.
    /// </summary>
    public static IReadOnlyList<string> KeyRingUpgradeCheckRoles { get; } = [MainRole, CronzRole];

    #endregion

    #region Methods

    /// <summary>The host role of a host's Application:ShortName (main, ingest, stream, cronz, integration), or null.</summary>
    public static string RoleOf(string shortName) => shortName switch
    {
        "main" => MainRole,
        "ingest" => IngestRole,
        "stream" => StreamRole,
        "cronz" => CronzRole,
        "integration" => IntegrationRole,
        _ => null
    };

    /// <summary>
    /// Whether the host of this Application:ShortName runs the key ring upgrade check at start: its role is one of
    /// <see cref="KeyRingUpgradeCheckRoles"/>. False for any other host and for a process without a host's name.
    /// </summary>
    public static bool RunsKeyRingUpgradeCheck(string shortName) => KeyRingUpgradeCheckRoles.Any(role => string.Equals(role, RoleOf(shortName), StringComparison.Ordinal));

    /// <summary>The relation an entry names.</summary>
    public static string RelationOf(string entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(entry);
        return entry[..entry.IndexOf('=', StringComparison.Ordinal)];
    }

    #endregion
}
