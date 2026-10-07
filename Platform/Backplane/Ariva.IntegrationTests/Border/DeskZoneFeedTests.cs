using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
