using System.Collections.Concurrent;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Reports;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Alerting;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-181b2 against TimescaleDB with scripts 0051 and 0053: the open desks below recommendation alert (F16) on the plan's
/// constructed case, rows of <c>lane_staffing</c> the staffing job could have written for the demo airport's Visitors lane
/// (DMO/A-VIS) on 2026-09-30, ticked live once a minute from 10:00 to 10:21. A rule at 2 or more desks short for 3 minutes,
/// clearing after 2, raises at 10:09 with 3 desks short and clears at 10:15, the minutes without a value (no row, a degraded
/// open count, a gap, an unknown open count, rows that fail the read-back check) neither counting toward nor breaking a run;
/// the backtest over the same range gives exactly that alert; another tick changes nothing; another site's lane of the same
/// name is never read; a read beyond the bound is refused (backtest) or skipped with one warning (live) and resumed; a stored
/// rule without an owner (from before the story, or changed in the database) is never evaluated, with one warning per rule
/// and values version in a host (ARV-187, lead decision P3, superseding the warning per tick) naming the site and the rule
/// only. Every channel answers holders of ArrivalWaveLanes.View only: the alerts API (list, get,
/// actions), the daily report (on demand and as delivered) and the weekly summary, per role and for an account holding an
/// airport-side and a border role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StaffingAlertTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly WarningCapture Warnings = new();
    private static Case _case;

    private static readonly DateTime Ten = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 9, 30);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertStaffing, configure: s => s.AddSingleton<ILoggerProvider>(Warnings));

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Case(Guid Admin, Guid Border, Guid Duty, Guid Handler, Guid Both, AlertRuleViewModel Rule, AlertRuleViewModel Changed, int Ticks);

    private static AlertRuleRequest DesksShort(string name) =>
        new("DMO", name, ["A-VIS"], "DesksBelowPlan", "GreaterOrEqual", 2, null, null, 3, 2, "Warning", RoleCodes.BorderShiftSupervisor, null, null, null, false);

    private Task<AlertTickResult> TickAsync(DateTime nowUtc, AccountsHost host = null) =>
        new AlertEvaluation((host ?? _host).Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance)
            .TickAsync(nowUtc, Ct);

    /// <summary>
    /// One interval-0 row of minute 10:mm as the engine writes it (script 0053's checks and the engine's pairings hold): 8 desks
    /// in service, a measured throughput, a queue of 40, 30 arriving; a gap means no recommendation; no arrivals with a
    /// recommendation passes the script but fails the read-back check (the engine always knows the arrivals it recommends for).
    /// </summary>
    private sealed record Planted(int Minute, int? Recommended, int? OpenNow, bool Degraded = false, string Gap = "None", bool Arrivals = true, string Site = "DMO");

    /// <summary>The constructed case of the plan (section 2), open now 4 unless stated.</summary>
    private static readonly Planted[] Rows =
    [
        new(0, 4, 4), new(1, 4, 4), new(2, 4, 4), new(3, 4, 4), new(4, 4, 4),
        new(5, 6, 4), // 2 short: held 1
        // 10:06: no row (the job missed the minute): skipped.
        new(7, 6, 4), // held 2
        new(8, 7, 4, Degraded: true), // the open count is degraded: skipped
        new(9, 7, 4), // 3 short: held 3, raised at 10:09 with 3
        new(10, null, 4, Gap: "FlightWithoutPassengers"), // a gap: skipped
        new(11, 5, null), // open now unknown: skipped
        new(12, 5, 4), // 1 short: clearing 1
        new(13, 6, 4), // 2 short again: clearing starts over
        new(14, 4, 4), // clearing 1
        new(15, 3, 5), // more open than recommended, 0: clearing 2, cleared at 10:15
        new(16, 4, 4),
        // 10:17 to 10:19: 8 short each, but without arrivals: the read-back check refuses them, so they never raise a second alert.
        new(17, 8, 0, Arrivals: false), new(18, 8, 0, Arrivals: false), new(19, 8, 0, Arrivals: false),
        // Another site's lane of the same name, 8 short every minute: never read for DMO's rule.
        .. Enumerable.Range(0, 20).Select(m => new Planted(m, 8, 0, Site: "ALX"))
    ];

    private async Task PlantAsync(IEnumerable<Planted> rows)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        foreach (var row in rows)
        {
            var minute = Ten.AddMinutes(row.Minute);
            const int desks = 8;
            await using var insert = new NpgsqlCommand("""
                INSERT INTO lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
                    wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now, open_now_degraded, desks,
                    target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc)
                VALUES (@zone, @minute, 0, 'VIS', @minute, @minutes, @arrivals, @recommended, @bound, @atLimit, NULL, @waitRecommended, @waitOpen, false, NULL, @gap,
                    'None', @queueMinute, 40, @open, @degraded, @desks, 10, 0.85, 0.5, 'Measured', @computed)
                ON CONFLICT DO NOTHING
                """, connection);
            insert.Parameters.AddWithValue("zone", row.Site + "/A-VIS");
            insert.Parameters.AddWithValue("minute", minute);
            insert.Parameters.AddWithValue("minutes", (short)(15 - (minute.Minute % 15)));
            insert.Parameters.AddWithValue("arrivals", row.Arrivals ? 30.0 : DBNull.Value);
            insert.Parameters.AddWithValue("recommended", row.Recommended is { } r ? (short)r : DBNull.Value);
            insert.Parameters.AddWithValue("bound", row.Recommended is null or 0 ? "None" : "Wait");
            insert.Parameters.AddWithValue("atLimit", row.Recommended == desks);
            insert.Parameters.AddWithValue("waitRecommended", row.Recommended is null ? DBNull.Value : 8.0);
            insert.Parameters.AddWithValue("waitOpen", row.Recommended is null || row.OpenNow is null ? DBNull.Value : 12.0);
            insert.Parameters.AddWithValue("gap", row.Gap);
            insert.Parameters.AddWithValue("queueMinute", minute.AddMinutes(-1));
            insert.Parameters.AddWithValue("open", row.OpenNow is { } o ? (short)o : DBNull.Value);
            insert.Parameters.AddWithValue("degraded", row.Degraded);
            insert.Parameters.AddWithValue("desks", (short)desks);
            insert.Parameters.AddWithValue("computed", minute.AddSeconds(45));
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }

    // The demo airport, the people, the two rules (the second changed in the database to no owner), the rows, then one tick a
    // minute from 10:00 to 10:21 (each tick after its minute has ended); once for the class.
    private async Task<Case> CaseAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_case is not null)
            {
                await _host.CreateUserAsync("it.staffing.probe." + Guid.NewGuid().ToString("N")[..8]);
                return _case;
            }

            var admin = await _host.CreateUserAsync("it.staffing.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("ALX", "Other airport"), Ct));
            async Task<Guid> Person(string name, params string[] roles)
            {
                var id = await _host.CreateUserAsync(name, roles: roles);
                await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(id, new SiteAccessRequest(false, ["DMO"]), Ct));
                return id;
            }

            var border = await Person("it.staffing.border", RoleCodes.BorderShiftSupervisor);
            var duty = await Person("it.staffing.duty", RoleCodes.TerminalDutyManager);
            var handler = await Person("it.staffing.handler", RoleCodes.HandlerStationManager);
            var both = await Person("it.staffing.both", RoleCodes.TerminalDutyManager, RoleCodes.BorderShiftSupervisor);

            var rule = await _host.AsCallerAsync(border, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(DesksShort("Desks short"), Ct));
            rule.HasErrors.Should().BeFalse(string.Join(", ", rule.ErrorMessages ?? []));
            var changed = await _host.AsCallerAsync(border, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(DesksShort("Desks short, changed"), Ct));
            changed.HasErrors.Should().BeFalse(string.Join(", ", changed.ErrorMessages ?? []));
            (await _host.ReadAsync<int>("UPDATE alert_rule SET owner_role = NULL WHERE id = @id RETURNING 1", changed.Data.Id)).Should().Be(1,
                "a rule from before the story, or changed in the database: no owner means every alert role");

            await PlantAsync(Rows);
            var ticks = 0;
            for (var minute = Ten; minute <= Ten.AddMinutes(21); minute = minute.AddMinutes(1))
            {
                (await TickAsync(minute.AddMinutes(1).AddSeconds(5))).Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0);
                ticks++;
            }

            _case = new Case(admin, border, duty, handler, both, rule.Data, changed.Data, ticks);
            return _case;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record Stored(string Zone, DateTime Raised, double Value, DateTime? Cleared, string State);

    private async Task<List<Stored>> AlertsOfAsync(Guid ruleId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT zone_name, raised_utc, raised_value, cleared_utc, state FROM alert WHERE rule_id = @id AND raised_utc < '2026-09-30T10:30:00Z' ORDER BY raised_utc", connection);
        command.Parameters.AddWithValue("id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Stored>();
        while (await reader.ReadAsync(Ct))
            rows.Add(new Stored(reader.GetString(0), reader.GetDateTime(1).ToUniversalTime(), reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToUniversalTime(), reader.GetString(4)));
        return rows;
    }

    [Fact]
    public async Task Live_Should_RaiseAt1009ClearAt1015AndEqualTheBacktest_When_TheConstructedCaseIsTickedEveryMinute()
    {
        var c = await CaseAsync();

        var stored = await AlertsOfAsync(c.Rule.Id);

        stored.Should().Equal([new Stored("A-VIS", Ten.AddMinutes(9), 3, Ten.AddMinutes(15), "Resolved")],
            "held at 10:05, 10:07 and 10:09 (10:06 missing and 10:08 degraded are skipped), cleared at 10:14 and 10:15 (the gap, the unknown open count " +
            "and 10:13's 2 short are not a clear minute), and the rows without arrivals at 10:17 to 10:19 never raise again");
        var backtest = await _host.AsCallerAsync(c.Border, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(
            new AlertBacktestRequest(DesksShort("Desks short"), Ten, Ten.AddMinutes(21)), Ct));
        backtest.HasErrors.Should().BeFalse(string.Join(", ", backtest.ErrorMessages ?? []));
        backtest.Data.Alerts.Select(a => new Stored(a.ZoneName, a.RaisedUtc, a.Value, a.ClearedUtc, a.ClearedUtc is null ? "Raised" : "Resolved"))
            .Should().Equal(stored, "the backtest is the live evaluation's fold on the same stored rows");
        backtest.Data.Should().Match<AlertBacktestViewModel>(b => b.Count == 1 && b.FirstRaisedUtc == Ten.AddMinutes(9) && b.Targets == 1 && b.TargetsWithData == 1);

        var before = await _host.ReadAsync<long>("SELECT count(*) FROM alert");
        (await TickAsync(Ten.AddMinutes(22).AddSeconds(5))).Should().Match<AlertTickResult>(t => t.Ran && t.Raised == 0 && t.Cleared == 0 && t.Failed == 0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert")).Should().Be(before, "another tick over the same minutes changes nothing");
        (await AlertsOfAsync(c.Rule.Id)).Should().Equal(stored);
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert WHERE zone_name = 'A-VIS' AND rule_id = @id", c.Rule.Id)).Should().Be(1,
            "ALX/A-VIS, 8 short every minute, is another site's lane and never read for DMO");
    }

    [Fact]
    public async Task Tick_Should_NeverEvaluateAStoredRuleWithoutABorderOwner_When_ItWasChangedInTheDatabase()
    {
        var c = await CaseAsync();
        (await AlertsOfAsync(c.Changed.Id)).Should().BeEmpty("the same case would raise at 10:09 for anyone (no owner) if it were evaluated");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule_state WHERE rule_id = @id", c.Changed.Id)).Should().Be(0, "not even its targets are kept");

        // ARV-187 (lead decision P3): one warning per rule and values version in a host process, so a host of its own here.
        await using var fresh = new AccountsHost(fixture, database: TestDatabase.AlertStaffing, configure: s => s.AddSingleton<ILoggerProvider>(Warnings));
        await fresh.CreateUserAsync("it.staffing.fresh." + Guid.NewGuid().ToString("N")[..8]);
        var before = Warnings.Of(c.Changed.Code).Count;
        (await TickAsync(Ten.AddMinutes(23).AddSeconds(5), fresh)).Failed.Should().Be(0);
        (await TickAsync(Ten.AddMinutes(24).AddSeconds(5), fresh)).Failed.Should().Be(0);

        var warnings = Warnings.Of(c.Changed.Code);
        warnings.Should().HaveCount(before + 1, "one warning for two ticks of the same values");
        warnings[^1].Should().Be($"Alert evaluation of site DMO rule {c.Changed.Code}: not evaluated, it fails the rule checks of its border metric");
        warnings.Should().OnlyContain(w => !w.Contains("A-VIS", StringComparison.Ordinal) && !w.Contains("Desks short", StringComparison.Ordinal),
            "the site, the rule code and a fixed reason only");
        Warnings.Of(c.Rule.Code).Should().BeEmpty("the border rule is evaluated");

        (await fresh.ReadAsync<int>("UPDATE alert_rule SET sustain_minutes = sustain_minutes % 5 + 1 WHERE id = @id RETURNING 1", c.Changed.Id)).Should().Be(1);
        (await TickAsync(Ten.AddMinutes(25).AddSeconds(5), fresh)).Failed.Should().Be(0);
        Warnings.Of(c.Changed.Code).Should().HaveCount(before + 2, "its values changed and it still fails the checks");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule_state WHERE rule_id = @id", c.Changed.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Reads_Should_BeRefusedOrSkippedAndResumed_When_TheyExceedTheStaffingRowBound()
    {
        var c = await CaseAsync();
        // 10:20 and 10:21, nothing short: two rows to read past the state's 10:19.
        await PlantAsync([new Planted(20, 4, 4), new Planted(21, 4, 4)]);
        const string State = "SELECT to_char(last_minute_utc AT TIME ZONE 'UTC', 'HH24:MI') FROM alert_rule_state WHERE rule_id = @id";
        await using var capped = new AccountsHost(fixture, database: TestDatabase.AlertStaffing, configure: s =>
        {
            s.AddScoped(sp => new AlertInputs(sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<Ariva.Core.Alerting.IArrivalWaveSource>()) { MaxStaffingRows = 1 });
            s.AddSingleton<ILoggerProvider>(Warnings);
        });
        var cappedBorder = await capped.CreateUserAsync("it.staffing.capped." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.BorderShiftSupervisor]);
        await capped.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", cappedBorder);
        (await _host.ReadAsync<string>(State, c.Rule.Id)).Should().Be("10:19", "the case's last computed minute");
        var skippedBefore = Warnings.Skipped(c.Rule.Code);

        (await TickAsync(Ten.AddMinutes(22).AddSeconds(5), capped)).Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0 && t.Raised == 0);

        Warnings.Skipped(c.Rule.Code).Should().Be(skippedBefore + 1, "one warning, the site and the rule code only");
        (await _host.ReadAsync<string>(State, c.Rule.Id)).Should().Be("10:19", "a skipped rule keeps its targets' states");
        var backtest = await capped.AsCallerAsync(cappedBorder, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(
            new AlertBacktestRequest(DesksShort("Probe"), Ten, Ten.AddMinutes(21)), Ct));
        backtest.ErrorMessages.Should().Equal([AlertRuleErrors.BacktestTooLarge], "refused, never cut short");

        (await TickAsync(Ten.AddMinutes(22).AddSeconds(5))).Failed.Should().Be(0);
        (await _host.ReadAsync<string>(State, c.Rule.Id)).Should().Be("10:21", "the next tick under the normal bound resumes from the same minutes");
    }

    /// <summary>Plants an alert row as an older release or a hand in the database could have left it.</summary>
    private async Task PlantAlertAsync(Guid id, AlertRuleViewModel rule, string zone, string metric, string owner, string escalateTo, bool escalated)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
                escalate_to_role, raised_utc, raised_value, state, escalated_utc, escalated_by)
            VALUES (@id, 'DMO', @rule, @code, 'Planted', @zone, NULL, @metric, 'Warning', @owner, @after, @to, '2026-09-30T11:00:00Z', 3, @state, @escalatedUtc, @escalatedBy)
            ON CONFLICT (id) DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("rule", rule.Id);
        insert.Parameters.AddWithValue("code", rule.Code);
        insert.Parameters.AddWithValue("zone", zone);
        insert.Parameters.AddWithValue("metric", metric);
        insert.Parameters.AddWithValue("owner", (object)owner ?? DBNull.Value);
        insert.Parameters.AddWithValue("after", escalateTo is null ? DBNull.Value : 10);
        insert.Parameters.AddWithValue("to", (object)escalateTo ?? DBNull.Value);
        insert.Parameters.AddWithValue("state", escalated ? "Escalated" : "Raised");
        insert.Parameters.AddWithValue("escalatedUtc", escalated ? new DateTime(2026, 9, 30, 11, 10, 0, DateTimeKind.Utc) : DBNull.Value);
        insert.Parameters.AddWithValue("escalatedBy", escalated ? "planted" : DBNull.Value);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static readonly Guid Ownerless = Guid.Parse("0199e9a0-0000-7000-8000-00000000b201");
    private static readonly Guid DutyOwned = Guid.Parse("0199e9a0-0000-7000-8000-00000000b202");
    private static readonly Guid WaitOwnerless = Guid.Parse("0199e9a0-0000-7000-8000-00000000b203");

    [Fact]
    public async Task Channels_Should_AnswerHoldersOfArrivalWaveLanesOnly_When_AnyRoleReadsOrActs()
    {
        var c = await CaseAsync();
        var raised = (await _host.ReadAsync<Guid>("SELECT id FROM alert WHERE rule_id = @id ORDER BY raised_utc LIMIT 1", c.Rule.Id));
        // Without an owner (every alert role's, as before the story), owned and escalated airport-side, and a wait alert without an
        // owner for comparison (other metrics keep their rule). Planted on the evaluated rule's code: a tick withdraws the open
        // alerts of the rule that is not evaluated (ARV-187, lead decision P4), and the class's ticks run in any order.
        await PlantAlertAsync(Ownerless, c.Rule, "A-CIT", "DesksBelowPlan", null, null, false);
        await PlantAlertAsync(DutyOwned, c.Rule, "A-RES", "DesksBelowPlan", RoleCodes.TerminalDutyManager, RoleCodes.TerminalDutyManager, true);
        var wait = (await _host.AsCallerAsync(c.Admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO", Text = "R-001" }, Ct)))
            .Data.Data.Single();
        await PlantAlertAsync(WaitOwnerless, wait, "A-EG", "Nowcast", null, null, false);
        Guid[] border = [raised, Ownerless, DutyOwned];

        async Task<List<Guid>> Listed(Guid caller) =>
            [.. (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAlerts>().SearchAsync(new AlertCriteria { SiteCode = "DMO", PageSize = 500 }, Ct)))
                .Data.Data.Select(a => a.Id)];

        foreach (var (caller, who) in new[] { (c.Duty, "terminal duty manager"), (c.Handler, "handler station manager") })
        {
            var listed = await Listed(caller);
            listed.Should().Contain(WaitOwnerless, who + ": other metrics keep their rule").And.NotContain(border, who);
            foreach (var id in border)
            {
                (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAlerts>().GetAsync(id, Ct))).ErrorMessages.Should().Equal([TopologyErrors.NotFound], who);
                (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAlerts>().AcknowledgeAsync(id, new AlertActionRequest("Mine"), Ct))).ErrorMessages
                    .Should().Equal([TopologyErrors.NotFound], who);
                (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAlerts>().ResolveAsync(id, new AlertActionRequest("Closed"), Ct))).ErrorMessages
                    .Should().Equal([TopologyErrors.NotFound], who);
            }

            (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAlerts>().SearchAsync(new AlertCriteria { SiteCode = "DMO", RuleCode = c.Rule.Code }, Ct)))
                .Data.TotalCount.Should().Be(0, who + ": not even counted");
        }

        (await Listed(c.Border)).Should().Contain([raised, Ownerless, WaitOwnerless]).And.NotContain(DutyOwned, "the supervisor is neither its owner nor its escalation role");
        (await Listed(c.Admin)).Should().Contain([raised, Ownerless, DutyOwned, WaitOwnerless]);
        (await Listed(c.Both)).Should().Contain([raised, Ownerless, DutyOwned, WaitOwnerless], "an account holding a border role besides");
        (await _host.AsCallerAsync(c.Border, s => s.GetRequiredService<ISvcAlerts>().AcknowledgeAsync(Ownerless, new AlertActionRequest("On it"), Ct)))
            .Data.State.Should().Be("Acknowledged");
        (await _host.ReadAsync<string>("SELECT state FROM alert WHERE id = @id", DutyOwned)).Should().Be("Escalated", "nobody airport-side acted on it");

        // The daily report on demand (its CSV is built from it) and as delivered (the reader with the recipient's roles).
        async Task<List<string>> Daily(Guid caller) =>
            [.. (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcReports>().DailyAsync("DMO", Day, Ct))).Data.Alerts.Select(a => a.RuleCode)];
        var codes = new[] { c.Rule.Code };
        (await Daily(c.Duty)).Should().Contain(wait.Code).And.NotContain(codes);
        (await Daily(c.Border)).Should().Contain(codes);
        var delivered = await _host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("DMO", Day, [RoleCodes.TerminalDutyManager], Ten.AddDays(1), Ct));
        delivered.Alerts.Select(a => a.RuleCode).Should().Contain(wait.Code).And.NotContain(codes);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("DMO", Day, [RoleCodes.HandlerStationManager, RoleCodes.TerminalDutyManager],
            Ten.AddDays(1), Ct))).Alerts.Select(a => a.RuleCode).Should().NotContain(codes);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("DMO", Day, [RoleCodes.SystemAdministrator], Ten.AddDays(1), Ct)))
            .Alerts.Select(a => a.RuleCode).Should().Contain(codes);

        // The weekly summary (no endpoint yet): the week of 2026-09-28.
        async Task<List<string>> Weekly(Guid caller)
        {
            var summary = await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcWeeklySummary>().GetAsync("DMO", new DateOnly(2026, 9, 28), Ct));
            summary.HasErrors.Should().BeFalse(string.Join(", ", summary.ErrorMessages ?? []));
            return [.. summary.Data.Alerts.ByRule.Select(r => r.RuleCode)];
        }

        (await Weekly(c.Duty)).Should().Contain(wait.Code).And.NotContain(codes);
        (await Weekly(c.Border)).Should().Contain(codes);
        (await Weekly(c.Both)).Should().Contain(codes);
    }

    /// <summary>Collects the warnings the alert evaluation logs (ARV-181b2: a rule not evaluated or skipped names the site and the rule code only).</summary>
    private sealed class WarningCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _queue = new();

        public IReadOnlyList<string> Of(string ruleCode) => [.. _queue.Where(w => w.Contains($"rule {ruleCode}: not evaluated", StringComparison.Ordinal))];

        public int Skipped(string ruleCode) => _queue.Count(w => w.Contains($"rule {ruleCode}: its stored inputs exceed", StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(WarningCapture capture, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Warning && category.EndsWith("AlertRuleTick", StringComparison.Ordinal))
                    capture._queue.Enqueue(formatter(state, exception));
            }
        }
    }
}
