using Ariva.Core;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Alerting;
using Ariva.Infra.Alerting;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-228b against TimescaleDB with every script: a sensor rule's minutes read through the typed reader and computed when taken
/// (<see cref="AlertInputs.OutageMinutes"/>), on the demo airport's Visitors queue (DMO/A-VIS) from 10:00 to 11:00 on 2026-09-30,
/// with three devices: SR-1's outages overlap, touch and end within a minute (one of them starts at 10:08:30, one ends at 10:31:10);
/// SR-2 has an outage that ended before the range and another site's outage under the same device code over the whole range,
/// neither read; SR-3's outage from 09:00 ends at 10:02 and the registry has it offline since 10:40:30, an outage the stream has not
/// closed yet. Each device's minutes are those the previous code built a minute at a time; the backtest raises and clears on them;
/// a read of more outages than <see cref="AlertInputs.MaxOutageRows"/> is refused, never cut short (the backtest answers too
/// large), and a read of exactly that many is not.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SensorAlertReadTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Guid? _admin;

    private static readonly DateTime Ten = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The outages of the DMO devices that overlap the hour: SR-1's four and SR-3's one.</summary>
    private const int OutagesInTheHour = 5;

    private static readonly (string Zone, string Device, DateTime From, DateTime To)[] Outages =
    [
        ("DMO/A-VIS", "SR-1", Ten.AddMinutes(5), Ten.AddMinutes(10)),
        ("DMO/A-VIS", "SR-1", Ten.AddMinutes(10), Ten.AddMinutes(12)),
        ("DMO/A-VIS", "SR-1", Ten.AddMinutes(8).AddSeconds(30), Ten.AddMinutes(9)),
        ("DMO/A-VIS", "SR-1", Ten.AddMinutes(30), Ten.AddMinutes(31).AddSeconds(10)),
        ("DMO/A-VIS", "SR-2", Ten.AddHours(-2), Ten.AddHours(-1)),
        ("XYZ/A-VIS", "SR-2", Ten.AddHours(-1), Ten.AddHours(2)),
        ("DMO/A-VIS", "SR-3", Ten.AddHours(-1), Ten.AddMinutes(2))
    ];

    private static readonly DateTime SeenLast = Ten.AddMinutes(40).AddSeconds(30);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertSensor);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlertRuleValues Rule { get; } = new("Sensors", ["A-VIS"], AlertMetric.SensorOffline, AlertComparator.IsTrue, null, null, null, 1, 1,
        AlertSeverity.Warning, null, null, null, null, false, true);

    private static AlertRuleRequest Request { get; } = new("DMO", "Sensors", ["A-VIS"], "SensorOffline", "IsTrue", null, null, null, 1, 1, "Warning", null, null, null,
        null, false);

    [Fact]
    public async Task SensorRule_Should_GiveThePreviousMinutes_When_OutagesOverlapTouchOrStayOpen()
    {
        var admin = await SeededAsync();

        var series = await _host.AsCallerAsync(admin, async s =>
        {
            var inputs = s.GetRequiredService<AlertInputs>();
            var targets = await inputs.TargetsAsync("DMO", Rule, Ct);
            return await inputs.ReadAsync("DMO", Rule, targets, Ten, Ten.AddHours(1), Ct);
        });

        series.Select(t => t.Target.DeviceCode).Should().Equal(["SR-1", "SR-2", "SR-3"], "the zone's commissioned devices, by code");
        foreach (var target in series)
        {
            target.Minutes.Should().BeOfType<AlertInputs.OutageMinutes>("a sensor rule's minutes are computed when taken, never stored");
            target.Minutes.Should().Equal(Previous(target.Target.DeviceCode), $"{target.Target.DeviceCode}: each minute as the list built a minute at a time had it");
        }

        Ones(series[0]).Should().Equal([5, 6, 7, 8, 9, 10, 11, 30, 31], "SR-1: 10:05 to 10:11 (touching and nested outages), 10:30 and 10:31 (an end at 10:31:10)");
        Ones(series[1]).Should().BeEmpty("SR-2: an outage before the hour, and another site's outage of the same code, are not read");
        Ones(series[2]).Should().Equal([1, .. Enumerable.Range(40, 21)], "SR-3: 10:01 (its outage ended at 10:02), then from 10:40 as the registry has it offline");
    }

    [Fact]
    public async Task Backtest_Should_RaiseAndClearOnTheComputedMinutes_When_ASensorRuleIsJudged()
    {
        var admin = await SeededAsync();

        var result = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(Request, Ten, Ten.AddHours(1)), Ct));

        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        result.Data.Targets.Should().Be(3);
        result.Data.Alerts.Select(a => (a.DeviceCode, a.RaisedUtc, a.ClearedUtc)).Should().Equal([
            ("SR-3", Ten, Ten.AddMinutes(2)),
            ("SR-1", Ten.AddMinutes(5), Ten.AddMinutes(12)),
            ("SR-1", Ten.AddMinutes(30), Ten.AddMinutes(32)),
            ("SR-3", Ten.AddMinutes(40), null)
        ], "raised on a device's first minute inside an outage, cleared on its first minute outside one");
    }

    [Fact]
    public async Task Reads_Should_BeRefusedNeverCutShort_When_TheyHoldMoreOutagesThanTheBound()
    {
        var admin = await SeededAsync();

        var (atBound, beyond) = await _host.AsCallerAsync(admin, async s =>
        {
            var unitOfWork = s.GetRequiredService<IUnitOfWork>();
            var exact = new AlertInputs(unitOfWork, new NoArrivalWave()) { MaxOutageRows = OutagesInTheHour };
            var under = new AlertInputs(unitOfWork, new NoArrivalWave()) { MaxOutageRows = OutagesInTheHour - 1 };
            var targets = await exact.TargetsAsync("DMO", Rule, Ct);
            return (await exact.ReadAsync("DMO", Rule, targets, Ten, Ten.AddHours(1), Ct), await under.ReadAsync("DMO", Rule, targets, Ten, Ten.AddHours(1), Ct));
        });

        atBound.Should().NotBeNull("the hour holds exactly the bound's outages");
        Ones(atBound[0]).Should().HaveCount(9);
        beyond.Should().BeNull("one outage more than the bound: refused, never cut short (CWE-120)");

        await using var capped = new AccountsHost(fixture, database: TestDatabase.AlertSensor, configure: s =>
            s.AddScoped(sp => new AlertInputs(sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<IArrivalWaveSource>()) { MaxOutageRows = OutagesInTheHour - 1 }));
        var cappedAdmin = await capped.CreateUserAsync("it.sensor.capped." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
        var backtest = await capped.AsCallerAsync(cappedAdmin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(Request, Ten, Ten.AddHours(1)), Ct));
        backtest.ErrorMessages.Should().Equal([AlertRuleErrors.BacktestTooLarge], "the backtest answers too large, it never judges part of the range");
    }

    #region The seed

    /// <summary>The minute offsets from 10:00 whose value is 1.</summary>
    private static List<int> Ones(AlertTargetSeries series) =>
        [.. series.Minutes.Where(m => m.Value == 1).Select(m => (int)(m.MinuteUtc - Ten).TotalMinutes)];

    /// <summary>The previous code's minutes for a device (AlertInputs.OutagesAsync before ARV-228b), from the same outages.</summary>
    private static List<AlertMinute> Previous(string device)
    {
        var spans = Outages.Where(o => o.Zone == "DMO/A-VIS" && o.Device == device && o.From <= Ten.AddHours(1) && o.To > Ten)
            .Select(o => (From: AlertInputs.Minute(o.From), o.To)).ToList();
        if (device == "SR-3")
            spans.Add((AlertInputs.Minute(SeenLast), DateTime.MaxValue));
        var list = new List<AlertMinute>();
        for (var m = Ten.AddMinutes(1); m <= Ten.AddHours(1); m = m.AddMinutes(1))
            list.Add(new AlertMinute(m, spans.Any(s => s.From <= m && m < s.To) ? 1 : 0));
        return list;
    }

    private async Task<Guid> SeededAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_admin is { } known)
            {
                // Each test's host starts its provider with its first account.
                await _host.CreateUserAsync("it.sensor.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var admin = await _host.CreateUserAsync("it.sensor.admin." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));

            await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
            await connection.OpenAsync(Ct);
            await using (var devices = new NpgsqlCommand("""
                INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                    orientation_degrees, footprint_radius_metres, footprint_source, queue_zone_name)
                SELECT gen_random_uuid(), d.code, 'DMO', 'StereoVision', 'Reference', 'HttpsPush', 'Canonical', 'Ntp', d.state, v.level_id, 0, 0, 3, 0, 2, 'Vendor', 'A-VIS'
                  FROM (VALUES ('SR-1', 'Online'), ('SR-2', 'Online'), ('SR-3', 'Offline')) d(code, state),
                       (SELECT z.level_id FROM zone z JOIN zone_profile p ON p.id = z.profile_id
                         WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.name = 'A-VIS') v
                """, connection))
                await devices.ExecuteNonQueryAsync(Ct);

            await using (var heartbeat = new NpgsqlCommand("""
                INSERT INTO device_heartbeat (device_id, site_code, last_seen_on, reported_online)
                SELECT id, 'DMO', @seen, false FROM device WHERE site_code = 'DMO' AND code = 'SR-3'
                """, connection))
            {
                heartbeat.Parameters.AddWithValue("seen", SeenLast);
                await heartbeat.ExecuteNonQueryAsync(Ct);
            }

            foreach (var (zone, device, from, to) in Outages)
            {
                await using var outage = new NpgsqlCommand("""
                    INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on) VALUES (@zone, @device, @from, @to, true, now())
                    """, connection);
                outage.Parameters.AddWithValue("zone", zone);
                outage.Parameters.AddWithValue("device", device);
                outage.Parameters.AddWithValue("from", from);
                outage.Parameters.AddWithValue("to", to);
                await outage.ExecuteNonQueryAsync(Ct);
            }

            _admin = admin;
            return admin;
        }
        finally
        {
            Gate.Release();
        }
    }

    #endregion
}
