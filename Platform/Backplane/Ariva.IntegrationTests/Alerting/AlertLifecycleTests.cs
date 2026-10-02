using System.Collections.Concurrent;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Alerting;
using Ariva.Infra.Live;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-039 against TimescaleDB with script 0022: alerts raised by the evaluation are seen and acted on only by the
/// roles responsible for them in the caller's sites; acknowledging, escalating and resolving move them forward only
/// (409 otherwise, also when two people act at once), are audited and announced after commit; an alert left
/// unacknowledged escalates by itself after the rule's minutes; and the runtime role cannot move one back.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AlertLifecycleTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (Guid Admin, Guid Border, Guid Terminal, Guid Handler, Guid Elsewhere)? _people;
    private static readonly ConcurrentQueue<AlertNotice> Announced = new();

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertLifecycle,
        configure: services => services.Replace(ServiceDescriptor.Singleton<IAlertNotices>(new Recording())));

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recording : IAlertNotices
    {
        public Task PublishAsync(IReadOnlyCollection<AlertNotice> notices, CancellationToken ct)
        {
            foreach (var n in notices)
                Announced.Enqueue(n);
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(Func<AlertNotice, Task> handler, CancellationToken ct) => Task.CompletedTask;
    }

    private Task<AlertTickResult> TickAsync(DateTime nowUtc) =>
        new AlertEvaluation(_host.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance).TickAsync(nowUtc, Ct);

    private static ISvcAlerts Alerts(IServiceProvider s) => s.GetRequiredService<ISvcAlerts>();

    private async Task<(Guid Admin, Guid Border, Guid Terminal, Guid Handler, Guid Elsewhere)> PeopleAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_people is { } known)
            {
                await _host.CreateUserAsync("it.life.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var admin = await _host.CreateUserAsync("it.life.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("ALX", "Other airport"), Ct));
            async Task<Guid> Person(string name, string role, string site)
            {
                var id = await _host.CreateUserAsync(name, roles: [role]);
                await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(id, new SiteAccessRequest(false, [site]), Ct));
                return id;
            }

            _people = (admin, await Person("it.life.border", RoleCodes.BorderShiftSupervisor, "DMO"), await Person("it.life.terminal", RoleCodes.TerminalDutyManager, "DMO"),
                await Person("it.life.handler", RoleCodes.HandlerStationManager, "DMO"), await Person("it.life.elsewhere", RoleCodes.BorderShiftSupervisor, "ALX"));
            return _people.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    // A rule of its own on one zone, its state created by a tick, then two minutes over its threshold: one alert, raised at `first`.
    private async Task<AlertViewModel> RaiseAsync(Guid admin, string zone, DateTime first, string owner = RoleCodes.BorderShiftSupervisor, int? after = 10)
    {
        var rule = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Life " + zone, [zone], "QueueLength",
            "GreaterThan", 1, null, null, 1, 1, "Warning", owner, after, after is null ? null : RoleCodes.TerminalDutyManager, null, false), Ct));
        rule.HasErrors.Should().BeFalse(string.Join(", ", rule.ErrorMessages ?? []));
        (await TickAsync(first.AddSeconds(5))).Failed.Should().Be(0);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            for (var i = 0; i < 2; i++)
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, length_degraded, updated_on) VALUES (@zone, @minute, 12, 5, false, now())
                    """, connection);
                insert.Parameters.AddWithValue("zone", "DMO/" + zone);
                insert.Parameters.AddWithValue("minute", first.AddMinutes(i - 1));
                await insert.ExecuteNonQueryAsync(Ct);
            }
        }

        (await TickAsync(first.AddMinutes(1).AddSeconds(5))).Raised.Should().Be(1);
        var page = await _host.AsCallerAsync(admin, s => Alerts(s).SearchAsync(new AlertCriteria { SiteCode = "DMO", ZoneName = zone, Open = true }, Ct));
        return page.Data.Data.Single();
    }

    [Fact]
    public async Task Alert_Should_BeAcknowledgedEscalatedAndResolvedOnlyByTheResponsible_When_ActedOn()
    {
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "D-CRW", ReferenceReplay.WallOf(21 * 60));
        alert.Should().Match<AlertViewModel>(a => a.State == "Raised" && a.OwnerRole == RoleCodes.BorderShiftSupervisor && a.EscalationDueUtc == a.RaisedUtc.AddMinutes(10));
        Announced.Should().Contain(n => n.AlertId == alert.Id && n.State == "Raised", "the evaluation announces a raise after commit");

        // Not responsible, or another site: the alert does not exist for them.
        foreach (var outsider in new[] { p.Terminal, p.Handler, p.Elsewhere })
        {
            (await _host.AsCallerAsync(outsider, s => Alerts(s).GetAsync(alert.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
            (await _host.AsCallerAsync(outsider, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest(null), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
            (await _host.AsCallerAsync(outsider, s => Alerts(s).SearchAsync(new AlertCriteria { ZoneName = "D-CRW" }, Ct))).Data.TotalCount.Should().Be(0);
        }

        var acknowledged = await _host.AsCallerAsync(p.Border, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest("  On it  "), Ct));
        acknowledged.Data.Should().Match<AlertViewModel>(a => a.State == "Acknowledged" && a.AcknowledgedBy == "it-admin" && a.AcknowledgedNote == "On it" && a.EscalationDueUtc == null);
        (await _host.AsCallerAsync(p.Border, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest(null), Ct))).ErrorMessages.Should().Equal(AlertErrors.InvalidTransition);
        (await _host.AsCallerAsync(p.Border, s => Alerts(s).EscalateAsync(alert.Id, new AlertActionRequest("Need the duty manager"), Ct))).Data.State.Should().Be("Escalated");

        var escalated = await _host.AsCallerAsync(p.Terminal, s => Alerts(s).GetAsync(alert.Id, Ct));
        escalated.Data.State.Should().Be("Escalated", "the escalation role sees it once escalated");
        (await _host.AsCallerAsync(p.Handler, s => Alerts(s).GetAsync(alert.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(p.Terminal, s => Alerts(s).ResolveAsync(alert.Id, new AlertActionRequest(" "), Ct))).ErrorMessages.Should().Equal(AlertErrors.InvalidNote);
        (await _host.AsCallerAsync(p.Terminal, s => Alerts(s).ResolveAsync(alert.Id, new AlertActionRequest("Bell\u0007"), Ct))).ErrorMessages.Should().Equal(AlertErrors.InvalidNote);
        var resolved = await _host.AsCallerAsync(p.Terminal, s => Alerts(s).ResolveAsync(alert.Id, new AlertActionRequest("Desks 9 to 12 opened"), Ct));
        resolved.Data.Should().Match<AlertViewModel>(a => a.State == "Resolved" && a.Resolution == "Manual" && a.ResolutionNote == "Desks 9 to 12 opened");
        (await _host.AsCallerAsync(p.Border, s => Alerts(s).EscalateAsync(alert.Id, new AlertActionRequest(null), Ct))).ErrorMessages.Should().Equal(AlertErrors.InvalidTransition);

        (await _host.ReadAsync<string>("SELECT string_agg(action, ',' ORDER BY occurred_on, action) FROM audit_entry WHERE target_id = @id", alert.Id))
            .Should().Be("Alert.Acknowledged,Alert.Escalated,Alert.Resolved");
        Announced.Where(n => n.AlertId == alert.Id).Select(n => n.State).Should().ContainInOrder("Raised", "Acknowledged", "Escalated", "Resolved");

        // The queue stays high: the target stays disarmed after a manual resolve and raises nothing new until it clears.
        (await TickAsync(ReferenceReplay.WallOf(21 * 60 + 2).AddSeconds(5))).Should().Match<AlertTickResult>(t => t.Failed == 0 && t.Raised == 0);
    }

    [Fact]
    public async Task Alert_Should_EscalateByItself_When_LeftUnacknowledgedForTheRulesMinutes()
    {
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "D-CIT", ReferenceReplay.WallOf(22 * 60));

        (await TickAsync(alert.RaisedUtc.AddMinutes(9).AddSeconds(5))).Escalated.Should().Be(0);
        (await TickAsync(alert.RaisedUtc.AddMinutes(10).AddSeconds(5))).Escalated.Should().Be(1);

        var seen = await _host.AsCallerAsync(p.Terminal, s => Alerts(s).GetAsync(alert.Id, Ct));
        seen.Data.Should().Match<AlertViewModel>(a => a.State == "Escalated" && a.EscalatedBy == "alert-evaluation" && a.EscalatedUtc == alert.RaisedUtc.AddMinutes(10).AddSeconds(5));
        using (var summary = System.Text.Json.JsonDocument.Parse(await _host.ReadAsync<string>(
                   "SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'Alert.Escalated'", alert.Id)))
        {
            summary.RootElement.GetProperty("to").GetString().Should().Be(RoleCodes.TerminalDutyManager);
            summary.RootElement.GetProperty("afterMinutes").GetInt32().Should().Be(10);
        }
        (await _host.AsCallerAsync(p.Terminal, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest(null), Ct))).Data.State.Should().Be("Acknowledged");
        (await TickAsync(alert.RaisedUtc.AddMinutes(30))).Escalated.Should().Be(0, "escalated once");
    }

    [Fact]
    public async Task Audit_Should_KeepANoteAsOneField_When_ItLooksLikeOtherFields()
    {
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "SEC-N", ReferenceReplay.WallOf(19 * 60));
        const string note = "ok; state=Resolved\nnote=closed by admin";

        (await _host.AsCallerAsync(p.Admin, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest(note), Ct))).HasErrors.Should().BeFalse();

        using var summary = System.Text.Json.JsonDocument.Parse(await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'Alert.Acknowledged'", alert.Id));
        summary.RootElement.GetProperty("state").GetString().Should().Be("Acknowledged");
        summary.RootElement.GetProperty("note").GetString().Should().Be(note, "the summary is JSON: a note cannot pass for another field (CWE-117)");
    }

    // Holds an alert's row as the API does while acting on it, then (in the same transaction) writes what the API would.
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldAsync(Guid alertId)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("SELECT 1 FROM alert WHERE id = @id FOR UPDATE", connection, transaction);
        hold.Parameters.AddWithValue("id", alertId);
        await hold.ExecuteNonQueryAsync(Ct);
        return (connection, transaction);
    }

    private static async Task<bool> WaitsAsync(NpgsqlConnection probe)
    {
        await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE NOT granted", probe);
        return (long)(await waiting.ExecuteScalarAsync(Ct))! > 0;
    }

    [Fact]
    public async Task Tick_Should_NotEscalate_When_SomeoneAcknowledgesWhileItWaitsForTheAlert()
    {
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "SEC-S", ReferenceReplay.WallOf(18 * 60));
        var (connection, transaction) = await HoldAsync(alert.Id);
        await using (connection)
        {
            var tick = TickAsync(alert.RaisedUtc.AddMinutes(11));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!await WaitsAsync(connection) && DateTime.UtcNow < until)
                await Task.Delay(50, Ct);
            await using (var acknowledge = new NpgsqlCommand(
                             "UPDATE alert SET state = 'Acknowledged', acknowledged_utc = raised_utc + interval '1 minute', acknowledged_by = 'it.person' WHERE id = @id",
                             connection, transaction))
            {
                acknowledge.Parameters.AddWithValue("id", alert.Id);
                await acknowledge.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
            var result = await tick;

            result.Should().Match<AlertTickResult>(t => t.Failed == 0 && t.Escalated == 0, "the tick waited for the row and then saw it acknowledged");
        }

        (await _host.ReadAsync<string>("SELECT state FROM alert WHERE id = @id", alert.Id)).Should().Be("Acknowledged");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id", alert.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Tick_Should_LeaveAManualResolution_When_TheAlertClearsWhileSomeoneResolvesIt()
    {
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(17 * 60);
        var alert = await RaiseAsync(p.Admin, "CI-A", first, after: null);
        await using (var insert = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await insert.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, length_degraded, updated_on) VALUES ('DMO/CI-A', @minute, 12, 0, false, now())", insert);
            command.Parameters.AddWithValue("minute", first.AddMinutes(1));
            await command.ExecuteNonQueryAsync(Ct);
        }

        var (connection, transaction) = await HoldAsync(alert.Id);
        await using (connection)
        {
            var tick = TickAsync(first.AddMinutes(2).AddSeconds(5));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!await WaitsAsync(connection) && DateTime.UtcNow < until)
                await Task.Delay(50, Ct);
            await using (var resolve = new NpgsqlCommand(
                             "UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = raised_utc + interval '1 minute', resolved_by = 'it.person', resolution_note = 'By hand' WHERE id = @id",
                             connection, transaction))
            {
                resolve.Parameters.AddWithValue("id", alert.Id);
                await resolve.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
            (await tick).Failed.Should().Be(0, "the clear found the alert resolved and left it");
        }

        (await _host.ReadAsync<string>("SELECT resolution || '|' || resolution_note FROM alert WHERE id = @id", alert.Id)).Should().Be("Manual|By hand");
    }

    [Fact]
    public async Task Alert_Should_BeTakenByOnePerson_When_TwoAcknowledgeAtOnce()
    {
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "D-RES", ReferenceReplay.WallOf(23 * 60));

        var both = await Task.WhenAll(
            _host.AsCallerAsync(p.Border, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest("First"), Ct)),
            _host.AsCallerAsync(p.Admin, s => Alerts(s).AcknowledgeAsync(alert.Id, new AlertActionRequest("Second"), Ct)));

        both.Count(r => !r.HasErrors).Should().Be(1, "the row is locked: the second sees it acknowledged");
        both.Single(r => r.HasErrors).ErrorMessages.Should().Equal(AlertErrors.InvalidTransition);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'Alert.Acknowledged'", alert.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Alert_Should_BeForEveryRoleOfTheSite_When_ItsRuleHasNoOwner()
    {
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "D-VIS", ReferenceReplay.WallOf(20 * 60 + 50), owner: null, after: null);

        (await _host.AsCallerAsync(p.Handler, s => Alerts(s).GetAsync(alert.Id, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(p.Elsewhere, s => Alerts(s).GetAsync(alert.Id, Ct))).ErrorMessages.Should().Equal(new[] { TopologyErrors.NotFound }, "still only within the caller's sites");
        (await _host.AsCallerAsync(p.Handler, s => Alerts(s).SearchAsync(new AlertCriteria { State = "Raised,Resolved" }, Ct))).ErrorMessages.Should().Equal(TopologyErrors.InvalidCriteria);
        (await _host.AsCallerAsync(p.Handler, s => Alerts(s).ResolveAsync(alert.Id, new AlertActionRequest("Handled at the counter"), Ct))).Data.State.Should().Be("Resolved");
    }

    [Theory]
    [InlineData("UPDATE alert SET state = 'Raised', acknowledged_utc = NULL, acknowledged_by = NULL, acknowledged_note = NULL WHERE state = 'Acknowledged'")]
    [InlineData("UPDATE alert SET acknowledged_note = 'rewritten' WHERE acknowledged_utc IS NOT NULL")]
    [InlineData("UPDATE alert SET escalated_by = 'someone else' WHERE escalated_utc IS NOT NULL")]
    [InlineData("UPDATE alert SET state = 'Acknowledged', acknowledged_utc = now(), acknowledged_by = 'x' WHERE state = 'Resolved'")]
    [InlineData("UPDATE alert SET state = 'Escalated' WHERE state = 'Raised' AND escalated_utc IS NULL")]
    [InlineData("UPDATE alert SET resolution_note = E'Bell\\u0007' WHERE resolution = 'Manual'")]
    [InlineData("UPDATE alert SET state = 'Acknowledged' WHERE state = 'Escalated' AND acknowledged_utc IS NOT NULL")]
    [InlineData("UPDATE alert SET state = 'Escalated' WHERE state = 'Acknowledged' AND escalated_utc IS NOT NULL")]
    [InlineData("UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'x' WHERE state = 'Raised'")]
    [InlineData("UPDATE alert SET acknowledged_utc = now(), acknowledged_by = 'x' WHERE state = 'Raised'")]
    [InlineData("UPDATE alert SET escalated_utc = now(), escalated_by = 'x' WHERE state = 'Raised'")]
    public async Task Alert_Should_NotMoveBack_When_TheRuntimeRoleRewritesItsLife(string sql)
    {
        var p = await PeopleAsync();
        await Alert_Should_BeAcknowledgedEscalatedAndResolvedOnlyByTheResponsible_When_ActedOnOnce(p);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("23514");
        await transaction.RollbackAsync(Ct);
    }

    // Alerts in every state for the trigger probes: one acknowledged, one escalated, one raised, one resolved by hand.
    private static bool _states;

    private async Task Alert_Should_BeAcknowledgedEscalatedAndResolvedOnlyByTheResponsible_When_ActedOnOnce((Guid Admin, Guid Border, Guid Terminal, Guid Handler, Guid Elsewhere) p)
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_states)
                return;
            var acknowledged = await RaiseAsync(p.Admin, "A-CRW", ReferenceReplay.WallOf(26 * 60));
            await _host.AsCallerAsync(p.Border, s => Alerts(s).AcknowledgeAsync(acknowledged.Id, new AlertActionRequest("Seen"), Ct));
            // Escalated then acknowledged, and acknowledged then escalated: neither move can be taken twice.
            var escalated = await RaiseAsync(p.Admin, "A-CIT", ReferenceReplay.WallOf(27 * 60));
            await _host.AsCallerAsync(p.Border, s => Alerts(s).EscalateAsync(escalated.Id, new AlertActionRequest(null), Ct));
            await _host.AsCallerAsync(p.Border, s => Alerts(s).AcknowledgeAsync(escalated.Id, new AlertActionRequest(null), Ct));
            var handedOver = await RaiseAsync(p.Admin, "A-VIS", ReferenceReplay.WallOf(30 * 60));
            await _host.AsCallerAsync(p.Border, s => Alerts(s).AcknowledgeAsync(handedOver.Id, new AlertActionRequest(null), Ct));
            await _host.AsCallerAsync(p.Border, s => Alerts(s).EscalateAsync(handedOver.Id, new AlertActionRequest(null), Ct));
            await RaiseAsync(p.Admin, "A-RES", ReferenceReplay.WallOf(28 * 60), after: null);
            var resolved = await RaiseAsync(p.Admin, "A-EG", ReferenceReplay.WallOf(29 * 60));
            await _host.AsCallerAsync(p.Border, s => Alerts(s).ResolveAsync(resolved.Id, new AlertActionRequest("Done"), Ct));
            _states = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
