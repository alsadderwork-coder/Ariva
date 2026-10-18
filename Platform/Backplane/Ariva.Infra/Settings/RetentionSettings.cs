using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>One hypertable whose TimescaleDB retention policy the migration job sets (ARV-111a), with its period in days.</summary>
public sealed record HypertableRetentionPeriod(string Table, int Days);

/// <summary>One relational data set the Cronz retention job thins (ARV-111a), with its period in days.</summary>
public sealed record RelationalRetentionPeriod(string DataSet, int Days);

/// <summary>
/// <c>Database:Retention</c> (ARV-111a): how long each kind of stored data is kept, in whole days, per environment. Every
/// value here is a migration-job parameter: the job (Ariva.Api.Main <c>--migrate</c>, the Helm hook) reads it with the migration
/// login after the versioned scripts and
/// <list type="bullet">
/// <item>sets the TimescaleDB retention policy of each hypertable in <see cref="Hypertables()"/> to it, replacing the value the
/// script that created the table gave (scripts 0017, 0019, 0040, 0053 and 0062 are immutable; their values are the defaults here),
/// only when it differs, so a second run changes nothing;</item>
/// <item>writes each relational period in <see cref="DataSets()"/> to <c>retention_period</c> (script 0054), a table the runtime
/// role may read but never write, which the Cronz retention job's database function reads: the runtime login can delete
/// alert emails, integration call records and report deliveries only once they are older than the period set here, because it
/// cannot move the time column the purge reads either (column grants on email_message, 0023, and report_delivery, 0054; no
/// UPDATE on integration_call, 0025) (CWE-269).</item>
/// </list>
/// In the clusters the values come from the Helm values <c>migration.retention</c> of each environment, passed to the job as
/// environment variables. The defaults are the scripts' values and the Proposed periods of docs/product/decisions.md
/// (2026-10-16, TC-62 and TC-66 to TC-68); docs/privacy/data-inventory.md lists every table with its period and where it is set.
/// Hypertables kept indefinitely (queue, desk and line minutes, bins, availability: the pilot's evidence) are not parameters:
/// shortening them is a decision for a reviewed script.
/// </summary>
public sealed class RetentionSettings
{
    #region Constants

    public const string SectionName = "Database:Retention";

    /// <summary>The relational data sets script 0054 knows, in the order the Cronz retention job visits them.</summary>
    public static readonly IReadOnlyList<string> DataSetNames = ["email_message", "integration_call", "report_delivery"];

    /// <summary>The longest period any value may take (10 years).</summary>
    public const int MaxDays = 3_650;

    /// <summary>
    /// Raw sensing (TC-62, the contract's dispute window): at least a week, longer than the ingest's maximum age and the sensing
    /// topics' retention (3 days), so nothing is dropped while it can still arrive.
    /// </summary>
    public const int MinRawSensingDays = 7;

    /// <summary>Desk zone readings: at least 2 days, so the desk feed can catch up after a day's outage.</summary>
    public const int MinDeskZoneReadingDays = 2;

    /// <summary>Desk zone readings are desk-level border data, an input only: at most 90 days.</summary>
    public const int MaxDeskZoneReadingDays = 90;

    /// <summary>The staffing recommendation minutes are the open desks alert's backtest history (90 days): never shorter.</summary>
    public const int MinLaneStaffingDays = 90;

    /// <summary>
    /// Relational records: at least 30 days, the floor script 0054 also enforces. Report deliveries must outlive the delivery
    /// round's look-back (3 days) by far, or a deleted row could be owed and sent again.
    /// </summary>
    public const int MinRelationalDays = 30;

    #endregion

    #region Properties

    /// <summary><c>sensing_event</c>, <c>sensing_batch</c> and <c>device_health_event</c>: one value, so a replay of a period finds its events and the devices' health together (TC-62).</summary>
    public int RawSensingDays { get; init; } = 90;

