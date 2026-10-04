using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Reports;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Notifications;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.IntegrationTests.Reports;

/// <summary>
/// ARV-060 against PostgreSQL with script 0036: the daily report reads the site's local day (DMO is in Asia/Dubai) from
/// stored minutes and merges their histograms, lists the alerts the caller's roles see and the devices' uptime;
/// schedules send only to accounts that may read the site's reports; a delivery round sends each recipient its own
/// report once, with the CSV files, skips those no longer allowed and retries failures.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    // The host's clock is 2026-10-01 08:00 UTC, 12:00 in Dubai: the previous local day is 2026-09-30, 20:00 UTC on the 29th
    // to 20:00 UTC on the 30th.
    private static readonly DateOnly Yesterday = new(2026, 9, 30);

    private readonly RecordingTransport _transport = new();
    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, new Dictionary<string, string>
    {
        ["Email:Enabled"] = "true",
        ["Email:FromAddress"] = "no-reply@ariva.test"
    }, TestDatabase.Reports, services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IEmailTransport>(_transport));
        services.TryAddScoped<ReportReader>();
        services.TryAddScoped<ISvcReportDeliveries, SvcReportDeliveries>();
        services.TryAddSingleton<ReportDeliveryRound>();
    });

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcReports Reports(IServiceProvider s) => s.GetRequiredService<ISvcReports>();

    private static ISvcReportSchedules Schedules(IServiceProvider s) => s.GetRequiredService<ISvcReportSchedules>();

    private async Task<Guid> AdminAsync()
    {
        var admin = await Host.CreateUserAsync("it.rep.admin." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                await Host.AsCallerAsync(null, s =>
                    new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), Host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
                await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("ALX", "Other airport"), Ct));
                await PlantDayAsync();
                _seeded = true;
            }
        }
        finally
        {
            SeedGate.Release();
        }

        return admin;
    }

    /// <summary>
    /// 17:00 in Dubai on the 30th: 17 waits of 5 minutes and 3 of 20 in A-CIT; R-001 (the border supervisor's) and R-002
    /// (anyone's) raised; device S-9 of A-CIT out for an hour. A minute of the 29th and one of October 1st stay out.
    /// </summary>
    private async Task PlantDayAsync()
    {
        (await Host.ReadAsync<int>("""
            WITH m AS (
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
                VALUES ('DMO/A-CIT', '2026-09-30T13:00:00Z', 1, 'Final', 17, 17, 17, 60, ARRAY[10], ARRAY[17], now()),
                       ('DMO/A-CIT', '2026-09-30T13:01:00Z', 1, 'Final', 3, 3, 3, 64, ARRAY[40], ARRAY[3], now()),
                       ('DMO/A-CIT', '2026-09-29T19:59:00Z', 1, 'Final', 30, 30, 30, 10, ARRAY[100], ARRAY[30], now()),
                       ('DMO/A-CIT', '2026-09-30T20:00:00Z', 1, 'Provisional', 30, 30, 30, 10, ARRAY[100], ARRAY[30], now())
                ON CONFLICT (zone_key, minute_utc) DO NOTHING
                RETURNING 1)
            SELECT 4
            """)).Should().Be(4);
        (await Host.ReadAsync<int>("""
            WITH a AS (
                INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, owner_role, raised_utc, raised_value, state)
                SELECT gen_random_uuid(), site_code, id, code, name, 'A-CIT', metric, severity, owner_role, '2026-09-30T13:05:00Z'::timestamptz, 18, 'Raised'
                FROM alert_rule WHERE code = 'R-001' AND site_code = 'DMO'
                UNION ALL
                SELECT gen_random_uuid(), site_code, id, code, name, 'A-VIS', metric, severity, owner_role, '2026-09-30T14:00:00Z'::timestamptz, 1, 'Raised'
                FROM alert_rule WHERE code = 'R-002' AND site_code = 'DMO'
                RETURNING 1)
            SELECT count(*)::int FROM a
            """)).Should().Be(2);
        (await Host.ReadAsync<int>("""
            WITH d AS (
                INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                    orientation_degrees, footprint_radius_metres, footprint_source, queue_zone_name)
                SELECT gen_random_uuid(), 'S-9', 'DMO', 'StereoVision', 'Reference', 'HttpsPush', 'Canonical', 'Ntp', 'Online', l.id, 0, 0, 3, 0, 2, 'Vendor', 'A-CIT'
                FROM level l WHERE l.site_code = 'DMO' ORDER BY l.code LIMIT 1 RETURNING 1),
            o AS (
                INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
                VALUES ('DMO/A-CIT', 'S-9', '2026-09-30T10:00:00Z', '2026-09-30T11:00:00Z', true, now()) RETURNING 1)
            SELECT (SELECT count(*) FROM d)::int + (SELECT count(*) FROM o)::int
            """)).Should().Be(2);
    }

    private async Task<Guid> UserAsync(Guid admin, string role, string site, string email = null, bool disabled = false)
    {
        var user = await Host.CreateUserAsync($"it.rep.{role.ToLowerInvariant()[..6]}.{Guid.NewGuid().ToString("N")[..8]}", disabled: disabled, roles: [role]);
        await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(user, new SiteAccessRequest(false, [site]), Ct));
        if (email is not null)
            (await Host.ReadAsync<int>("UPDATE \"user\" SET email = @secret WHERE id = @id RETURNING 1", user, email)).Should().Be(1);
        return user;
    }

    [Fact]
    public async Task Daily_Should_MergeTheLocalDaysMinutes_And_ListTheAlertsTheRolesSee()
    {
        var admin = await AdminAsync();
        var border = await UserAsync(admin, RoleCodes.BorderShiftSupervisor, "DMO");
        var manager = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO");
        var elsewhere = await UserAsync(admin, RoleCodes.BorderShiftSupervisor, "ALX");

        var report = (await Host.AsCallerAsync(border, s => Reports(s).DailyAsync("DMO", Yesterday, Ct))).Data;

        report.Should().Match<DailyReport>(r => r.TimeZoneId == "Asia/Dubai" && r.FromUtc == new DateTime(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc));
        var cit = report.Lanes.Single(l => l.Zone == "A-CIT");
        cit.LaneCategory.Should().Be("CIT", "the zone's lane in the published profile");
        cit.Waits.Should().Be(20, "the minutes of the 29th and of October 1st are other days");
        cit.Hours.Single(h => h.Start == "17:00").Should().Match<LaneHour>(h => h.P50Minutes == 5.5 && h.P90Minutes == 20.5 && h.MaxQueueLength == 64 && !h.Provisional);
        report.Lanes.Should().Contain(l => l.Zone == "A-VIS" && l.Waits == 0, "every queue zone of the published profile is listed");
        report.Alerts.Select(a => a.RuleCode).Should().Equal(new[] { "R-001", "R-002" }, "the border supervisor owns R-001; R-002 is anyone's");
        report.Alerts[0].RaisedLocal.Should().Be("2026-09-30 17:05");
        report.Devices.Single(d => d.Code == "S-9").Should().Match<DeviceUptime>(d => d.UptimePercent == 95.83 && d.OutageMinutes == 60);

        var managers = (await Host.AsCallerAsync(manager, s => Reports(s).DailyAsync("DMO", Yesterday, Ct))).Data;
        managers.Alerts.Select(a => a.RuleCode).Should().Equal(new[] { "R-002" }, "R-001 belongs to the border supervisors");
        managers.Lanes.Single(l => l.Zone == "A-CIT").P90Minutes.Should().Be(20.5, "lane waits are the same for every reader");

        (await Host.AsCallerAsync(elsewhere, s => Reports(s).DailyAsync("DMO", Yesterday, Ct))).ErrorMessages.Should().Equal(ReportErrors.NotFound);
        (await Host.AsCallerAsync(admin, s => Reports(s).DailyAsync("NOPE", Yesterday, Ct))).ErrorMessages.Should().Equal(ReportErrors.NotFound);
        (await Host.AsCallerAsync(border, s => Reports(s).DailyAsync("DMO", new DateOnly(2026, 10, 2), Ct))).ErrorMessages.Should()
            .Equal(new[] { ReportErrors.InvalidDate }, "tomorrow at the site");
        (await Host.AsCallerAsync(border, s => Reports(s).DailyAsync("DMO", null, Ct))).ErrorMessages.Should().Equal(ReportErrors.InvalidDate);
        (await Host.AsCallerAsync(border, s => Reports(s).DailyAsync("DMO", new DateOnly(2025, 8, 1), Ct))).ErrorMessages.Should()
            .Equal(new[] { ReportErrors.InvalidDate }, "more than 400 days back");
    }

    [Fact]
    public async Task Schedule_Should_SendOnlyToAccountsThatMayReadTheSite_And_BeAudited()
    {
        var admin = await AdminAsync();
        var border = await UserAsync(admin, RoleCodes.BorderShiftSupervisor, "DMO");
        var manager = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO");
        var handler = await UserAsync(admin, RoleCodes.HandlerStationManager, "DMO");
        var other = await UserAsync(admin, RoleCodes.TerminalDutyManager, "ALX");
        var off = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", disabled: true);
        ReportScheduleRequest Request(params Guid[] recipients) => new("DMO", "Morning peaks", "DailyPeaks", "07:00", recipients);

        var eligible = (await Host.AsCallerAsync(border, s => Schedules(s).RecipientsAsync("DMO", Ct))).Data.Select(r => r.Id).ToList();
        eligible.Should().Contain([border, manager, admin]).And.NotContain([handler, other, off]);

        foreach (var (request, error) in new (ReportScheduleRequest, string)[]
                 {
                     (Request(handler), ReportErrors.InvalidRecipients),
                     (Request(other), ReportErrors.InvalidRecipients),
                     (Request(off), ReportErrors.InvalidRecipients),
                     (Request(Guid.NewGuid()), ReportErrors.InvalidRecipients),
                     (Request(), ReportErrors.InvalidRecipients),
                     (Request([.. Enumerable.Range(0, 21).Select(_ => Guid.NewGuid())]), ReportErrors.InvalidRecipients),
                     (Request(border) with { Name = "Peaks\nroles=x" }, ReportErrors.InvalidName),
                     (Request(border) with { SendAt = "7:00" }, ReportErrors.InvalidSendAt),
                     (Request(border) with { SendAt = "24:00" }, ReportErrors.InvalidSendAt),
                     (Request(border) with { Template = "Weekly" }, ReportErrors.InvalidTemplate),
                     (Request(border) with { Template = "0" }, ReportErrors.InvalidTemplate),
                     (Request(border) with { SiteCode = "ALX" }, ReportErrors.NotFound)
                 })
        {
            (await Host.AsCallerAsync(border, s => Schedules(s).CreateAsync(request, Ct))).ErrorMessages.Should().Equal(new[] { error }, request.ToString());
        }

        var created = await Host.AsCallerAsync(border, s => Schedules(s).CreateAsync(Request(border, manager), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.Should().Match<ReportScheduleViewModel>(v => v.SendAt == "07:00" && v.Template == "DailyPeaks" && v.OwnerId == border && v.Recipients.Count == 2);
        (await Host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'ReportSchedule.Created'", created.Data.Id))
            .Should().Contain("sendAt=07:00").And.Contain(manager.ToString());

        var changed = await Host.AsCallerAsync(manager, s => Schedules(s).UpdateAsync(created.Data.Id, Request(manager) with { SendAt = "06:30", Enabled = false }, Ct));
        changed.Data.Should().Match<ReportScheduleViewModel>(v => v.SendAt == "06:30" && !v.Enabled && v.Recipients.Single().Id == manager);
        (await Host.AsCallerAsync(other, s => Schedules(s).GetAsync(created.Data.Id, Ct))).ErrorMessages.Should().Equal(new[] { ReportErrors.NotFound }, "another site's schedule");
        (await Host.AsCallerAsync(other, s => Schedules(s).SearchAsync("DMO", Ct))).Data.Should().BeEmpty();
        (await Host.AsCallerAsync(other, s => Schedules(s).DeleteAsync(created.Data.Id, Ct))).ErrorMessages.Should().Equal(ReportErrors.NotFound);

        (await Host.AsCallerAsync(border, s => Schedules(s).DeleteAsync(created.Data.Id, Ct))).HasErrors.Should().BeFalse();
        (await Host.AsCallerAsync(border, s => Schedules(s).GetAsync(created.Data.Id, Ct))).ErrorMessages.Should().Equal(ReportErrors.NotFound);
        (await Host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id", created.Data.Id)).Should().Be(3);
    }

    [Fact]
    public async Task Round_Should_SendEachRecipientItsOwnReportOnce_SkipThoseNoLongerAllowed_And_RetryFailures()
    {
        var admin = await AdminAsync();
        var border = await UserAsync(admin, RoleCodes.BorderShiftSupervisor, "DMO", "border.report@ariva.test");
        var manager = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", "manager.report@ariva.test");
        var noEmail = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO");
        var leaver = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", "leaver.report@ariva.test");
        var flaky = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", "flaky.report@ariva.test");
        var disabledLater = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", "disabled.report@ariva.test");
        var roleLost = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", "rolelost.report@ariva.test");
        var resetLater = await UserAsync(admin, RoleCodes.TerminalDutyManager, "DMO", "reset.report@ariva.test");
        var due = (await Host.AsCallerAsync(border, s => Schedules(s).CreateAsync(
            new ReportScheduleRequest("DMO", "Morning peaks", null, "06:00", [border, manager, noEmail, leaver, flaky, disabledLater, roleLost, resetLater]), Ct))).Data;
        var later = (await Host.AsCallerAsync(border, s => Schedules(s).CreateAsync(new ReportScheduleRequest("DMO", "Evening", null, "23:59", [border]), Ct))).Data;
        later.Should().NotBeNull();
        // The leaver loses the site after being chosen: the round checks again.
        await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(leaver, new SiteAccessRequest(false, ["ALX"]), Ct));
        // So do an account disabled, one that lost its role and one sent back to a temporary password (pending scope).
        (await Host.ReadAsync<int>("UPDATE \"user\" SET is_disabled = true WHERE id = @id RETURNING 1", disabledLater)).Should().Be(1);
        (await Host.ReadAsync<int>("DELETE FROM user_role WHERE user_id = @id RETURNING 1", roleLost)).Should().Be(1);
        (await Host.ReadAsync<int>("UPDATE \"user\" SET must_change_password = true WHERE id = @id RETURNING 1", resetLater)).Should().Be(1);
        (await Host.AsCallerAsync(border, s => Schedules(s).CreateAsync(new ReportScheduleRequest("DMO", "Pending", null, "06:00", [resetLater]), Ct)))
            .ErrorMessages.Should().Equal(new[] { ReportErrors.InvalidRecipients }, "an account still on its first sign-in is not chosen either");
        _transport.FailFor.Add("flaky.report@ariva.test");

        var first = await RunAsync();

        first.Should().Be(new ReportDeliveryRun(8, 2, 5, 1),
            "two sent; the account without an address, the leaver, the disabled, the one without the role and the pending one skipped; one failed; 23:59 is not due at 12:00");
        _transport.Sent.Select(e => e.Recipient).Should().BeEquivalentTo(["border.report@ariva.test", "manager.report@ariva.test"]);
        var toBorder = _transport.Sent.Single(e => e.Recipient == "border.report@ariva.test");
        toBorder.Subject.Should().Be("Ariva daily report DMO 2026-09-30");
        toBorder.Body.Should().Contain("Worst peak hour: A-CIT at 17:00, P90 20.5 min.").And.Contain("Alerts: 2 (1 critical).");
        toBorder.Attachments.Select(a => a.FileName).Should().Equal("ariva-DMO-2026-09-30-hours.csv", "ariva-DMO-2026-09-30-alerts.csv", "ariva-DMO-2026-09-30-devices.csv");
        Text(toBorder.Attachments[1]).Should().Contain("R-001");
        Text(_transport.Sent.Single(e => e.Recipient == "manager.report@ariva.test").Attachments[1]).Should()
            .NotContain("R-001", "each recipient gets the alerts its own roles see").And.Contain("R-002");
        Text(toBorder.Attachments[0]).Should().Contain("DMO,2026-09-30,17:00,18:00,A-CIT,CIT,20,20,5.5,20.5,64,Final,Complete");
        (await Host.ReadAsync<string>("SELECT string_agg(status || ':' || attempts, ',' ORDER BY status) FROM report_delivery WHERE schedule_id = @id", due.Id))
            .Should().Be("Failed:1,Sent:1,Sent:1,Skipped:0,Skipped:0,Skipped:0,Skipped:0,Skipped:0");

        _transport.FailFor.Clear();
        _transport.Sent.Clear();
        var second = await RunAsync();
        second.Should().Be(new ReportDeliveryRun(1, 1, 0, 0), "only the failed delivery is due again; the sent and skipped ones never are");
        _transport.Sent.Single().Recipient.Should().Be("flaky.report@ariva.test");
        (await RunAsync()).Should().Be(new ReportDeliveryRun(0, 0, 0, 0), "a round is idempotent");
        (await Host.ReadAsync<long>("SELECT count(*) FROM report_delivery WHERE schedule_id = @id", later.Id)).Should().Be(0);
    }

    private Task<ReportDeliveryRun> RunAsync() => Host.Provider.GetRequiredService<ReportDeliveryRound>().RunAsync(Ct);

    private static string Text(EmailAttachment attachment) => Encoding.UTF8.GetString(attachment.Content);

    private sealed class RecordingTransport : IEmailTransport
    {
        public List<OutgoingEmail> Sent { get; } = [];
        public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<Guid, string>> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct)
        {
            var failures = new Dictionary<Guid, string>();
            foreach (var email in emails)
            {
                if (FailFor.Contains(email.Recipient))
                    failures[email.Id] = "421 try again later";
                else
                    Sent.Add(email);
            }

            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(failures);
        }
    }
}
