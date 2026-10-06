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
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-115 against TimescaleDB with script 0039: the overflow scenario case (seed 9303's evening with the Visitors snake
/// holding 60 people, so A-VIS spills into its A-OV band from 18:03 to 18:31 and from 19:33 to 19:52) as the stream
/// stored it, the demo airport's R-002 ("Overflow band occupied", sustain and clear 3 minutes) and a rule on the band
/// itself (A-OV, sustain and clear 1 minute), evaluated live once a minute from 17:00 to 20:35. R-002 raises on A-VIS at
/// 18:05 and 19:35 and clears at 18:34 and 19:55; every overflow rule's backtest over the evening gives exactly the alerts
/// the live evaluation stored.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OverflowAlertTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _evening;

    private static readonly DateTime From = ReferenceReplay.From;
    private static readonly DateTime Until = ReferenceReplay.To.AddMinutes(5);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertOverflow);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<AlertTickResult> TickAsync(DateTime nowUtc) =>
        new AlertEvaluation(_host.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance).TickAsync(nowUtc, Ct);

    private static AlertRuleRequest RequestOf(AlertRuleViewModel r) =>
        new(r.SiteCode, r.Name, r.Zones, r.Metric, r.Comparator, r.Threshold, r.MinQueueLength, r.ClearThreshold, r.SustainMinutes, r.ClearAfterMinutes, r.Severity,
            r.OwnerRole, r.EscalateAfterMinutes, r.EscalateToRole, r.EscalationContact, r.NotifyByEmail, r.Enabled, r.LeadMinutes);

    // The demo airport, its rules (only the overflow ones left enabled) and a rule on the band, the overflow evening's
    // stream outputs, then one tick a minute; once for the class.
    private async Task<Guid> EveningAsync()
    {
        var admin = await _host.CreateUserAsync("it.overflow.admin." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await Gate.WaitAsync(Ct);
        try
        {
            if (_evening)
                return admin;
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            var band = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Visitors band in use", ["A-OV"],
                "OverflowOccupied", "IsTrue", null, null, null, 1, 1, "Warning", null, null, null, null, false), Ct));
            band.HasErrors.Should().BeFalse(string.Join(", ", band.ErrorMessages ?? []));
            (await _host.ReadAsync<int>("UPDATE alert_rule SET enabled = (metric = 'OverflowOccupied') WHERE site_code = 'DMO' RETURNING 1")).Should().Be(1);
            (await _host.ReadAsync<int>("UPDATE alert_rule SET created_on = '2026-09-28T16:00:00Z' WHERE site_code = 'DMO' RETURNING 1")).Should().Be(1);

            var (_, outputs) = ReferenceReplay.RunOverflow();
            var store = new StreamStore(new DatabaseSettings
            {
                Host = fixture.Hostname, Port = fixture.Port, Name = await _host.DatabaseAsync(), Username = fixture.AdminUsername, Password = fixture.AdminPassword
            }, TimeProvider.System);
            await store.SaveAsync(new StreamCheckpoint("it-overflow", [.. outputs.Values.SelectMany(o => o)], [], [], []), Ct);
            (await _host.ReadAsync<long>("SELECT count(*) FROM overflow_minute WHERE zone_key = 'DMO/A-VIS' AND band_name = 'A-OV' AND max_occupancy > 0")).Should().Be(49);
            (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.flow.overflow-detected.v1' AND message_key = 'DMO/A-VIS'")).Should().Be(4);

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

    private Task<List<(string Rule, string Zone, DateTime Raised, DateTime? Cleared)>> StoredAsync() => _host.AsCallerAsync(null, async s =>
    {
        var alerts = await NHibernate.Linq.LinqExtensionMethods.ToListAsync(s.GetRequiredService<IUnitOfWork>().StorageProvider.Query<Alert>()
            .Where(a => a.RaisedUtc >= From && a.RaisedUtc <= Until), Ct);
        return alerts.OrderBy(a => a.RaisedUtc).ThenBy(a => a.RuleCode, StringComparer.Ordinal).ThenBy(a => a.ZoneName, StringComparer.Ordinal)
            .Select(a => (a.RuleCode, a.ZoneName, a.RaisedUtc, a.ClearedUtc <= Until ? a.ClearedUtc : null)).ToList();
    });

    [Fact]
    public async Task Evening_Should_RaiseR002WhileTheBandIsOccupiedAndMatchTheBacktest_When_EvaluatedLiveEveryMinute()
    {
        var admin = await EveningAsync();
        var stored = await StoredAsync();
        DateTime At(int hour, int minute) => ReferenceReplay.WallOf(hour * 60 + minute);

        stored.Where(a => a.Rule == "R-002").Should().Equal(
            ("R-002", "A-VIS", At(18, 5), (DateTime?)At(18, 34)),
            ("R-002", "A-VIS", At(19, 35), (DateTime?)At(19, 55)));
        var bandRule = stored.Where(a => a.Rule != "R-002").ToList();
        bandRule.Select(a => (a.Zone, a.Raised, a.Cleared)).Should().Equal(
            ("A-OV", At(18, 3), (DateTime?)At(18, 32)),
            ("A-OV", At(19, 33), (DateTime?)At(19, 53)));
        (await _host.ReadAsync<string>("SELECT state || '|' || coalesce(resolution, '') || '|' || metric FROM alert WHERE rule_code = 'R-002' ORDER BY raised_utc LIMIT 1"))
            .Should().Be("Resolved|Cleared|OverflowOccupied", "auto-resolved by the clear condition");

        var rules = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO" }, Ct))).Data.Data
            .Where(r => r.Metric == "OverflowOccupied").ToList();
        rules.Should().HaveCount(2);
        foreach (var rule in rules)
        {
            var backtest = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(rule), From, Until), Ct));
            backtest.HasErrors.Should().BeFalse(string.Join(", ", backtest.ErrorMessages ?? []));
            backtest.Data.Alerts.Select(a => (rule.Code, a.ZoneName, a.RaisedUtc, a.ClearedUtc))
                .Should().Equal(stored.Where(a => a.Rule == rule.Code), $"{rule.Code}: the backtest is the live evaluation's fold over the same band minutes");
            backtest.Data.TargetsWithData.Should().Be(1, "only the Visitors queue has a band that reports");
        }
    }

    [Fact]
    public async Task Tick_Should_RaiseNothingMore_When_RunAgainAfterTheEvening()
    {
        await EveningAsync();
        var before = await StoredAsync();

        (await TickAsync(Until.AddMinutes(1).AddSeconds(5))).Raised.Should().Be(0);

        (await StoredAsync()).Should().Equal(before, "a minute already taken changes nothing");
    }

    // ARV-115 review (CWE-863): another site with zones and a band of the same names, every minute occupied and running
    // past DMO's last band minute. DMO's overflow series, its latest band minute and R-002's backtest read DMO's rows only,
    // and a zone that is not in DMO's published profile watches nothing.
    [Fact]
    public async Task Inputs_Should_ReadTheSitesOwnBandsOnly_When_AnotherSiteHasZonesAndBandsOfTheSameNames()
    {
        var admin = await EveningAsync();
        var stored = await StoredAsync();
        (await _host.ReadAsync<int>("""
            INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on)
            SELECT 'XXX/A-VIS', b, m, 1, 9, 9, now()
            FROM (SELECT min(minute_utc) - interval '60 minutes' AS f, max(minute_utc) + interval '30 minutes' AS t FROM overflow_minute WHERE zone_key = 'DMO/A-VIS') r,
                 generate_series(r.f, r.t, interval '1 minute') AS m, unnest(ARRAY['A-OV', 'A-VIS']) AS b
            ON CONFLICT DO NOTHING
            RETURNING 1
            """)).Should().Be(1);
        try
        {
            var dmoLatest = (await _host.ReadAsync<DateTime>("SELECT max(minute_utc) FROM overflow_minute WHERE zone_key = 'DMO/A-VIS'")).ToUniversalTime();
            var dmoMinutes = await _host.ReadAsync<long>("SELECT count(*) FROM overflow_minute WHERE zone_key = 'DMO/A-VIS' AND band_name = 'A-OV'");
            var probe = new AlertRuleValues("Cross-site probe", ["A-VIS", "A-OV", "A-NOWHERE"], AlertMetric.OverflowOccupied, AlertComparator.IsTrue, null, null, null, 3, 3,
                AlertSeverity.Warning, null, null, null, null, false, true);

            var (latest, series) = await _host.AsCallerAsync(admin, async s =>
            {
                var inputs = s.GetRequiredService<AlertInputs>();
                var latest = await inputs.LatestOverflowMinutesAsync("DMO", ["A-VIS", "A-OV", "A-NOWHERE"], Until.AddHours(2), Ct);
                var series = await inputs.ReadAsync("DMO", probe, [new AlertTarget("A-VIS", null), new AlertTarget("A-OV", null), new AlertTarget("A-NOWHERE", null)],
                    From.AddHours(-2), Until.AddHours(2), Ct);
                return (latest, series);
            });

            latest.Should().BeEquivalentTo(new Dictionary<string, DateTime> { ["A-VIS"] = dmoLatest, ["A-OV"] = dmoLatest },
                "XXX's later minutes are not DMO's, and a zone outside DMO's profile has none");
            var bySeries = series.ToDictionary(t => t.Target.ZoneName, t => t.Minutes);
            foreach (var zone in new[] { "A-VIS", "A-OV" })
            {
                bySeries[zone].Should().HaveCount((int)dmoMinutes, $"{zone} reads DMO's band minutes only");
                bySeries[zone].Count(m => m.Value == 1).Should().Be(49, "DMO's band is occupied 49 minutes; XXX's is occupied in every one");
            }

            bySeries["A-NOWHERE"].Should().BeEmpty("a zone that is not in DMO's published profile watches no band");

            var r002 = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().SearchAsync(new AlertRuleCriteria { SiteCode = "DMO" }, Ct))).Data.Data
                .Single(r => r.Code == "R-002");
            var backtest = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().BacktestAsync(new AlertBacktestRequest(RequestOf(r002), From, Until), Ct));
            backtest.HasErrors.Should().BeFalse(string.Join(", ", backtest.ErrorMessages ?? []));
            backtest.Data.Alerts.Select(a => ("R-002", a.ZoneName, a.RaisedUtc, a.ClearedUtc)).Should().Equal(stored.Where(a => a.Rule == "R-002"),
                "R-002 at DMO is unchanged by another site's bands");
        }
        finally
        {
            await _host.ReadAsync<int>("DELETE FROM overflow_minute WHERE zone_key = 'XXX/A-VIS' RETURNING 1");
        }
    }
}
