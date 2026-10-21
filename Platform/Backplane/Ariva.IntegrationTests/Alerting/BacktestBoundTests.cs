using System.Diagnostics;
using System.Globalization;
using Ariva.Api.Common.Settings;
using Ariva.Core;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Core.Services;
using Ariva.Core.Services.Alerting;
using Ariva.Infra.Alerting;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-096b (CWE-400, ASVS 5.0 V15.2.2): the alert rule backtest (POST api/v1/admin/alert-rules/backtest,
/// <see cref="ISvcAlertRules.BacktestAsync"/>) at its bound, measured against the default request timeout
/// (<see cref="RequestTimeoutsSettings.DefaultSeconds"/>, 120 s), which the endpoint runs under. The bound is a rule on the 64 zones a
/// rule may watch (<see cref="AlertRule.MaxZones"/>) judged over the longest range a backtest takes (<c>SvcAlertRules.MaxBacktest</c>,
/// 24 hours): 92,160 zone minutes, or for a sensor rule the 1,000 devices it may watch (<see cref="AlertInputs.MaxDevices"/>) over
/// the day's 1,441 minutes.
/// <para>
/// The seed, in a database of this class alone: the demo airport (DMO) with 64 more queue zones (BT-01 to BT-64) in its published
/// profile; for each of them three UTC days (2026-09-27 to 2026-09-29) of <c>queue_minute</c> rows (counts and live part), 15-minute
/// <c>queue_bin</c> rows (a second revision for one bin in four) and <c>zone_health_bin</c> rows, written by the stream's own binary
/// COPY writer (<see cref="StreamStore"/>), so a P90 rule's look-back (150 minutes plus a day) is full and the tables hold rows outside
/// the range; the backtested day of <c>lane_staffing</c> as the staffing job writes it (13 intervals per lane minute), by binary
/// COPY; 1,000 commissioned devices on the 64 zones with four outages a day each. The tables are vacuumed and analysed, as
/// autovacuum keeps them in service.
/// </para>
/// <para>
/// Each metric that reads the most is backtested over 2026-09-29 once to warm up and once measured: the wall time of
/// <see cref="ISvcAlertRules.BacktestAsync"/> alone and the managed bytes allocated meanwhile (<see cref="GC.GetTotalAllocatedBytes"/>,
/// the whole process: the only other work is the test host's own), printed in a fixed format and held well under the default
/// timeout (half of it); a run that comes near it is the reason to give the backtest its own timeout policy. The predicted
/// nowcast reads its arrival wave from a test double that gives every zone a wave on every minute: the production projection
/// starts at the present, so for a past day it has none, and the double is the most work a prediction can take.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BacktestBoundTests(PostgresFixture fixture) : IAsyncDisposable
{
    #region The bound

    /// <summary>
    /// This class's own database (created and given the shipped scripts once per run): its seed is far larger than any other
    /// alerting test's, so it shares a database with none of them.
    /// </summary>
    private const string Database = "it_alert_backtest_bound";

    private const string Site = "DMO";
    private const string ZonePrefix = "BT-";

    /// <summary>The most zones one rule watches.</summary>
    private const int Zones = AlertRule.MaxZones;

    /// <summary>The most devices one sensor rule watches.</summary>
    private const int Devices = AlertInputs.MaxDevices;

    private const int MinutesPerDay = 1440;
    private const int BinMinutes = 15;
    private const int BinsPerDay = MinutesPerDay / BinMinutes;
    private const int SeededDays = 3;
    private const int OutagesPerDay = 4;
    private const int StaffingIntervals = 13;
    private const double Throughput = 5;

    /// <summary>The day backtested, 24 hours in UTC; the two days before it hold the look-back of a P90 rule and rows outside the range.</summary>
    private static readonly DateTime Day = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime FirstSeeded = Day.AddDays(1 - SeededDays);

    /// <summary>The timeout of every endpoint without its own policy, the backtest's included.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(new RequestTimeoutsSettings().DefaultSeconds);

    /// <summary>Well within the default timeout: half of it.</summary>
    private static readonly TimeSpan WellWithin = DefaultTimeout / 2;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Guid? _admin;

    private readonly AccountsHost _host = new(fixture, new Dictionary<string, string> { ["Database:Name"] = Database }, TestDatabase.AlertRules,
        s => s.AddScoped<IArrivalWaveSource, BoundWave>());

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ZoneName(int zone) => string.Create(CultureInfo.InvariantCulture, $"{ZonePrefix}{zone + 1:00}");

    #endregion

    #region The measurement

    public static TheoryData<AlertMetric> Heaviest => new()
    {
        AlertMetric.QueueLength, AlertMetric.Nowcast, AlertMetric.BinP90, AlertMetric.PredictedNowcast, AlertMetric.DesksBelowPlan,
        AlertMetric.ConservationResidual, AlertMetric.SensorOffline
    };

    [Theory]
    [MemberData(nameof(Heaviest))]
    public async Task BacktestAsync_Should_AnswerWellWithinTheDefaultRequestTimeout_When_ARuleOn64ZonesIsJudgedOverADay(AlertMetric metric)
    {
        var admin = await SeededAsync();
        var values = RuleOf(metric);
        values.Problems().Should().BeEmpty();
        var request = new AlertBacktestRequest(RequestOf(values), Day, Day.AddDays(1));
        var targets = metric == AlertMetric.SensorOffline ? Devices : Zones;
        // The minutes after the range's start up to its end: each zone's stored minutes of the day, or each device's every minute
        // from 00:00 to 24:00 inclusive.
        var expected = metric == AlertMetric.SensorOffline ? (long)Devices * (MinutesPerDay + 1) : (long)Zones * MinutesPerDay;

        var warmUp = await MeasureAsync(admin, request);
        var measured = await MeasureAsync(admin, request);
        var (read, inputsAlone) = await MinutesReadAsync(admin, values, request);

        Report("Backtest warm-up", metric, read, warmUp);
        Report("Backtest at the bound", metric, read, measured);
        Report("Backtest inputs read alone", metric, read, inputsAlone);
        measured.Result.HasErrors.Should().BeFalse(string.Join(", ", measured.Result.ErrorMessages ?? []));
        Outcome(metric, measured.Result.Data);
        read.Should().Be(expected, "the backtest judges the whole range: a day of minutes for each of the rule's targets");
        measured.Result.Data.Targets.Should().Be(targets);
        measured.Result.Data.TargetsWithData.Should().Be(targets, "every target has values on the seeded day");
        measured.Result.Data.Count.Should().BePositive("the seeded day crosses the rule's threshold, so the fold raises and clears");
        warmUp.Result.Data.Count.Should().Be(measured.Result.Data.Count, "the same minutes give the same alerts");
        foreach (var run in new[] { warmUp, measured })
        {
            run.Elapsed.Should().BeLessThan(WellWithin, string.Create(CultureInfo.InvariantCulture,
                $"ARV-096b: a backtest of {metric} at its bound (64 zones, 24 hours) answers well within the default request timeout of {DefaultTimeout.TotalSeconds:0} s (under {WellWithin.TotalSeconds:0} s), or it needs a timeout policy of its own"));
        }
    }

    private sealed record Run(Fluentx.Result<AlertBacktestViewModel> Result, TimeSpan Elapsed, long Allocated);

    /// <summary>
    /// One backtest as one request would run it (a fresh scope and unit of work): the wall time of the service call alone and
    /// the managed bytes the process allocated meanwhile (precise, across threads, so across the awaits).
    /// </summary>
    private Task<Run> MeasureAsync(Guid admin, AlertBacktestRequest request) => _host.AsCallerAsync(admin, async s =>
    {
        var rules = s.GetRequiredService<ISvcAlertRules>();
        // Each run starts from a collected heap, so one run's garbage is not collected in the next one's time.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var result = await rules.BacktestAsync(request, Ct);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        return new Run(result, watch.Elapsed, allocated);
    });

    /// <summary>
    /// The minutes the backtest's fold judges, read through the same inputs over the same range; the read alone is measured as
    /// the backtest is, so the two lines show how the backtest's time divides between the read and the fold.
    /// </summary>
    private Task<(long Minutes, Run Read)> MinutesReadAsync(Guid admin, AlertRuleValues values, AlertBacktestRequest request) => _host.AsCallerAsync(admin, async s =>
    {
        var inputs = s.GetRequiredService<AlertInputs>();
        var targets = await inputs.TargetsAsync(Site, values, Ct);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var series = await inputs.ReadAsync(Site, values, targets, AlertInputs.Minute(request.FromUtc).AddMinutes(-1), AlertInputs.Minute(request.ToUtc), Ct);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        series.Should().NotBeNull("the range is within the reads' bounds");
        return (series.Sum(t => (long)t.Minutes.Count), new Run(null, watch.Elapsed, allocated));
    });

    private static void Report(string label, AlertMetric metric, long minutes, Run run)
    {
        var what = metric == AlertMetric.SensorOffline ? string.Create(CultureInfo.InvariantCulture, $"{metric} over {Devices:N0} devices") : metric.ToString();
        Send(string.Create(CultureInfo.InvariantCulture,
            $"{label}: metric {what}, {Zones} zones, 24 h, {minutes:N0} minutes read, {run.Elapsed.TotalSeconds:0.00} s, {run.Allocated / 1048576.0:0.0} MB allocated"));
    }

    private static void Outcome(AlertMetric metric, AlertBacktestViewModel view) =>
        Send(string.Create(CultureInfo.InvariantCulture,
            $"Backtest outcome: metric {metric}, {view.Count:N0} alerts ({view.Alerts.Count} listed, truncated {view.Truncated}), {view.Targets:N0} targets, {view.TargetsWithData:N0} with data"));

    // The diagnostic message for the runner's diagnostics, the same line in the test's output for the console logger.
    private static void Send(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    #endregion

    #region The rules

    /// <summary>A rule on the 64 zones for each metric, with thresholds the seeded day crosses (border metrics owned by the border).</summary>
    private static AlertRuleValues RuleOf(AlertMetric metric)
    {
        var zones = Enumerable.Range(0, Zones).Select(ZoneName).ToList();

        AlertRuleValues Rule(AlertComparator comparator, double? threshold, int sustain, int clearAfter, double? clear = null, int? minQueue = null,
            string owner = null, int? lead = null) =>
            new($"Bound {metric}", zones, metric, comparator, threshold, minQueue, clear, sustain, clearAfter, AlertSeverity.Warning, owner, null, null, null,
                false, true, lead);

        return metric switch
        {
            AlertMetric.QueueLength => Rule(AlertComparator.GreaterThan, 60, 3, 3, clear: 50),
            AlertMetric.Nowcast => Rule(AlertComparator.GreaterThan, 12, 3, 3, clear: 10, minQueue: 10),
            AlertMetric.BinP90 => Rule(AlertComparator.GreaterThan, 15, 1, 1),
            AlertMetric.PredictedNowcast => Rule(AlertComparator.GreaterThan, 20, 2, 2, owner: RoleCodes.BorderShiftSupervisor, lead: AlertRule.MaxLeadMinutes),
            AlertMetric.DesksBelowPlan => Rule(AlertComparator.GreaterOrEqual, 2, 3, 2, owner: RoleCodes.BorderShiftSupervisor),
            AlertMetric.ConservationResidual => Rule(AlertComparator.GreaterThan, 3, 1, 1),
            AlertMetric.SensorOffline => Rule(AlertComparator.IsTrue, null, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }

    private static AlertRuleRequest RequestOf(AlertRuleValues v) =>
        new(Site, v.Name, v.Zones, v.Metric.ToString(), v.Comparator.ToString(), v.Threshold, v.MinQueueLength, v.ClearThreshold, v.SustainMinutes,
            v.ClearAfterMinutes, v.Severity.ToString(), v.OwnerRole, v.EscalateAfterMinutes, v.EscalateToRole, v.EscalationContact, v.NotifyByEmail, v.Enabled,
            v.LeadMinutes);

    #endregion

    #region The seed

    /// <summary>The seed, once for the class; then this test's host (one per test, as in the other alerting tests).</summary>
    private async Task<Guid> SeededAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_admin is { } known)
            {
                await _host.CreateUserAsync("it.bound.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var watch = Stopwatch.StartNew();
            await CreateDatabaseAsync();
            var admin = await _host.CreateUserAsync("it.bound.admin", roles: [RoleCodes.SystemAdministrator], allSites: true);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await ZonesAndDevicesAsync();
            var store = new StreamStore(new DatabaseSettings
            {
                Host = fixture.Hostname, Port = fixture.Port, Name = Database, Username = fixture.AdminUsername, Password = fixture.AdminPassword
            }, TimeProvider.System);
            for (var day = 0; day < SeededDays; day++)
                await store.SaveAsync(Checkpoint(day), Ct);
            await StaffingAsync();
            await VacuumAsync();

            var counts = await CountsAsync();
            Send(string.Create(CultureInfo.InvariantCulture,
                $"Backtest bound seed: {counts[0]:N0} queue zones, queue_minute {counts[1]:N0} rows, queue_bin {counts[2]:N0}, zone_health_bin {counts[3]:N0}, lane_staffing {counts[4]:N0}, device {counts[5]:N0}, zone_outage {counts[6]:N0}, in {watch.Elapsed.TotalSeconds:0.0} s"));
            counts.Should().Equal([
                Zones, (long)Zones * SeededDays * MinutesPerDay, (long)Zones * SeededDays * BinsPerDay * 5 / 4, (long)Zones * SeededDays * BinsPerDay,
                (long)Zones * MinutesPerDay * StaffingIntervals, Devices, (long)Devices * SeededDays * OutagesPerDay
            ], "zones, queue_minute, queue_bin, zone_health_bin, lane_staffing, device and zone_outage rows as planned");
            _admin = admin;
            return admin;
        }
        finally
        {
            Gate.Release();
        }
    }

    // On a shared server (PostgresFixture.ExternalVariable) the database may be left from an earlier run: dropped first.
    private async Task CreateDatabaseAsync()
    {
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString("postgres")))
        {
            await connection.OpenAsync(Ct);
            await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS it_alert_backtest_bound WITH (FORCE)", connection))
                await drop.ExecuteNonQueryAsync(Ct);
            await using var create = new NpgsqlCommand("CREATE DATABASE it_alert_backtest_bound", connection);
            await create.ExecuteNonQueryAsync(Ct);
        }

        await new SqlScriptRunner(fixture.ConnectionString(Database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(Database));
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>
    /// The 64 queue zones in the published profile (written as a draft's zones would have been, past the triggers that keep a
    /// published profile's geometry, as AlertRuleServiceTests does), with A-VIS's level and outline; 1,000 commissioned devices
    /// spread over them; four outages of 12 minutes a day for each device.
    /// </summary>
    private async Task ZonesAndDevicesAsync()
    {
        await using var connection = await OpenAsync();
        await using (var zones = new NpgsqlCommand("""
            SET session_replication_role = replica;
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon)
            SELECT gen_random_uuid(), v.profile_id, 'BT-' || lpad(CAST(n AS text), 2, '0'), 'Queue', v.level_id, v.polygon
              FROM generate_series(1, @zones) n,
                   (SELECT z.profile_id, z.level_id, z.polygon FROM zone z JOIN zone_profile p ON p.id = z.profile_id
                     WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.name = 'A-VIS') v;
            SET session_replication_role = origin;
            """, connection))
        {
            zones.Parameters.AddWithValue("zones", Zones);
            await zones.ExecuteNonQueryAsync(Ct);
        }

        await using (var devices = new NpgsqlCommand("""
            INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                orientation_degrees, footprint_radius_metres, footprint_source, queue_zone_name)
            SELECT gen_random_uuid(), 'BT-S' || lpad(CAST(n AS text), 4, '0'), 'DMO', 'StereoVision', 'Reference', 'HttpsPush', 'Canonical', 'Ntp', 'Online',
                   v.level_id, 0, 0, 3, 0, 2, 'Vendor', 'BT-' || lpad(CAST(n % @zones + 1 AS text), 2, '0')
              FROM generate_series(0, @devices - 1) n,
                   (SELECT z.level_id FROM zone z JOIN zone_profile p ON p.id = z.profile_id
                     WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.name = 'BT-01') v
            """, connection))
        {
            devices.Parameters.AddWithValue("zones", Zones);
            devices.Parameters.AddWithValue("devices", Devices);
            await devices.ExecuteNonQueryAsync(Ct);
        }

        await using var outages = new NpgsqlCommand("""
            INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
            SELECT 'DMO/' || d.queue_zone_name, d.code, o.from_utc, o.from_utc + interval '12 minutes', true, now()
              FROM device d
             CROSS JOIN LATERAL (SELECT @first + k * interval '6 hours' + (hashtext(d.code) & 255) * interval '1 minute' AS from_utc
                                   FROM generate_series(0, @outages - 1) k) o
             WHERE d.site_code = 'DMO' AND d.code LIKE 'BT-S%'
            """, connection);
        outages.Parameters.AddWithValue("first", NpgsqlDbType.TimestampTz, FirstSeeded);
        outages.Parameters.AddWithValue("outages", SeededDays * OutagesPerDay);
        await outages.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The queue length of a zone at a minute: a wave of 5 to 75 people every 30 minutes, each zone a little later.</summary>
    private static int Length(int zone, int minute) => (int)Math.Round(40 + 35 * Math.Sin(2 * Math.PI * (minute + 3 * zone) / 30.0));

    private static readonly WaitSummary MinuteWaits = new(6, 4.5, 4.0, 7.5, 8.0, 0.9, [(8, 1), (9, 1), (12, 2), (15, 1), (16, 1)]);

    /// <summary>One day of the stream's outputs for the 64 zones: minutes (counts and live part), bins and health checks.</summary>
    private static StreamCheckpoint Checkpoint(int day)
    {
        var outputs = new List<ZoneOutputs>(Zones);
        for (var z = 0; z < Zones; z++)
        {
            var name = ZoneName(z);
            var key = ZoneKeys.For(Site, name);
            var minutes = new List<MinuteResult>(MinutesPerDay);
            var live = new List<QueueLiveMinute>(MinutesPerDay);
            for (var t = 0; t < MinutesPerDay; t++)
            {
                var index = day * MinutesPerDay + t;
                var at = FirstSeeded.AddMinutes(index);
                var length = Length(z, index);
                minutes.Add(new MinuteResult(name, at, BinStatus.Final, 5 + index % 7, 5 + (index + 3) % 7, MinuteWaits));
                // One minute in 97 has a degraded length (no nowcast judged), as a sensor outage leaves it.
                live.Add(new QueueLiveMinute(key, at, length, true, index % 97 == 0, (length + 1) / Throughput, Throughput, null, false));
            }

            var bins = new List<BinResult>(BinsPerDay * 5 / 4);
            var health = new List<ZoneHealthBin>(BinsPerDay);
            for (var b = 0; b < BinsPerDay; b++)
            {
                var index = day * BinsPerDay + b;
                var start = FirstSeeded.AddMinutes(index * BinMinutes);
                // Three hours of long waits, three of short ones; a recomputed revision for one bin in four.
                var p90 = (index + z) % 24 < 12 ? 20.0 : 8.0;
                bins.Add(Bin(name, start, 1, p90, null));
                if (index % 4 == 0)
                    bins.Add(Bin(name, start, 2, p90 + 1, "Late events"));
                health.Add(Health(name, start, (index + z) % 9 - 4));
            }

            outputs.Add(new ZoneOutputs(key, minutes, bins, live, [], Health: health));
        }

        return new StreamCheckpoint("it-backtest-bound", outputs, [], [], []);
    }

    private static BinResult Bin(string zone, DateTime start, int revision, double p90, string reason) =>
        new(zone, start, TimeSpan.FromMinutes(BinMinutes), revision, BinStatus.Final, BinQuality.Good, 60, 58,
            new WaitSummary(55, p90 * 0.6, p90 * 0.5, p90, p90 * 1.1, 0.8, []), 2, 0, 0, 0, 0, 0, revision == 1 ? 0 : 3, 12, reason);

    /// <summary>A settled bin whose conservation residual is <paramref name="residual"/> (-4 to 4 people).</summary>
    private static ZoneHealthBin Health(string zone, DateTime start, long residual)
    {
        const int occupancyStart = 20;
        const long entries = 30, exits = 28;
        var occupancyEnd = (int)(occupancyStart + entries - exits - residual);
        return new ZoneHealthBin(zone, start, TimeSpan.FromMinutes(BinMinutes), 1, BinStatus.Final, 12, entries, exits, occupancyStart, occupancyEnd, residual,
            30, 27, 2, 0, 0, 0, 1, 0.9, BinMinutes, BinMinutes, 0);
    }

    /// <summary>
    /// The backtested day of <c>lane_staffing</c> as the staffing job writes it (script 0053): per lane minute the 13 intervals of
    /// the plan, interval 0 the current quarter hour from the minute; 8 desks, 4 open, a recommendation of 4 to 7 desks that
    /// changes every 5 minutes; every row passes the read-back check.
    /// </summary>
    private async Task StaffingAsync()
    {
        await using var connection = await OpenAsync();
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
                wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now, open_now_degraded, desks,
                target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc) FROM STDIN (FORMAT BINARY)
            """, Ct);
        for (var z = 0; z < Zones; z++)
        {
            var key = ZoneKeys.For(Site, ZoneName(z));
            var lane = LaneCategory.Reference[z % LaneCategory.Reference.Count];
            for (var t = 0; t < MinutesPerDay; t++)
            {
                var minute = Day.AddMinutes(t);
                var quarter = minute.AddMinutes(-(t % BinMinutes));
                var recommended = (short)(4 + (t / 5 + z) % 4);
                for (short k = 0; k < StaffingIntervals; k++)
                {
                    await copy.StartRowAsync(Ct);
                    await copy.WriteAsync(key, NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(minute, NpgsqlDbType.TimestampTz, Ct);
                    await copy.WriteAsync(k, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(lane, NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(k == 0 ? minute : quarter.AddMinutes(k * BinMinutes), NpgsqlDbType.TimestampTz, Ct);
                    await copy.WriteAsync((short)(k == 0 ? BinMinutes - t % BinMinutes : BinMinutes), NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(30.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(recommended, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync("Wait", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(false, NpgsqlDbType.Boolean, Ct);
                    await copy.WriteNullAsync(Ct);
                    await copy.WriteAsync(8.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(12.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(false, NpgsqlDbType.Boolean, Ct);
                    await copy.WriteNullAsync(Ct);
                    await copy.WriteAsync("None", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync("None", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(minute.AddMinutes(-1), NpgsqlDbType.TimestampTz, Ct);
                    await copy.WriteAsync(40.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync((short)4, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(false, NpgsqlDbType.Boolean, Ct);
                    await copy.WriteAsync((short)8, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(10.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(0.85, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(0.5, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync("Measured", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(minute.AddSeconds(45), NpgsqlDbType.TimestampTz, Ct);
                }
            }
        }

        await copy.CompleteAsync(Ct);
    }

    /// <summary>The tables as autovacuum keeps them in service: dead rows of the upserts cleared and statistics for the planner.</summary>
    private async Task VacuumAsync()
    {
        await using var connection = await OpenAsync();
        await using var vacuum = new NpgsqlCommand("VACUUM (ANALYZE) queue_minute, queue_bin, zone_health_bin, lane_staffing, device, zone_outage, zone", connection);
        vacuum.CommandTimeout = 600;
        await vacuum.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<long>> CountsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id
                     WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.kind = 'Queue' AND z.name LIKE 'BT-%'),
                   (SELECT count(*) FROM queue_minute WHERE zone_key LIKE 'DMO/BT-%'),
                   (SELECT count(*) FROM queue_bin WHERE zone_key LIKE 'DMO/BT-%'),
                   (SELECT count(*) FROM zone_health_bin WHERE zone_key LIKE 'DMO/BT-%'),
                   (SELECT count(*) FROM lane_staffing WHERE zone_key LIKE 'DMO/BT-%'),
                   (SELECT count(*) FROM device WHERE site_code = 'DMO' AND code LIKE 'BT-S%'),
                   (SELECT count(*) FROM zone_outage WHERE zone_key LIKE 'DMO/BT-%')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetInt64)];
    }

    #endregion

    #region Test double

    /// <summary>
    /// An arrival wave on every minute for each of the 64 zones (6 people a minute on average against 5 served, swinging by 3 over
    /// the hour), in place of the AODB and AMAN projection: the most work a predicted nowcast's backtest can take.
    /// </summary>
    private sealed class BoundWave : IArrivalWaveSource
    {
        public Task<IReadOnlyDictionary<DateTime, double>> ArrivalsAsync(string siteCode, string zoneName, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            if (!string.Equals(siteCode, Site, StringComparison.Ordinal) || zoneName is null || !zoneName.StartsWith(ZonePrefix, StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyDictionary<DateTime, double>>(null);
            var minutes = new Dictionary<DateTime, double>();
            for (var m = AlertInputs.Minute(fromUtc).AddMinutes(1); m <= toUtc; m = m.AddMinutes(1))
                minutes[m] = 6 + 3 * Math.Sin(2 * Math.PI * m.Minute / 60.0);
            return Task.FromResult<IReadOnlyDictionary<DateTime, double>>(minutes);
        }
    }

    #endregion
}
