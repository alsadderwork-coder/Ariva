using Ariva.Core;
using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Topology;
using Ariva.Core.Staffing;
using Ariva.Di.Extensions;
using Ariva.Infra.Settings;
using Ariva.Infra.Staffing;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Staffing;

/// <summary>
/// ARV-181a against TimescaleDB with script 0053: a planted site with an arrivals hall (Immigration checkpoint, VIS and CIT
/// lane queues) and a departures hall (Emigration, left out), its last hour of queue and desk minutes, an AODB arrival on
/// block with seats and an SSIM arrival without passengers. One run writes each arrivals lane's 13 intervals once (the
/// numbers the formulas give, no guess after the flight without passengers), a second run of the minute writes nothing, a
/// replica holding the site's lock makes the run skip, a site beyond the desk-minute, lane-desk or lane bound is refused with
/// nothing written while a site at the bound is computed, a lane with more desks than the bound is refused on its own while
/// the site's other lanes are written, one site that fails does not stop the others, the runtime role can insert and read
/// but never change or delete a row and the table's checks refuse what the engine never writes; the API service answers
/// border roles only, within their sites (not found before the lane bound's 409), says Stale for an old minute and does not
/// show a row that fails its bounds as read (CWE-501).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StaffingTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, database: TestDatabase.Staffing, configure: services =>
    {
        services.AddArivaStaffingJob(Empty);
        services.AddArivaStaffingRecommendations(Empty);
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    #region Helpers

    private DateTime Now => Host.Clock.GetUtcNow().UtcDateTime;

    private DateTime Minute => new(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private async Task<NpgsqlConnection> ConnectAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        return connection;
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

    /// <summary>
    /// A site with an arrivals level (checkpoint IMM, Immigration: desks AR-01 to AR-04 for VIS, AR-05 for VIS out of service,
    /// AC-01 for CIT and RES; queue zones A-VIS and A-CIT) and a departures level (checkpoint EMI, Emigration: DP-01 for VIS;
    /// queue zone D-VIS), published.
    /// </summary>
    private async Task PlantSiteAsync(string site, string iata)
    {
        await Host.CreateUserAsync("it.staff.probe." + Guid.NewGuid().ToString("N")[..8]);
        await ExecuteAsync("""
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site);
            INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, @iata, 'UTC');
            INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', @site FROM airport a WHERE a.iata_code = @iata;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
            SELECT gen_random_uuid(), t.id, @site, l.code, l.code, l.floor, 100, 100 FROM terminal t, (VALUES ('ARR', 0), ('DEP', 1)) l(code, floor) WHERE t.site_code = @site;
            INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
            SELECT gen_random_uuid(), l.id, @site, c.code, c.code, c.kind FROM level l
              JOIN (VALUES ('ARR', 'IMM', 'Immigration'), ('DEP', 'EMI', 'Emigration')) c(level, code, kind) ON c.level = l.code WHERE l.site_code = @site;
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes, in_service)
            SELECT gen_random_uuid(), c.id, @site, d.code, d.code, 'Desk', d.lanes, d.service FROM checkpoint c
              JOIN (VALUES ('IMM', 'AR-01', 'VIS', true), ('IMM', 'AR-02', 'VIS', true), ('IMM', 'AR-03', 'VIS', true), ('IMM', 'AR-04', 'VIS', true),
                           ('IMM', 'AR-05', 'VIS', false), ('IMM', 'AC-01', 'CIT,RES', true), ('EMI', 'DP-01', 'VIS', true)) d(checkpoint, code, lanes, service)
                ON d.checkpoint = c.code WHERE c.site_code = @site;
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (gen_random_uuid(), @site, 'Profile', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, lane_category)
            SELECT gen_random_uuid(), p.id, z.name, 'Queue', l.id, '1.000 1.000,3.000 1.000,3.000 2.000,1.000 2.000', z.lane
              FROM zone_profile p, level l, (VALUES ('A-VIS', 'ARR', 'VIS'), ('A-CIT', 'ARR', 'CIT'), ('D-VIS', 'DEP', 'VIS')) z(name, level, lane)
             WHERE p.site_code = @site AND l.site_code = @site AND l.code = z.level;
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('a', 64), published_on = @published, published_by = 'it'
             WHERE site_code = @site AND status = 'Draft';
            """, ("site", site), ("iata", iata), ("published", Now.AddDays(-1)));
    }

    /// <summary>
    /// The last hour before the current minute: A-VIS with 10 waiting and 2 exits a minute, its desks AR-01 to AR-04 serving
    /// every minute (r = 0.5); D-VIS and DP-01 the same (the departures hall is left out); an arrival on block 5 minutes before
    /// the current minute with 125 seats (100 passengers at the load factor 0.8, the default mix: 35 VIS, 22 e-gate eligible) and an SSIM
    /// arrival scheduled an hour on with no passenger figure.
    /// </summary>
    private async Task PlantHourAsync(string site)
    {
        var from = Minute.AddMinutes(-60);
        var to = Minute.AddMinutes(-1);
        await ExecuteAsync("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, exits, queue_length, length_degraded, updated_on)
            SELECT k, m, 1, 2, 10, false, now() FROM generate_series(@from, @to, INTERVAL '1 minute') m, unnest(ARRAY[@site || '/A-VIS', @site || '/D-VIS']) k;
            INSERT INTO desk_minute (desk_code, lane, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, transactions,
                                     sensor_derived_seconds, present_seconds, degraded, updated_on)
            SELECT @site || '/' || d, 'VIS', m, 0, 0, 60, 0, 0, 1, 0, 0, false, now()
              FROM generate_series(@from, @to, INTERVAL '1 minute') m, unnest(ARRAY['IMM/AR-01', 'IMM/AR-02', 'IMM/AR-03', 'IMM/AR-04', 'EMI/DP-01']) d;
            INSERT INTO flight_leg (id, site_code, flight_key, direction, carrier, number, scheduled_utc, seats, schedule_source_utc, on_block_utc, on_block_source_utc,
                                    cancelled, diverted, status, feed, schedule_feed, updated_utc)
            VALUES (gen_random_uuid(), @site, 'QR900-ON', 'Arrival', 'QR', '900', @onBlock, 125, @onBlock, @onBlock, @onBlock, false, false, 'OnBlock', 'aodb', 'aodb', @onBlock);
            INSERT INTO flight_leg (id, site_code, flight_key, direction, carrier, number, scheduled_utc, schedule_source_utc, cancelled, diverted, status, feed,
                                    schedule_feed, schedule_fallback, updated_utc)
            VALUES (gen_random_uuid(), @site, 'XY123-SSIM', 'Arrival', 'XY', '123', @ssim, @onBlock, false, false, 'Scheduled', 'ssim', 'ssim', true, @onBlock);
            """, ("site", site), ("from", from), ("to", to), ("onBlock", Minute.AddMinutes(-5)), ("ssim", Minute.AddMinutes(60)));
    }

    /// <summary>
    /// A site whose arrivals level (checkpoint IMM, Immigration) has <paramref name="lanes"/> VIS queue zones Q-001 onwards
    /// and <paramref name="desks"/> VIS desks W-0001 onwards, and, when <paramref name="cit"/>, a CIT queue zone A-CIT with
    /// one CIT desk C-01, and, when <paramref name="unkeyed"/>, a VIS queue zone named with 200 U's (its key, the site code, a
    /// slash and the name, is longer than a message key: the profile editor refuses such a name, so it is planted);
    /// published. Planted with generate_series.
    /// </summary>
    private async Task PlantLanesAsync(string site, string iata, int lanes, int desks, bool cit = false, bool unkeyed = false)
    {
        await Host.CreateUserAsync("it.staff.probe." + Guid.NewGuid().ToString("N")[..8]);
        await ExecuteAsync("""
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site);
            INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, @iata, 'UTC');
            INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', @site FROM airport a WHERE a.iata_code = @iata;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
            SELECT gen_random_uuid(), t.id, @site, 'ARR', 'ARR', 0, 100, 100 FROM terminal t WHERE t.site_code = @site;
            INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
            SELECT gen_random_uuid(), l.id, @site, 'IMM', 'IMM', 'Immigration' FROM level l WHERE l.site_code = @site;
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes, in_service)
            SELECT gen_random_uuid(), c.id, @site, 'W-' || lpad(i::text, 4, '0'), 'W-' || lpad(i::text, 4, '0'), 'Desk', 'VIS', true
              FROM checkpoint c, generate_series(1, @desks) i WHERE c.site_code = @site;
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes, in_service)
            SELECT gen_random_uuid(), c.id, @site, 'C-01', 'C-01', 'Desk', 'CIT', true FROM checkpoint c WHERE c.site_code = @site AND @cit;
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (gen_random_uuid(), @site, 'Profile', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, lane_category)
            SELECT gen_random_uuid(), p.id, 'Q-' || lpad(i::text, 3, '0'), 'Queue', l.id, '1.000 1.000,3.000 1.000,3.000 2.000,1.000 2.000', 'VIS'
              FROM zone_profile p, level l, generate_series(1, @lanes) i WHERE p.site_code = @site AND l.site_code = @site;
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, lane_category)
            SELECT gen_random_uuid(), p.id, 'A-CIT', 'Queue', l.id, '1.000 1.000,3.000 1.000,3.000 2.000,1.000 2.000', 'CIT'
              FROM zone_profile p, level l WHERE p.site_code = @site AND l.site_code = @site AND @cit;
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, lane_category)
            SELECT gen_random_uuid(), p.id, repeat('U', 200), 'Queue', l.id, '1.000 1.000,3.000 1.000,3.000 2.000,1.000 2.000', 'VIS'
              FROM zone_profile p, level l WHERE p.site_code = @site AND l.site_code = @site AND @unkeyed;
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('a', 64), published_on = @published, published_by = 'it'
             WHERE site_code = @site AND status = 'Draft';
            """, ("site", site), ("iata", iata), ("lanes", lanes), ("desks", desks), ("cit", cit), ("unkeyed", unkeyed), ("published", Now.AddDays(-1)));
    }

    private Task<long> CountAsync(string zonePrefix) =>
        ScalarAsync<long>("SELECT count(*) FROM lane_staffing WHERE zone_key LIKE @prefix || '/%'", ("prefix", zonePrefix));

    private StaffingRecommendations Runner(StaffingSettings settings = null, TimeProvider clock = null) =>
        new(Host.Provider.GetRequiredService<IServiceScopeFactory>(), Host.Provider.GetRequiredService<DatabaseSettings>(), clock ?? Host.Clock, settings ?? new StaffingSettings(),
            new ArrivalWaveSettings(), new EgateCouplingSettings(), NullLogger<StaffingRecommendations>.Instance);

    [Fact]
    public async Task Run_Should_TimeItselfWithTheHostsClock_When_ItRuns()
    {
        // ARV-124a: the run's duration (the job warns when it takes its whole minute) is the host clock's, from before the sites
        // are read to after the last site: a clock whose timestamps step 250 ms at each reading gives exactly one step. The host
        // starts with its first account, so the test does not depend on another having run first.
        await Host.CreateUserAsync("it.staff.probe." + Guid.NewGuid().ToString("N")[..8]);
        var run = await Runner(clock: new SteppingTimestamps(Host.Clock, TimeSpan.FromMilliseconds(250))).RunAsync(Ct);

        run.Elapsed.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    /// <summary>The host's clock, with timestamps that move <paramref name="step"/> at every reading.</summary>
    private sealed class SteppingTimestamps(TimeProvider clock, TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);
    }

    private sealed record Row(int Index, string Lane, DateTime Start, int Minutes, double? Arrivals, int? Recommended, string Bound, bool AtLimit, int? Shortfall,
        double? WaitRecommended, double? WaitOpen, double? Backlog, string Gap, string LaneGap, double? QueueNow, int? OpenNow, int Desks, double? Rate, string Source);

    private async Task<List<Row>> RowsAsync(string zoneKey, DateTime minute)
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("""
            SELECT interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall, wait_recommended, wait_open, backlog_end, gap, lane_gap,
                   queue_now, open_now, desks, throughput_per_desk, throughput_source
              FROM lane_staffing WHERE zone_key = @key AND minute_utc = @minute ORDER BY interval_index
            """, connection);
        command.Parameters.AddWithValue("key", zoneKey);
        command.Parameters.AddWithValue("minute", minute);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Row>();
        while (await reader.ReadAsync(Ct))
            rows.Add(new Row(reader.GetInt16(0), reader.GetString(1), reader.GetFieldValue<DateTime>(2).ToUniversalTime(), reader.GetInt16(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetInt16(5), reader.GetString(6), reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetDouble(9), reader.IsDBNull(10) ? null : reader.GetDouble(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11), reader.GetString(12), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetDouble(14),
                reader.IsDBNull(15) ? null : reader.GetInt16(15), reader.GetInt16(16), reader.IsDBNull(17) ? null : reader.GetDouble(17),
                reader.IsDBNull(18) ? null : reader.GetString(18)));
        return rows;
    }

    private async Task<Guid> UserAsync(string role, string site = null)
    {
        var user = await Host.CreateUserAsync("it.staff." + Guid.NewGuid().ToString("N")[..10], roles: [role]);
        if (site is null)
            await Host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", user);
        else
            await ExecuteAsync("INSERT INTO user_site (id, user_id, site_code) VALUES (gen_random_uuid(), @user, @site)", ("user", user), ("site", site));
        return user;
    }

    private Task<Fluentx.Result<StaffingRecommendationViewModel>> GetAsync(string site, Guid? caller) =>
        Host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcStaffingRecommendations>().GetAsync(site, Ct));

    #endregion

    [Fact]
    public async Task Run_Should_WriteEachArrivalsLanesThirteenIntervalsOnce_When_TheMinuteIsComputed()
    {
        await PlantSiteAsync("STA", "XSA");
        await PlantHourAsync("STA");
        Host.Clock.Advance(TimeSpan.FromSeconds(45));
        var runner = Runner();

        var pass = await runner.SiteAsync("STA", Ct);

        pass.Should().Be(new StaffingSitePass(2, 26), "A-VIS and A-CIT, 13 intervals each; the departures hall's D-VIS is left out");
        (await runner.SiteAsync("STA", Ct)).Should().Be(new StaffingSitePass(2, 0), "the first computation of a minute stands");
        (await ScalarAsync<long>("SELECT count(*) FROM lane_staffing WHERE zone_key = 'STA/D-VIS'")).Should().Be(0);
        (await runner.SitesAsync(Ct)).Should().Contain("STA");

        var vis = await RowsAsync("STA/A-VIS", Minute);
        vis.Should().HaveCount(13);
        vis.Select(r => r.Start).Should().Equal(Enumerable.Range(0, 13).Select(k => Minute.AddMinutes(15 * k)), "08:00 is on a quarter hour: a full first interval");
        vis.Should().OnlyContain(r => r.Lane == "VIS" && r.QueueNow == 10 && r.OpenNow == 4 && r.Desks == 4 && r.LaneGap == "None" && r.Source == "Measured" &&
            Math.Abs(r.Rate.Value - 0.5) < 1e-12, "AR-05 is out of service and DP-01 is in the departures hall");
        // Interval 0: 35 VIS x 0.82 of the spread (08:06 to 08:14) + 0.07 x 22 e-gate eligible x 0.74 a minute earlier = 29.8396.
        vis[0].Arrivals.Should().BeApproximately(29.8396, 1e-6);
        vis[0].Should().Match<Row>(r => r.Recommended == 4 && r.AtLimit && r.Shortfall == 1 && r.Bound == "Busy" && r.Gap == "None");
        vis[0].Backlog.Should().BeApproximately(9.8396, 1e-6);
        vis[0].WaitRecommended.Should().BeApproximately(5, 1e-9);
        vis[0].WaitOpen.Should().BeApproximately(5, 1e-9);
        vis[1].Arrivals.Should().BeApproximately(6.7004, 1e-6);
        vis[1].Should().Match<Row>(r => r.Recommended == 2 && !r.AtLimit && r.Bound == "Both");
        // The SSIM arrival (on block 09:00, hall 09:11 to 09:22) has no passenger figure: no guess there or after.
        vis.Select(r => r.Gap).Should().Equal(["None", "None", "None", "None", "FlightWithoutPassengers", "FlightWithoutPassengers",
            .. Enumerable.Repeat("BacklogUnknown", 7)]);
        vis.Skip(4).Should().OnlyContain(r => r.Recommended == null && r.WaitRecommended == null);

        var cit = await RowsAsync("STA/A-CIT", Minute);
        cit.Should().HaveCount(13).And.OnlyContain(r => r.LaneGap == "NoThroughput" && r.Recommended == null && r.Desks == 1 && r.Rate == null,
            "AC-01 reports nothing and no fallback is configured");
        cit.Select(r => r.Gap).Should().Equal([.. Enumerable.Repeat("NoThroughput", 4), "FlightWithoutPassengers", "FlightWithoutPassengers",
            .. Enumerable.Repeat("NoThroughput", 7)]);

        Host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await runner.RunAsync(Ct)).Should().Match<StaffingRun>(r => r.Rows >= 26 && r.SitesFailed == 0);
        (await RowsAsync("STA/A-VIS", Minute)).Should().HaveCount(13);
    }

    [Fact]
    public async Task Run_Should_SkipTheSite_When_AnotherReplicaHoldsItsLock()
    {
        await PlantSiteAsync("STB", "XSB");
        await using var other = await ConnectAsync();
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(51, hashtext('STB'))", other, transaction))
            await take.ExecuteNonQueryAsync(Ct);

        (await Runner().SiteAsync("STB", Ct)).Should().BeNull();
        (await ScalarAsync<long>("SELECT count(*) FROM lane_staffing WHERE zone_key LIKE 'STB/%'")).Should().Be(0);
    }

    [Fact]
    public async Task Run_Should_RefuseTheSiteAndWriteNothing_When_ItHasMoreDeskMinutesThanTheBound()
    {
        // CWE-400, CWE-120: refused, never cut short (five desks over an hour is 300 rows, above a bound of 100).
        await PlantSiteAsync("STC", "XSC");
        await PlantHourAsync("STC");
        Host.Clock.Advance(TimeSpan.FromSeconds(45));

        var pass = await Runner(new StaffingSettings { MaxDeskMinuteRows = 100 }).SiteAsync("STC", Ct);

        pass.Should().Be(new StaffingSitePass(0, 0, StaffingInputs.TooManyDeskMinutes));
        (await ScalarAsync<long>("SELECT count(*) FROM lane_staffing WHERE zone_key LIKE 'STC/%'")).Should().Be(0);
    }

    [Fact]
    public async Task Table_Should_KeepTheRowsWriteOnceAndRefuseWhatTheEngineNeverWrites_When_Written()
    {
        await Host.DatabaseAsync();
        foreach (var privilege in new[] { "INSERT", "SELECT" })
            (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'lane_staffing', @p)", ("p", privilege))).Should().BeTrue(privilege);
        foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            (await ScalarAsync<bool>("SELECT has_table_privilege('ariva_runtime', 'lane_staffing', @p)", ("p", privilege))).Should().BeFalse(privilege);
        (await ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM timescaledb_information.jobs WHERE hypertable_name = 'lane_staffing' AND proc_name = 'policy_retention')"))
            .Should().BeTrue("kept 90 days");

        async Task<string> Refused(short? recommended, short desks, string gap, bool atLimit, short index = 0)
        {
            try
            {
                await using var connection = await ConnectAsync();
                await using var command = new NpgsqlCommand("""
                    INSERT INTO lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, recommended, bound, at_limit, no_service_open, gap, lane_gap,
                                               open_now_degraded, desks, target_minutes, busy_cap, computed_utc)
                    VALUES ('ZZ1/A-VIS', '2026-10-01T08:00:00Z', @index, 'VIS', '2026-10-01T08:00:00Z', 15, @recommended, 'Wait', @atLimit, false, @gap, 'None', false,
                            @desks, 10, 0.85, '2026-10-01T08:00:45Z')
                    """, connection);
                command.Parameters.Add(new NpgsqlParameter("index", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = index });
                command.Parameters.Add(new NpgsqlParameter("recommended", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = recommended is { } r ? r : DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("desks", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = desks });
                command.Parameters.AddWithValue("gap", gap);
                command.Parameters.AddWithValue("atLimit", atLimit);
                await command.ExecuteNonQueryAsync(Ct);
                return null;
            }
            catch (PostgresException e)
            {
                return e.SqlState;
            }
        }

        (await Refused(5, 4, "None", false)).Should().Be("23514", "more desks recommended than the lane has");
        (await Refused(null, 4, "None", false)).Should().Be("23514", "an interval without a gap has a recommendation");
        (await Refused(3, 4, "None", true)).Should().Be("23514", "at the limit the recommendation is the lane's desks");
        (await Refused(3, 4, "Guess", false)).Should().Be("23514", "only Ariva's reasons");
        (await Refused(4, 4, "None", true)).Should().BeNull();
        (await Refused(null, 4, "ArrivalsOutOfRange", false, index: 1)).Should().BeNull("the engine's reason for arrivals out of range");
    }

    [Fact]
    public async Task Service_Should_AnswerBorderRolesWithinTheirSitesOnly_AndSayStale_When_TheMinuteIsOld()
    {
        await PlantSiteAsync("STD", "XSD");
        await PlantSiteAsync("STE", "XSE");
        await PlantHourAsync("STD");
        Host.Clock.Advance(TimeSpan.FromSeconds(45));
        await Runner().SiteAsync("STD", Ct);
        var supervisor = await UserAsync(RoleCodes.BorderShiftSupervisor, "STD");
        var administrator = await UserAsync(RoleCodes.SystemAdministrator);
        var dutyManager = await UserAsync(RoleCodes.TerminalDutyManager, "STD");
        var otherSupervisor = await UserAsync(RoleCodes.BorderShiftSupervisor, "STE");

        var answer = (await GetAsync("STD", supervisor)).Data;

        answer.Should().NotBeNull();
        answer.Indicative.Should().BeTrue();
        answer.IntervalMinutes.Should().Be(15);
        answer.Lanes.Select(l => l.Zone).Should().Equal("A-CIT", "A-VIS");
        var vis = answer.Lanes.Single(l => l.Zone == "A-VIS");
        vis.Should().Match<LaneStaffingViewModel>(l => l.ZoneKey == "STD/A-VIS" && l.Lane == "VIS" && l.Gap == "None" && l.OpenNow == 4 && l.Desks == 4 &&
            l.QueueNow == 10 && l.CycleMinutes == 2 && l.ThroughputSource == "Measured" && l.TargetMinutes == 10 && l.BusyCap == 0.85 && l.MinuteUtc == Minute);
        vis.Intervals.Should().HaveCount(13);
        vis.Intervals[0].Should().Match<StaffingIntervalViewModel>(i => i.Recommended == 4 && i.AtLimit && i.ShortfallDesks == 1 && i.Bound == "Busy" &&
            i.Arrivals == 29.8 && i.WaitRecommendedMinutes == 5);
        answer.Lanes.Single(l => l.Zone == "A-CIT").Gap.Should().Be("NoThroughput");
        System.Text.Json.JsonSerializer.Serialize(answer).Should().NotContain("AR-").And.NotContain("AC-").And.NotContain("IMM/");
        (await GetAsync("STD", administrator)).Data.Lanes.Should().HaveCount(2);

        // Not border, another site's supervisor, an unknown site, no caller: not found, as if the site did not exist.
        foreach (var (site, caller) in new (string, Guid?)[] { ("STD", dutyManager), ("STD", otherSupervisor), ("NOPE", administrator), ("STD", null), ("st d", administrator) })
            (await GetAsync(site, caller)).ErrorMessages.Should().Equal([TopologyErrors.NotFound], $"{site} as {caller}");

        // A row changed outside Ariva so that the lane's minute no longer holds together is not shown (CWE-501).
        await ExecuteAsync("UPDATE lane_staffing SET lane = 'CIT' WHERE zone_key = 'STD/A-VIS' AND interval_index = 5");
        (await GetAsync("STD", supervisor)).Data.Lanes.Single(l => l.Zone == "A-VIS").Should().Match<LaneStaffingViewModel>(l => l.Gap == StaffingErrors.Stale &&
            l.Intervals.Count == 0 && l.OpenNow == null);

        Host.Clock.Advance(TimeSpan.FromMinutes(4));
        (await GetAsync("STD", supervisor)).Data.Lanes.Should().OnlyContain(l => l.Gap == StaffingErrors.Stale && l.Intervals.Count == 0,
            "the latest minute is more than 3 minutes old");
    }

    [Fact]
    public async Task ApiAndRun_Should_RefuseAProfileWithMoreLanesThanTheBound_And_AnswerOneAtTheBound()
    {
        // CWE-120, CWE-400: 101 arrivals-hall lanes are refused, never cut short; 100 are answered. The caller is checked
        // first: outside the site or without the permission it is still not found (the 404 comes before the 409).
        await PlantLanesAsync("STF", "XSF", 101, 0);
        await PlantLanesAsync("STG", "XSG", 100, 0);
        var supervisor = await UserAsync(RoleCodes.BorderShiftSupervisor, "STF");
        var dutyManager = await UserAsync(RoleCodes.TerminalDutyManager, "STF");
        var otherSupervisor = await UserAsync(RoleCodes.BorderShiftSupervisor, "STG");
        Host.Clock.Advance(TimeSpan.FromSeconds(45));

        (await GetAsync("STF", supervisor)).ErrorMessages.Should().Equal([StaffingErrors.TooManyLanes]);
        (await GetAsync("STF", otherSupervisor)).ErrorMessages.Should().Equal([TopologyErrors.NotFound], "outside the caller's sites");
        (await GetAsync("STF", dutyManager)).ErrorMessages.Should().Equal([TopologyErrors.NotFound], "without ArrivalWaveLanes.View");
        (await GetAsync("STF", null)).ErrorMessages.Should().Equal([TopologyErrors.NotFound], "no caller");

        var runner = Runner();
        (await runner.SiteAsync("STF", Ct)).Should().Be(new StaffingSitePass(0, 0, StaffingInputs.TooManyLanes));
        (await CountAsync("STF")).Should().Be(0, "refused with nothing written");

        (await runner.SiteAsync("STG", Ct)).Should().Be(new StaffingSitePass(100, 1_300));
        var answer = (await GetAsync("STG", otherSupervisor)).Data;
        answer.Lanes.Should().HaveCount(100);
        answer.Lanes.Should().OnlyContain(l => l.Gap == nameof(StaffingGap.NoDesks) && l.Intervals.Count == 13 && l.Desks == 0, "no desk serves the planted lanes");
    }

    [Fact]
    public async Task Run_Should_RefuseTheSite_When_ItHasMoreLaneDesksThanTheBound()
    {
        // CWE-400, CWE-120: STI has 6 lane and desk pairs (A-VIS with AR-01 to AR-05, A-CIT with AC-01): a bound of 5 refuses
        // the site with nothing written; a bound of 6 takes it.
        await PlantSiteAsync("STI", "XSI");
        Host.Clock.Advance(TimeSpan.FromSeconds(45));

        (await Runner(new StaffingSettings { MaxLaneDeskRows = 5 }).SiteAsync("STI", Ct)).Should().Be(new StaffingSitePass(0, 0, StaffingInputs.TooManyDeskRows));
        (await CountAsync("STI")).Should().Be(0);

        (await Runner(new StaffingSettings { MaxLaneDeskRows = 6 }).SiteAsync("STI", Ct)).Should().Be(new StaffingSitePass(2, 26));
    }

    [Fact]
    public async Task Run_Should_RefuseOnlyTheLaneWithMoreDesksThanTheBound_When_TheSitesOtherLanesAreWithinIt()
    {
        // CWE-120: Q-001 has 1,001 VIS desks, above the 1,000 a lane takes: it has no minute; A-CIT is still written.
        await PlantLanesAsync("STJ", "XSJ", 1, 1_001, cit: true);
        Host.Clock.Advance(TimeSpan.FromSeconds(45));

        var pass = await Runner().SiteAsync("STJ", Ct);

        pass.Should().Be(new StaffingSitePass(1, 13, null, 1));
        (await CountAsync("STJ")).Should().Be(13);
        (await RowsAsync("STJ/A-CIT", Minute)).Should().HaveCount(13).And.OnlyContain(r => r.Desks == 1);
        (await RowsAsync("STJ/Q-001", Minute)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Should_KeepAZoneWhoseKeyDoesNotFitInTheSharing_AndWriteNothingForIt_When_ItsNameIsTooLong()
    {
        // ARV-185 (CWE-501, CWE-120): STM's VIS queue Q-001 and a VIS queue whose key does not fit share the lane: the read
        // keeps the second, without queue minutes and not taken, counted apart from the desk bound; Q-001 is written with half
        // of VIS's arrivals. STN's only queue does not fit: nothing is read from queue_minute (no key to read), nothing is written.
        await PlantLanesAsync("STM", "XSM", 1, 2, unkeyed: true);
        await PlantLanesAsync("STN", "XSN", 0, 1, unkeyed: true);
        await ExecuteAsync("""
            INSERT INTO flight_leg (id, site_code, flight_key, direction, carrier, number, scheduled_utc, seats, schedule_source_utc, on_block_utc, on_block_source_utc,
                                    cancelled, diverted, status, feed, schedule_feed, updated_utc)
            VALUES (gen_random_uuid(), 'STM', 'QR900-ON', 'Arrival', 'QR', '900', @onBlock, 125, @onBlock, @onBlock, @onBlock, false, false, 'OnBlock', 'aodb', 'aodb', @onBlock);
            """, ("onBlock", Minute.AddMinutes(-5)));
        Host.Clock.Advance(TimeSpan.FromSeconds(45));
        var settings = new StaffingSettings();

        var read = await Host.AsCallerAsync(null, s => s.GetRequiredService<StaffingInputs>().ReadAsync("STM", Now, settings, Ct));

        read.Should().Match<StaffingRead>(r => r.Refused == null && r.RefusedLanes == 0 && r.UnkeyedLanes == 1);
        var unkeyed = read.Input.Lanes.Single(l => l.Zone.Length == 200);
        unkeyed.Should().Match<StaffingLaneInput>(l => !l.KeyFits && l.Lane == "VIS" && l.QueueMinutes.Count == 0 && l.DeskKeys.Count == 2);
        SiteStaffing.Takes(unkeyed).Should().BeFalse();
        var shared = SiteStaffing.Compute(read.Input, settings, new ArrivalWaveSettings(), new EgateCouplingSettings()).Single();
        var alone = SiteStaffing.Compute(read.Input with { Lanes = [.. read.Input.Lanes.Where(l => l.KeyFits)] }, settings, new ArrivalWaveSettings(),
            new EgateCouplingSettings()).Single();
        shared.Zone.Should().Be("Q-001");
        alone.Staffing.Intervals[0].Arrivals.Should().BePositive();
        shared.Staffing.Intervals[0].Arrivals.Should().BeApproximately(alone.Staffing.Intervals[0].Arrivals.Value / 2, 1e-9, "shared with the zone that does not fit");

        (await Runner().SiteAsync("STM", Ct)).Should().Be(new StaffingSitePass(1, 13, null, 1));
        (await RowsAsync("STM/Q-001", Minute)).Should().HaveCount(13);
        (await CountAsync("STM")).Should().Be(13);

        var only = await Host.AsCallerAsync(null, s => s.GetRequiredService<StaffingInputs>().ReadAsync("STN", Now, settings, Ct));
        only.Should().Match<StaffingRead>(r => r.Refused == null && r.RefusedLanes == 0 && r.UnkeyedLanes == 1 && r.Input.Lanes.Count == 1);
        (await Runner().SiteAsync("STN", Ct)).Should().Be(new StaffingSitePass(0, 0, null, 1));
        (await CountAsync("STN")).Should().Be(0);
    }

    [Fact]
    public async Task Run_Should_GoOnWithTheOtherSites_When_OneSiteFails()
    {
        // A planted trigger makes every insert of STK fail: the run counts it failed and still writes STL.
        await PlantSiteAsync("STK", "XSK");
        await PlantSiteAsync("STL", "XSL");
        Host.Clock.Advance(TimeSpan.FromSeconds(45));
        await ExecuteAsync("""
            CREATE OR REPLACE FUNCTION it_staffing_fail() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.zone_key LIKE 'STK/%' THEN
                    RAISE EXCEPTION 'planted failure';
                END IF;
                RETURN NEW;
            END
            $$;
            CREATE TRIGGER it_staffing_fail BEFORE INSERT ON lane_staffing FOR EACH ROW EXECUTE FUNCTION it_staffing_fail();
            """);
        StaffingRun run;
        try
        {
            run = await Runner().RunAsync(Ct);
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER IF EXISTS it_staffing_fail ON lane_staffing; DROP FUNCTION IF EXISTS it_staffing_fail();");
        }

        run.SitesFailed.Should().Be(1);
        (await CountAsync("STK")).Should().Be(0);
        (await CountAsync("STL")).Should().Be(26);
    }
}
