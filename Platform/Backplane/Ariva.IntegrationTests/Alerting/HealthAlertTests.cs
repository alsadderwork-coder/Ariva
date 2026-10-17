using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Alerting;
using Ariva.Infra.Alerting;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-114b against TimescaleDB with scripts 0038 and 0051: three health-check rules on the demo airport's Visitors queue
/// (A-VIS), one per metric, evaluated live once a minute while a constructed evening is written as the stream writes it:
/// each minute's queue row, and each bin's <c>zone_health_bin</c> row provisional while it runs, then with its residual and
/// end sample one to four minutes after its end, then final up to 40 minutes after it. The live alerts are exactly each
/// rule's backtest over the same range (a rule waits for a bin to settle rather than judge its minutes without a value);
/// a health rule watches queue zones only; the tables refuse an unknown metric and a threshold beyond a health metric's
/// unit, whoever writes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HealthAlertTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _evening;

    private static readonly DateTime From = new(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc);
    private const int LastMinute = 230;
    private static readonly DateTime Until = From.AddMinutes(LastMinute);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertHealth);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime At(int minute) => From.AddMinutes(minute);

    private Task<AlertTickResult> TickAsync(DateTime nowUtc) =>
        new AlertEvaluation(_host.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance).TickAsync(nowUtc, Ct);

    private static AlertRuleRequest RequestOf(AlertRuleViewModel r) =>
        new(r.SiteCode, r.Name, r.Zones, r.Metric, r.Comparator, r.Threshold, r.MinQueueLength, r.ClearThreshold, r.SustainMinutes, r.ClearAfterMinutes, r.Severity,
            r.OwnerRole, r.EscalateAfterMinutes, r.EscalateToRole, r.EscalationContact, r.NotifyByEmail, r.Enabled, r.LeadMinutes);

    /// <summary>One bin: its residual, tracks, minutes outside the capacity, and when after its end the stream stored its end and its final row.</summary>
    private sealed record Plan(int Start, long Residual, long Entered, long Exited, int Outside, int EndedAfter, int FinalAfter);

    // The constructed evening of HealthAlertSeriesTests (unit), on PostgreSQL.
    private static readonly Plan[] Bins =
    [
        new(0, 0, 20, 20, 0, 0, 6),
        new(15, 7, 20, 15, 2, 1, 19),
        new(30, -9, 40, 39, 3, 3, 4),
        new(45, 1, 20, 17, 0, 0, 30),
        new(60, 0, 10, 6, 0, 2, 12),
        new(75, 12, 30, 29, 5, 1, 9),
        new(90, 0, 0, 0, 0, 0, 1),
        new(105, -5, 20, 20, 0, 4, 25),
        new(120, 0, 20, 19, 0, 0, 3),
        new(135, 6, 20, 10, 1, 2, 40)
    ];

    private static readonly (string Name, string Metric, string Comparator, double Threshold)[] Rules =
    [
        ("Residual above 4 people", "ConservationResidual", "GreaterThan", 4),
        ("Track completion below 90 percent", "TrackCompletion", "LessThan", 90),
        ("Occupancy out of range", "OccupancyOutOfRange", "GreaterThan", 0)
    ];

    // Writes what the stream had stored by the end of minute t: the queue minute t and every bin's row as it then stood.
    private async Task StoreByAsync(NpgsqlConnection connection, int t)
    {
        if (t <= LastMinute)
        {
            await using var minute = new NpgsqlCommand("""
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, nowcast_minutes, throughput_per_minute, length_degraded, updated_on)
                VALUES ('DMO/A-VIS', @minute, 12, 5, 1.2, 5, false, now())
                """, connection);
            minute.Parameters.AddWithValue("minute", At(t));
            await minute.ExecuteNonQueryAsync(Ct);
        }

        foreach (var bin in Bins.Where(b => b.Start <= t))
        {
            var end = bin.Start + 15;
            var final = t >= end + bin.FinalAfter;
            var ended = t >= end + bin.EndedAfter;
            var exited = final ? bin.Exited : ended ? Math.Min(bin.Exited, bin.Entered / 2) : 0;
            var entered = ended ? bin.Entered : bin.Entered / 2;
            var abandoned = final ? bin.Entered - bin.Exited : 0;
            var minutes = ended ? 15 : Math.Max(0, t - bin.Start);
            await using var row = new NpgsqlCommand("""
                INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
                    conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
                    track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
                VALUES ('DMO/A-VIS', @start, 1, 15, @status, 12, @entered, @exited, 10, @end, @residual, @entered, @exited, @abandoned, 0, 0, 0, @open,
                    @rate, @minutes, @minutes, @outside, now())
                ON CONFLICT (zone_key, start_utc, revision) DO UPDATE SET status = EXCLUDED.status, entries = EXCLUDED.entries, exits = EXCLUDED.exits,
                    occupancy_end = EXCLUDED.occupancy_end, conservation_residual = EXCLUDED.conservation_residual, tracks_entered = EXCLUDED.tracks_entered,
                    tracks_exited = EXCLUDED.tracks_exited, tracks_abandoned = EXCLUDED.tracks_abandoned, tracks_open = EXCLUDED.tracks_open,
                    track_completion_rate = EXCLUDED.track_completion_rate, occupancy_minutes = EXCLUDED.occupancy_minutes, capacity_minutes = EXCLUDED.capacity_minutes,
                    minutes_outside_capacity = EXCLUDED.minutes_outside_capacity, updated_on = EXCLUDED.updated_on
                WHERE zone_health_bin.status <> 'Final'
                """, connection);
            row.Parameters.AddWithValue("start", At(bin.Start));
            row.Parameters.AddWithValue("status", final ? "Final" : "Provisional");
            row.Parameters.AddWithValue("entered", entered);
            row.Parameters.AddWithValue("exited", exited);
            row.Parameters.AddWithValue("end", ended ? 8 : DBNull.Value);
            row.Parameters.AddWithValue("residual", ended ? bin.Residual : DBNull.Value);
            row.Parameters.AddWithValue("abandoned", abandoned);
            row.Parameters.AddWithValue("open", entered - exited - abandoned);
            row.Parameters.AddWithValue("rate", entered > 0 ? (double)exited / entered : DBNull.Value);
            row.Parameters.AddWithValue("minutes", minutes);
            row.Parameters.AddWithValue("outside", ended ? bin.Outside : 0);
            await row.ExecuteNonQueryAsync(Ct);
        }
    }

    // The demo airport, the three rules (every other rule disabled), then the evening written and ticked a minute at a time.
    private async Task<Guid> EveningAsync()
    {
        var admin = await _host.CreateUserAsync("it.health.admin." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await Gate.WaitAsync(Ct);
        try
        {
            if (_evening)
                return admin;
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            foreach (var (name, metric, comparator, threshold) in Rules)
            {
                var created = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", name, ["A-VIS"], metric,
                    comparator, threshold, null, null, 1, 1, "Warning", RoleCodes.BorderShiftSupervisor, null, null, null, false), Ct));
                created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
            }

            (await _host.ReadAsync<int>("""
                UPDATE alert_rule SET enabled = (metric IN ('ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange')), created_on = '2026-09-30T16:00:00Z'
                WHERE site_code = 'DMO' RETURNING 1
                """)).Should().Be(1);
            // The rules' first tick: each target starts at the present, 16:59 its first minute.
            (await TickAsync(From.AddSeconds(5))).Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0);

            await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
            await connection.OpenAsync(Ct);
            for (var t = 0; t <= LastMinute + 60; t++)
            {
                await StoreByAsync(connection, t);
                (await TickAsync(At(t + 1).AddSeconds(5))).Should().Match<AlertTickResult>(r => r.Ran && r.Failed == 0, $"the tick after {At(t):HH:mm}");
            }

            _evening = true;
            return admin;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Task<List<(string Metric, string Zone, DateTime Raised, DateTime? Cleared, double Value, DateTime? Bin)>> StoredAsync() => _host.AsCallerAsync(null, async s =>
    {
        var alerts = await NHibernate.Linq.LinqExtensionMethods.ToListAsync(s.GetRequiredService<IUnitOfWork>().StorageProvider.Query<Alert>()
            .Where(a => a.RaisedUtc >= From && a.RaisedUtc <= Until), Ct);
        return alerts.OrderBy(a => a.Metric.ToString(), StringComparer.Ordinal).ThenBy(a => a.RaisedUtc)
            .Select(a => (a.Metric.ToString(), a.ZoneName, a.RaisedUtc, a.ClearedUtc <= Until ? a.ClearedUtc : null, a.RaisedValue, a.BinStartUtc)).ToList();
    });

    [Fact]
    public async Task Evening_Should_RaiseLiveExactlyWhatTheBacktestGives_When_EachBinSettlesAfterItsEnd()
    {
        var admin = await EveningAsync();
        var stored = await StoredAsync();
        DateTime? Open() => null;

        stored.Where(a => a.Metric == "ConservationResidual").Select(a => (a.Raised, a.Cleared, a.Value, a.Bin)).Should().Equal(
            (At(29), At(59), 7.0, At(15)), (At(89), At(104), 12.0, At(75)), (At(119), At(134), 5.0, At(105)), (At(149), Open(), 6.0, At(135)));
        stored.Where(a => a.Metric == "TrackCompletion").Select(a => (a.Raised, a.Cleared, a.Value, a.Bin)).Should().Equal(
            (At(29), At(44), 75.0, At(15)), (At(59), At(89), 85.0, At(45)), (At(149), Open(), 50.0, At(135)));
        stored.Where(a => a.Metric == "OccupancyOutOfRange").Select(a => (a.Raised, a.Cleared, a.Value, a.Bin)).Should().Equal(
            (At(29), At(59), 2.0, At(15)), (At(89), At(104), 5.0, At(75)), (At(149), Open(), 1.0, At(135)));

        var rules = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO" }, Ct))).Data.Data
            .Where(r => r.Enabled).ToList();
        rules.Select(r => r.Metric).Should().BeEquivalentTo(Rules.Select(r => r.Metric));
        foreach (var rule in rules)
        {
            var backtest = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(rule), From, Until), Ct));
            backtest.HasErrors.Should().BeFalse(string.Join(", ", backtest.ErrorMessages ?? []));
            backtest.Data.Alerts.Select(a => (rule.Metric, a.ZoneName, a.RaisedUtc, a.ClearedUtc, a.Value, a.BinStartUtc))
                .Should().Equal(stored.Where(a => a.Metric == rule.Metric), $"{rule.Metric}: the backtest is the live evaluation's fold over the same settled bins");
            backtest.Data.TargetsWithData.Should().Be(1);
        }
    }

    [Fact]
    public async Task Tick_Should_RaiseNothingMore_When_RunAgainAfterTheEvening()
    {
        await EveningAsync();
        var before = await StoredAsync();

        (await TickAsync(At(LastMinute + 62).AddSeconds(5))).Raised.Should().Be(0);

        (await StoredAsync()).Should().Equal(before, "a minute already taken changes nothing");
    }

    [Fact]
    public async Task Rules_Should_WatchQueueZonesOnly_When_TheyJudgeAHealthCheck()
    {
        var admin = await EveningAsync();
        var refused = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Band completion", ["A-VIS", "A-OV"],
            "TrackCompletion", "LessThan", 90, null, null, 1, 1, "Warning", RoleCodes.BorderShiftSupervisor, null, null, null, false, false), Ct));
        refused.HasErrors.Should().BeTrue();
        refused.ErrorMessages.Should().Contain(AlertRuleErrors.QueueZonesOnly);

        var overflowRule = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Band occupied", ["A-OV"],
            "OverflowOccupied", "IsTrue", null, null, null, 1, 1, "Warning", RoleCodes.BorderShiftSupervisor, null, null, null, false, false), Ct));
        overflowRule.HasErrors.Should().BeFalse("other metrics still watch overflow zones");

        var probe = new AlertRuleValues("Probe", ["A-VIS", "A-OV", "A-NOWHERE"], AlertMetric.ConservationResidual, AlertComparator.GreaterThan, 4, null, null, 1, 1,
            AlertSeverity.Warning, null, null, null, null, false, true);
        var targets = await _host.AsCallerAsync(admin, s => s.GetRequiredService<AlertInputs>().TargetsAsync("DMO", probe, Ct));
        targets.Should().ContainSingle().Which.Should().Be(new AlertTarget("A-VIS", null), "a health rule's targets are the queue zones of the published profile");
    }

    [Fact]
    public async Task Inputs_Should_ReadTheSitesOwnHealthRowsOnly_When_AnotherSiteHasAZoneOfTheSameName()
    {
        var admin = await EveningAsync();
        (await _host.ReadAsync<int>("""
            INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
                conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
                track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
            SELECT 'XXX/A-VIS', s, 1, 15, 'Final', 1, 0, 0, 0, 900, -900, 10, 0, 10, 0, 0, 0, 0, 0, 15, 15, 15, now()
            FROM generate_series(timestamptz '2026-09-30T16:00:00Z', timestamptz '2026-09-30T21:00:00Z', interval '15 minutes') AS s
            RETURNING 1
            """)).Should().Be(1);
        try
        {
            var stored = await StoredAsync();
            foreach (var metric in new[] { "ConservationResidual", "TrackCompletion", "OccupancyOutOfRange" })
            {
                var rule = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO", Metric = metric }, Ct)))
                    .Data.Data.Single(r => r.Enabled);
                var backtest = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(rule), From, Until), Ct));
                backtest.Data.Alerts.Select(a => (rule.Metric, a.ZoneName, a.RaisedUtc, a.ClearedUtc, a.Value, a.BinStartUtc))
                    .Should().Equal(stored.Where(a => a.Metric == metric), $"{metric} at DMO is unchanged by another site's rows (CWE-863)");
            }
        }
        finally
        {
            await _host.ReadAsync<int>("DELETE FROM zone_health_bin WHERE zone_key = 'XXX/A-VIS' RETURNING 1");
        }
    }

    // ARV-114b security review (CWE-120): D-VIS's sensors stopped 30 days before the evening while A-VIS kept a month of bins.
    // The live bound reads each zone in its own window around its own latest minute, so 60 rows are enough for both zones
    // (one window from D-VIS's last minute to A-VIS's would read A-VIS's 2,880 bins of the month); and a cap below what the
    // windows hold refuses the read rather than cutting it short.
    [Fact]
    public async Task Inputs_Should_ReadEachZoneInItsOwnWindowAndRefuseBeyondTheCap_When_OneZoneStoppedAMonthAgo()
    {
        var admin = await EveningAsync();
        var stopped = From.AddDays(-30);
        (await _host.ReadAsync<int>("""
            INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
                conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
                track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
            SELECT 'DMO/A-VIS', s, 1, 15, 'Final', 12, 20, 20, 10, 10, 0, 20, 20, 0, 0, 0, 0, 0, 1, 15, 15, 0, now()
            FROM generate_series(timestamptz '2026-08-31T17:00:00Z', timestamptz '2026-09-30T16:45:00Z', interval '15 minutes') AS s
            UNION ALL
            SELECT 'DMO/D-VIS', timestamptz '2026-08-31T17:00:00Z', 1, 15, 'Provisional', 12, 20, 5, 10, NULL, NULL, 20, 5, 0, 0, 0, 0, 15, 0.25, 15, 15, 0, now()
            RETURNING 1
            """)).Should().Be(1);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            for (var minute = 0; minute <= 20; minute++)
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, nowcast_minutes, throughput_per_minute, length_degraded, updated_on)
                    VALUES ('DMO/D-VIS', @minute, 12, 5, 1.2, 5, false, now())
                    """, connection);
                insert.Parameters.AddWithValue("minute", stopped.AddMinutes(minute));
                await insert.ExecuteNonQueryAsync(Ct);
            }
        }

        try
        {
            (await _host.ReadAsync<long>("SELECT count(*) FROM zone_health_bin WHERE zone_key = 'DMO/A-VIS' AND start_utc < '2026-09-30T17:00:00Z'"))
                .Should().Be(2_880, "a month of A-VIS's bins lies between the two zones' latest minutes");
            var zones = new[] { "A-VIS", "D-VIS" };
            var notAfter = At(LastMinute + 60);
            var (bounded, refused, readA, refusedRange) = await _host.AsCallerAsync(admin, async s =>
            {
                var unitOfWork = s.GetRequiredService<IUnitOfWork>();
                var small = new AlertInputs(unitOfWork, new Ariva.Core.Alerting.NoArrivalWave()) { MaxHealthRows = 60 };
                var tiny = new AlertInputs(unitOfWork, new Ariva.Core.Alerting.NoArrivalWave()) { MaxHealthRows = 5 };
                var rule = new AlertRuleValues("Probe", ["A-VIS"], AlertMetric.ConservationResidual, AlertComparator.GreaterThan, 4, null, null, 1, 1,
                    AlertSeverity.Warning, null, null, null, null, false, true);
                return (await small.LatestHealthMinutesAsync("DMO", AlertMetric.ConservationResidual, zones, notAfter, Ct),
                    await tiny.LatestHealthMinutesAsync("DMO", AlertMetric.ConservationResidual, zones, notAfter, Ct),
                    await small.ReadAsync("DMO", rule, [new AlertTarget("A-VIS", null)], From, From.AddMinutes(60), Ct),
                    await small.ReadAsync("DMO", rule, [new AlertTarget("A-VIS", null)], From.AddHours(-24), From, Ct));
            });

            bounded.Should().NotBeNull("each zone is read in its own window, about a dozen bins each");
            bounded["A-VIS"].Should().Be(At(LastMinute), "every A-VIS bin of the evening is settled");
            bounded["D-VIS"].Should().Be(stopped.AddMinutes(13), "D-VIS's own 17:00 bin, a month ago, has no residual yet: its first minute, 17:14, waits");
            refused.Should().BeNull("beyond the cap the read is refused, never cut short");
            readA.Should().ContainSingle().Which.Minutes.Should().HaveCount(60, "an hour of A-VIS reads its own bins only");
            refusedRange.Should().BeNull("a day of A-VIS's history holds 96 bins, above a cap of 60");
        }
        finally
        {
            await _host.ReadAsync<int>("DELETE FROM zone_health_bin WHERE (zone_key = 'DMO/A-VIS' AND start_utc < '2026-09-30T17:00:00Z') OR zone_key = 'DMO/D-VIS' RETURNING 1");
            await _host.ReadAsync<int>("DELETE FROM queue_minute WHERE zone_key = 'DMO/D-VIS' RETURNING 1");
        }
    }

    // ARV-114b re-check (CWE-120, the cap's paths): with a cap of one row, every health rule's tick is skipped with one warning
    // that names the site and the rule code only, its targets' states unchanged, and a backtest answers BacktestTooLarge; the
    // next tick under the normal cap resumes from the same minutes and clears the three open alerts on a healthy bin.
    [Fact]
    public async Task Tick_Should_SkipTheRuleWithOneWarningAndResume_When_ItsReadsExceedTheCap()
    {
        var admin = await EveningAsync();
        var codes = await _host.AsCallerAsync(admin, async s => (await s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO" }, Ct))
            .Data.Data.Where(r => r.Enabled).Select(r => r.Code).Order(StringComparer.Ordinal).ToList());
        codes.Should().HaveCount(3);
        // After the evening: A-VIS's minutes 20:51 to 21:10 and a final, healthy 20:45 bin (it governs from 20:59).
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            for (var t = LastMinute + 1; t <= LastMinute + 20; t++)
            {
                await using var minute = new NpgsqlCommand("""
                    INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, nowcast_minutes, throughput_per_minute, length_degraded, updated_on)
                    VALUES ('DMO/A-VIS', @minute, 12, 5, 1.2, 5, false, now())
                    """, connection);
                minute.Parameters.AddWithValue("minute", At(t));
                await minute.ExecuteNonQueryAsync(Ct);
            }

            await using var bin = new NpgsqlCommand("""
                INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
                    conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
                    track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
                VALUES ('DMO/A-VIS', @start, 1, 15, 'Final', 12, 20, 20, 10, 10, 0, 20, 20, 0, 0, 0, 0, 0, 1, 15, 15, 0, now())
                """, connection);
            bin.Parameters.AddWithValue("start", At(LastMinute - 5));
            await bin.ExecuteNonQueryAsync(Ct);
        }

        const string States = """
            SELECT string_agg(r.code || ':' || s.armed || ':' || s.sustained || ':' || s.clearing || ':' || coalesce(to_char(s.last_minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), '-'), ',' ORDER BY r.code)
            FROM alert_rule_state s JOIN alert_rule r ON r.id = s.rule_id WHERE r.enabled
            """;
        var capture = new WarningCapture();
        await using var capped = new AccountsHost(fixture, database: TestDatabase.AlertHealth, configure: s =>
        {
            s.AddScoped(sp => new AlertInputs(sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<Ariva.Core.Alerting.IArrivalWaveSource>()) { MaxHealthRows = 1 });
            s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(capture);
        });
        var cappedAdmin = await capped.CreateUserAsync("it.health.capped." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator]);
        await capped.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", cappedAdmin);
        try
        {
            var before = await _host.ReadAsync<string>(States);
            var skipped = await new AlertEvaluation(capped.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(),
                NullLogger<AlertEvaluation>.Instance).TickAsync(At(LastMinute + 21).AddSeconds(5), Ct);

            skipped.Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0 && t.Raised == 0 && t.Cleared == 0);
            capture.Warnings.Should().HaveCount(3, "one warning per rule skipped");
            foreach (var code in codes)
                capture.Warnings.Should().ContainSingle(w => w.Contains($"site DMO rule {code}:", StringComparison.Ordinal));
            capture.Warnings.Should().OnlyContain(w => !w.Contains("A-VIS", StringComparison.Ordinal) && !w.Contains("residual", StringComparison.OrdinalIgnoreCase),
                "the site and the rule code only: no zone, no value");
            (await _host.ReadAsync<string>(States)).Should().Be(before, "a skipped rule keeps its targets' states");

            var backtest = await capped.AsCallerAsync(cappedAdmin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(
                new AlertRuleRequest("DMO", "Probe", ["A-VIS"], "ConservationResidual", "GreaterThan", 4, null, null, 1, 1, "Warning", RoleCodes.BorderShiftSupervisor,
                    null, null, null, false), From, Until), Ct));
            backtest.HasErrors.Should().BeTrue();
            backtest.ErrorMessages.Should().Equal(AlertRuleErrors.BacktestTooLarge);

            var resumed = await TickAsync(At(LastMinute + 21).AddSeconds(5));
            resumed.Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0 && t.Cleared == 3, "the next tick under the normal cap clears all three on the healthy bin");
            (await _host.ReadAsync<long>("""
                SELECT count(*) FROM alert WHERE metric IN ('ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange') AND cleared_utc = '2026-09-30T20:59:00Z'
                """)).Should().Be(3, "cleared at 20:59, the healthy 20:45 bin's first minute: the skipped minutes were judged, not lost");
            (await _host.ReadAsync<string>(States)).Should().NotBe(before, "the states moved on");
        }
        finally
        {
            await _host.ReadAsync<int>("DELETE FROM queue_minute WHERE zone_key = 'DMO/A-VIS' AND minute_utc > '2026-09-30T20:50:00Z' RETURNING 1");
            await _host.ReadAsync<int>("DELETE FROM zone_health_bin WHERE zone_key = 'DMO/A-VIS' AND start_utc = '2026-09-30T20:45:00Z' RETURNING 1");
        }
    }

    /// <summary>Collects the warnings the alert evaluation logs (ARV-114b: a skipped rule names the site and the rule code only).</summary>
    private sealed class WarningCapture : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Queue { get; } = new();

        public IReadOnlyList<string> Warnings => [.. Queue];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(WarningCapture capture, string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning && category.EndsWith("AlertRuleTick", StringComparison.Ordinal))
                    capture.Queue.Enqueue(formatter(state, exception));
            }
        }
    }

    [Theory]
    [InlineData("UPDATE alert_rule SET threshold = 101 WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET threshold = 1441 WHERE metric = 'OccupancyOutOfRange'", "23514")]
    [InlineData("UPDATE alert_rule SET threshold = 100001 WHERE metric = 'ConservationResidual'", "23514")]
    [InlineData("UPDATE alert_rule SET comparator = 'IsTrue', threshold = NULL WHERE metric = 'ConservationResidual'", "23514")]
    [InlineData("UPDATE alert_rule SET min_queue_length = 5 WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET lead_minutes = 30 WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET metric = 'TrackCompletionExpression' WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET metric = 'trackcompletion' WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET threshold = 'NaN' WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET threshold = 'Infinity' WHERE metric = 'ConservationResidual'", "23514")]
    [InlineData("UPDATE alert_rule SET threshold = '-Infinity' WHERE metric = 'OccupancyOutOfRange'", "23514")]
    [InlineData("UPDATE alert_rule SET clear_threshold = 'NaN' WHERE metric = 'TrackCompletion'", "23514")]
    [InlineData("UPDATE alert_rule SET clear_threshold = 'Infinity' WHERE metric = 'TrackCompletion'", "23514")]
    public async Task Tables_Should_RefuseTheRuntimeRole_When_ItStoresARuleTheEvaluatorCannotApply(string sql, string state)
    {
        await EveningAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(state);
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Tables_Should_AcceptEachHealthMetricAtTheBoundOfItsUnit_When_TheRuntimeRoleWritesIt()
    {
        await EveningAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        await using var bounds = new NpgsqlCommand("""
            UPDATE alert_rule SET threshold = CASE metric WHEN 'TrackCompletion' THEN 100 WHEN 'OccupancyOutOfRange' THEN 1440 ELSE 100000 END
            WHERE metric IN ('ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange')
            """, connection, transaction);

        (await bounds.ExecuteNonQueryAsync(Ct)).Should().Be(3);
        await transaction.RollbackAsync(Ct);
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert WHERE metric IN ('ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange')"))
            .Should().Be(10, "script 0051 lets alerts of the three metrics be stored");
    }
}
