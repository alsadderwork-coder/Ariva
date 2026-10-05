using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Alerting;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-038 against TimescaleDB with scripts 0020 and 0021: the reference evening (seed 9303) as the stream stored it
/// (minute rows, bins and the S-17 outage written by the stream store), the demo airport's R-001 to R-005, and the live
/// evaluation ticking once a minute from 17:00 to 20:35. R-001 raises on the Visitors queue at 18:05, R-003 on S-17
/// while it is out; every rule's backtest over the evening gives exactly the alerts the live evaluation stored. A
/// repeated tick changes nothing (dedupe), a withdrawn rule's alerts resolve, another evaluator holding the lock makes a
/// tick skip, and the runtime role can neither delete an alert nor rewrite what was raised.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AlertEvaluationTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _evening;

    private static readonly DateTime From = ReferenceReplay.From;
    private static readonly DateTime Until = ReferenceReplay.To.AddMinutes(5);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertEvaluation);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<AlertTickResult> TickAsync(DateTime nowUtc) =>
        new AlertEvaluation(_host.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance).TickAsync(nowUtc, Ct);

    // The demo airport and its rules (created before the evening), the S-17 device of the Visitors queue, the evening's
    // stream outputs, then one tick a minute; once for the class.
    private async Task<Guid> EveningAsync()
    {
        var admin = await _host.CreateUserAsync("it.eval.admin." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await Gate.WaitAsync(Ct);
        try
        {
            if (_evening)
                return admin;
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            (await _host.ReadAsync<int>("UPDATE alert_rule SET created_on = '2026-09-28T16:00:00Z' WHERE site_code = 'DMO' RETURNING 1")).Should().Be(1);
            (await _host.ReadAsync<int>("""
                INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                    orientation_degrees, footprint_radius_metres, footprint_source, queue_zone_name)
                SELECT gen_random_uuid(), 'S-17', 'DMO', 'StereoVision', 'Reference', 'HttpsPush', 'Canonical', 'Ntp', 'Online', l.id, 0, 0, 3, 0, 2, 'Vendor', 'A-VIS'
                FROM level l WHERE l.site_code = 'DMO' ORDER BY l.code LIMIT 1 RETURNING 1
                """)).Should().Be(1);

            var (_, outputs) = ReferenceReplay.Run();
            var store = new StreamStore(new DatabaseSettings
            {
                Host = fixture.Hostname, Port = fixture.Port, Name = await _host.DatabaseAsync(), Username = fixture.AdminUsername, Password = fixture.AdminPassword
            }, TimeProvider.System);
            await store.SaveAsync(new StreamCheckpoint("it-alerts", [.. outputs.Values.SelectMany(o => o)], [], [], []), Ct);
            (await _host.ReadAsync<long>("SELECT count(*) FROM queue_minute WHERE zone_key = 'DMO/A-VIS'")).Should().BeGreaterThan(200);

            for (var minute = From; minute <= Until; minute = minute.AddMinutes(1))
                (await TickAsync(minute.AddMinutes(1).AddSeconds(5))).Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0);
            _evening = true;
            return admin;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Task<List<(string Rule, string Zone, string Device, DateTime Raised, DateTime? Cleared)>> StoredAsync() => _host.AsCallerAsync(null, async s =>
    {
        // Alerts of the evening's evaluation (not the one the withdrawal test plants, nor the other tests' later ones).
        var alerts = await NHibernate.Linq.LinqExtensionMethods.ToListAsync(s.GetRequiredService<IUnitOfWork>().StorageProvider.Query<Alert>()
            .Where(a => a.Resolution != Ariva.Core.Domain.Enums.AlertResolution.RuleWithdrawn && a.RaisedUtc >= From && a.RaisedUtc <= Until), Ct);
        return alerts.OrderBy(a => a.RaisedUtc).ThenBy(a => a.RuleCode, StringComparer.Ordinal).ThenBy(a => a.ZoneName, StringComparer.Ordinal)
            // A clear after the evening (later ticks of other tests) is, within the evening, still open.
            .Select(a => (a.RuleCode, a.ZoneName, a.DeviceCode, a.RaisedUtc, a.ClearedUtc <= Until ? a.ClearedUtc : null)).ToList();
    });

    private static AlertRuleRequest RequestOf(AlertRuleViewModel r) =>
        new(r.SiteCode, r.Name, r.Zones, r.Metric, r.Comparator, r.Threshold, r.MinQueueLength, r.ClearThreshold, r.SustainMinutes, r.ClearAfterMinutes, r.Severity,
            r.OwnerRole, r.EscalateAfterMinutes, r.EscalateToRole, r.EscalationContact, r.NotifyByEmail, r.Enabled, r.LeadMinutes);

    [Fact]
    public async Task Evening_Should_RaiseR001At1805AndMatchTheBacktest_When_EvaluatedLiveEveryMinute()
    {
        var admin = await EveningAsync();
        var stored = await StoredAsync();

        var r001 = stored.Where(a => a.Rule == "R-001").ToList();
        r001.Should().NotBeEmpty();
        r001[0].Zone.Should().Be("A-VIS");
        r001[0].Raised.Should().Be(ReferenceReplay.WallOf(18 * 60 + 5), "R-001 at 18:05, as in the prototype");
        stored.Should().Contain(a => a.Rule == "R-003" && a.Device == "S-17" && a.Raised == ReferenceReplay.WallOf(1100) && a.Cleared == ReferenceReplay.WallOf(1111),
            "S-17 was out from 18:20 (last heard) until it was heard again at 18:31");
        (await _host.ReadAsync<string>("SELECT state || '|' || coalesce(resolution, '') || '|' || coalesce(resolved_by, '') FROM alert WHERE device_code = 'S-17'"))
            .Should().Be("Resolved|Cleared|alert-evaluation", "auto-resolved by the clear condition");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert WHERE rule_code = 'R-001' AND zone_name = 'A-VIS' AND rule_name = 'Nowcast above 15 min' AND owner_role = 'BorderShiftSupervisor' AND severity = 'Critical'"))
            .Should().Be(r001.Count, "an alert keeps what the rule said when it was raised");

        var rules = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO" }, Ct))).Data.Data
            .Where(r => string.CompareOrdinal(r.Code, "R-005") <= 0).ToList();
        rules.Should().HaveCount(5);
        foreach (var rule in rules)
        {
            var backtest = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(rule), From, Until), Ct));
            backtest.HasErrors.Should().BeFalse(string.Join(", ", backtest.ErrorMessages ?? []));
            backtest.Data.Alerts.Select(a => (rule.Code, a.ZoneName, a.DeviceCode, a.RaisedUtc, a.ClearedUtc))
                .Should().Equal(stored.Where(a => a.Rule == rule.Code), $"{rule.Code}: the backtest is the live evaluation's fold over the same minutes");
            backtest.Data.Count.Should().Be(stored.Count(a => a.Rule == rule.Code));
            backtest.Data.FirstRaisedUtc.Should().Be(stored.Where(a => a.Rule == rule.Code).Select(a => (DateTime?)a.Raised).FirstOrDefault());
        }
    }

    [Fact]
    public async Task Tick_Should_ChangeNothing_When_RunAgainForTheSameMinute()
    {
        await EveningAsync();
        var later = Until.AddMinutes(10).AddSeconds(5);
        await TickAsync(later);
        var before = await StoredAsync();
        var states = await _host.ReadAsync<string>("SELECT string_agg(rule_id || zone_name || device_code || armed || sustained || clearing || coalesce(last_minute_utc::text, ''), ',' ORDER BY rule_id, zone_name, device_code) FROM alert_rule_state");

        var again = await TickAsync(later);

        again.Raised.Should().Be(0);
        again.Cleared.Should().Be(0);
        (await StoredAsync()).Should().Equal(before, "one open alert per rule and target: a repeated minute raises nothing");
        (await _host.ReadAsync<string>("SELECT string_agg(rule_id || zone_name || device_code || armed || sustained || clearing || coalesce(last_minute_utc::text, ''), ',' ORDER BY rule_id, zone_name, device_code) FROM alert_rule_state"))
            .Should().Be(states);
    }

    [Fact]
    public async Task Tick_Should_Skip_When_AnotherEvaluatorHoldsTheLock()
    {
        await EveningAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(38, 0)", connection, transaction))
            await hold.ExecuteNonQueryAsync(Ct);

        (await TickAsync(Until.AddMinutes(2))).Ran.Should().BeFalse("one evaluator at a time across replicas");
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Rule_Should_HaveItsOpenAlertsResolved_When_ItIsWithdrawn()
    {
        var admin = await EveningAsync();
        var rules = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO", Text = "R-002" }, Ct))).Data.Data;
        var r002 = rules.Single();
        // An open alert of R-002 as an earlier evaluation would have left it (the stream stores no overflow occupancy yet).
        (await _host.ReadAsync<int>("""
            WITH a AS (
                INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, raised_utc, raised_value, state)
                SELECT gen_random_uuid(), site_code, id, code, name, 'A-VIS', metric, severity, '2026-09-28T19:00:00Z', 1, 'Raised' FROM alert_rule WHERE code = 'R-002' AND site_code = 'DMO'
                RETURNING id, rule_id)
            INSERT INTO alert_rule_state (id, rule_id, zone_name, device_code, armed, sustained, clearing, open_alert_id, rule_values, updated_on)
            SELECT gen_random_uuid(), rule_id, 'A-VIS', '', false, 0, 0, id, repeat('0', 64), now() FROM a
            ON CONFLICT (rule_id, zone_name, device_code) DO UPDATE SET armed = false, open_alert_id = excluded.open_alert_id RETURNING 1
            """)).Should().Be(1);

        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().UpdateAsync(r002.Id, RequestOf(r002) with { Enabled = false }, Ct))).HasErrors.Should().BeFalse();
        var tick = await TickAsync(Until.AddMinutes(3));

        tick.Withdrawn.Should().Be(1);
        (await _host.ReadAsync<string>("SELECT state || '|' || resolution FROM alert WHERE rule_code = 'R-002'")).Should().Be("Resolved|RuleWithdrawn");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule_state WHERE rule_id = @id", r002.Id)).Should().Be(0);
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().UpdateAsync(r002.Id, RequestOf(r002), Ct))).HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Backtest_Should_BeRefused_When_ItsRangeOrSiteIsOutOfBounds()
    {
        var admin = await EveningAsync();
        var r001 = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO", Text = "R-001" }, Ct))).Data.Data.Single();
        var elsewhere = await _host.CreateUserAsync("it.eval.elsewhere", roles: [RoleCodes.BorderShiftSupervisor]);

        foreach (var (from, to) in new[]
                 {
                     (From, From.AddHours(25)), (From, From), (From.AddHours(1), From), (DateTime.SpecifyKind(From, DateTimeKind.Unspecified), Until),
                     (_host.Clock.GetUtcNow().UtcDateTime.AddDays(-91), _host.Clock.GetUtcNow().UtcDateTime.AddDays(-90.5)),
                     (_host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-5), _host.Clock.GetUtcNow().UtcDateTime.AddMinutes(5))
                 })
            (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(r001), from, to), Ct)))
                .ErrorMessages.Should().Equal(new[] { AlertRuleErrors.BacktestRange }, $"{from:o} to {to:o}");
        (await _host.AsCallerAsync(elsewhere, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(r001), From, Until), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.UnknownSite);
        // Every site but no role that sees live queues: past queue values stay hidden (CWE-863).
        var noLiveQueues = await _host.CreateUserAsync("it.eval.nolive");
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", noLiveQueues);
        (await _host.AsCallerAsync(noLiveQueues, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(r001), From, Until), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.UnknownSite);
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(
                new AlertBacktestRequest(RequestOf(r001) with { Zones = ["NOT-A-ZONE"] }, From, Until), Ct)))
            .ErrorMessages.Should().Equal(AlertRuleErrors.UnknownZones);
        var preview = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(
            new AlertBacktestRequest(RequestOf(r001) with { Threshold = 18, ClearThreshold = 16 }, From, Until), Ct));
        preview.Data.FirstRaisedUtc.Should().BeAfter(ReferenceReplay.WallOf(18 * 60 + 5), "an unsaved variant is judged too: a higher threshold fires later");
        preview.Data.Targets.Should().Be(10);
        preview.Data.TargetsWithData.Should().Be(1, "only the Visitors queue has minutes on the reference evening");
        var untrimmed = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(
            new AlertBacktestRequest(RequestOf(r001) with { Zones = [" A-VIS ", "A-VIS"] }, From, Until), Ct));
        untrimmed.Data.Should().Match<AlertBacktestViewModel>(b => b.Targets == 1 && b.TargetsWithData == 1 && b.FirstRaisedUtc == ReferenceReplay.WallOf(18 * 60 + 5),
            "zones are judged as the saved rule keeps them: trimmed, once each");
    }

    // A rule of its own on zones the evening has no minutes for, its states created by a tick at the given time.
    private async Task<AlertRuleViewModel> RuleAsync(Guid admin, string name, string[] zones, DateTime firstTick, string metric = "QueueLength")
    {
        var created = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", name, zones, metric, "GreaterThan",
            1, null, null, 1, 1, "Warning", null, null, null, null, false), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        (await TickAsync(firstTick)).Failed.Should().Be(0);
        return created.Data;
    }

    // Queue minutes for one zone of the demo airport, as the stream would write them.
    private async Task MinutesAsync(string zone, DateTime first, params long[] lengths)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        for (var i = 0; i < lengths.Length; i++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, nowcast_minutes, throughput_per_minute, length_degraded, updated_on)
                VALUES (@zone, @minute, 12, @length, (@length + 1) / 5.0, 5, false, now())
                """, connection);
            insert.Parameters.AddWithValue("zone", "DMO/" + zone);
            insert.Parameters.AddWithValue("minute", first.AddMinutes(i));
            insert.Parameters.AddWithValue("length", lengths[i]);
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task ScreenRule_Should_FireLiveAtThePreviewsFirstMinute_When_TickedEveryMinute()
    {
        // ARV-056: the rule the alert rules screen's test creates (queue above 40 for 3 minutes, clear after 2), judged
        // live a minute at a time and by the backtest the screen previews: the same first minute, 12 minutes in.
        var admin = await EveningAsync();
        var first = ReferenceReplay.WallOf(28 * 60);
        var request = new AlertRuleRequest("DMO", "Screen preview", ["D-CRW"], "QueueLength", "GreaterThan", 40, null, null, 3, 2, "Critical",
            RoleCodes.BorderShiftSupervisor, null, null, null, false);
        var rule = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(request, Ct))).Data;
        (await TickAsync(first.AddSeconds(5))).Failed.Should().Be(0);
        await MinutesAsync("D-CRW", first, [.. Enumerable.Repeat(5L, 10), .. Enumerable.Repeat(50L, 20)]);

        for (var minute = 1; minute <= 30; minute++)
            (await TickAsync(first.AddMinutes(minute).AddSeconds(5))).Failed.Should().Be(0);

        var preview = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(
            new AlertBacktestRequest(request, first.AddMinutes(-10), first.AddMinutes(40)), Ct));
        preview.HasErrors.Should().BeFalse(string.Join(", ", preview.ErrorMessages ?? []));
        preview.Data.Should().Match<AlertBacktestViewModel>(b => b.Count == 1 && b.FirstRaisedUtc == first.AddMinutes(12));
        (await _host.ReadAsync<DateTime>("SELECT raised_utc FROM alert WHERE rule_id = @id", rule.Id)).ToUniversalTime()
            .Should().Be(first.AddMinutes(12), "the live evaluation fires at the minute the preview showed");

        // Withdrawn and its alert resolved here, so later ticks of other tests in this class find nothing of it.
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().UpdateAsync(rule.Id, request with { Enabled = false }, Ct))).HasErrors.Should().BeFalse();
        (await TickAsync(first.AddMinutes(31).AddSeconds(5))).Withdrawn.Should().Be(1);
    }

    [Fact]
    public async Task Tick_Should_RaiseClearAndRaiseAgain_When_OneTargetFlapsWithinOneTick()
    {
        var admin = await EveningAsync();
        var first = ReferenceReplay.WallOf(22 * 60);
        var rule = await RuleAsync(admin, "Flap", ["D-CRW"], first.AddSeconds(5));
        await MinutesAsync("D-CRW", first, 5, 0, 5, 0, 5, 5);

        var tick = await TickAsync(first.AddMinutes(7).AddSeconds(5));

        tick.Failed.Should().Be(0, "a resolve reaches the database before the same target's next raise");
        (await _host.ReadAsync<string>("SELECT string_agg(to_char(raised_utc AT TIME ZONE 'UTC', 'HH24:MI') || ' ' || state, ',' ORDER BY raised_utc) FROM alert WHERE rule_id = @id", rule.Id))
            .Should().Be("22:00 Resolved,22:02 Resolved,22:04 Raised");
    }

    [Fact]
    public async Task Tick_Should_WaitForTheLivePartOfAMinute_When_OnlyItsCountsAreStoredYet()
    {
        // ARV-064 rehearsal: the stream stores a minute's counts while the minute runs and its queue length and nowcast about
        // half a minute after it ends. A tick in between took the minute without a nowcast and never looked at it again,
        // so R-001 missed the visitor wave whenever the evaluator's timer fell in that gap.
        var admin = await EveningAsync();
        var first = ReferenceReplay.WallOf(24 * 60);
        var rule = await RuleAsync(admin, "Live part", ["D-CRW"], first.AddSeconds(5), metric: "Nowcast");
        await MinutesAsync("D-CRW", first, 0, 0);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using var counts = new NpgsqlCommand("""
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, updated_on)
                VALUES ('DMO/D-CRW', @minute, 12, 'Provisional', 9, 4, 0, now())
                """, connection);
            counts.Parameters.AddWithValue("minute", first.AddMinutes(2));
            await counts.ExecuteNonQueryAsync(Ct);
        }

        (await TickAsync(first.AddMinutes(3).AddSeconds(5))).Failed.Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert WHERE rule_id = @id", rule.Id)).Should().Be(0);

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using var live = new NpgsqlCommand("""
                UPDATE queue_minute SET queue_length = 40, length_degraded = false, nowcast_minutes = 8.2, throughput_per_minute = 5
                WHERE zone_key = 'DMO/D-CRW' AND minute_utc = @minute
                """, connection);
            live.Parameters.AddWithValue("minute", first.AddMinutes(2));
            (await live.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        (await TickAsync(first.AddMinutes(3).AddSeconds(40))).Failed.Should().Be(0);
        (await _host.ReadAsync<string>("SELECT to_char(raised_utc AT TIME ZONE 'UTC', 'HH24:MI') || ' ' || raised_value FROM alert WHERE rule_id = @id", rule.Id))
            .Should().Be("00:02 8.2", "the minute is judged once its nowcast is stored");
    }

    [Fact]
    public async Task Tick_Should_RecordTheBound_When_AStoredValueIsBeyondWhatAnAlertKeeps()
    {
        var admin = await EveningAsync();
        var first = ReferenceReplay.WallOf(23 * 60);
        var rule = await RuleAsync(admin, "Poison", ["D-CIT"], first.AddSeconds(5));
        await MinutesAsync("D-CIT", first, 2_000_000_000);

        var tick = await TickAsync(first.AddMinutes(2).AddSeconds(5));

        tick.Failed.Should().Be(0, "an implausible value is recorded at the bound, not a reason to stop evaluating");
        (await _host.ReadAsync<double>("SELECT raised_value FROM alert WHERE rule_id = @id", rule.Id)).Should().Be(Alert.MaxRecordedValue);
    }

    [Fact]
    public async Task Alerts_Should_BeResolved_When_TheirTargetLeavesTheRuleOrTheMetricChanges()
    {
        var admin = await EveningAsync();
        var first = ReferenceReplay.WallOf(21 * 60);
        var rule = await RuleAsync(admin, "Shrinking scope", ["D-RES", "D-VIS"], first.AddSeconds(5));
        await MinutesAsync("D-RES", first, 5, 5);
        await MinutesAsync("D-VIS", first, 5, 5);
        (await TickAsync(first.AddMinutes(2).AddSeconds(5))).Raised.Should().Be(2);

        var request = new AlertRuleRequest("DMO", "Shrinking scope", ["D-VIS"], "QueueLength", "GreaterThan", 1, null, null, 1, 1, "Warning", null, null, null, null, false);
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().UpdateAsync(rule.Id, request, Ct))).HasErrors.Should().BeFalse();
        (await TickAsync(first.AddMinutes(3).AddSeconds(5))).Withdrawn.Should().Be(1);
        (await _host.ReadAsync<string>("SELECT state || '|' || resolution FROM alert WHERE rule_id = @id AND zone_name = 'D-RES'", rule.Id)).Should().Be("Resolved|TargetWithdrawn");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule_state WHERE rule_id = @id AND zone_name = 'D-RES'", rule.Id)).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT state FROM alert WHERE rule_id = @id AND zone_name = 'D-VIS'", rule.Id)).Should().Be("Raised", "an edit keeps an open alert on a target still in scope");

        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().UpdateAsync(rule.Id, request with { Metric = "Nowcast", Threshold = 30 }, Ct)))
            .HasErrors.Should().BeFalse();
        (await TickAsync(first.AddMinutes(4).AddSeconds(5))).Withdrawn.Should().Be(1);
        (await _host.ReadAsync<string>("SELECT state || '|' || resolution FROM alert WHERE rule_id = @id AND zone_name = 'D-VIS'", rule.Id)).Should().Be("Resolved|RuleChanged",
            "an alert about queue length is not left to clear under a nowcast threshold");
    }

    [Fact]
    public async Task Target_Should_StartAtThePresent_When_ARuleIsReEnabled()
    {
        var admin = await EveningAsync();
        // The next night, away from the evening the other tests compare.
        var first = ReferenceReplay.WallOf(26 * 60);
        var rule = await RuleAsync(admin, "Re-enabled", ["D-EG"], first.AddSeconds(5));
        // An hour of minutes that ended before the rule's first tick (the minute just ended, 01:59, is its first).
        await MinutesAsync("D-EG", first.AddMinutes(-61), [.. Enumerable.Repeat(5L, 60)]);

        (await TickAsync(first.AddMinutes(1).AddSeconds(5))).Failed.Should().Be(0);

        (await _host.ReadAsync<long>("SELECT count(*) FROM alert WHERE rule_id = @id", rule.Id)).Should().Be(0,
            "the minutes before the rule's first tick are history, for the backtest; the live evaluation does not raise stale alerts");
    }

    [Theory]
    [InlineData("DELETE FROM alert", "42501")]
    [InlineData("TRUNCATE alert", "42501")]
    [InlineData("UPDATE alert SET raised_value = 1", "23514")]
    [InlineData("UPDATE alert SET raised_utc = raised_utc - interval '1 hour'", "23514")]
    [InlineData("UPDATE alert SET state = 'Raised', resolution = NULL, resolved_utc = NULL, resolved_by = NULL WHERE state = 'Resolved'", "23514")]
    [InlineData("UPDATE alert_rule SET lead_minutes = 30 WHERE metric = 'Nowcast'", "23514")]
    public async Task Tables_Should_RefuseTheRuntimeRole_When_ItRewritesOrRemovesAlerts(string sql, string state)
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
}
