using System.Collections.Concurrent;
using Ariva.Core;
using Ariva.Core.Alerting;
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
/// ARV-187 against TimescaleDB (owner decision 2026-10-16): the predicted nowcast is border data. A lane queue is stepped with
/// its share of the arrival wave (here a fixed wave of 12 people a minute on the demo airport's Visitors and Citizens lanes,
/// DMO/A-VIS and DMO/A-CIT, in place of the AODB and AMAN projection), a queue of 30 served at 5 a minute, so the projected
/// nowcast 30 minutes ahead is (30 + 30 x 7 + 1) / 5 = 48.2 minutes. A rule owned by the border shift supervisor raises
/// live with its predicted minute, and its backtest over the same minutes gives the same alert. A rule changed in the
/// database to an airport-side or empty owner, or an airport-side escalation role, is not evaluated: its open alert is
/// withdrawn, its states dropped, and one warning names it per values version in the host (lead decisions P3 and P4).
/// Every channel answers holders of ArrivalWaveLanes.View only: the alerts API (list, count, get, actions), the daily report
/// (on demand and as delivered) and the weekly summary, per role and for an account holding an airport-side and a border role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PredictedAlertTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly WarningCapture Warnings = new();
    private static Case _case;

    private static readonly DateTime Two = new(2026, 9, 30, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 9, 30);

    /// <summary>The projected nowcast of every planted minute (see the class summary), and the minutes ahead it is for.</summary>
    private const double Peak = 48.2;

    private const int Lead = 30;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertPredicted, configure: s =>
    {
        s.AddScoped<IArrivalWaveSource, FixedWave>();
        s.AddSingleton<ILoggerProvider>(Warnings);
    });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Case(Guid Admin, Guid Border, Guid Duty, Guid Handler, Guid Both, AlertRuleViewModel Rule);

    private static AlertRuleRequest Predicted(string name, string zone = "A-VIS", string owner = RoleCodes.BorderShiftSupervisor) =>
        new("DMO", name, [zone], "PredictedNowcast", "GreaterThan", 20, null, null, 2, 2, "Warning", owner, null, null, null, false, LeadMinutes: Lead);

    private static ISvcAlertRules Rules(IServiceProvider s) => s.GetRequiredService<ISvcAlertRules>();

    private Task<AlertTickResult> TickAsync(DateTime nowUtc) =>
        new AlertEvaluation(_host.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance).TickAsync(nowUtc, Ct);

    /// <summary>Queue minutes of a zone as the stream stores them: 30 queuing, 5 served a minute, the length not degraded.</summary>
    private async Task PlantMinutesAsync(string zone, DateTime first, int count)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        for (var i = 0; i < count; i++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, nowcast_minutes, throughput_per_minute, length_degraded, updated_on)
                VALUES (@zone, @minute, 12, 30, 6.2, 5, false, now())
                ON CONFLICT DO NOTHING
                """, connection);
            insert.Parameters.AddWithValue("zone", "DMO/" + zone);
            insert.Parameters.AddWithValue("minute", first.AddMinutes(i));
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }

    // The demo airport, the people and the supervisor's rule on A-VIS, ticked live at 14:01, 14:02 and 14:03; once for the class.
    private async Task<Case> CaseAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_case is not null)
            {
                await _host.CreateUserAsync("it.predicted.probe." + Guid.NewGuid().ToString("N")[..8]);
                return _case;
            }

            var admin = await _host.CreateUserAsync("it.predicted.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            async Task<Guid> Person(string name, params string[] roles)
            {
                var id = await _host.CreateUserAsync(name, roles: roles);
                await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(id, new SiteAccessRequest(false, ["DMO"]), Ct));
                return id;
            }

            var border = await Person("it.predicted.border", RoleCodes.BorderShiftSupervisor);
            var duty = await Person("it.predicted.duty", RoleCodes.TerminalDutyManager);
            var handler = await Person("it.predicted.handler", RoleCodes.HandlerStationManager);
            var both = await Person("it.predicted.both", RoleCodes.TerminalDutyManager, RoleCodes.BorderShiftSupervisor);

            var rule = await _host.AsCallerAsync(border, s => Rules(s).CreateAsync(Predicted("Visitors wave ahead"), Ct));
            rule.HasErrors.Should().BeFalse(string.Join(", ", rule.ErrorMessages ?? []));
            await PlantMinutesAsync("A-VIS", Two, 4);
            foreach (var minute in new[] { 0, 1, 2 })
                (await TickAsync(Two.AddMinutes(minute + 1).AddSeconds(5))).Should().Match<AlertTickResult>(t => t.Ran && t.Failed == 0);

            _case = new Case(admin, border, duty, handler, both, rule.Data);
            return _case;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record Stored(string Zone, DateTime Raised, double Value, DateTime? PredictedFor, string State, string Resolution);

    private async Task<List<Stored>> AlertsOfAsync(Guid ruleId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT zone_name, raised_utc, raised_value, predicted_for_utc, state, resolution FROM alert WHERE rule_id = @id AND raised_utc >= '2026-09-30T12:00:00Z' ORDER BY raised_utc",
            connection);
        command.Parameters.AddWithValue("id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Stored>();
        while (await reader.ReadAsync(Ct))
            rows.Add(new Stored(reader.GetString(0), reader.GetDateTime(1).ToUniversalTime(), reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToUniversalTime(), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    [Fact]
    public async Task Live_Should_RaiseAPredictedBreachAndEqualTheBacktest_When_TheRuleBelongsToTheSupervisor()
    {
        var c = await CaseAsync();

        var stored = await AlertsOfAsync(c.Rule.Id);

        stored.Should().Equal([new Stored("A-VIS", Two.AddMinutes(1), Peak, Two.AddMinutes(1 + Lead), "Raised", null)],
            "held at 14:00 and 14:01 (sustain 2), the peak 30 minutes ahead, and 14:02 keeps it open");
        (await _host.ReadAsync<string>("SELECT owner_role FROM alert WHERE rule_id = @id AND zone_name = 'A-VIS'", c.Rule.Id)).Should().Be(RoleCodes.BorderShiftSupervisor);
        var backtest = await _host.AsCallerAsync(c.Border, s => Rules(s).BacktestAsync(new AlertBacktestRequest(Predicted("Visitors wave ahead"), Two, Two.AddMinutes(2)), Ct));
        backtest.HasErrors.Should().BeFalse(string.Join(", ", backtest.ErrorMessages ?? []));
        backtest.Data.Alerts.Select(a => new Stored(a.ZoneName, a.RaisedUtc, Math.Round(a.Value, 6), a.PredictedForUtc, a.ClearedUtc is null ? "Raised" : "Resolved", null))
            .Should().Equal(stored.Select(s => s with { Value = Math.Round(s.Value, 6) }), "the backtest is the live evaluation's fold on the same minutes");
        (await _host.AsCallerAsync(c.Admin, s => Rules(s).BacktestAsync(new AlertBacktestRequest(Predicted("Admin probe"), Two, Two.AddMinutes(2)), Ct)))
            .Data.Count.Should().Be(1);

        foreach (var (caller, who) in new[] { (c.Duty, "terminal duty manager"), (c.Handler, "handler station manager") })
            (await _host.AsCallerAsync(caller, s => Rules(s).BacktestAsync(new AlertBacktestRequest(Predicted("Probe"), Two, Two.AddMinutes(2)), Ct))).ErrorMessages
                .Should().Equal([AlertRuleErrors.BorderOnly], who + ": the lane split could be solved from the peaks");
        Warnings.Of(c.Rule.Code).Should().BeEmpty("the supervisor's rule is evaluated");
    }

    public static TheoryData<int, string> LegacyRules => new()
    {
        { 15, "owner TerminalDutyManager" },
        { 16, "no owner" },
        { 17, "escalated to TerminalDutyManager" }
    };

    /// <summary>
    /// ARV-187: a predicted nowcast rule from before the story with an airport-side or empty owner, or an airport-side
    /// escalation role (made here by changing a supervisor's rule in the database), is never evaluated: its open alert is
    /// withdrawn with the rule (P4), its states dropped, nothing new is raised although every minute is above the threshold,
    /// and one warning names it per values version in the host (P3), again when its values change.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyRules))]
    public async Task Tick_Should_WithdrawAndWarnOnce_When_APredictedRuleNoLongerBelongsToTheBorder(int hour, string legacy)
    {
        var c = await CaseAsync();
        var start = new DateTime(2026, 9, 30, hour, 0, 0, DateTimeKind.Utc);
        var rule = (await _host.AsCallerAsync(c.Border, s => Rules(s).CreateAsync(Predicted("Citizens wave ahead, " + legacy, "A-CIT"), Ct))).Data;
        await PlantMinutesAsync("A-CIT", start, 8);
        (await TickAsync(start.AddMinutes(1).AddSeconds(5))).Failed.Should().Be(0);
        (await TickAsync(start.AddMinutes(2).AddSeconds(5))).Failed.Should().Be(0);
        (await AlertsOfAsync(rule.Id)).Should().ContainSingle().Which.State.Should().Be("Raised", "evaluated while it belongs to the supervisor");

        var changed = legacy switch
        {
            "owner TerminalDutyManager" => await _host.ReadAsync<int>("UPDATE alert_rule SET owner_role = 'TerminalDutyManager' WHERE id = @id RETURNING 1", rule.Id),
            "no owner" => await _host.ReadAsync<int>("UPDATE alert_rule SET owner_role = NULL WHERE id = @id RETURNING 1", rule.Id),
            _ => await _host.ReadAsync<int>(
                "UPDATE alert_rule SET escalate_after_minutes = 10, escalate_to_role = 'TerminalDutyManager' WHERE id = @id RETURNING 1", rule.Id)
        };
        changed.Should().Be(1);
        var before = Warnings.Of(rule.Code).Count;

        var withdrawing = await TickAsync(start.AddMinutes(3).AddSeconds(5));
        withdrawing.Failed.Should().Be(0);
        withdrawing.Withdrawn.Should().BeGreaterThanOrEqualTo(1);
        (await TickAsync(start.AddMinutes(4).AddSeconds(5))).Failed.Should().Be(0);

        (await AlertsOfAsync(rule.Id)).Should().Equal([new Stored("A-CIT", start.AddMinutes(1), Peak, start.AddMinutes(1 + Lead), "Resolved", "RuleWithdrawn")],
            "the open alert is withdrawn with the rule and nothing is raised again");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule_state WHERE rule_id = @id", rule.Id)).Should().Be(0, "its states are dropped");
        var warnings = Warnings.Of(rule.Code);
        warnings.Should().HaveCount(before + 1, "one warning for two ticks of the same values");
        warnings[^1].Should().Be($"Alert evaluation of site DMO rule {rule.Code}: not evaluated, it fails the rule checks of its border metric");

        (await _host.ReadAsync<int>("UPDATE alert_rule SET threshold = 21 WHERE id = @id RETURNING 1", rule.Id)).Should().Be(1);
        (await TickAsync(start.AddMinutes(5).AddSeconds(5))).Failed.Should().Be(0);
        (await TickAsync(start.AddMinutes(6).AddSeconds(5))).Failed.Should().Be(0);
        Warnings.Of(rule.Code).Should().HaveCount(before + 2, "its values changed and it still fails the checks");
        (await AlertsOfAsync(rule.Id)).Should().ContainSingle("still not evaluated");

        (await _host.AsCallerAsync(c.Duty, s => Rules(s).GetAsync(rule.Id, Ct))).ErrorMessages.Should().Equal([TopologyErrors.NotFound],
            "even owned by the duty manager, a predicted rule is border data");
    }

    /// <summary>Plants an alert row as an older release or a hand in the database could have left it.</summary>
    private async Task PlantAlertAsync(Guid id, AlertRuleViewModel rule, string zone, string metric, string owner, string escalateTo, bool escalated)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
                escalate_to_role, raised_utc, raised_value, predicted_for_utc, state, escalated_utc, escalated_by)
            VALUES (@id, 'DMO', @rule, @code, 'Planted', @zone, NULL, @metric, 'Warning', @owner, @after, @to, '2026-09-30T11:00:00Z', 48.2, @predictedFor, @state,
                @escalatedUtc, @escalatedBy)
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
        insert.Parameters.AddWithValue("predictedFor", metric == "PredictedNowcast" ? new DateTime(2026, 9, 30, 11, 30, 0, DateTimeKind.Utc) : DBNull.Value);
        insert.Parameters.AddWithValue("state", escalated ? "Escalated" : "Raised");
        insert.Parameters.AddWithValue("escalatedUtc", escalated ? new DateTime(2026, 9, 30, 11, 10, 0, DateTimeKind.Utc) : DBNull.Value);
        insert.Parameters.AddWithValue("escalatedBy", escalated ? "planted" : DBNull.Value);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static readonly Guid Ownerless = Guid.Parse("0199e9a0-0000-7000-8000-00000000c701");
    private static readonly Guid DutyOwned = Guid.Parse("0199e9a0-0000-7000-8000-00000000c702");
    private static readonly Guid WaitOwnerless = Guid.Parse("0199e9a0-0000-7000-8000-00000000c703");

    [Fact]
    public async Task Channels_Should_AnswerHoldersOfArrivalWaveLanesOnly_When_AnyRoleReadsOrActs()
    {
        var c = await CaseAsync();
        var raised = await _host.ReadAsync<Guid>("SELECT id FROM alert WHERE rule_id = @id AND zone_name = 'A-VIS' ORDER BY raised_utc LIMIT 1", c.Rule.Id);
        // Planted on the evaluated rule's code (a tick withdraws the open alerts of a rule that is not evaluated): without an
        // owner (every alert role's, as before the story), owned and escalated airport-side, and a wait alert without an owner
        // for comparison (other metrics keep their rule).
        await PlantAlertAsync(Ownerless, c.Rule, "A-RES", "PredictedNowcast", null, null, false);
        await PlantAlertAsync(DutyOwned, c.Rule, "A-CRW", "PredictedNowcast", RoleCodes.TerminalDutyManager, RoleCodes.TerminalDutyManager, true);
        var wait = (await _host.AsCallerAsync(c.Admin, s => Rules(s).SearchAsync(new AlertRuleCriteria { SiteCode = "DMO", Text = "R-001" }, Ct))).Data.Data.Single();
        await PlantAlertAsync(WaitOwnerless, wait, "A-EG", "Nowcast", null, null, false);
        Guid[] border = [raised, Ownerless, DutyOwned];

        async Task<List<AlertViewModel>> Listed(Guid caller) =>
            [.. (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAlerts>().SearchAsync(new AlertCriteria { SiteCode = "DMO", PageSize = 500 }, Ct))).Data.Data];

        foreach (var (caller, who) in new[] { (c.Duty, "terminal duty manager"), (c.Handler, "handler station manager") })
        {
            var listed = await Listed(caller);
            listed.Select(a => a.Id).Should().Contain(WaitOwnerless, who + ": other metrics keep their rule").And.NotContain(border, who);
            listed.Should().NotContain(a => a.Metric == "PredictedNowcast" || a.PredictedForUtc != null, who + ": no predicted value or minute");
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

        (await Listed(c.Border)).Select(a => a.Id).Should().Contain([raised, Ownerless, WaitOwnerless])
            .And.NotContain(DutyOwned, "the supervisor is neither its owner nor its escalation role");
        (await Listed(c.Admin)).Select(a => a.Id).Should().Contain([raised, Ownerless, DutyOwned, WaitOwnerless]);
        (await Listed(c.Both)).Select(a => a.Id).Should().Contain([raised, Ownerless, DutyOwned, WaitOwnerless], "an account holding a border role besides");
        var seen = (await _host.AsCallerAsync(c.Border, s => s.GetRequiredService<ISvcAlerts>().GetAsync(raised, Ct))).Data;
        seen.Should().Match<AlertViewModel>(a => a.RaisedValue == Peak && a.PredictedForUtc == Two.AddMinutes(1 + Lead), "the border sees the predicted value and minute");
        (await _host.AsCallerAsync(c.Border, s => s.GetRequiredService<ISvcAlerts>().AcknowledgeAsync(Ownerless, new AlertActionRequest("On it"), Ct)))
            .Data.State.Should().Be("Acknowledged");
        (await _host.ReadAsync<string>("SELECT state FROM alert WHERE id = @id", DutyOwned)).Should().Be("Escalated", "nobody airport-side acted on it");

        // The daily report on demand (its CSV is built from it) and as delivered (the reader with the recipient's roles).
        async Task<List<string>> Daily(Guid caller) =>
            [.. (await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcReports>().DailyAsync("DMO", Day, Ct))).Data.Alerts.Select(a => a.RuleCode)];
        (await Daily(c.Duty)).Should().Contain(wait.Code).And.NotContain(c.Rule.Code);
        (await Daily(c.Handler)).Should().NotContain(c.Rule.Code);
        (await Daily(c.Border)).Should().Contain(c.Rule.Code);
        var delivered = await _host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("DMO", Day, [RoleCodes.TerminalDutyManager], Two.AddDays(1), Ct));
        delivered.Data.Alerts.Select(a => a.RuleCode).Should().Contain(wait.Code).And.NotContain(c.Rule.Code);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("DMO", Day, [RoleCodes.HandlerStationManager, RoleCodes.TerminalDutyManager],
            Two.AddDays(1), Ct))).Data.Alerts.Select(a => a.RuleCode).Should().NotContain(c.Rule.Code);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("DMO", Day, [RoleCodes.SystemAdministrator], Two.AddDays(1), Ct)))
            .Data.Alerts.Select(a => a.RuleCode).Should().Contain(c.Rule.Code);

        // The weekly summary (no endpoint yet): the week of 2026-09-28.
        async Task<List<string>> Weekly(Guid caller)
        {
            var summary = await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcWeeklySummary>().GetAsync("DMO", new DateOnly(2026, 9, 28), Ct));
            summary.HasErrors.Should().BeFalse(string.Join(", ", summary.ErrorMessages ?? []));
            return [.. summary.Data.Alerts.ByRule.Select(r => r.RuleCode)];
        }

        (await Weekly(c.Duty)).Should().Contain(wait.Code).And.NotContain(c.Rule.Code);
        (await Weekly(c.Border)).Should().Contain(c.Rule.Code);
        (await Weekly(c.Both)).Should().Contain(c.Rule.Code);
    }

    /// <summary>A fixed arrival wave of 12 people a minute on A-VIS and A-CIT of DMO; no projection elsewhere.</summary>
    private sealed class FixedWave : IArrivalWaveSource
    {
        public Task<IReadOnlyDictionary<DateTime, double>> ArrivalsAsync(string siteCode, string zoneName, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            if (siteCode != "DMO" || zoneName is not ("A-VIS" or "A-CIT"))
                return Task.FromResult<IReadOnlyDictionary<DateTime, double>>(null);
            var first = AlertInputs.Minute(fromUtc).AddMinutes(1);
            var minutes = new Dictionary<DateTime, double>();
            for (var m = first; m <= toUtc; m = m.AddMinutes(1))
                minutes[m] = 12;
            return Task.FromResult<IReadOnlyDictionary<DateTime, double>>(minutes);
        }
    }

    /// <summary>Collects the warnings the alert evaluation logs (a rule not evaluated names the site and the rule code only).</summary>
    private sealed class WarningCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _queue = new();

        public IReadOnlyList<string> Of(string ruleCode) => [.. _queue.Where(w => w.Contains($"rule {ruleCode}: not evaluated", StringComparison.Ordinal))];

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
