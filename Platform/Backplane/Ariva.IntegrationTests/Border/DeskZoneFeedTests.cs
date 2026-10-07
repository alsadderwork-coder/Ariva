using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.IntegrationTests.Border;

/// <summary>
/// ARV-116 on PostgreSQL: the desk feed takes the staff and service zone readings the stream wrote to desk_zone_reading
/// into the site's desk engine. A desk with zones and no AMAN code gets a state from them alone (sensor-derived); an AMAN
/// desk keeps AMAN's higher rank while AMAN lives and falls back to its zones, flagged Degraded, once AMAN is silent;
/// silent zones turn the desk Unknown after T_stale; an e-gate's zone is no desk source; a site with desk zones and no
/// AMAN desk is read too; each reading is taken once.
/// </summary>
public sealed partial class ImmigrationIntakeTests
{
    /// <summary>
    /// Sites XS3 (an AMAN desk A1 and a sensor-only desk S1 with staff and service zones, an e-gate G1 with a service zone,
    /// a desk N1 with no zone and no AMAN code) and XS4 (a sensor-only desk only), each with a published profile (once).
    /// </summary>
    private Task DeskZoneSitesAsync() => ExecuteAsMigrationAsync("""
        DO $zones$
        BEGIN
            IF EXISTS (SELECT 1 FROM zone_profile WHERE id = 'a1160000-0000-0000-0000-000000000020') THEN
                RETURN;
            END IF;
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XS3', 'Desk zones probe'), (gen_random_uuid(), 'XS4', 'Sensor desks probe');
            INSERT INTO terminal (id, airport_id, code, name, site_code)
                 SELECT 'a1160000-0000-0000-0000-000000000001', id, 'XT3', 'Probe terminal 3', 'XS3' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
            INSERT INTO terminal (id, airport_id, code, name, site_code)
                 SELECT 'a1160000-0000-0000-0000-000000000101', id, 'XT4', 'Probe terminal 4', 'XS4' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres) VALUES
                ('a1160000-0000-0000-0000-000000000002', 'a1160000-0000-0000-0000-000000000001', 'XS3', 'L0', 'Arrivals', 0, 100, 100),
                ('a1160000-0000-0000-0000-000000000102', 'a1160000-0000-0000-0000-000000000101', 'XS4', 'L0', 'Departures', 0, 100, 100);
            INSERT INTO checkpoint (id, level_id, site_code, code, name, kind) VALUES
                ('a1160000-0000-0000-0000-000000000003', 'a1160000-0000-0000-0000-000000000002', 'XS3', 'IMM', 'Immigration', 'Immigration'),
                ('a1160000-0000-0000-0000-000000000103', 'a1160000-0000-0000-0000-000000000102', 'XS4', 'CI', 'Check-in', 'CheckIn');
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                ('a1160000-0000-0000-0000-000000000011', 'a1160000-0000-0000-0000-000000000003', 'XS3', 'A1', 'AMAN desk', 'Desk', 'VIS'),
                ('a1160000-0000-0000-0000-000000000012', 'a1160000-0000-0000-0000-000000000003', 'XS3', 'S1', 'Sensor desk', 'Desk', 'VIS'),
                ('a1160000-0000-0000-0000-000000000013', 'a1160000-0000-0000-0000-000000000003', 'XS3', 'G1', 'E-gate', 'EGate', 'EG'),
                ('a1160000-0000-0000-0000-000000000014', 'a1160000-0000-0000-0000-000000000003', 'XS3', 'N1', 'Plain desk', 'Desk', 'VIS'),
                ('a1160000-0000-0000-0000-000000000111', 'a1160000-0000-0000-0000-000000000103', 'XS4', 'C01', 'Counter 1', 'Counter', NULL);
            INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code) VALUES
                (gen_random_uuid(), 'Aman', 'X3A1', 'a1160000-0000-0000-0000-000000000011', 'XS3');
            INSERT INTO zone_profile (id, site_code, name, status) VALUES
                ('a1160000-0000-0000-0000-000000000020', 'XS3', 'Desk zones', 'Draft'),
                ('a1160000-0000-0000-0000-000000000120', 'XS4', 'Sensor desks', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                ('a1160000-0000-0000-0000-000000000021', 'a1160000-0000-0000-0000-000000000020', 'Q3', 'Queue', 'a1160000-0000-0000-0000-000000000002', NULL, NULL, 'probe'),
                ('a1160000-0000-0000-0000-000000000121', 'a1160000-0000-0000-0000-000000000120', 'Q4', 'Queue', 'a1160000-0000-0000-0000-000000000102', NULL, NULL, 'probe');
            INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                (gen_random_uuid(), 'a1160000-0000-0000-0000-000000000020', 'A1 staff', 'Staff', 'a1160000-0000-0000-0000-000000000002', 'a1160000-0000-0000-0000-000000000021', 'a1160000-0000-0000-0000-000000000011', 'probe'),
                (gen_random_uuid(), 'a1160000-0000-0000-0000-000000000020', 'A1 service', 'Service', 'a1160000-0000-0000-0000-000000000002', 'a1160000-0000-0000-0000-000000000021', 'a1160000-0000-0000-0000-000000000011', 'probe'),
                (gen_random_uuid(), 'a1160000-0000-0000-0000-000000000020', 'S1 staff', 'Staff', 'a1160000-0000-0000-0000-000000000002', 'a1160000-0000-0000-0000-000000000021', 'a1160000-0000-0000-0000-000000000012', 'probe'),
                (gen_random_uuid(), 'a1160000-0000-0000-0000-000000000020', 'S1 service', 'Service', 'a1160000-0000-0000-0000-000000000002', 'a1160000-0000-0000-0000-000000000021', 'a1160000-0000-0000-0000-000000000012', 'probe'),
                (gen_random_uuid(), 'a1160000-0000-0000-0000-000000000020', 'G1 service', 'Service', 'a1160000-0000-0000-0000-000000000002', 'a1160000-0000-0000-0000-000000000021', 'a1160000-0000-0000-0000-000000000013', 'probe'),
                (gen_random_uuid(), 'a1160000-0000-0000-0000-000000000120', 'C01 staff', 'Staff', 'a1160000-0000-0000-0000-000000000102', 'a1160000-0000-0000-0000-000000000121', 'a1160000-0000-0000-0000-000000000111', 'probe');
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('c', 64), published_on = now()
             WHERE id IN ('a1160000-0000-0000-0000-000000000020', 'a1160000-0000-0000-0000-000000000120');
        END
        $zones$;
        """);

