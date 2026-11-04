using Ariva.Core;
using Ariva.Core.Reports;
using Ariva.Core.Services;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Notifications;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Services.Reports;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.IntegrationTests.Reports;

/// <summary>
/// ARV-096d, ARV-234 (CWE-400, CWE-120, ASVS 5.0 V15.2.2) against PostgreSQL 17 with TimescaleDB: the daily report reads at most its
/// bound of queue zone hours, zones, devices and outages (each read takes the bound plus one row) and of merged histogram pairs
/// (counted row by row, no histogram read past the bound), and refuses a day past one, never cutting it short; a scheduled
/// delivery of such a day is failed once with the fixed reason and not tried again. The report at its own bound is measured in
/// <see cref="DailyReportHeapTests"/>. Sites of their own per test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DailyReportBoundsTests(PostgresFixture fixture) : IAsyncDisposable
{
    /// <summary>The host's clock is 2026-10-01 08:00 UTC; these sites have no airport, so their day is UTC's.</summary>
    private static readonly DateOnly Day = new(2026, 9, 30);

    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private DailyReportLimits _deliveryLimits = DailyReportLimits.Default;
    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, new Dictionary<string, string>
    {
        ["Email:Enabled"] = "true",
        ["Email:FromAddress"] = "no-reply@ariva.test"
    }, TestDatabase.ReportBounds, services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IEmailTransport>(new CountingTransport()));
        // The deliveries' reader with this test's bounds.
        services.Replace(ServiceDescriptor.Scoped(s => new ReportReader(s.GetRequiredService<IUnitOfWork>()) { Limits = _deliveryLimits }));
        services.TryAddScoped<ISvcReportDeliveries, SvcReportDeliveries>();
        services.TryAddSingleton<ReportDeliveryRound>();
    });

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Fluentx.Result<DailyReport>> ReadAsync(string site, DailyReportLimits limits) =>
        Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()) { Limits = limits }
            .ReadAsync(site, Day, [RoleCodes.SystemAdministrator], Now, Ct));

    [Fact]
    public async Task Read_Should_RefuseTheDayAtEachBound_When_ItHoldsOneRowMore()
    {
        await Host.CreateUserAsync("it.bounds.reader", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await Host.ReadAsync<int>("""
            WITH site AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'BND', 'Bounds') RETURNING code),
            airport AS (INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), 'BNX', 'Bounds airport', 'UTC') RETURNING id),
            terminal AS (INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), id, 'T1', 'T1', 'BND' FROM airport RETURNING id),
            level AS (
                INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                SELECT gen_random_uuid(), id, 'BND', 'L0', 'Arrivals', 0, 100, 100 FROM terminal RETURNING id),
            minutes AS (
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
                SELECT 'BND/Q' || z, timestamptz '2026-09-30T10:00:00Z' + m * interval '1 minute', 1, 'Final', 2, 2, 2, 5, ARRAY[4, 8], ARRAY[1, 1], now()
                FROM generate_series(1, 3) z CROSS JOIN generate_series(0, 9) m
                RETURNING 1),
            devices AS (
                INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                    orientation_degrees, footprint_radius_metres, footprint_source, queue_zone_name)
                SELECT gen_random_uuid(), 'S-' || d, 'BND', 'StereoVision', 'Reference', 'HttpsPush', 'Canonical', 'Ntp', 'Online', l.id, 0, 0, 3, 0, 2, 'Vendor', 'Q1'
                FROM level l CROSS JOIN generate_series(1, 4) d
                RETURNING 1),
            outages AS (
                INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
                SELECT 'BND/Q1', 'S-1', timestamptz '2026-09-30T00:00:00Z' + o * interval '1 hour', timestamptz '2026-09-30T00:10:00Z' + o * interval '1 hour', true, now()
                FROM generate_series(0, 4) o
                RETURNING 1)
            SELECT (SELECT count(*) FROM minutes)::int + (SELECT count(*) FROM devices)::int + (SELECT count(*) FROM outages)::int
            """)).Should().Be(30 + 4 + 5);

        // Three zone hours (three zones' 10:00 hour), each merging two buckets: six pairs.
        var exact = await ReadAsync("BND", new DailyReportLimits(MaxZoneHours: 3, MaxZones: 3, MaxDevices: 4, MaxOutages: 5, MaxHistogramPairs: 6));
        exact.HasErrors.Should().BeFalse(string.Join(" ", exact.ErrorMessages ?? []));
        exact.Data.Lanes.Should().HaveCount(3).And.OnlyContain(l => l.Passengers == 20 && l.P90Minutes == 4.5, "each read at its bound is served whole");

        foreach (var (why, limits) in new[]
                 {
                     ("zone hour", new DailyReportLimits(MaxZoneHours: 2, MaxZones: 3, MaxDevices: 4, MaxOutages: 5, MaxHistogramPairs: 6)),
                     ("histogram pair", new DailyReportLimits(MaxZoneHours: 3, MaxZones: 3, MaxDevices: 4, MaxOutages: 5, MaxHistogramPairs: 5)),
                     ("device", new DailyReportLimits(MaxZoneHours: 3, MaxZones: 3, MaxDevices: 3, MaxOutages: 5, MaxHistogramPairs: 6)),
                     ("outage", new DailyReportLimits(MaxZoneHours: 3, MaxZones: 3, MaxDevices: 4, MaxOutages: 4, MaxHistogramPairs: 6))
                 })
        {
            (await ReadAsync("BND", limits)).ErrorMessages.Should().Equal([ReportErrors.TooLarge], $"one {why} past the bound refuses the day");
        }

        // Past the pairs bound the reader counts every row's pairs but reads no histogram once the count passes it (the first
        // row's two pairs are within a bound of three, the next rows' are not).
        var (from, to) = DailyReports.DayBounds(Day, TimeZoneInfo.Utc);
        var read = await Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()) { Limits = new DailyReportLimits(MaxHistogramPairs: 3) }
            .ReadHoursAsync("BND", from, to, Ct));
        read.Pairs.Should().Be(6, "every row's pairs are counted");
        read.Hours.Should().HaveCount(3);
        read.Hours.Count(h => h.Histogram.Count > 0).Should().Be(1, "no histogram is read past the pairs bound");

        // The zone hours read stop at the bound plus one row, whatever the day holds beyond it.
        var capped = await Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()) { Limits = new DailyReportLimits(MaxZoneHours: 1) }
            .ReadHoursAsync("BND", from, to, Ct));
        capped.Hours.Should().HaveCount(2, "the read takes its bound plus one row, not the day's three");
    }

    [Fact]
    public async Task Read_Should_RefuseTheDay_When_TheProfilesNameMoreQueueZonesThanTheBound()
    {
        // The demo topology's DMO: its published profile's queue zones are the names the report reads.
        await Host.CreateUserAsync("it.bounds.zones", roles: [RoleCodes.SystemAdministrator], allSites: true);
        await Host.AsCallerAsync(null, s =>
            new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), Host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
        var zones = (int)await Host.ReadAsync<long>("""
            SELECT count(DISTINCT z.name) FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = 'DMO' AND z.kind = 'Queue' AND p.status IN ('Published', 'Retired')
            """);
        zones.Should().BePositive();

        var exact = await ReadAsync("DMO", new DailyReportLimits(MaxZones: zones));
        var past = await ReadAsync("DMO", new DailyReportLimits(MaxZones: zones - 1));

        exact.HasErrors.Should().BeFalse(string.Join(" ", exact.ErrorMessages ?? []));
        past.ErrorMessages.Should().Equal([ReportErrors.TooLarge], "one queue zone name past the bound refuses the day");
    }

    [Fact]
    public async Task Delivery_Should_FailOnceWithTheFixedReason_When_TheDayIsPastABound()
    {
        _deliveryLimits = new DailyReportLimits(MaxZoneHours: 0);
        var admin = await Host.CreateUserAsync("it.bounds.admin", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await Host.ReadAsync<int>("UPDATE \"user\" SET email = @secret WHERE id = @id RETURNING 1", admin, "bounds.admin@ariva.test")).Should().Be(1);
        (await Host.ReadAsync<int>("""
            WITH site AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'BDL', 'Bounds delivery') RETURNING code),
            minutes AS (
                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, updated_on)
                SELECT 'BDL/Q1', timestamptz '2026-09-30T10:00:00Z' + m * interval '1 minute', 1, 'Final', 0, 0, 0, 1, now()
                FROM generate_series(0, 5) m
                RETURNING 1)
            SELECT count(*)::int FROM minutes
            """)).Should().Be(6);
        var schedule = (await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReportSchedules>()
            .CreateAsync(new ReportScheduleRequest("BDL", "Past the bound", null, "06:00", [admin]), Ct))).Data;

        var first = await Host.Provider.GetRequiredService<ReportDeliveryRound>().RunAsync(Ct);
        var second = await Host.Provider.GetRequiredService<ReportDeliveryRound>().RunAsync(Ct);

        first.Failed.Should().Be(1);
        (await Host.ReadAsync<string>("SELECT status || '|' || attempts || '|' || reason FROM report_delivery WHERE schedule_id = @id", schedule.Id))
            .Should().Be($"Failed|{ISvcReportDeliveries.MaxAttempts}|{ReportErrors.TooLarge}", "recorded once, with the fixed reason, at the attempt limit");
        (second.Sent + second.Failed).Should().Be(0, "a day past the bound stays past it, so it is not tried again");
    }

    /// <summary>Counts what the relay would send; nothing leaves the test.</summary>
    private sealed class CountingTransport : IEmailTransport
    {
        public Task<EmailBatch> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct) =>
            Task.FromResult(EmailBatch.Tried(new Dictionary<Guid, string>()));
    }
}
