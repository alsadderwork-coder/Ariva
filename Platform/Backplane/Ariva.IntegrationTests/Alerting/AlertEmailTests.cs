using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Alerting;
using Ariva.Infra.Notifications;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-040 against TimescaleDB with script 0023 and an SMTP server on loopback: an alert raised, escalated by the
/// evaluation or escalated by hand writes one email per responsible person (enabled, not break-glass, holding the role
/// and the site, with a valid address) in the transaction of that change, only when the rule notifies by email; an
/// address past the hourly limit is held back; Ariva.Api.Integration's sender sends what is due within the per-minute limit,
/// without two replicas taking the same email, retries a refused email later and gives it up after its attempts; what
/// reaches the server has one recipient and no header a value added; and the runtime role cannot delete or forge one.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AlertEmailTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Border = "border.email@ariva.test";
    private const string BorderUpper = "Border.Email.Two@ariva.test";
    private const string Terminal = "terminal.email@ariva.test";
    private const string Handler = "handler.email@ariva.test";
    private const string Elsewhere = "elsewhere.email@ariva.test";
    private const string Disabled = "disabled.email@ariva.test";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim _probes = new(1, 1);
    private static (Guid Admin, Guid Border, Guid Terminal)? _people;

    private readonly SmtpSink _sink = new();
    private AccountsHost _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        await _sink.DisposeAsync();
    }

    private AccountsHost Host(int perHour = 1000, int perMinute = 60, int attempts = 8, int? port = null)
    {
        _host = new AccountsHost(fixture, new Dictionary<string, string>
        {
            ["Email:Enabled"] = "true",
            ["Email:FromAddress"] = "no-reply@ariva.test",
            ["Email:MaxPerRecipientPerHour"] = perHour.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:MaxPerMinute"] = perMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:MaxAttempts"] = attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, TestDatabase.AlertEmails, services =>
        {
            // The sending side, as Ariva.Api.Integration registers it, against the sink.
            services.AddSingleton(new SmtpSettings { Host = "127.0.0.1", Port = port ?? _sink.Port, Security = "None", AllowInsecure = true, TimeoutSeconds = 5 });
            services.AddSingleton<IEmailTransport, MailKitTransport>();
            services.AddScoped<EmailSender>();
        });
        return _host;
    }

    private Task<AlertTickResult> TickAsync(DateTime nowUtc) =>
        new AlertEvaluation(_host.Provider.GetRequiredService<IServiceScopeFactory>(), new AlertEvaluationSettings(), NullLogger<AlertEvaluation>.Instance).TickAsync(nowUtc, Ct);

    private Task<EmailRound> SendAsync(DateTime nowUtc) =>
        _host.AsCallerAsync(null, s => s.GetRequiredService<EmailSender>().SendDueAsync(nowUtc, Ct));

    private async Task<(Guid Admin, Guid Border, Guid Terminal)> PeopleAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_people is { } known)
            {
                await _host.CreateUserAsync("it.mail.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var admin = await _host.CreateUserAsync("it.mail.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("ALX", "Other airport"), Ct));
            async Task<Guid> Person(string name, string role, string site, string email, bool disabled = false)
            {
                var id = await _host.CreateUserAsync(name, disabled: disabled, roles: [role]);
                await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(id, new SiteAccessRequest(false, [site]), Ct));
                if (email is not null)
                    await _host.ReadAsync<int>("UPDATE \"user\" SET email = @secret WHERE id = @id RETURNING 1", id, email);
                return id;
            }

            var border = await Person("it.mail.border", RoleCodes.BorderShiftSupervisor, "DMO", Border);
            await Person("it.mail.border2", RoleCodes.BorderShiftSupervisor, "DMO", BorderUpper);
            var terminal = await Person("it.mail.terminal", RoleCodes.TerminalDutyManager, "DMO", Terminal);
            await Person("it.mail.handler", RoleCodes.HandlerStationManager, "DMO", Handler);
            // Not to be told: another site, disabled, an address that is not one plain address, no address, break-glass.
            await Person("it.mail.elsewhere", RoleCodes.BorderShiftSupervisor, "ALX", Elsewhere);
            await Person("it.mail.disabled", RoleCodes.BorderShiftSupervisor, "DMO", Disabled, disabled: true);
            await Person("it.mail.injected", RoleCodes.BorderShiftSupervisor, "DMO", "inject@ariva.test\r\nBcc: attacker@example.com");
            await Person("it.mail.display", RoleCodes.BorderShiftSupervisor, "DMO", "Border <display@ariva.test>");
            await Person("it.mail.none", RoleCodes.BorderShiftSupervisor, "DMO", null);
            var glass = await _host.IssueBreakGlassAsync(rotate: false);
            await _host.ReadAsync<int>("UPDATE \"user\" SET email = 'glass.email@ariva.test' WHERE is_break_glass RETURNING 1");
            await _host.ReadAsync<int>(
                "INSERT INTO user_role (id, user_id, role_code) SELECT gen_random_uuid(), id, 'BorderShiftSupervisor' FROM \"user\" WHERE is_break_glass RETURNING 1");
            glass.Should().NotBeNull();

            _people = (admin, border, terminal);
            return _people.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    // A rule of its own on one zone, its state created by a tick, then two minutes over its threshold: one alert, raised at `first`.
    private async Task<AlertViewModel> RaiseAsync(Guid admin, string zone, DateTime first, bool email = true, string owner = RoleCodes.BorderShiftSupervisor, int? after = 10)
    {
        var rule = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Mail\r\nBcc: x@evil.test " + zone, [zone],
            "QueueLength", "GreaterThan", 1, null, null, 1, 1, "Warning", owner, after, after is null ? null : RoleCodes.TerminalDutyManager, null, email), Ct));
        if (rule.HasErrors)
        {
            // A rule name is one line of text; the template gets its value cleaned anyway (unit tested).
            rule = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Mail " + zone, [zone],
                "QueueLength", "GreaterThan", 1, null, null, 1, 1, "Warning", owner, after, after is null ? null : RoleCodes.TerminalDutyManager, null, email), Ct));
        }

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
        var page = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlerts>().SearchAsync(new AlertCriteria { SiteCode = "DMO", ZoneName = zone, Open = true }, Ct));
        return page.Data.Data.Single();
    }

    private sealed record Row(string Kind, string Recipient, string Status, int Attempts, string Reason, string Subject, DateTime NextAttemptUtc);

    private async Task<List<Row>> EmailsAsync(Guid alertId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT kind, recipient, status, attempts, reason, subject, next_attempt_utc FROM email_message WHERE alert_id = @id ORDER BY kind, recipient", connection);
        command.Parameters.AddWithValue("id", alertId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Row>();
        while (await reader.ReadAsync(Ct))
            rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetDateTime(6)));
        return rows;
    }

    // Emails other tests left pending (they share the database; a tick may also escalate their alerts) are not this test's to send.
    private Task SetAsidePendingAsync(Guid keep) =>
        _host.ReadAsync<int>(
            "WITH x AS (UPDATE email_message SET status = 'Suppressed', reason = 'set aside by a test' WHERE status = 'Pending' AND alert_id <> @id RETURNING 1) SELECT 1", keep);

    [Fact]
    public async Task Raise_Should_EmailTheOwnerRolesPeopleAtTheSiteOnly_When_TheRuleNotifiesByEmail()
    {
        Host();
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(31 * 60);
        var alert = await RaiseAsync(p.Admin, "A-CRW", first);

        var rows = await EmailsAsync(alert.Id);
        rows.Select(r => (r.Kind, r.Recipient, r.Status)).Should().BeEquivalentTo(new[]
        {
            ("AlertRaised", BorderUpper, "Pending"), ("AlertRaised", Border, "Pending")
        }, "the owner role's enabled people at DMO with one plain address each; not ALX, the disabled, the malformed, or break-glass");
        rows[0].Subject.Should().StartWith("[Ariva]").And.Contain(alert.RuleCode).And.NotContainAny("\r", "\n");

        await SetAsidePendingAsync(alert.Id);
        var round = await SendAsync(first.AddMinutes(2));
        round.Should().Be(new EmailRound(2, 0, 0));
        (await EmailsAsync(alert.Id)).Should().OnlyContain(r => r.Status == "Sent" && r.Attempts == 1 && r.Reason == null);

        var received = _sink.Received.ToList();
        received.Select(m => m.To.Single()).Should().BeEquivalentTo([Border, BorderUpper], "one recipient per message, never a list");
        received.Should().OnlyContain(m => m.From == "no-reply@ariva.test");
        foreach (var message in received)
        {
            message.Headers.Should().NotContain(h => h.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("Cc:", StringComparison.OrdinalIgnoreCase),
                "no value adds a header (CWE-93)");
            message.Headers.Should().ContainSingle(h => h.StartsWith("To:", StringComparison.OrdinalIgnoreCase)).Which.Should().NotContain(",");
            message.Headers.Should().Contain("Auto-Submitted: auto-generated");
            message.Body.Should().Contain("A-CRW").And.Contain(alert.RuleCode);
        }

        (await SendAsync(first.AddMinutes(3))).Should().Be(new EmailRound(0, 0, 0), "a sent email is sent once");

        // Left unacknowledged, the evaluation escalates it to the duty managers, who get the escalation email.
        (await TickAsync(alert.RaisedUtc.AddMinutes(10).AddSeconds(5))).Escalated.Should().BeGreaterThanOrEqualTo(1);
        await SetAsidePendingAsync(alert.Id);
        (await EmailsAsync(alert.Id)).Where(r => r.Kind == "AlertEscalated").Select(r => r.Recipient).Should().Equal(Terminal);
        (await SendAsync(alert.RaisedUtc.AddMinutes(11))).Sent.Should().Be(1);
        _sink.Received.Last().To.Should().Equal(Terminal);
        _sink.Received.Last().Headers.Should().Contain(h => h.StartsWith("Subject:", StringComparison.Ordinal) && h.Contains("escalated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Alert_Should_WriteNoEmail_When_TheRuleDoesNotNotifyByEmail()
    {
        Host();
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "A-CIT", ReferenceReplay.WallOf(33 * 60), email: false);
        (await EmailsAsync(alert.Id)).Should().BeEmpty();

        (await _host.AsCallerAsync(p.Border, s => s.GetRequiredService<ISvcAlerts>().EscalateAsync(alert.Id, new AlertActionRequest("Need help"), Ct))).HasErrors.Should().BeFalse();
        (await EmailsAsync(alert.Id)).Should().BeEmpty("escalating by hand writes none either");
    }

    [Fact]
    public async Task Ownerless_Should_EmailEveryOperationalRole_When_RaisedAndTheEscalationRole_When_EscalatedByHand()
    {
        Host();
        var p = await PeopleAsync();
        var alert = await RaiseAsync(p.Admin, "A-RES", ReferenceReplay.WallOf(35 * 60), owner: null);
        (await EmailsAsync(alert.Id)).Select(r => r.Recipient).Should().BeEquivalentTo([Border, BorderUpper, Terminal, Handler]);
        await SetAsidePendingAsync(Guid.Empty);

        var escalated = await _host.AsCallerAsync(p.Terminal, s => s.GetRequiredService<ISvcAlerts>().EscalateAsync(alert.Id, new AlertActionRequest("Over to you"), Ct));
        escalated.HasErrors.Should().BeFalse(string.Join(", ", escalated.ErrorMessages ?? []));
        (await EmailsAsync(alert.Id)).Where(r => r.Kind == "AlertEscalated").Select(r => r.Recipient).Should().Equal(Terminal);

        // Asked again for the same alert and kind, nothing new is written (one email per alert, kind and address).
        var again = await _host.AsCallerAsync(p.Admin, async s =>
        {
            var entity = await s.GetRequiredService<IUnitOfWork>().StorageProvider.GetAsync<Alert>(alert.Id, Ct);
            var queued = await s.GetRequiredService<AlertEmails>().EnqueueAsync(entity, EmailKind.AlertEscalated, alert.RaisedUtc.AddMinutes(5), Ct);
            s.GetRequiredService<IUnitOfWork>().PromiseToCommit();
            return queued;
        });
        again.Should().Be(0);
        (await EmailsAsync(alert.Id)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Address_Should_BeHeldBack_When_ItHadTheHourlyLimitOfAlertEmails()
    {
        Host(perHour: 1);
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(37 * 60);
        var one = await RaiseAsync(p.Admin, "A-EG", first);
        (await EmailsAsync(one.Id)).Should().OnlyContain(r => r.Status == "Pending");

        var two = await RaiseAsync(p.Admin, "D-EG", first.AddMinutes(5));
        (await EmailsAsync(two.Id)).Should().HaveCount(2).And.OnlyContain(r => r.Status == "Suppressed" && r.Reason.Contains("in an hour", StringComparison.Ordinal),
            "the second alert email to the same address within the hour is written as held back, never sent");

        // An hour later the address is told again.
        var three = await RaiseAsync(p.Admin, "D-RES", first.AddMinutes(70));
        (await EmailsAsync(three.Id)).Should().HaveCount(2).And.OnlyContain(r => r.Status == "Pending");
        await SetAsidePendingAsync(Guid.Empty);
    }

    [Fact]
    public async Task Sender_Should_RetryARefusedEmailLaterAndGiveItUp_When_TheServerKeepsRefusing()
    {
        Host(attempts: 3);
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(39 * 60);
        var alert = await RaiseAsync(p.Admin, "D-CRW", first);
        await SetAsidePendingAsync(alert.Id);
        _sink.Refuse[BorderUpper] = true;

        var now = first.AddMinutes(2);
        (await SendAsync(now)).Should().Be(new EmailRound(1, 0, 1), "one refused, the other still goes");
        var refused = (await EmailsAsync(alert.Id)).Single(r => r.Recipient == BorderUpper);
        refused.Should().Match<Row>(r => r.Status == "Pending" && r.Attempts == 1 && r.NextAttemptUtc == now.AddMinutes(1));
        refused.Reason.Should().Be("SMTP refused the message: SmtpCommandException", "the reason names the error type, never the server's reply or the address");

        (await SendAsync(now.AddSeconds(30))).Should().Be(new EmailRound(0, 0, 0), "not due yet");
        (await SendAsync(now.AddMinutes(1))).Should().Be(new EmailRound(0, 0, 1));
        (await SendAsync(now.AddMinutes(3))).Should().Be(new EmailRound(0, 1, 0));
        (await EmailsAsync(alert.Id)).Single(r => r.Recipient == BorderUpper).Should().Match<Row>(r => r.Status == "Failed" && r.Attempts == 3);
        (await SendAsync(now.AddHours(2))).Should().Be(new EmailRound(0, 0, 0), "given up");
        _sink.Received.Select(m => m.To.Single()).Should().Equal(Border);
    }

    [Fact]
    public async Task Sender_Should_KeepEveryEmailForLater_When_TheServerCannotBeReached()
    {
        // A port nothing listens on: the connection fails for the whole round, and the reason is the error type.
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Host(port: port);
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(41 * 60);
        var alert = await RaiseAsync(p.Admin, "D-CIT", first);
        await SetAsidePendingAsync(alert.Id);

        (await SendAsync(first.AddMinutes(2))).Should().Be(new EmailRound(0, 0, 2));
        (await EmailsAsync(alert.Id)).Should().OnlyContain(r => r.Status == "Pending" && r.Attempts == 1 && r.Reason.StartsWith("SMTP connection failed: ", StringComparison.Ordinal));
        await SetAsidePendingAsync(Guid.Empty);
    }

    [Fact]
    public async Task Sender_Should_KeepToThePerMinuteLimitAndSkipEmailsAnotherReplicaHolds_When_ManyAreDue()
    {
        Host(perMinute: 2);
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(43 * 60);
        var alert = await RaiseAsync(p.Admin, "CI-A", first, owner: null);
        await SetAsidePendingAsync(alert.Id);
        (await EmailsAsync(alert.Id)).Should().HaveCount(4);

        // Another replica holds the oldest due email: this one skips it rather than wait, and never sends it twice.
        await using var other = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await other.OpenAsync(Ct);
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT id FROM email_message WHERE alert_id = @id AND recipient = @to FOR UPDATE", other, transaction))
        {
            hold.Parameters.AddWithValue("id", alert.Id);
            hold.Parameters.AddWithValue("to", Border);
            (await hold.ExecuteScalarAsync(Ct)).Should().NotBeNull();
        }

        var now = first.AddMinutes(2);
        var sending = SendAsync(now);
        (await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(20), Ct))).Should().BeSameAs(sending, "a held row is skipped, not waited for");
        (await sending).Sent.Should().Be(2, "at most two a minute");
        (await SendAsync(now.AddSeconds(20))).Sent.Should().Be(0, "the minute's two are spent");
        await transaction.RollbackAsync(Ct);
        (await SendAsync(now.AddSeconds(61))).Sent.Should().Be(2);
        _sink.Received.Select(m => m.To.Single()).Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    [Fact]
    public async Task Address_Should_BeHeldBack_When_OneTickRaisesOrEscalatesSeveralAlertsForIt()
    {
        Host(perHour: 1);
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(47 * 60);
        var rule = await _host.AsCallerAsync(p.Admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("DMO", "Mail burst", ["CI-B", "CI-C"],
            "QueueLength", "GreaterThan", 1, null, null, 1, 1, "Warning", RoleCodes.BorderShiftSupervisor, 10, RoleCodes.TerminalDutyManager, null, true), Ct));
        rule.HasErrors.Should().BeFalse(string.Join(", ", rule.ErrorMessages ?? []));
        (await TickAsync(first.AddSeconds(5))).Failed.Should().Be(0);
        // That tick may have escalated other tests' alerts to the same people; their emails are not this test's.
        await SetAsidePendingAsync(Guid.Empty);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            foreach (var zone in new[] { "CI-B", "CI-C" })
            {
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
        }

        // One tick, one transaction, two alerts for the same people: each address gets one email, the other is held back.
        (await TickAsync(first.AddMinutes(1).AddSeconds(5))).Raised.Should().Be(2);
        var alerts = (await _host.AsCallerAsync(p.Admin, s => s.GetRequiredService<ISvcAlerts>().SearchAsync(new AlertCriteria { SiteCode = "DMO", Open = true }, Ct)))
            .Data.Data.Where(a => a.RuleId == rule.Data.Id).ToList();
        alerts.Should().HaveCount(2);
        var raised = (await EmailsAsync(alerts[0].Id)).Concat(await EmailsAsync(alerts[1].Id)).ToList();
        foreach (var address in new[] { Border, BorderUpper })
            raised.Where(r => r.Recipient == address).Select(r => r.Status).Should().BeEquivalentTo(["Pending", "Suppressed"], address);

        // The evaluation escalates both in one housekeeping transaction: the duty managers' address gets one, not two.
        (await TickAsync(alerts.Max(a => a.RaisedUtc).AddMinutes(10).AddSeconds(5))).Escalated.Should().BeGreaterThanOrEqualTo(2);
        var escalated = (await EmailsAsync(alerts[0].Id)).Concat(await EmailsAsync(alerts[1].Id)).Where(r => r.Kind == "AlertEscalated").ToList();
        escalated.Select(r => (r.Recipient, r.Status)).Should().BeEquivalentTo(new[] { (Terminal, "Pending"), (Terminal, "Suppressed") });
        await SetAsidePendingAsync(Guid.Empty);
    }

    [Fact]
    public async Task Sender_Should_HoldBackAnEmail_When_ItsRecipientWasDisabledOrMovedAfterItWasWritten()
    {
        Host();
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(49 * 60);
        var alert = await RaiseAsync(p.Admin, "SEC-N", first, owner: null);
        await SetAsidePendingAsync(alert.Id);
        (await EmailsAsync(alert.Id)).Should().HaveCount(4);

        // The handler is disabled and the second supervisor loses the site before the round.
        var handler = await _host.ReadAsync<Guid>("SELECT id FROM \"user\" WHERE user_name = 'it.mail.handler'");
        var second = await _host.ReadAsync<Guid>("SELECT id FROM \"user\" WHERE user_name = 'it.mail.border2'");
        await _host.ReadAsync<int>("UPDATE \"user\" SET is_disabled = true WHERE id = @id RETURNING 1", handler);
        await _host.ReadAsync<int>("WITH x AS (DELETE FROM user_site WHERE user_id = @id RETURNING 1) SELECT 1", second);
        try
        {
            var round = await SendAsync(first.AddMinutes(2));
            round.Should().Be(new EmailRound(2, 0, 0, 2));
            _sink.Received.Select(m => m.To.Single()).Should().BeEquivalentTo([Border, Terminal]);
            (await EmailsAsync(alert.Id)).Where(r => r.Status == "Suppressed").Select(r => r.Recipient).Should().BeEquivalentTo([Handler, BorderUpper]);
        }
        finally
        {
            await _host.ReadAsync<int>("UPDATE \"user\" SET is_disabled = false WHERE id = @id RETURNING 1", handler);
            await _host.AsCallerAsync(p.Admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(second, new SiteAccessRequest(false, ["DMO"]), Ct));
        }
    }

    [Fact]
    public async Task Sender_Should_SkipTheRound_When_AnotherReplicaIsSending()
    {
        Host();
        var p = await PeopleAsync();
        var first = ReferenceReplay.WallOf(51 * 60);
        var alert = await RaiseAsync(p.Admin, "CI-D", first);
        await SetAsidePendingAsync(alert.Id);

        await using var other = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await other.OpenAsync(Ct);
        await using (var transaction = await other.BeginTransactionAsync(Ct))
        {
            await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", other, transaction))
            {
                take.Parameters.AddWithValue("key", EmailSender.RoundLock);
                await take.ExecuteNonQueryAsync(Ct);
            }

            (await SendAsync(first.AddMinutes(2))).Should().Be(new EmailRound(0, 0, 0), "one replica sends at a time, so the per-minute limit holds over all of them");
            await transaction.RollbackAsync(Ct);
        }

        (await SendAsync(first.AddMinutes(2))).Sent.Should().Be(2);
    }

    [Theory]
    [InlineData("border@ariva.test\r\nBcc: attacker@example.com")]
    [InlineData("Border <border@ariva.test>")]
    [InlineData("border@ariva.test, other@ariva.test")]
    [InlineData("border sup@ariva.test")]
    [InlineData("border@localhost")]
    public async Task Users_Should_RefuseAnAddressThatIsNotOnePlainAddress_When_CreatedOrUpdated(string email)
    {
        Host();
        var p = await PeopleAsync();
        var users = (Func<IServiceProvider, ISvcUsers>)(s => s.GetRequiredService<ISvcUsers>());
        (await _host.AsCallerAsync(p.Admin, s => users(s).UpdateAsync(p.Terminal, new UpdateUserRequest("Terminal", email), Ct))).ErrorMessages
            .Should().Equal(AdministrationErrors.InvalidEmail);
        (await _host.AsCallerAsync(p.Admin, s => users(s).CreateAsync(new CreateUserRequest("it.mail.new." + Guid.NewGuid().ToString("N")[..6], null, email), Ct))).ErrorMessages
            .Should().Equal(AdministrationErrors.InvalidEmail);
        (await _host.ReadAsync<string>("SELECT email FROM \"user\" WHERE id = @id", p.Terminal)).Should().Be(Terminal);
        (await _host.AsCallerAsync(p.Admin, s => users(s).UpdateAsync(p.Terminal, new UpdateUserRequest("Terminal", "  " + Terminal + " "), Ct))).HasErrors.Should().BeFalse();
    }

    [Theory]
    [InlineData("DELETE FROM email_message", "42501")]
    [InlineData("TRUNCATE email_message", "42501")]
    [InlineData("UPDATE email_message SET recipient = 'someone@attacker.example'", "42501")]
    [InlineData("UPDATE email_message SET body = 'Click here'", "42501")]
    [InlineData("UPDATE email_message SET subject = 'Urgent'", "42501")]
    [InlineData("UPDATE email_message SET alert_id = alert_id", "42501")]
    [InlineData("UPDATE email_message SET kind = 'AlertEscalated'", "42501")]
    [InlineData("UPDATE email_message SET status = 'Pending', sent_utc = NULL WHERE status = 'Sent'", "23514")]
    [InlineData("UPDATE email_message SET status = 'Pending' WHERE status IN ('Suppressed', 'Failed')", "23514")]
    [InlineData("UPDATE email_message SET attempts = 0 WHERE attempts > 0", "23514")]
    [InlineData("UPDATE email_message SET status = 'Sent', sent_utc = NULL WHERE status = 'Pending'", "23514")]
    public async Task Email_Should_NotBeDeletedOrForged_When_TheRuntimeRoleTries(string sql, string state)
    {
        Host();
        var p = await PeopleAsync();
        // Rows in every state the probes need: one sent, one given up, one pending.
        if (await _host.ReadAsync<long>("SELECT count(*) FROM email_message WHERE status = 'Pending'") == 0 ||
            await _host.ReadAsync<long>("SELECT count(*) FROM email_message WHERE status = 'Sent' AND attempts > 0") == 0 ||
            await _host.ReadAsync<long>("SELECT count(*) FROM email_message WHERE status = 'Failed'") == 0)
        {
            await _probes.WaitAsync(Ct);
            try
            {
                if (await _host.ReadAsync<long>("SELECT count(*) FROM email_message WHERE status = 'Failed'") == 0)
                {
                    var first = ReferenceReplay.WallOf(45 * 60);
                    var alert = await RaiseAsync(p.Admin, "SEC-S", first, owner: null);
                    await SetAsidePendingAsync(alert.Id);
                    _sink.Refuse[Handler] = true;
                    await SendAsync(first.AddMinutes(2));
                    await SendAsync(first.AddMinutes(4));
                    await SendAsync(first.AddMinutes(8));
                    await SendAsync(first.AddMinutes(16));
                    await SendAsync(first.AddMinutes(40));
                    await SendAsync(first.AddMinutes(80));
                    await SendAsync(first.AddMinutes(160));
                    await SendAsync(first.AddMinutes(300));
                    await RaiseAsync(p.Admin, "D-VIS", first.AddMinutes(310));
                }
            }
            finally
            {
                _probes.Release();
            }
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM email_message WHERE status = 'Failed'")).Should().BeGreaterThan(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM email_message WHERE status = 'Pending'")).Should().BeGreaterThan(0);
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