    /// <summary>Writes readings as the stream's checkpoint does (one written time per call).</summary>
    private async Task WriteReadingsAsync(DateTime writtenUtc, params (string Site, string Desk, DeskSource Source, DateTime At, int Count)[] readings)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        foreach (var r in readings)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO desk_zone_reading (id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc)
                VALUES (@id, @site, @desk, @source, @at, @count, false, @zone, 1, @written)
                """, connection);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("site", r.Site);
            insert.Parameters.AddWithValue("desk", r.Desk);
            insert.Parameters.AddWithValue("source", r.Source.ToString());
            insert.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = r.At });
            insert.Parameters.AddWithValue("count", (short)r.Count);
            insert.Parameters.AddWithValue("zone", r.Site + "/Q");
            insert.Parameters.Add(new NpgsqlParameter("written", NpgsqlDbType.TimestampTz) { Value = writtenUtc });
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task DeskFeed_Should_GiveDesksAStateFromTheirZones_When_AmanIsAbsentOrSilent()
    {
        await SeedAsync();
        // A day of its own, so that no other test's records of the shared database fall in its minutes.
        _host.Clock.Advance(TimeSpan.FromDays(16));
        await DeskZoneSitesAsync();
        var feed = new Ariva.Infra.Border.DeskFeed(_host.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>(), _host.Clock,
            new Ariva.Infra.Border.DeskFeedSettings(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Ariva.Infra.Border.DeskFeed>.Instance);
        var t0 = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
        _host.Clock.Advance(t0 - Now);
        (await feed.SiteAsync("XS3", Ct)).Should().BeTrue("the engine starts now");
        var id = 0;
        DateTimeOffset At(int m, int s = 0) => new(t0.AddMinutes(m).AddSeconds(s));

        // Minutes 0 to 5: AMAN reports A1 logged out each minute (so its feed lives) while both desks' staff are present;
        // S1 serves in minutes 0 to 2. Minutes 6 to 9: AMAN is silent, the zones go on; from 10 on everything is silent.
        for (var m = 0; m < 10; m++)
        {
            _host.Clock.Advance(t0.AddMinutes(m + 1).AddSeconds(5) - Now);
            if (m < 6)
            {
                (await Apply<DeskSessionChanged>(i => i.ApplyDeskSessionsAsync(ImmigrationScope.ForSite("XS3"), "aman-kafka",
                    [new DeskSessionChanged("XS3", "X3A1", DeskSessionState.Closed, "", At(m, 10), $"x3-{++id}")], Ct))).Should().OnlyContain(r => r.Applied);
            }

            await WriteReadingsAsync(Now,
                ("XS3", "XS3/IMM/S1", DeskSource.StaffZone, t0.AddMinutes(m), 1), ("XS3", "XS3/IMM/S1", DeskSource.ServiceZone, t0.AddMinutes(m), m < 3 ? 1 : 0),
                ("XS3", "XS3/IMM/A1", DeskSource.StaffZone, t0.AddMinutes(m), 1), ("XS3", "XS3/IMM/A1", DeskSource.ServiceZone, t0.AddMinutes(m), 1),
                ("XS3", "XS3/IMM/G1", DeskSource.ServiceZone, t0.AddMinutes(m), 1));
            (await feed.SiteAsync("XS3", Ct)).Should().BeTrue();
        }

        _host.Clock.Advance(t0.AddMinutes(17) - Now);
        (await feed.SiteAsync("XS3", Ct)).Should().BeTrue();
        // A read again with nothing new takes no reading twice.
        (await feed.SiteAsync("XS3", Ct)).Should().BeTrue();

        var minutes = await RowsAsync("""
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60,
                   concat_ws('|', closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, sensor_derived_seconds, present_seconds, degraded)
              FROM desk_minute WHERE desk_code LIKE 'XS3/%' AND minute_utc >= @from AND minute_utc < @to
            """, r => (r.GetString(0), r.GetString(1)), t0, t0.AddMinutes(15));
        string Minute(string desk, int m) => minutes[$"XS3/IMM/{desk}@{m}"];

        // S1, no AMAN: Serving (row 6, sensor-derived) while a passenger is at the desk, then Idle; healthy sensors are not Degraded.
        for (var m = 0; m < 3; m++)
            Minute("S1", m).Should().Be("0|0|60|0|0|60|0|f", "S1 serving in minute {0}", m);
        for (var m = 3; m < 10; m++)
            Minute("S1", m).Should().Be("0|60|0|0|0|60|0|f", "S1 idle in minute {0}", m);
        // Last read at minute 9: stale two minutes later, Unknown and Degraded from minute 11.
        for (var m = 11; m < 15; m++)
            Minute("S1", m).Should().Be("0|0|0|0|60|0|0|t", "S1 silent in minute {0}", m);

        // A1, AMAN logged out: Closed whatever the zones show, its staff recorded as present and not processing (rank 2 over 3 and 4).
        for (var m = 1; m < 6; m++)
            Minute("A1", m).Should().Be("60|0|0|0|0|0|60|f", "A1 logged out in minute {0}", m);
        // AMAN silent since minute 5: its sources are stale from minute 7 and A1 falls back to its zones, Degraded.
        for (var m = 8; m < 10; m++)
            Minute("A1", m).Should().Be("0|0|60|0|0|60|0|t", "A1 from its zones in minute {0}", m);
        for (var m = 11; m < 15; m++)
            Minute("A1", m).Should().EndWith("|60|0|0|t", "A1 with every source silent in minute {0}", m);

        minutes.Keys.Should().NotContain(k => k.StartsWith("XS3/IMM/G1@", StringComparison.Ordinal) || k.StartsWith("XS3/IMM/N1@", StringComparison.Ordinal),
            "an e-gate's zone is no desk source, and a desk with no zone and no AMAN code is not in the engine");

        // ARV-117a: the sensor-only desk engine beside it, fed the same zone readings and nothing from AMAN. A1, which AMAN
        // reports logged out (Closed above), is Serving from its staff and service zones; S1 is as in the published engine;
        // nothing is Degraded while the zones live (AMAN is no source of this engine), and both turn Unknown once silent.
        const string SensorRows = """
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60,
                   concat_ws('|', closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on)
              FROM desk_sensor_minute WHERE desk_code LIKE 'XS3/%' AND minute_utc >= @from AND minute_utc < @to
            """;
        var sensor = await RowsAsync(SensorRows, r => (r.GetString(0), r.GetString(1)), t0, t0.AddMinutes(15));
        string Sensor(string desk, int m) => sensor[$"XS3/IMM/{desk}@{m}"][..sensor[$"XS3/IMM/{desk}@{m}"].LastIndexOf('|')];
        for (var m = 0; m < 10; m++)
        {
            Sensor("A1", m).Should().Be("0|0|60|0|0|f", "A1 from its zones alone in minute {0}, whatever AMAN reports", m);
            Sensor("S1", m).Should().Be(m < 3 ? "0|0|60|0|0|f" : "0|60|0|0|0|f", "S1 in minute {0}", m);
        }

        for (var m = 11; m < 15; m++)
        {
            Sensor("A1", m).Should().Be("0|0|0|0|60|t", "A1 silent in minute {0}", m);
            Sensor("S1", m).Should().Be("0|0|0|0|60|t", "S1 silent in minute {0}", m);
        }

        sensor.Keys.Should().NotContain(k => k.StartsWith("XS3/IMM/G1@", StringComparison.Ordinal) || k.StartsWith("XS3/IMM/N1@", StringComparison.Ordinal),
            "an e-gate's zone and a desk without zones are not in the sensor-only engine");
        var saved = await RowsAsync("""
            SELECT site_code, (SELECT string_agg(d->>'deskCode', ',' ORDER BY d->>'deskCode') FROM jsonb_array_elements(state->'sensorEngine'->'desks') d)
              FROM desk_feed_state WHERE site_code = 'XS3' AND updated_on BETWEEN @from AND @to
            """, r => (r.GetString(0), r.GetString(1)), t0, t0.AddMinutes(30));
        saved.Should().Equal(new Dictionary<string, string> { ["XS3"] = "XS3/IMM/A1,XS3/IMM/S1" }, "the sensor-only engine is part of the desk feed's snapshot");

        // A snapshot that holds AMAN memory is refused and the engine rebuilt by replaying the stored readings of the last
        // 15 minutes: every minute it covers is written again (a new updated_on) with the same values.
        await ExecuteAsMigrationAsync("""
            UPDATE desk_feed_state SET state = jsonb_set(state, '{sensorEngine,desks,0,memory,session}', '"Opened"') WHERE site_code = 'XS3'
            """);
        _host.Clock.Advance(t0.AddMinutes(18) - Now);
        (await feed.SiteAsync("XS3", Ct)).Should().BeTrue();
        var rebuilt = await RowsAsync(SensorRows, r => (r.GetString(0), r.GetString(1)), t0, t0.AddMinutes(15));
        foreach (var (key, row) in sensor.Where(r => r.Key.EndsWith("@3", StringComparison.Ordinal) || r.Key.EndsWith("@9", StringComparison.Ordinal) ||
                                                 r.Key.EndsWith("@12", StringComparison.Ordinal)))
        {
            rebuilt[key][..rebuilt[key].LastIndexOf('|')].Should().Be(row[..row.LastIndexOf('|')], "{0} is rebuilt with the same values", key);
            rebuilt[key].Should().NotBe(row, "{0} is written again by the replay", key);
        }

        // XS4 has no AMAN desk at all: it is one of the sites the feed reads, for its sensor-only counter. (The test reads its
        // own sites only: DMO's and XS2's feed states are shared with the other tests of this database.)
        (await feed.SitesAsync(Ct)).Should().Contain(["DMO", "XS2", "XS3", "XS4"]);
        _host.Clock.Advance(t0.AddMinutes(20).AddSeconds(5) - Now);
        (await feed.SiteAsync("XS4", Ct)).Should().BeTrue();
        await WriteReadingsAsync(Now, ("XS4", "XS4/CI/C01", DeskSource.StaffZone, t0.AddMinutes(20), 1));
        _host.Clock.Advance(t0.AddMinutes(24).AddSeconds(5) - Now);
        (await feed.SiteAsync("XS4", Ct)).Should().BeTrue();
        var counter = await RowsAsync("""
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60, concat_ws('|', lane, idle_seconds, sensor_derived_seconds)
              FROM desk_minute WHERE desk_code LIKE 'XS4/%' AND minute_utc >= @from AND minute_utc < @to
            """, r => (r.GetString(0), r.GetString(1)), t0.AddMinutes(20), t0.AddMinutes(22));
        counter.Should().Contain("XS4/CI/C01@0", "UNASSIGNED|60|60").And.Contain("XS4/CI/C01@1", "UNASSIGNED|60|60");
        // Its sensor-only engine gives the same (a site without AMAN: the shadow's desk term is the published one).
        var counterSensor = await RowsAsync("""
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60, concat_ws('|', idle_seconds, serving_seconds, unknown_seconds, degraded)
              FROM desk_sensor_minute WHERE desk_code LIKE 'XS4/%' AND minute_utc >= @from AND minute_utc < @to
            """, r => (r.GetString(0), r.GetString(1)), t0.AddMinutes(20), t0.AddMinutes(22));
        counterSensor.Should().Contain("XS4/CI/C01@0", "60|0|0|f").And.Contain("XS4/CI/C01@1", "60|0|0|f");
    }

    /// <summary>Site XS5: three sensor-only desks (no AMAN code) with staff zones, a published profile (once).</summary>
    private Task SensorCapSiteAsync() => ExecuteAsMigrationAsync("""
        DO $cap$
        BEGIN
            IF EXISTS (SELECT 1 FROM zone_profile WHERE id = 'a117a000-0000-0000-0000-000000000020') THEN
                RETURN;
            END IF;
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XS5', 'Sensor-only cap probe');
            INSERT INTO terminal (id, airport_id, code, name, site_code)
                 SELECT 'a117a000-0000-0000-0000-000000000001', id, 'XT5', 'Probe terminal 5', 'XS5' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres) VALUES
                ('a117a000-0000-0000-0000-000000000002', 'a117a000-0000-0000-0000-000000000001', 'XS5', 'L0', 'Arrivals', 0, 100, 100);
            INSERT INTO checkpoint (id, level_id, site_code, code, name, kind) VALUES
                ('a117a000-0000-0000-0000-000000000003', 'a117a000-0000-0000-0000-000000000002', 'XS5', 'IMM', 'Immigration', 'Immigration');
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                ('a117a000-0000-0000-0000-000000000011', 'a117a000-0000-0000-0000-000000000003', 'XS5', 'D1', 'Desk 1', 'Desk', 'VIS'),
                ('a117a000-0000-0000-0000-000000000012', 'a117a000-0000-0000-0000-000000000003', 'XS5', 'D2', 'Desk 2', 'Desk', 'VIS'),
                ('a117a000-0000-0000-0000-000000000013', 'a117a000-0000-0000-0000-000000000003', 'XS5', 'D3', 'Desk 3', 'Desk', 'VIS');
            INSERT INTO zone_profile (id, site_code, name, status) VALUES ('a117a000-0000-0000-0000-000000000020', 'XS5', 'Cap probe', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                ('a117a000-0000-0000-0000-000000000021', 'a117a000-0000-0000-0000-000000000020', 'Q5', 'Queue', 'a117a000-0000-0000-0000-000000000002', NULL, NULL, 'probe');
            INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                (gen_random_uuid(), 'a117a000-0000-0000-0000-000000000020', 'D1 staff', 'Staff', 'a117a000-0000-0000-0000-000000000002', 'a117a000-0000-0000-0000-000000000021', 'a117a000-0000-0000-0000-000000000011', 'probe'),
                (gen_random_uuid(), 'a117a000-0000-0000-0000-000000000020', 'D2 staff', 'Staff', 'a117a000-0000-0000-0000-000000000002', 'a117a000-0000-0000-0000-000000000021', 'a117a000-0000-0000-0000-000000000012', 'probe'),
                (gen_random_uuid(), 'a117a000-0000-0000-0000-000000000020', 'D3 staff', 'Staff', 'a117a000-0000-0000-0000-000000000002', 'a117a000-0000-0000-0000-000000000021', 'a117a000-0000-0000-0000-000000000013', 'probe');
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('d', 64), published_on = now()
             WHERE id = 'a117a000-0000-0000-0000-000000000020';
        END
        $cap$;
        """);

    // ARV-117a security review (CWE-120): a catch-up beyond the sensor-only minutes cap writes exactly what the cap leaves
    // in one read, warns with the site and the count only, and the remaining minutes follow on the next read with none
    // missing. The sensor-only warnings (readings refused, minutes over the cap, a refused snapshot) name the site and a
    // count only: no desk key and no exception text. A snapshot whose open state is not sensor-derived is refused.
    [Fact]
    public async Task DeskFeed_Should_WriteExactlyTheSensorMinutesTheCapLeaves_When_ItCatchesUpAndLogTheSiteAndACountOnly()
    {
        await SeedAsync();
        _host.Clock.Advance(TimeSpan.FromDays(3));
        await SensorCapSiteAsync();
        var log = new CapturingLogger();
        var feed = new Ariva.Infra.Border.DeskFeed(_host.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>(), _host.Clock,
            new Ariva.Infra.Border.DeskFeedSettings { MaxSensorMinutesPerRead = 100 }, log);
        var t0 = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
        _host.Clock.Advance(t0.AddSeconds(5) - Now);
        (await feed.SiteAsync("XS5", Ct)).Should().BeTrue("the engines start now; the sensor-only one is rebuilt from no stored readings");
        const string Rows = """
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60, updated_on::text
              FROM desk_sensor_minute WHERE desk_code LIKE 'XS5/%' AND minute_utc >= @from AND minute_utc < @to
            """;
        var first = await RowsAsync(Rows, r => (r.GetString(0), r.GetString(1)), t0.AddHours(-1), t0.AddHours(3));
        first.Count.Should().BeInRange(1, 100, "the first read is within the cap");
        log.Entries.Should().NotContain(e => e.Message.Contains("minutes in one read", StringComparison.Ordinal));

        // A reading for a desk the site does not have (refused, counted), then an hour of catch-up: 3 desks x 60 minutes.
        await WriteReadingsAsync(Now, ("XS5", "XS5/IMM/ZZ", DeskSource.StaffZone, t0, 1));
        _host.Clock.Advance(TimeSpan.FromHours(1));
        (await feed.SiteAsync("XS5", Ct)).Should().BeTrue();
        var second = await RowsAsync(Rows, r => (r.GetString(0), r.GetString(1)), t0.AddHours(-1), t0.AddHours(3));
        (second.Count - first.Count).Should().Be(100, "one read writes exactly the minutes the cap leaves, never a whole step beyond it");
        log.Entries.Should().ContainSingle(e => e.Message == "Sensor-only desk engine of site XS5: 100 minutes in one read; the rest follow on the next read");
        log.Entries.Should().ContainSingle(e => e.Message == "Sensor-only desk engine of site XS5: 1 zone readings refused (bounds or validation)");

        // The rest on the next read (the clock barely moves): no minute is missing for any desk, and no new cap warning.
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        (await feed.SiteAsync("XS5", Ct)).Should().BeTrue();
        var third = await RowsAsync(Rows, r => (r.GetString(0), r.GetString(1)), t0.AddHours(-1), t0.AddHours(3));
        (third.Count - second.Count).Should().BeInRange(1, 100);
        log.Entries.Count(e => e.Message.Contains("minutes in one read", StringComparison.Ordinal)).Should().Be(1);
        // Minute numbers count from the query's @from, an hour before t0.
        var last = (int)((Now.AddSeconds(-90) - t0.AddHours(-1)).Ticks / TimeSpan.TicksPerMinute) - 1;
        foreach (var desk in new[] { "D1", "D2", "D3" })
        {
            var minutes = third.Keys.Where(k => k.StartsWith($"XS5/IMM/{desk}@", StringComparison.Ordinal))
                .Select(k => int.Parse(k[(k.IndexOf('@', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
            minutes.Should().Equal(Enumerable.Range(minutes[0], last - minutes[0] + 1), "every minute of {0} up to the watermark is written, once", desk);
        }

        // A snapshot whose desk is open without being sensor-derived (only AMAN could make it so) is refused and rebuilt.
        await ExecuteAsMigrationAsync("""
            UPDATE desk_feed_state SET state = jsonb_set(state, '{sensorEngine,desks,0,current}',
                '{"status":"Serving","sensorDerived":false,"presentNotProcessing":false,"degraded":false}') WHERE site_code = 'XS5'
            """);
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await feed.SiteAsync("XS5", Ct)).Should().BeTrue();
        log.Entries.Should().ContainSingle(e => e.Message == "The sensor-only desk state of site XS5 is not valid; it is rebuilt from the stored zone readings");

        // Every sensor-only log line names the site and a count only: no exception, no desk key, no other value.
        var sensorOnly = log.Entries.Where(e => e.Message.StartsWith("Sensor-only desk engine of site", StringComparison.Ordinal) ||
                                                e.Message.StartsWith("The sensor-only desk state of site", StringComparison.Ordinal)).ToList();
        sensorOnly.Should().HaveCountGreaterThanOrEqualTo(4);
        sensorOnly.Should().AllSatisfy(e =>
        {
            e.Exception.Should().BeNull();
            e.Message.Should().NotContain("XS5/");
            e.Values.Keys.Should().BeSubsetOf(["Site", "Count", "{OriginalFormat}"]);
            e.Values["Site"].Should().Be("XS5");
        });
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception Exception, IReadOnlyDictionary<string, object> Values);

    // Keeps every entry with its structured values, to prove what a log line carries.
    private sealed class CapturingLogger : ILogger<Ariva.Infra.Border.DeskFeed>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = state is IReadOnlyList<KeyValuePair<string, object>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) : [];
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, values));
        }
    }
}