    /// <summary><c>desk_zone_reading</c> (script 0040's 7 days).</summary>
    public int DeskZoneReadingDays { get; init; } = 7;

    /// <summary><c>lane_staffing</c> (script 0053's 90 days).</summary>
    public int LaneStaffingDays { get; init; } = 90;

    /// <summary>
    /// <c>advertising_play</c>, the proof of play (ARV-144, script 0062's 400 days): a year's campaigns and their comparison
    /// with the year before's last month. The installation's value for its sites (Proposed, decisions.md 2026-10-18 ARV-144).
    /// </summary>
    public int AdvertisingPlayDays { get; init; } = 400;

    /// <summary><c>email_message</c>: the record of who was told what, with staff email addresses (TC-67, Proposed 180 days).</summary>
    public int AlertEmailDays { get; init; } = 180;

    /// <summary><c>integration_call</c>: the audit of every Integration API call (TC-68, Proposed 400 days).</summary>
    public int IntegrationCallDays { get; init; } = 400;

    /// <summary><c>report_delivery</c>: one row per schedule, local day and recipient (TC-66, Proposed 400 days).</summary>
    public int ReportDeliveryDays { get; init; } = 400;

    #endregion

    #region Methods

    /// <summary>The hypertables whose policy the migration job sets, with their periods (a method, so the binder never sees it).</summary>
    public IReadOnlyList<HypertableRetentionPeriod> Hypertables() =>
    [
        new("sensing_event", RawSensingDays),
        new("sensing_batch", RawSensingDays),
        new("device_health_event", RawSensingDays),
        new("desk_zone_reading", DeskZoneReadingDays),
        new("lane_staffing", LaneStaffingDays),
        new("advertising_play", AdvertisingPlayDays)
    ];

    /// <summary>The relational data sets the Cronz retention job thins, with their periods (names as script 0054 accepts them).</summary>
    public IReadOnlyList<RelationalRetentionPeriod> DataSets() =>
    [
        new(DataSetNames[0], AlertEmailDays),
        new(DataSetNames[1], IntegrationCallDays),
        new(DataSetNames[2], ReportDeliveryDays)
    ];

    public static RetentionSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<RetentionSettings>() ?? new RetentionSettings();
    }

    /// <summary>What is out of range; empty when every period may be applied.</summary>
    public IEnumerable<string> Problems()
    {
        if (RawSensingDays is < MinRawSensingDays or > MaxDays)
            yield return Range(nameof(RawSensingDays), MinRawSensingDays, MaxDays);
        if (DeskZoneReadingDays is < MinDeskZoneReadingDays or > MaxDeskZoneReadingDays)
            yield return Range(nameof(DeskZoneReadingDays), MinDeskZoneReadingDays, MaxDeskZoneReadingDays);
        if (LaneStaffingDays is < MinLaneStaffingDays or > MaxDays)
            yield return Range(nameof(LaneStaffingDays), MinLaneStaffingDays, MaxDays);
        if (AdvertisingPlayDays is < MinRelationalDays or > MaxDays)
            yield return Range(nameof(AdvertisingPlayDays), MinRelationalDays, MaxDays);
        if (AlertEmailDays is < MinRelationalDays or > MaxDays)
            yield return Range(nameof(AlertEmailDays), MinRelationalDays, MaxDays);
        if (IntegrationCallDays is < MinRelationalDays or > MaxDays)
            yield return Range(nameof(IntegrationCallDays), MinRelationalDays, MaxDays);
        if (ReportDeliveryDays is < MinRelationalDays or > MaxDays)
            yield return Range(nameof(ReportDeliveryDays), MinRelationalDays, MaxDays);
    }

    /// <summary>Stops the migration job before anything changes when a period is out of range.</summary>
    public void EnsureValid()
    {
        var problems = Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    private static string Range(string name, int min, int max) =>
        string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{name} is a whole number of days from {min:N0} to {max:N0} (ARV-111a).");

    #endregion
}
