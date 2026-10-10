using Ariva.Core;
using Ariva.Core.Availability;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Services.Quality;
using Ariva.Infra.Availability;
using Ariva.Infra.Live;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Availability;

/// <summary>
/// ARV-118 against PostgreSQL with script 0042: the site operating calendar through its service (recorded before it takes
/// effect, audited, site-scoped), and the availability ledger the Cronz job writes: one row per site and minute, decided
/// live from Redis snapshots and queue_minute rows, idempotent under a second run and a second replica, catching up after
/// downtime from the database alone; the read per local day at a site with daylight saving; the runtime role's
/// privileges. Each test plants a site of its own (code, airport, terminal, level, published profile with queue zones)
/// so the ledger's cursor of one test never meets another's.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AvailabilityTests(PostgresFixture fixture) : IAsyncDisposable
{
    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, database: TestDatabase.Availability);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    #region Helpers

    private async Task<NpgsqlConnection> ConnectAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<T> ScalarAsync<T>([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await ConnectAsync();
#pragma warning disable CA2100 // test helper: every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is DBNull or null ? default : (T)result;
    }

    private async Task ExecuteAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await ConnectAsync();
#pragma warning disable CA2100 // test helper: every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>A site with its airport (time zone), terminal, level and a published profile (version 1) with the given queue zones.</summary>
    private async Task PlantSiteAsync(string site, string iata, string timeZone, DateTime publishedUtc, params string[] zones)
    {
        await Host.DatabaseAsync();
        // The zones go in while the profile is a draft (a published profile's zones never change), then it is published.
        await ExecuteAsync("""
            WITH s AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site) RETURNING code),
                 a AS (INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, @iata, @tz) RETURNING id),
                 t AS (INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', s.code FROM a, s RETURNING id, site_code)
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
            SELECT gen_random_uuid(), t.id, t.site_code, 'L1', 'L1', 0, 100, 100 FROM t;
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (gen_random_uuid(), @site, 'Profile', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon)
            SELECT gen_random_uuid(), p.id, z, 'Queue', l.id, '[]'
              FROM zone_profile p, level l, unnest(@zones) AS z WHERE p.site_code = @site AND l.site_code = @site;
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('a', 64), published_on = @published, published_by = 'it'
             WHERE site_code = @site;
            """, ("site", site), ("iata", iata), ("tz", timeZone), ("published", publishedUtc), ("zones", zones));
    }

    /// <summary>queue_minute rows for each zone and minute of [from, to].</summary>
    private Task PlantMinutesAsync(string site, string zone, DateTime from, DateTime to) =>
        ExecuteAsync("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, updated_on)
            SELECT @key, m, 1, now() FROM generate_series(@from, @to, INTERVAL '1 minute') AS m
            ON CONFLICT DO NOTHING
            """, ("key", $"{site}/{zone}"), ("from", from), ("to", to));

    private sealed class FakeSnapshots : ILiveSnapshotStore
    {
        public Dictionary<string, LiveZoneSnapshot> Values { get; } = new(StringComparer.Ordinal);

        public bool Fail { get; set; }

        public int Reads { get; private set; }

        public void Fresh(string zoneKey, DateTime minuteUtc, DateTime publishedUtc) =>
            Values[zoneKey] = new LiveZoneSnapshot(zoneKey, minuteUtc, 4, true, false, 2, 2, null, false, publishedUtc);

        public Task PublishAsync(IReadOnlyCollection<LiveZoneSnapshot> snapshots, CancellationToken ct) => Task.CompletedTask;

        public Task<LiveZoneSnapshot> GetAsync(string zoneKey, CancellationToken ct)
        {
            Reads++;
            if (Fail)
                throw new TimeoutException("Redis is down");
            return Task.FromResult(Values.GetValueOrDefault(zoneKey));
        }

        public Task SubscribeAsync(Func<LiveZoneSnapshot, Task> handler, CancellationToken ct) => Task.CompletedTask;
    }

    private async Task<AvailabilityLedger> LedgerAsync(FakeSnapshots snapshots, AvailabilitySettings settings = null, Microsoft.Extensions.Logging.ILogger<AvailabilityLedger> logger = null)
    {
        await Host.CreateUserAsync("it.av." + Guid.NewGuid().ToString("N")[..8]);
        return new AvailabilityLedger(Host.Provider.GetRequiredService<DatabaseSettings>(), snapshots, Host.Clock, settings ?? new AvailabilitySettings(),
            logger ?? NullLogger<AvailabilityLedger>.Instance);
    }

    /// <summary>Keeps the warnings a ledger logs (no logging test package in this project).</summary>
    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger<AvailabilityLedger>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Enqueue(formatter(state, exception));
        }
    }

    private sealed record Row(DateTime Minute, string LocalDate, string Calendar, string State, string Reasons, short Expected, short Stale, short Missing, short Lagging);

    private async Task<List<Row>> RowsAsync(string site)
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("""
            SELECT minute_utc, to_char(local_date, 'YYYY-MM-DD'), calendar, state, array_to_string(reasons, ','), zones_expected, zones_stale, zones_missing, zones_lagging
            FROM availability_minute WHERE site_code = @site ORDER BY minute_utc
            """, connection);
        command.Parameters.AddWithValue("site", site);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Row>();
        while (await reader.ReadAsync(Ct))
            rows.Add(new Row(reader.GetFieldValue<DateTime>(0).ToUniversalTime(), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt16(5), reader.GetInt16(6), reader.GetInt16(7), reader.GetInt16(8)));
        return rows;
    }

    private static DateTime Utc(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    #endregion

    #region Calendar service

    [Fact]
    public async Task Calendar_Should_RecordEntriesBeforeTheyTakeEffect_When_AnAdministratorSetsThem()
    {
        // The host's clock: Thursday 1 October 2026 08:00 UTC, 12:00 in Dubai.
        await PlantSiteAsync("AVCAL", "AVC", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var admin = await Host.CreateUserAsync("it.av.cal." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
        ISvcSiteCalendar Calendar(IServiceProvider s) => s.GetRequiredService<ISvcSiteCalendar>();

        var week = await Host.AsCallerAsync(admin, s => Calendar(s).SetWeekAsync("AVCAL",
            new SetOperatingWeekRequest("2026-10-02", [new("Friday", "18:00", "02:00"), new("Monday", "06:00", "22:00")]), Ct));
        week.HasErrors.Should().BeFalse(string.Join(" ", week.ErrorMessages ?? []));
        week.Data.TimeZoneId.Should().Be("Asia/Dubai");
        week.Data.Today.Should().Be("2026-10-01");
        week.Data.Weeks.Should().ContainSingle().Which.Hours.Select(h => (h.Day, h.Opens, h.Closes))
            .Should().Equal(("Monday", "06:00", "22:00"), ("Friday", "18:00", "02:00"));

        // The same day again replaces the version (PUT); a version for today is refused.
        (await Host.AsCallerAsync(admin, s => Calendar(s).SetWeekAsync("AVCAL", new SetOperatingWeekRequest("2026-10-02", [new("Monday", "07:00", "21:00")]), Ct)))
            .Data.Weeks.Should().ContainSingle().Which.Hours.Should().ContainSingle();
        (await Host.AsCallerAsync(admin, s => Calendar(s).SetWeekAsync("AVCAL", new SetOperatingWeekRequest("2026-10-01", []), Ct)))
            .ErrorMessages.Should().Equal(SiteCalendarErrors.InvalidDate);

        var exception = await Host.AsCallerAsync(admin, s => Calendar(s).AddExceptionAsync("AVCAL", new AddOperatingExceptionRequest("2026-10-05", true, [], "Closure"), Ct));
        exception.Data.Exceptions.Should().ContainSingle().Which.Closed.Should().BeTrue();
        (await Host.AsCallerAsync(admin, s => Calendar(s).AddExceptionAsync("AVCAL", new AddOperatingExceptionRequest("2026-10-05", false, [new("10:00", "11:00")], "Again"), Ct)))
            .ErrorMessages.Should().Equal(SiteCalendarErrors.ExceptionTaken);

        var window = await Host.AsCallerAsync(admin, s => Calendar(s).AddMaintenanceAsync("AVCAL",
            new AddMaintenanceWindowRequest("2026-10-03T04:00:00Z", "2026-10-03T05:00:00Z", "Upgrade"), Ct));
        window.Data.MaintenanceWindows.Should().ContainSingle().Which.RecordedUtc.Should().Be(Utc(10, 1, 8, 0));
        (await Host.AsCallerAsync(admin, s => Calendar(s).AddMaintenanceAsync("AVCAL",
            new AddMaintenanceWindowRequest("2026-10-01T08:00:00Z", "2026-10-01T09:00:00Z", "Now"), Ct))).ErrorMessages.Should().Equal(SiteCalendarErrors.InvalidWindow);

        (await ScalarAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name = 'AVCAL' AND action LIKE 'SiteCalendar.%'")).Should().Be(4,
            "two weekly settings, the exception and the window");
        (await ScalarAsync<string>("SELECT after_summary FROM audit_entry WHERE target_name = 'AVCAL' AND action = 'SiteCalendar.MaintenanceAdded'"))
            .Should().Contain("startsUtc=2026-10-03T04:00Z");

        // Before they take effect, entries are removed (soft deleted), leave the calendar and are audited with what they were.
        var laterWeek = await Host.AsCallerAsync(admin, s => Calendar(s).SetWeekAsync("AVCAL", new SetOperatingWeekRequest("2026-10-09", [new("Tuesday", "08:00", "20:00")]), Ct));
        var laterWeekId = laterWeek.Data.Weeks.Single(w => w.EffectiveFrom == "2026-10-09").Id;
        var laterException = await Host.AsCallerAsync(admin, s => Calendar(s).AddExceptionAsync("AVCAL", new AddOperatingExceptionRequest("2026-10-06", true, [], "Drill"), Ct));
        var laterExceptionId = laterException.Data.Exceptions.Single(e => e.Date == "2026-10-06").Id;
        var laterWindow = await Host.AsCallerAsync(admin, s => Calendar(s).AddMaintenanceAsync("AVCAL",
            new AddMaintenanceWindowRequest("2026-10-04T02:00:00Z", "2026-10-04T03:00:00Z", "Patch"), Ct));
        var laterWindowId = laterWindow.Data.MaintenanceWindows.Single(w => w.StartsUtc == Utc(10, 4, 2, 0)).Id;

        var weekRemoved = await Host.AsCallerAsync(admin, s => Calendar(s).RemoveWeekAsync("AVCAL", laterWeekId, Ct));
        weekRemoved.HasErrors.Should().BeFalse(string.Join(" ", weekRemoved.ErrorMessages ?? []));
        weekRemoved.Data.Weeks.Select(w => w.Id).Should().NotContain(laterWeekId).And.HaveCount(1);
        var exceptionRemoved = await Host.AsCallerAsync(admin, s => Calendar(s).RemoveExceptionAsync("AVCAL", laterExceptionId, Ct));
        exceptionRemoved.HasErrors.Should().BeFalse(string.Join(" ", exceptionRemoved.ErrorMessages ?? []));
        exceptionRemoved.Data.Exceptions.Select(e => e.Id).Should().NotContain(laterExceptionId).And.HaveCount(1);
        var windowCancelled = await Host.AsCallerAsync(admin, s => Calendar(s).CancelMaintenanceAsync("AVCAL", laterWindowId, Ct));
        windowCancelled.HasErrors.Should().BeFalse(string.Join(" ", windowCancelled.ErrorMessages ?? []));
        windowCancelled.Data.MaintenanceWindows.Select(w => w.Id).Should().NotContain(laterWindowId).And.HaveCount(1);
        (await Host.AsCallerAsync(admin, s => Calendar(s).GetAsync("AVCAL", Ct))).Data.Should().Match<SiteCalendarViewModel>(c =>
            c.Weeks.Count == 1 && c.Exceptions.Count == 1 && c.MaintenanceWindows.Count == 1);

        (await ScalarAsync<DateTime?>("SELECT deleted_on FROM operating_week WHERE id = @id", ("id", laterWeekId))).Should().Be(Utc(10, 1, 8, 0));
        (await ScalarAsync<DateTime?>("SELECT deleted_on FROM operating_day_exception WHERE id = @id", ("id", laterExceptionId))).Should().Be(Utc(10, 1, 8, 0));
        (await ScalarAsync<DateTime?>("SELECT deleted_on FROM maintenance_window WHERE id = @id", ("id", laterWindowId))).Should().Be(Utc(10, 1, 8, 0));
        foreach (var (action, id, before) in new[]
                 {
                     ("SiteCalendar.WeekRemoved", laterWeekId, "site=AVCAL; effectiveFrom=2026-10-09; hours="),
                     ("SiteCalendar.ExceptionRemoved", laterExceptionId, "site=AVCAL; date=2026-10-06; hours=[]; reason=\"Drill\""),
                     ("SiteCalendar.MaintenanceCancelled", laterWindowId, "startsUtc=2026-10-04T02:00Z")
                 })
        {
            (await ScalarAsync<long>("SELECT count(*) FROM audit_entry WHERE action = @action AND target_id = @id AND after_summary IS NULL", ("action", action), ("id", id)))
                .Should().Be(1, action);
            (await ScalarAsync<string>("SELECT before_summary FROM audit_entry WHERE action = @action AND target_id = @id", ("action", action), ("id", id)))
                .Should().Contain(before, action);
        }

        // Once in effect, an entry is part of the record: removing it is refused.
        var exceptionId = exception.Data.Exceptions[0].Id;
        var windowId = window.Data.MaintenanceWindows[0].Id;
        Host.Clock.Advance(TimeSpan.FromDays(4));
        (await Host.AsCallerAsync(admin, s => Calendar(s).RemoveExceptionAsync("AVCAL", exceptionId, Ct))).ErrorMessages.Should().Equal(SiteCalendarErrors.Started);
        (await Host.AsCallerAsync(admin, s => Calendar(s).CancelMaintenanceAsync("AVCAL", windowId, Ct))).ErrorMessages.Should().Equal(SiteCalendarErrors.Started);
        (await Host.AsCallerAsync(admin, s => Calendar(s).RemoveWeekAsync("AVCAL", week.Data.Weeks[0].Id, Ct))).ErrorMessages.Should().Equal(SiteCalendarErrors.Started);
    }

    [Fact]
    public async Task Calendar_Should_AnswerNotFound_When_TheSiteOrEntryIsBeyondTheCaller()
    {
        await PlantSiteAsync("AVOWN", "AVO", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        await PlantSiteAsync("AVFOR", "AVF", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var admin = await Host.CreateUserAsync("it.av.all." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
        var limited = await Host.CreateUserAsync("it.av.own." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator]);
        await ExecuteAsync("INSERT INTO user_site (id, user_id, site_code) VALUES (gen_random_uuid(), @user, 'AVOWN')", ("user", limited));
        ISvcSiteCalendar Calendar(IServiceProvider s) => s.GetRequiredService<ISvcSiteCalendar>();

        var foreign = await Host.AsCallerAsync(admin, s => Calendar(s).AddMaintenanceAsync("AVFOR",
            new AddMaintenanceWindowRequest("2026-10-03T04:00:00Z", "2026-10-03T05:00:00Z", "Upgrade"), Ct));
        var foreignId = foreign.Data.MaintenanceWindows[0].Id;

        (await Host.AsCallerAsync(limited, s => Calendar(s).GetAsync("AVFOR", Ct))).ErrorMessages.Should().Equal(SiteCalendarErrors.NotFound);
        (await Host.AsCallerAsync(limited, s => Calendar(s).SetWeekAsync("AVFOR", new SetOperatingWeekRequest("x", null), Ct)))
            .ErrorMessages.Should().Equal([SiteCalendarErrors.NotFound], "the site is checked before the request");
        (await Host.AsCallerAsync(limited, s => Calendar(s).CancelMaintenanceAsync("AVOWN", foreignId, Ct)))
            .ErrorMessages.Should().Equal([SiteCalendarErrors.NotFound], "another site's entry through the caller's own site");
        (await Host.AsCallerAsync(limited, s => Calendar(s).GetAsync("ZZ9", Ct))).ErrorMessages.Should().Equal(SiteCalendarErrors.NotFound);
        (await Host.AsCallerAsync(limited, s => Calendar(s).GetAsync("AVOWN", Ct))).HasErrors.Should().BeFalse();
        (await ScalarAsync<DateTime?>("SELECT deleted_on FROM maintenance_window WHERE id = @id", ("id", foreignId))).Should().BeNull();
        (await Host.AsCallerAsync(limited, s => s.GetRequiredService<ISvcAvailability>().GetAsync("AVFOR", new AvailabilityCriteria("2026-10-01", "2026-10-01"), Ct)))
            .ErrorMessages.Should().Equal(AvailabilityErrors.NotFound);
    }

    #endregion

    #region Ledger

    [Fact]
    public async Task Ledger_Should_RecordAvailableMinutes_When_EveryZoneIsLiveAndHasItsMinutes()
    {
        await PlantSiteAsync("AVLIVE", "AVL", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1", "Q2");
        var snapshots = new FakeSnapshots();
        var ledger = await LedgerAsync(snapshots);
        // 08:00:30: the latest decidable minute is 07:57. A first pass starts there.
        Host.Clock.Advance(TimeSpan.FromSeconds(30));
        await PlantMinutesAsync("AVLIVE", "Q1", Utc(10, 1, 7, 50), Utc(10, 1, 8, 10));
        await PlantMinutesAsync("AVLIVE", "Q2", Utc(10, 1, 7, 50), Utc(10, 1, 8, 10));
        snapshots.Fresh("AVLIVE/Q1", Utc(10, 1, 7, 59), Utc(10, 1, 8, 0));
        snapshots.Fresh("AVLIVE/Q2", Utc(10, 1, 7, 59), Utc(10, 1, 8, 0));

        var pass = await ledger.SiteAsync("AVLIVE", Ct);

        pass.Minutes.Should().Be(1);
        var row = (await RowsAsync("AVLIVE")).Should().ContainSingle().Subject;
        row.Should().Be(new Row(Utc(10, 1, 7, 57), "2026-10-01", "Operating", "Available", "", 2, 0, 0, 0));

        // A minute later: 07:58 is decided; a second pass in the same minute writes nothing (idempotent per site and minute).
        Host.Clock.Advance(TimeSpan.FromMinutes(1));
        snapshots.Fresh("AVLIVE/Q1", Utc(10, 1, 8, 0), Utc(10, 1, 8, 1));
        snapshots.Fresh("AVLIVE/Q2", Utc(10, 1, 8, 0), Utc(10, 1, 8, 1));
        (await ledger.SiteAsync("AVLIVE", Ct)).Minutes.Should().Be(1);
        (await ledger.SiteAsync("AVLIVE", Ct)).Minutes.Should().Be(0);
        (await RowsAsync("AVLIVE")).Select(r => (r.Minute, r.State)).Should().Equal((Utc(10, 1, 7, 57), "Available"), (Utc(10, 1, 7, 58), "Available"));
    }

    [Fact]
    public async Task Ledger_Should_RecordTheReasons_When_AZoneIsStaleLaggingOrMissingItsMinute()
    {
        await PlantSiteAsync("AVBAD", "AVB", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1", "Q2", "Q3");
        var snapshots = new FakeSnapshots();
        var ledger = await LedgerAsync(snapshots);
        Host.Clock.Advance(TimeSpan.FromSeconds(30));
        await PlantMinutesAsync("AVBAD", "Q1", Utc(10, 1, 7, 50), Utc(10, 1, 8, 0));
        await PlantMinutesAsync("AVBAD", "Q2", Utc(10, 1, 7, 50), Utc(10, 1, 8, 0));
        // Q1 stale (published 3 minutes ago), Q2 lagging (its live minute is 07:50), Q3 fresh but without queue minutes.
        snapshots.Fresh("AVBAD/Q1", Utc(10, 1, 7, 59), Utc(10, 1, 7, 57));
        snapshots.Fresh("AVBAD/Q2", Utc(10, 1, 7, 50), Utc(10, 1, 8, 0));
        snapshots.Fresh("AVBAD/Q3", Utc(10, 1, 7, 59), Utc(10, 1, 8, 0));

        await ledger.SiteAsync("AVBAD", Ct);

        (await RowsAsync("AVBAD")).Should().ContainSingle().Which.Should().Be(
            new Row(Utc(10, 1, 7, 57), "2026-10-01", "Operating", "Unavailable", "StaleZone,MissingMinute,StreamLag", 3, 1, 1, 1));

        // Redis unreachable: every zone counts as stale, the pass still commits and says so.
        snapshots.Fail = true;
        Host.Clock.Advance(TimeSpan.FromMinutes(1));
        var failed = await ledger.SiteAsync("AVBAD", Ct);
        failed.Unavailable.Should().Be(1);
        failed.RedisFailed.Should().BeTrue();
        (await RowsAsync("AVBAD"))[^1].Should().Be(new Row(Utc(10, 1, 7, 58), "2026-10-01", "Operating", "Unavailable", "StaleZone,MissingMinute", 3, 3, 1, 0));

        // Later in the same run, the other sites do not wait for Redis again: no read, every zone stale.
        snapshots.Fail = false;
        Host.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = snapshots.Reads;
        (await ledger.SiteAsync("AVBAD", redisDown: true, Ct)).Unavailable.Should().Be(1);
        snapshots.Reads.Should().Be(reads);
        (await RowsAsync("AVBAD"))[^1].Reasons.Should().Be("StaleZone,MissingMinute");
    }

    [Fact]
    public async Task Ledger_Should_CatchUpFromTheDatabaseAlone_When_TheJobWasDown()
    {
        await PlantSiteAsync("AVGAP", "AVG", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var snapshots = new FakeSnapshots();
        var ledger = await LedgerAsync(snapshots, new AvailabilitySettings { MaxMinutesPerRun = 60 });
        Host.Clock.Advance(TimeSpan.FromSeconds(30));
        await PlantMinutesAsync("AVGAP", "Q1", Utc(10, 1, 7, 0), Utc(10, 1, 11, 0));
        snapshots.Fresh("AVGAP/Q1", Utc(10, 1, 7, 59), Utc(10, 1, 8, 0));
        (await ledger.SiteAsync("AVGAP", Ct)).Available.Should().Be(1, "07:57 is decided live");

        // Down for two hours; one queue minute (08:30) was never written. Redis now shows a perfect live state.
        await ExecuteAsync("DELETE FROM queue_minute WHERE zone_key = 'AVGAP/Q1' AND minute_utc = '2026-10-01T08:30:00Z'");
        Host.Clock.Advance(TimeSpan.FromHours(2));
        snapshots.Fresh("AVGAP/Q1", Utc(10, 1, 9, 59), Utc(10, 1, 10, 0));

        // 10:00:30: latest decidable 09:57; live from 09:55. First pass: the live minutes 09:55 to 09:57 and the backlog's first 60 minutes.
        var first = await ledger.SiteAsync("AVGAP", Ct);
        first.Minutes.Should().Be(63);
        first.Available.Should().Be(3);
        first.CaughtUp.Should().Be(60);
        var rows = await RowsAsync("AVGAP");
        rows.Where(r => r.Minute >= Utc(10, 1, 9, 55)).Select(r => r.State).Should().Equal(["Available", "Available", "Available"]);
        rows.Single(r => r.Minute == Utc(10, 1, 8, 30)).Should().Be(new Row(Utc(10, 1, 8, 30), "2026-10-01", "Operating", "Unavailable", "MissingMinute,NotObservedLive", 1, 0, 1, 0));
        rows.Where(r => r.Minute > Utc(10, 1, 7, 57) && r.Minute < Utc(10, 1, 8, 58) && r.Minute != Utc(10, 1, 8, 30))
            .Should().OnlyContain(r => r.State == "Unobserved" && r.Reasons == "NotObservedLive", "never available from today's Redis state");

        // Later passes fill the rest of the backlog from the cursor; nothing is written twice.
        await ledger.SiteAsync("AVGAP", Ct);
        await ledger.SiteAsync("AVGAP", Ct);
        rows = await RowsAsync("AVGAP");
        rows.Select(r => r.Minute).Should().OnlyHaveUniqueItems();
        rows.Should().HaveCount(121, "07:57 to 09:57, every minute once");
        rows.Count(r => r.State == "Unobserved").Should().Be(116, "07:58 to 09:54 but 08:30");
        rows.Count(r => r.State == "Available").Should().Be(4);
        (await ledger.SiteAsync("AVGAP", Ct)).Minutes.Should().Be(0);
    }

    [Fact]
    public async Task Ledger_Should_SkipTheSite_When_AnotherReplicaHoldsIt()
    {
        await PlantSiteAsync("AVLOCK", "AVK", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var ledger = await LedgerAsync(new FakeSnapshots());
        await using var other = await ConnectAsync();
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(50, hashtext('AVLOCK'))", other, transaction))
            await take.ExecuteNonQueryAsync(Ct);

        (await ledger.SiteAsync("AVLOCK", Ct)).Should().BeNull();
        (await RowsAsync("AVLOCK")).Should().BeEmpty();

        await transaction.RollbackAsync(Ct);
        (await ledger.SiteAsync("AVLOCK", Ct)).Minutes.Should().Be(1);
    }

    [Fact]
    public async Task Ledger_Should_ApplyTheCalendar_When_HoursExceptionsAndMaintenanceAreRecorded()
    {
        await PlantSiteAsync("AVHRS", "AVH", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var admin = await Host.CreateUserAsync("it.av.hrs." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
        ISvcSiteCalendar Calendar(IServiceProvider s) => s.GetRequiredService<ISvcSiteCalendar>();
        // Recorded on 1 October: every day 06:00 to 22:00 Dubai from 2 October; 3 October closed; maintenance 4 October 05:00 to 06:00 UTC.
        await Host.AsCallerAsync(admin, s => Calendar(s).SetWeekAsync("AVHRS",
            new SetOperatingWeekRequest("2026-10-02", [.. WeeklyHours.Days.Select(d => new OperatingDayHoursRequest(d.ToString(), "06:00", "22:00"))]), Ct));
        await Host.AsCallerAsync(admin, s => Calendar(s).AddExceptionAsync("AVHRS", new AddOperatingExceptionRequest("2026-10-03", true, [], "Closed"), Ct));
        await Host.AsCallerAsync(admin, s => Calendar(s).AddMaintenanceAsync("AVHRS", new AddMaintenanceWindowRequest("2026-10-04T05:00:00Z", "2026-10-04T06:00:00Z", "Upgrade"), Ct));
        // A window recorded at or after its start never counts: the API refuses it and the table's check refuses a forged row
        // (Tables_Should_RefuseALateMaintenanceWindow_When_ItIsInsertedDirectly); the calendar's own guard is unit tested.
        var ledger = await LedgerAsync(new FakeSnapshots(), new AvailabilitySettings { MaxMinutesPerRun = 10_080 });
        await ExecuteAsync("INSERT INTO availability_cursor (site_code, next_minute_utc, updated_on) VALUES ('AVHRS', '2026-10-01T20:00:00Z', now())");
        Host.Clock.Advance(TimeSpan.FromDays(4));

        await ledger.SiteAsync("AVHRS", Ct);

        var days = (await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAvailability>().GetAsync("AVHRS", new AvailabilityCriteria("2026-10-02", "2026-10-04"), Ct))).Data;
        days.TimeZoneId.Should().Be("Asia/Dubai");
        days.Days.Select(d => (d.Date, d.Counts.RecordedMinutes, d.Counts.OperatingMinutes, d.Counts.MaintenanceMinutes, d.Counts.ClosedMinutes)).Should().Equal(
            (new DateOnly(2026, 10, 2), 1_440, 960, 0, 480),
            (new DateOnly(2026, 10, 3), 1_440, 0, 0, 1_440),
            (new DateOnly(2026, 10, 4), 1_440, 900, 60, 480));
        days.Total.OperatingMinutes.Should().Be(1_860);
        days.Total.AvailableMinutes.Should().Be(0, "no stream and no snapshots in this test");
        days.Total.Availability.Should().Be(0);
        days.Total.MaintenanceMinutes.Should().Be(60);
        days.Total.AvailabilityMaintenanceAsUnavailable.Should().Be(0, "the stricter figure, over 1,920 minutes");
        days.Weeks.Should().ContainSingle().Which.Days.Should().Be(3);
    }

    [Fact]
    public async Task Ledger_Should_GroupMinutesByTheLocalDay_When_TheSiteHasDaylightSaving()
    {
        // London falls back on 25 October 2026: that local day has 25 hours of minutes.
        await PlantSiteAsync("AVLON", "AVN", "Europe/London", Utc(9, 1, 0, 0), "Q1");
        var admin = await Host.CreateUserAsync("it.av.lon." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);
        await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteCalendar>().SetWeekAsync("AVLON",
            new SetOperatingWeekRequest("2026-10-20", [.. WeeklyHours.Days.Select(d => new OperatingDayHoursRequest(d.ToString(), "00:30", "03:00"))]), Ct));
        var ledger = await LedgerAsync(new FakeSnapshots(), new AvailabilitySettings { MaxMinutesPerRun = 10_080 });
        await ExecuteAsync("INSERT INTO availability_cursor (site_code, next_minute_utc, updated_on) VALUES ('AVLON', '2026-10-23T23:00:00Z', now())");
        Host.Clock.Advance(new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc) - Host.Clock.GetUtcNow().UtcDateTime);

        await ledger.SiteAsync("AVLON", Ct);

        var view = (await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAvailability>().GetAsync("AVLON", new AvailabilityCriteria("2026-10-24", "2026-10-26"), Ct))).Data;
        view.Days.Select(d => (d.Counts.RecordedMinutes, d.Counts.OperatingMinutes)).Should().Equal((1_440, 150), (1_500, 210), (1_440, 150));
        (await ScalarAsync<long>("SELECT count(*) FROM availability_minute WHERE site_code = 'AVLON' AND local_date = '2026-10-25'")).Should().Be(1_500);
    }

    [Fact]
    public async Task Ledger_Should_ListTheSitesWithProfiles_When_ARunStarts()
    {
        await PlantSiteAsync("AVRUN", "AVR", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var ledger = await LedgerAsync(new FakeSnapshots());

        (await ledger.SitesAsync(Ct)).Should().Contain("AVRUN");
        var run = await ledger.RunAsync(Ct);
        run.SitesFailed.Should().Be(0);
        (await RowsAsync("AVRUN")).Should().ContainSingle().Which.Reasons.Should().Be("StaleZone,MissingMinute");
    }

    [Fact]
    public async Task Ledger_Should_SkipTheSiteWithoutWritingAMinute_When_ItsTimeZoneIsUnknownOnTheHost()
    {
        await PlantSiteAsync("AVTZ", "AVZ", "Mars/Olympus_Mons", Utc(9, 1, 0, 0), "Q1");
        await PlantSiteAsync("AVUTC", "AVU", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        // A site without an airport is kept on UTC: its terminal is moved to no airport by deleting the airport row.
        await ExecuteAsync("UPDATE airport SET deleted_on = now() WHERE iata_code = 'AVU'");
        var logger = new WarningLog();
        var ledger = await LedgerAsync(new FakeSnapshots(), logger: logger);
        Host.Clock.Advance(TimeSpan.FromSeconds(30));

        var first = await ledger.SiteAsync("AVTZ", Ct);
        Host.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await ledger.SiteAsync("AVTZ", Ct);

        first.TimeZoneUnresolved.Should().BeTrue();
        second.TimeZoneUnresolved.Should().BeTrue();
        (first.Minutes + second.Minutes).Should().Be(0);
        (await RowsAsync("AVTZ")).Should().BeEmpty("nothing permanent is decided against the wrong local hours");
        (await ScalarAsync<long>("SELECT count(*) FROM availability_cursor WHERE site_code = 'AVTZ'")).Should().Be(0, "the cursor does not move");
        logger.Warnings.Should().ContainSingle("one warning per site, not one per run").Which.Should().Contain("AVTZ").And.NotContain("Mars");

        var utc = await ledger.SiteAsync("AVUTC", Ct);
        utc.TimeZoneUnresolved.Should().BeFalse();
        (await RowsAsync("AVUTC")).Should().ContainSingle().Which.LocalDate.Should().Be("2026-10-01");
        var run = await ledger.RunAsync(Ct);
        run.SitesSkipped.Should().BeGreaterThanOrEqualTo(1);
        run.SitesFailed.Should().Be(0);
    }

    #endregion

    #region Concurrency

    /// <summary>
    /// Holds a raw transaction that has inserted <paramref name="sql"/> (a calendar row of the same key) and the audit entry
    /// its request would write; runs <paramref name="call"/> until it waits on that transaction, commits it, and returns the
    /// call's result: the service's insert then fails on the unique index.
    /// </summary>
    private async Task<Fluentx.Result<SiteCalendarViewModel>> RaceAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, string site, string action,
        Func<Task<Fluentx.Result<SiteCalendarViewModel>>> call)
    {
        await using var connection = await ConnectAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // the statement is a literal of the caller
        await using (var insert = new NpgsqlCommand(sql, connection, transaction))
#pragma warning restore CA2100
        {
            insert.Parameters.AddWithValue("site", site);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using (var audit = new NpgsqlCommand("""
                         INSERT INTO audit_entry (id, occurred_on, action, target_type, target_name) VALUES (gen_random_uuid(), now(), @action, 'Race', @site)
                         """, connection, transaction))
        {
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("site", site);
            await audit.ExecuteNonQueryAsync(Ct);
        }

        var pending = call();
        await using var probe = await ConnectAsync();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && !pending.IsCompleted)
        {
            await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE @pid = ANY (pg_blocking_pids(pid))", probe);
            waiting.Parameters.AddWithValue("pid", connection.ProcessID);
            if ((long)(await waiting.ExecuteScalarAsync(Ct))! > 0)
                break;
            await Task.Delay(25, Ct);
        }

        pending.IsCompleted.Should().BeFalse("the service's insert waits for the concurrent row");
        await transaction.CommitAsync(Ct);
        return await pending;
    }

    [Fact]
    public async Task AddException_Should_AnswerTheDayIsTaken_When_AConcurrentRequestAddedItFirst()
    {
        await PlantSiteAsync("AVRACE", "AVA", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var admin = await Host.CreateUserAsync("it.av.race." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);

        var result = await RaceAsync("""
            INSERT INTO operating_day_exception (id, site_code, date, hours, reason, recorded_utc) VALUES (gen_random_uuid(), @site, '2026-10-05', '[]', 'First', now())
            """, "AVRACE", "SiteCalendar.ExceptionAdded",
            () => Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteCalendar>().AddExceptionAsync("AVRACE",
                new AddOperatingExceptionRequest("2026-10-05", true, [], "Second"), Ct)));

        result.ErrorMessages.Should().Equal(SiteCalendarErrors.ExceptionTaken);
        SiteCalendarErrors.Conflicts.Should().Contain(SiteCalendarErrors.ExceptionTaken, "a 409");
        (await ScalarAsync<long>("SELECT count(*) FROM operating_day_exception WHERE site_code = 'AVRACE'")).Should().Be(1);
        (await ScalarAsync<string>("SELECT reason FROM operating_day_exception WHERE site_code = 'AVRACE'")).Should().Be("First");
        (await ScalarAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name = 'AVRACE'")).Should().Be(1, "the winner's entry only");
    }

    [Fact]
    public async Task SetWeek_Should_AnswerAConflict_When_AConcurrentRequestSetTheSameVersionFirst()
    {
        await PlantSiteAsync("AVRACW", "AVW", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");
        var admin = await Host.CreateUserAsync("it.av.racw." + Guid.NewGuid().ToString("N")[..8], roles: [RoleCodes.SystemAdministrator], allSites: true);

        var result = await RaceAsync("""
            INSERT INTO operating_week (id, site_code, effective_from, hours, recorded_utc) VALUES (gen_random_uuid(), @site, '2026-10-05', '[]', now())
            """, "AVRACW", "SiteCalendar.WeekSet",
            () => Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteCalendar>().SetWeekAsync("AVRACW",
                new SetOperatingWeekRequest("2026-10-05", [new("Monday", "06:00", "22:00")]), Ct)));

        result.ErrorMessages.Should().Equal(SiteCalendarErrors.WeekConcurrent);
        SiteCalendarErrors.Conflicts.Should().Contain(SiteCalendarErrors.WeekConcurrent, "a 409");
        (await ScalarAsync<long>("SELECT count(*) FROM operating_week WHERE site_code = 'AVRACW'")).Should().Be(1);
        (await ScalarAsync<string>("SELECT hours FROM operating_week WHERE site_code = 'AVRACW'")).Should().Be("[]", "the first version stands");
        (await ScalarAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name = 'AVRACW'")).Should().Be(1, "the winner's entry only");
    }

    #endregion

    #region Privileges

    [Fact]
    public async Task Tables_Should_KeepTheLedgerWriteOnce_When_TheRuntimeRoleWrites()
    {
        await Host.DatabaseAsync();

        (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'availability_minute', 'INSERT')")).Should().BeTrue();
        (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'availability_minute', 'SELECT')")).Should().BeTrue();
        (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'availability_minute', 'UPDATE')")).Should().BeFalse();
        (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'availability_minute', 'DELETE')")).Should().BeFalse();
        (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'availability_minute', 'TRUNCATE')")).Should().BeFalse();
        foreach (var table in new[] { "operating_week", "operating_day_exception", "maintenance_window", "availability_cursor" })
        {
            (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', @t, 'UPDATE')", ("t", table))).Should().BeTrue(table);
            (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', @t, 'DELETE')", ("t", table))).Should().BeFalse(table);
        }

        (await ScalarAsync<long>("SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'availability_minute'")).Should().Be(1);
    }

    [Fact]
    public async Task Tables_Should_RefuseALateMaintenanceWindow_When_ItIsInsertedDirectly()
    {
        await PlantSiteAsync("AVLATE", "AVT", "Asia/Dubai", Utc(9, 1, 0, 0), "Q1");

        // Recorded at its start and after it: a check violation (23514), whoever writes the row.
        foreach (var recorded in new[] { "2026-10-04T07:00:00Z", "2026-10-04T07:30:00Z" })
        {
            var refused = await FluentActions.Awaiting(() => ExecuteAsync("""
                INSERT INTO maintenance_window (id, site_code, starts_utc, ends_utc, reason, recorded_utc)
                VALUES (gen_random_uuid(), 'AVLATE', '2026-10-04T07:00:00Z', '2026-10-04T08:00:00Z', 'Late', CAST(@recorded AS timestamptz))
                """, ("recorded", recorded))).Should().ThrowAsync<PostgresException>();
            refused.Which.SqlState.Should().Be("23514", recorded);
        }

        await ExecuteAsync("""
            INSERT INTO maintenance_window (id, site_code, starts_utc, ends_utc, reason, recorded_utc)
            VALUES (gen_random_uuid(), 'AVLATE', '2026-10-04T07:00:00Z', '2026-10-04T08:00:00Z', 'Announced', '2026-10-04T06:59:59Z')
            """);
        (await ScalarAsync<long>("SELECT count(*) FROM maintenance_window WHERE site_code = 'AVLATE'")).Should().Be(1);
    }

    [Fact]
    public async Task Tables_Should_RefuseRowsTheLedgerCouldNotWrite_When_ChecksAreBroken()
    {
        await Host.DatabaseAsync();
        var refused = new[]
        {
            // Available with a reason, a reason not in the list, a minute with seconds, more stale zones than expected.
            "INSERT INTO availability_minute VALUES ('DMX', '2026-10-01T08:00:00Z', '2026-10-01', 'Operating', 'Available', ARRAY['StaleZone'], 1, 1, 0, 0, 1, 1, now())",
            "INSERT INTO availability_minute VALUES ('DMX', '2026-10-01T08:00:00Z', '2026-10-01', 'Operating', 'Unavailable', ARRAY['Bogus'], 1, 0, 0, 0, 1, 1, now())",
            "INSERT INTO availability_minute VALUES ('DMX', '2026-10-01T08:00:30Z', '2026-10-01', 'Operating', 'Available', '{}', 1, 0, 0, 0, 1, 1, now())",
            "INSERT INTO availability_minute VALUES ('DMX', '2026-10-01T08:00:00Z', '2026-10-01', 'Operating', 'Unavailable', ARRAY['StaleZone'], 1, 2, 0, 0, 1, 1, now())",
            "INSERT INTO availability_minute VALUES ('DMX', '2026-10-01T08:00:00Z', '2026-10-01', 'Open', 'Available', '{}', 1, 0, 0, 0, 1, 1, now())"
        };
        foreach (var sql in refused)
        {
            await using var connection = await ConnectAsync();
#pragma warning disable CA2100 // the statements are literals above
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            await FluentActions.Awaiting(() => command.ExecuteNonQueryAsync(Ct)).Should().ThrowAsync<PostgresException>(sql);
        }
    }

    #endregion
}
