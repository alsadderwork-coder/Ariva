using Ariva.Core;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Reports;
using Ariva.Core.Sensing;
using Ariva.Core.Services;
using Ariva.Core.Services.Quality;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Quality;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Reports;

/// <summary>
/// ARV-119 against TimescaleDB: the weekly summary of the reference week built from the simulator (seed 9303) at the demo
/// airport DMO (Asia/Dubai). Monday 28 September holds the reference evening as the stream stored it (A-VIS and CI-C from
/// 17:00 to 20:30 UTC, 21:00 to 00:30 local, so its last half hour is Tuesday's: minutes, bins, health checks and S-17's
/// outage), the overflow scenario case's band minutes of A-VIS, and the evening's arrival immigration desks (AR-01 to AR-22)
/// in the states the simulator gave them; Thursday holds the same evening again; the rest of the week has nothing. Alerts
/// and the availability ledger are planted. A neighbour site DMO-X holds a copy of every row under its own keys and the
/// same checkpoint and desk codes. Checked: each lane's day equals that day's daily report and the replay's own counts
/// (so DMO-X's rows never count), the border-only figures (an airport role gets no desk row, and each border day and week
/// only with three desks open in it, the days all or none), a desk key shared with a soft-deleted checkpoint of another
/// kind counted nowhere, a check-in counter and a security lane as lanes for the duty manager only, availability equal to
/// the ledger read, time to acknowledge, device uptime, the week refused before any read when it is not a started Monday
/// within 57 weeks or the caller may not read the site's reports (a pending account included), reads refused at their
/// bounds rather than cut short, a daylight-saving week of 169 hours, a running week and a week before the data.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WeeklySummaryTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Planted _planted;

    private static readonly DateOnly Week = new(2026, 9, 28);
    private static readonly string[] ArrivalLanes = ["A-CRW", "A-CIT", "A-RES", "A-VIS"];

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.WeeklySummary);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The users and the oracle of the reference week, computed from the simulator and the replay, not from the database.</summary>
    private sealed record Planted(
        Guid Admin,
        Guid Border,
        Guid Manager,
        Guid Handler,
        Guid Observer,
        Guid Elsewhere,
        Guid Pending,
        IReadOnlyDictionary<(string Zone, DateOnly Date), long> Passengers,
        IReadOnlyDictionary<(string Zone, DateTime HourUtc), IReadOnlyList<(int Bucket, long Count)>> Hours,
        IReadOnlyDictionary<DateOnly, int> OverflowMinutes,
        IReadOnlyList<BinResult> Bins,
        IReadOnlyList<ZoneHealthBin> Health,
        IReadOnlyDictionary<string, (string Lane, DeskStateSeconds Week)> Desks,
        IReadOnlyList<DeskMinute> DeskMinutes);

    #region Setup

    private async Task ExecuteAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Guid> UserAsync(string role, string site, bool allSites = false, bool temporary = false)
    {
        var user = await _host.CreateUserAsync($"it.week.{role.ToLowerInvariant()[..6]}.{Guid.NewGuid():N}"[..30], temporary: temporary, roles: [role], allSites: allSites);
        if (site is not null)
            (await _host.ReadAsync<int>("INSERT INTO user_site (id, user_id, site_code) SELECT gen_random_uuid(), @id, @secret RETURNING 1", user, site)).Should().Be(1);
        return user;
    }

    private async Task<Planted> PlantAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_planted is not null)
            {
                await _host.CreateUserAsync("it.week.probe." + Guid.NewGuid().ToString("N")[..8]);
                return _planted;
            }

            var admin = await UserAsync(RoleCodes.SystemAdministrator, null, allSites: true);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await ExecuteAsync("""
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'DMO-X', 'Weekly summary neighbour');
                INSERT INTO terminal (id, airport_id, code, name, site_code)
                     SELECT 'a1190000-0000-0000-0000-000000000001', id, 'TX', 'Neighbour terminal', 'DMO-X' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
                INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                     VALUES ('a1190000-0000-0000-0000-000000000002', 'a1190000-0000-0000-0000-000000000001', 'DMO-X', 'ARR', 'Arrivals', 0, 100, 60);
                INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
                     VALUES ('a1190000-0000-0000-0000-000000000003', 'a1190000-0000-0000-0000-000000000002', 'DMO-X', 'IMM', 'Arrival immigration', 'Immigration');
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes)
                     SELECT gen_random_uuid(), 'a1190000-0000-0000-0000-000000000003', 'DMO-X', 'AR-' || lpad(n::text, 2, '0'), 'Desk ' || n, 'Desk', 'VIS'
                       FROM generate_series(1, 22) AS n;
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XTZ', 'Unknown time zone');
                INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), 'ZZT', 'Unknown time zone airport', 'Mars/Olympus');
                INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), id, 'T1', 'T1', 'XTZ' FROM airport WHERE iata_code = 'ZZT';
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XBE', 'Berlin probe');
                INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), 'ZBE', 'Berlin probe airport', 'Europe/Berlin');
                INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), id, 'T1', 'T1', 'XBE' FROM airport WHERE iata_code = 'ZBE';
                INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                    orientation_degrees, footprint_radius_metres, footprint_source, queue_zone_name)
                     SELECT gen_random_uuid(), 'S-17', 'DMO', 'StereoVision', 'Reference', 'HttpsPush', 'Canonical', 'Ntp', 'Online', l.id, 0, 0, 3, 0, 2, 'Vendor', 'A-VIS'
                       FROM level l WHERE l.site_code = 'DMO' AND l.code = 'ARR';
                """);

            // The reference evening as the stream stored it, the overflow case's band minutes of A-VIS, and the arrival desks.
            var evening = Stream(ReferenceReplay.Zones, ReferenceReplay.Ingested);
            var bands = Stream(ReferenceReplay.OverflowZones, ReferenceReplay.OverflowIngested).SelectMany(o => o.Overflow).ToList();
            var deskMinutes = DeskMinutes();
            var store = new StreamStore(new DatabaseSettings
            {
                Host = fixture.Hostname, Port = fixture.Port, Name = await _host.DatabaseAsync(), Username = fixture.AdminUsername, Password = fixture.AdminPassword
            }, TimeProvider.System);
            await store.SaveAsync(new StreamCheckpoint("it-weekly", [.. evening, new ZoneOutputs("DMO/A-VIS", [], [], [], []) { Overflow = bands }], [],
                deskMinutes, []), Ct);
            (await _host.ReadAsync<long>("SELECT count(*) FROM zone_outage WHERE zone_key = 'DMO/A-VIS' AND device_code = 'S-17'")).Should().Be(1, "S-17 is out 18:20 to 18:30");

            // Thursday: the same evening again. DMO-X: a copy of every row of the week under its own keys. XBE (Berlin): a week that
            // ends with the autumn change (25 October 2026), minutes on both sides of it.
            await ExecuteAsync("""
                CREATE TEMP TABLE c_minute AS SELECT * FROM queue_minute WHERE zone_key LIKE 'DMO/%';
                UPDATE c_minute SET minute_utc = minute_utc + interval '3 days';
                INSERT INTO queue_minute SELECT * FROM c_minute;
                CREATE TEMP TABLE c_bin AS SELECT * FROM queue_bin WHERE zone_key LIKE 'DMO/%';
                UPDATE c_bin SET start_utc = start_utc + interval '3 days';
                INSERT INTO queue_bin SELECT * FROM c_bin;
                CREATE TEMP TABLE c_health AS SELECT * FROM zone_health_bin WHERE zone_key LIKE 'DMO/%';
                UPDATE c_health SET start_utc = start_utc + interval '3 days';
                INSERT INTO zone_health_bin SELECT * FROM c_health;
                CREATE TEMP TABLE c_overflow AS SELECT * FROM overflow_minute WHERE zone_key LIKE 'DMO/%';
                UPDATE c_overflow SET minute_utc = minute_utc + interval '3 days';
                INSERT INTO overflow_minute SELECT * FROM c_overflow;
                CREATE TEMP TABLE c_desk AS SELECT * FROM desk_minute WHERE desk_code LIKE 'DMO/%';
                UPDATE c_desk SET minute_utc = minute_utc + interval '3 days';
                INSERT INTO desk_minute SELECT * FROM c_desk;
                CREATE TEMP TABLE c_outage AS SELECT * FROM zone_outage WHERE zone_key LIKE 'DMO/%';
                UPDATE c_outage SET from_utc = from_utc + interval '3 days', to_utc = to_utc + interval '3 days';
                INSERT INTO zone_outage SELECT * FROM c_outage;

                INSERT INTO availability_minute (site_code, minute_utc, local_date, calendar, state, reasons, zones_expected, zones_stale, zones_missing, zones_lagging,
                                                 profile_version, rule_version, decided_utc)
                SELECT 'DMO', m, CAST(m AT TIME ZONE 'Asia/Dubai' AS date), 'Operating',
                       CASE WHEN extract(hour FROM m) = 18 AND extract(minute FROM m) BETWEEN 20 AND 29 AND CAST(m AS date) IN (DATE '2026-09-28', DATE '2026-10-01')
                            THEN 'Unavailable' ELSE 'Available' END,
                       CASE WHEN extract(hour FROM m) = 18 AND extract(minute FROM m) BETWEEN 20 AND 29 AND CAST(m AS date) IN (DATE '2026-09-28', DATE '2026-10-01')
                            THEN ARRAY['StaleZone'] ELSE '{}'::text[] END,
                       16, CASE WHEN extract(hour FROM m) = 18 AND extract(minute FROM m) BETWEEN 20 AND 29 AND CAST(m AS date) IN (DATE '2026-09-28', DATE '2026-10-01')
                            THEN 1 ELSE 0 END, 0, 0, 12, 2, m + interval '2 minutes'
                  FROM generate_series(TIMESTAMPTZ '2026-09-27T20:00:00Z', TIMESTAMPTZ '2026-10-01T19:59:00Z', interval '1 minute') AS m;

                INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, owner_role, raised_utc, raised_value, state,
                                   acknowledged_utc, acknowledged_by, cleared_utc, resolved_utc, resolution, resolved_by)
                SELECT gen_random_uuid(), 'DMO', r.id, r.code, r.name, a.zone, r.metric, r.severity, r.owner_role, a.raised, 1, a.state, a.acknowledged,
                       CASE WHEN a.acknowledged IS NOT NULL THEN 'it' END, a.cleared, a.cleared, CASE WHEN a.cleared IS NOT NULL THEN 'Cleared' END,
                       CASE WHEN a.cleared IS NOT NULL THEN 'system' END
                  FROM (VALUES ('R-001', 'A-VIS', TIMESTAMPTZ '2026-09-28T18:05:00Z', 'Acknowledged', TIMESTAMPTZ '2026-09-28T18:09:00Z', CAST(NULL AS timestamptz)),
                               ('R-002', 'A-VIS', TIMESTAMPTZ '2026-09-28T18:20:00Z', 'Resolved', NULL, TIMESTAMPTZ '2026-09-28T18:30:00Z'),
                               ('R-002', 'A-CIT', TIMESTAMPTZ '2026-10-01T18:05:00Z', 'Acknowledged', TIMESTAMPTZ '2026-10-01T18:15:00Z', NULL),
                               ('R-001', 'A-CIT', TIMESTAMPTZ '2026-09-27T19:00:00Z', 'Resolved', NULL, TIMESTAMPTZ '2026-09-27T19:30:00Z'))
                       AS a(code, zone, raised, state, acknowledged, cleared)
                  JOIN alert_rule r ON r.code = a.code AND r.site_code = 'DMO';

                CREATE TEMP TABLE x_minute AS SELECT * FROM queue_minute WHERE zone_key LIKE 'DMO/%';
                UPDATE x_minute SET zone_key = 'DMO-X/' || substr(zone_key, 5);
                INSERT INTO queue_minute SELECT * FROM x_minute;
                CREATE TEMP TABLE x_bin AS SELECT * FROM queue_bin WHERE zone_key LIKE 'DMO/%';
                UPDATE x_bin SET zone_key = 'DMO-X/' || substr(zone_key, 5);
                INSERT INTO queue_bin SELECT * FROM x_bin;
                CREATE TEMP TABLE x_health AS SELECT * FROM zone_health_bin WHERE zone_key LIKE 'DMO/%';
                UPDATE x_health SET zone_key = 'DMO-X/' || substr(zone_key, 5);
                INSERT INTO zone_health_bin SELECT * FROM x_health;
                CREATE TEMP TABLE x_overflow AS SELECT * FROM overflow_minute WHERE zone_key LIKE 'DMO/%';
                UPDATE x_overflow SET zone_key = 'DMO-X/' || substr(zone_key, 5);
                INSERT INTO overflow_minute SELECT * FROM x_overflow;
                CREATE TEMP TABLE x_desk AS SELECT * FROM desk_minute WHERE desk_code LIKE 'DMO/%';
                UPDATE x_desk SET desk_code = 'DMO-X/' || substr(desk_code, 5);
                INSERT INTO desk_minute SELECT * FROM x_desk;
                CREATE TEMP TABLE x_outage AS SELECT * FROM zone_outage WHERE zone_key LIKE 'DMO/%';
                UPDATE x_outage SET zone_key = 'DMO-X/' || substr(zone_key, 5);
                INSERT INTO zone_outage SELECT * FROM x_outage;
                CREATE TEMP TABLE x_availability AS SELECT * FROM availability_minute WHERE site_code = 'DMO';
                UPDATE x_availability SET site_code = 'DMO-X', state = 'Unavailable', reasons = ARRAY['MissingMinute'];
                INSERT INTO availability_minute SELECT * FROM x_availability;
                INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, owner_role, raised_utc, raised_value, state,
                                   cleared_utc, resolved_utc, resolution, resolved_by)
                SELECT gen_random_uuid(), 'DMO-X', r.id, r.code, r.name, 'A-VIS', r.metric, r.severity, NULL, TIMESTAMPTZ '2026-09-29T10:00:00Z' + n * interval '1 minute', 1,
                       'Resolved', TIMESTAMPTZ '2026-09-29T11:00:00Z', TIMESTAMPTZ '2026-09-29T11:00:00Z', 'Cleared', 'system'
                  FROM alert_rule r, generate_series(1, 5) AS n WHERE r.code = 'R-002' AND r.site_code = 'DMO';

                INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
                VALUES ('XBE/Q-1', '2026-10-18T22:00:00Z', 1, 'Final', 20, 20, 20, 5, ARRAY[4], ARRAY[20], now()),
                       ('XBE/Q-1', '2026-10-24T21:59:00Z', 1, 'Final', 20, 20, 20, 5, ARRAY[4], ARRAY[20], now()),
                       ('XBE/Q-1', '2026-10-25T00:30:00Z', 1, 'Final', 20, 20, 20, 5, ARRAY[8], ARRAY[20], now()),
                       ('XBE/Q-1', '2026-10-25T01:30:00Z', 1, 'Final', 20, 20, 20, 5, ARRAY[20], ARRAY[20], now()),
                       ('XBE/Q-1', '2026-10-25T22:59:00Z', 1, 'Final', 20, 20, 20, 5, ARRAY[4], ARRAY[20], now()),
                       ('XBE/Q-1', '2026-10-25T23:00:00Z', 1, 'Final', 20, 20, 20, 5, ARRAY[4], ARRAY[20], now());
                """);

            // A checkpoint code reused across kinds: a soft-deleted check-in checkpoint IMM (another level) with a counter AR-23, and a
            // live arrival desk AR-23, so the key DMO/IMM/AR-23 belongs to desks of two kinds; its minutes must count nowhere. And
            // one check-in counter and one security lane with minutes (airport desks). Planted after DMO-X's copies: DMO's own.
            await ExecuteAsync("""
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes)
                     SELECT gen_random_uuid(), c.id, 'DMO', 'AR-23', 'Shared code probe', 'Desk', 'VIS'
                       FROM checkpoint c WHERE c.site_code = 'DMO' AND c.code = 'IMM' AND c.deleted_on IS NULL;
                INSERT INTO checkpoint (id, level_id, site_code, code, name, kind, deleted_on, deleted_by)
                     SELECT 'a1190000-0000-0000-0000-000000000013', l.id, 'DMO', 'IMM', 'Former check-in IMM', 'CheckIn', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'it'
                       FROM level l WHERE l.site_code = 'DMO' AND l.code = 'DEP';
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, deleted_on, deleted_by)
                     VALUES (gen_random_uuid(), 'a1190000-0000-0000-0000-000000000013', 'DMO', 'AR-23', 'Former counter', 'Counter', TIMESTAMPTZ '2026-09-01T00:00:00Z', 'it');
                INSERT INTO desk_minute (desk_code, lane, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, transactions,
                                         sensor_derived_seconds, present_seconds, degraded, updated_on)
                SELECT 'DMO/IMM/AR-23', 'VIS', m, 0, 0, 60, 0, 0, 1, 0, 0, false, now()
                  FROM generate_series(TIMESTAMPTZ '2026-09-28T17:00:00Z', TIMESTAMPTZ '2026-09-28T17:59:00Z', interval '1 minute') AS m
                UNION ALL
                SELECT 'DMO/CI/C01', '', m, 0, 0, 60, 0, 0, 1, 0, 0, false, now()
                  FROM generate_series(TIMESTAMPTZ '2026-09-29T06:00:00Z', TIMESTAMPTZ '2026-09-29T06:59:00Z', interval '1 minute') AS m
                UNION ALL
                SELECT 'DMO/SEC-N/N1', '', m, 0, 60, 0, 0, 0, 0, 0, 0, false, now()
                  FROM generate_series(TIMESTAMPTZ '2026-09-29T07:00:00Z', TIMESTAMPTZ '2026-09-29T07:29:00Z', interval '1 minute') AS m;
                """);

            _planted = new Planted(
                admin,
                await UserAsync(RoleCodes.BorderShiftSupervisor, "DMO"),
                await UserAsync(RoleCodes.TerminalDutyManager, "DMO"),
                await UserAsync(RoleCodes.HandlerStationManager, "DMO"),
                await UserAsync(RoleCodes.ValidationObserver, "DMO"),
                await UserAsync(RoleCodes.BorderShiftSupervisor, "DMO-X"),
                await UserAsync(RoleCodes.BorderShiftSupervisor, "DMO", temporary: true),
                Passengers(evening),
                Histograms(evening),
                OverflowMinutes(bands),
                [.. evening.Where(o => o.ZoneKey == "DMO/A-VIS").SelectMany(o => o.Bins).GroupBy(b => b.StartUtc).Select(g => g.OrderBy(b => b.Revision).Last())],
                [.. evening.Where(o => o.ZoneKey == "DMO/A-VIS").SelectMany(o => o.Health).GroupBy(h => h.StartUtc).Select(g => g.OrderBy(h => h.Revision).Last())],
                DeskOracle(deskMinutes),
                deskMinutes);
            return _planted;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The evening's arrival immigration desks, minute by minute, in the state the simulator gave each (60 seconds of it).</summary>
    private static List<DeskMinute> DeskMinutes()
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var minutes = new List<DeskMinute>();
        foreach (var lane in ArrivalLanes)
        {
            var q = ScenarioModel.Q(lane);
            for (var m = 1020; m < 1230; m++)
            {
                foreach (var server in day.ServerStates(q, m))
                {
                    TimeSpan Of(params string[] states) => states.Contains(server.State) ? TimeSpan.FromSeconds(60) : TimeSpan.Zero;
                    minutes.Add(new DeskMinute(DeskKeys.For("DMO", "IMM", server.Id), ScenarioModel.Queues[q].Lane, ReferenceReplay.WallOf(m),
                        Of("closed", "oos"), Of("idle"), Of("serving"), Of("paused"), Of("unknown"), 0, TimeSpan.Zero, TimeSpan.Zero, false));
                }
            }
        }

        return minutes;
    }

    private static Dictionary<string, (string Lane, DeskStateSeconds Week)> DeskOracle(IReadOnlyList<DeskMinute> minutes) =>
        minutes.GroupBy(m => m.DeskCode[(m.DeskCode.LastIndexOf('/') + 1)..]).ToDictionary(g => g.Key, g => (g.First().Lane, new DeskStateSeconds(
            2 * g.Count(), 2 * (long)g.Sum(m => m.Closed.TotalSeconds), 2 * (long)g.Sum(m => m.Idle.TotalSeconds), 2 * (long)g.Sum(m => m.Serving.TotalSeconds),
            2 * (long)g.Sum(m => m.Paused.TotalSeconds), 2 * (long)g.Sum(m => m.Unknown.TotalSeconds))), StringComparer.Ordinal);

    private static bool Open(DeskMinute m) => m.Idle + m.Serving + m.Paused > TimeSpan.Zero;

    private static DeskStateSeconds SecondsOf(IEnumerable<DeskMinute> minutes)
    {
        var list = minutes.ToList();
        return new DeskStateSeconds(list.Count, (long)list.Sum(m => m.Closed.TotalSeconds), (long)list.Sum(m => m.Idle.TotalSeconds),
            (long)list.Sum(m => m.Serving.TotalSeconds), (long)list.Sum(m => m.Paused.TotalSeconds), (long)list.Sum(m => m.Unknown.TotalSeconds));
    }

    /// <summary>
    /// The arrival lanes as the simulator had them, Monday's evening and Thursday's copy: per lane the desks with open time in the
    /// week, and per local date the desks with open time and the seconds per state.
    /// </summary>
    private static Dictionary<string, (int Open, DeskStateSeconds Week, WeeklyDeskDay[] Days)> LaneOracle(IReadOnlyList<DeskMinute> minutes)
    {
        var week = minutes.Concat(minutes.Select(m => m with { MinuteUtc = m.MinuteUtc.AddDays(3) })).ToList();
        return week.GroupBy(m => m.Lane).ToDictionary(g => g.Key, g => (
            g.Where(Open).Select(m => m.DeskCode).Distinct().Count(),
            SecondsOf(g),
            Enumerable.Range(0, 7).Select(i =>
            {
                var day = g.Where(m => LocalDate(m.MinuteUtc) == Week.AddDays(i)).ToList();
                return new WeeklyDeskDay(Week.AddDays(i), day.Where(Open).Select(m => m.DeskCode).Distinct().Count(), SecondsOf(day));
            }).ToArray()));
    }

    /// <summary>
    /// The zones' outputs as the stream engine makes them, through ZoneReplay's own loop (the replay ledger's JSON does not
    /// carry the minutes' wait histograms back, so its read-back outputs are not used here).
    /// </summary>
    private static List<ZoneOutputs> Stream(IReadOnlyList<string> zones, (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) source)
    {
        var settings = new ZoneProcessorSettings();
        var outputs = new List<ZoneOutputs>();
        foreach (var zone in zones)
        {
            var processor = new ZoneProcessor(ZoneKeys.For(ReferenceReplay.Site, zone), ReferenceReplay.GeometryOf(zone), ReferenceReplay.ProfileVersion, settings);
            foreach (var input in ReferenceReplay.InputsOf(zone, source))
            {
                processor.Offer(input.ToBatch(), ReferenceReplay.To);
                if (processor.Full)
                    outputs.Add(processor.Drain());
            }

            processor.Finish(ReferenceReplay.To, ReferenceReplay.To + ZoneReplay.Settle(settings));
            outputs.Add(processor.Drain());
        }

        return outputs;
    }

    /// <summary>The last word on each minute of the evening, by zone name.</summary>
    private static IEnumerable<(string Zone, MinuteResult Minute)> LastMinutes(IReadOnlyList<ZoneOutputs> evening) =>
        evening.SelectMany(o => o.Minutes.Select(m => (Zone: o.ZoneKey[4..], Minute: m))).GroupBy(x => (x.Zone, x.Minute.StartUtc)).Select(g => g.Last());

    /// <summary>Each zone's entries per local date of Monday's evening.</summary>
    private static Dictionary<(string Zone, DateOnly Date), long> Passengers(IReadOnlyList<ZoneOutputs> evening) =>
        LastMinutes(evening).GroupBy(x => (x.Zone, LocalDate(x.Minute.StartUtc))).ToDictionary(g => g.Key, g => g.Sum(x => x.Minute.Entries));

    /// <summary>Each zone's merged wait histogram per UTC hour of Monday's evening (Dubai's hours are UTC hours, 4 hours on).</summary>
    private static Dictionary<(string Zone, DateTime HourUtc), IReadOnlyList<(int Bucket, long Count)>> Histograms(IReadOnlyList<ZoneOutputs> evening) =>
        LastMinutes(evening).GroupBy(x => (x.Zone, new DateTime(x.Minute.StartUtc.Year, x.Minute.StartUtc.Month, x.Minute.StartUtc.Day, x.Minute.StartUtc.Hour, 0, 0, DateTimeKind.Utc)))
            .ToDictionary(g => g.Key, g => WaitStatistics.Merge([.. g.Select(x => x.Minute.Waits.Histogram ?? [])]));

    private static Dictionary<DateOnly, int> OverflowMinutes(IReadOnlyList<OverflowMinute> bands) =>
        bands.GroupBy(b => b.MinuteUtc).Where(g => g.Max(b => b.MaxOccupancy) > 0).GroupBy(g => LocalDate(g.Key)).ToDictionary(g => g.Key, g => g.Count());

    private static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(4));

    #endregion

    private Task<Fluentx.Result<WeeklySummary>> SummaryAsync(Guid caller, string site, DateOnly? week) =>
        _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcWeeklySummary>().GetAsync(site, week, Ct));

    private void After() => _host.Clock.Advance(TimeSpan.FromDays(5));

    [Fact]
    public async Task Week_Should_AddTheReferenceWeekAsTheDailyReportsDo_When_ABorderSupervisorReadsIt()
    {
        var p = await PlantAsync();
        After();

        var summary = (await SummaryAsync(p.Border, "DMO", Week)).Data;

        summary.Should().Match<WeeklySummary>(s => s.TimeZoneId == "Asia/Dubai" && s.Complete && s.Hours == 168 && s.WeekEnd == new DateOnly(2026, 10, 4)
                                                   && s.FromUtc == new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc));
        foreach (var (zone, date) in p.Passengers.Keys)
        {
            var lane = summary.Lanes.Single(l => l.Zone == zone);
            lane.Days.Single(d => d.Date == date).Passengers.Should().Be(p.Passengers[(zone, date)], $"{zone} on {date}: the replay's own count");
            lane.Days.Single(d => d.Date == date.AddDays(3)).Passengers.Should().Be(p.Passengers[(zone, date)], $"{zone} on {date}, three days later");
            lane.Passengers.Should().Be(2 * p.Passengers.Where(x => x.Key.Zone == zone).Sum(x => x.Value), "Monday's evening twice, DMO-X's copy never");
        }

        summary.Lanes.Single(l => l.Zone == "A-VIS").Days.Where(d => d.Date.DayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Saturday or DayOfWeek.Sunday)
            .Should().OnlyContain(d => d.Passengers == 0 && d.P90Minutes == null && d.Peak == null, "days without data are listed empty");

        // Every lane's day reads as that day's report.
        for (var date = Week; date <= Week.AddDays(6); date = date.AddDays(1))
        {
            var daily = (await _host.AsCallerAsync(p.Border, s => s.GetRequiredService<ISvcReports>().DailyAsync("DMO", date, Ct))).Data;
            foreach (var lane in daily.Lanes)
            {
                var day = summary.Lanes.Single(l => l.Zone == lane.Zone).Days.Single(d => d.Date == date);
                day.Should().Match<WeeklyLaneDay>(d => d.Passengers == lane.Passengers && d.Waits == lane.Waits && d.P50Minutes == lane.P50Minutes &&
                                                       d.P90Minutes == lane.P90Minutes && d.MaxQueueLength == lane.MaxQueueLength && d.Provisional == lane.Provisional,
                    $"{lane.Zone} on {date}");
                day.Peak?.Start.Should().Be(lane.Peak?.Start, $"{lane.Zone} peak on {date}");
                day.Peak?.P90Minutes.Should().Be(lane.Peak?.P90Minutes);
                day.Hours.Select(h => (h.Start, h.Waits, h.P50Minutes, h.P90Minutes, h.MaxQueueLength)).Should()
                    .Equal(lane.Hours.Select(h => (h.Start, h.Waits, h.P50Minutes, h.P90Minutes, h.MaxQueueLength)), $"{lane.Zone} hours on {date}");
            }
        }

        // Every hour of the evening, Monday's and Thursday's, reads the engine's own minutes merged (F7).
        foreach (var ((zone, hourUtc), histogram) in p.Hours)
        {
            var lane = summary.Lanes.Single(l => l.Zone == zone);
            foreach (var start in new[] { hourUtc, hourUtc.AddDays(3) })
            {
                var hour = lane.Days.SelectMany(d => d.Hours).Single(h => h.StartUtc == start);
                hour.P90Minutes.Should().Be(WaitStatistics.Percentile(histogram, 0.9), $"{zone} at {start:u}");
                hour.P50Minutes.Should().Be(WaitStatistics.Percentile(histogram, 0.5), $"{zone} at {start:u}");
            }
        }

        var vis = summary.Lanes.Single(l => l.Zone == "A-VIS");
        vis.Peak.Should().NotBeNull("the reference evening has hours of 20 waits and more");
        vis.P90Minutes.Should().Be(WaitStatistics.Percentile(WaitStatistics.Merge([.. p.Hours.Where(h => h.Key.Zone == "A-VIS").Select(h => h.Value)]), 0.9),
            "the week merges the same histograms twice, which leaves the percentiles where they were");
        summary.PeakHours.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(WeeklySummaries.MaxPeakHours);
        summary.PeakHours[0].P90Minutes.Should().Be(summary.Lanes.Where(l => l.Peak is not null).Max(l => l.Peak.P90Minutes));

        // Overflow: the band minutes of A-VIS, twice.
        vis.Days.Select(d => d.OverflowMinutes).Should().Equal(Enumerable.Range(0, 7).Select(i =>
            p.OverflowMinutes.GetValueOrDefault(Week.AddDays(i)) + p.OverflowMinutes.GetValueOrDefault(Week.AddDays(i - 3))));
        vis.OverflowMinutes.Should().Be(2 * p.OverflowMinutes.Values.Sum()).And.BeGreaterThan(0);

        // Data quality from the stored bins and health checks: the replay's last word on each bin, twice.
        vis.Quality.Should().Match<WeeklyQuality>(q => q.Bins == 2 * p.Bins.Count && q.FinalBins == 2 * p.Bins.Count(b => b.Status == BinStatus.Final) &&
                                                       q.DegradedBins == 2 * p.Bins.Count(b => b.Quality == BinQuality.Degraded) &&
                                                       q.UnknownBins == 2 * p.Bins.Count(b => b.Quality == BinQuality.Unknown) &&
                                                       q.HealthBins == 2 * p.Health.Count);
        var final = p.Health.Where(h => h.Status == BinStatus.Final).ToList();
        vis.Quality.TracksEntered.Should().Be(2 * final.Sum(h => h.TracksEntered));
        vis.Quality.TracksExited.Should().Be(2 * final.Sum(h => Math.Min(h.TracksExited, h.TracksEntered)));
        vis.Quality.BinsBelowTrackTarget.Should().Be(2 * final.Count(h => HealthChecks.TrackCompletionBelow(h.TracksEntered, h.TracksExited, HealthChecks.TrackCompletionTarget)));
        vis.Quality.ResidualBins.Should().Be(2 * p.Health.Count(h => h.ConservationResidual.HasValue));
        summary.DataQuality.Bins.Should().Be(summary.Lanes.Sum(l => l.Quality.Bins));

        // Availability as the ledger's own read.
        var ledger = (await _host.AsCallerAsync(p.Admin, s => s.GetRequiredService<ISvcAvailability>()
            .GetAsync("DMO", new AvailabilityCriteria("2026-09-28", "2026-10-04"), Ct))).Data;
        summary.Availability.Total.Should().Be(ledger.Total);
        summary.Availability.Days.Should().Equal(ledger.Days);
        summary.Availability.Total.Should().Match<Ariva.Core.Availability.AvailabilityCounts>(c => c.RecordedMinutes == 5760 && c.AvailableMinutes == 5740 &&
                                                                                               c.UnavailableMinutes == 20 && c.StaleZoneMinutes == 20 && c.MissingMinuteMinutes == 0);

        // Alerts the border supervisor sees: R-001 (its own) and R-002 (anyone's); the R-001 of the Sunday before is not this week's.
        summary.Alerts.Should().Match<WeeklyAlerts>(a => a.Total == 3 && a.Acknowledged == 2 && a.UnacknowledgedClosed == 1 && a.UnacknowledgedOpen == 0 &&
                                                         a.AcknowledgeP50Minutes == 4 && a.AcknowledgeP90Minutes == 10 && a.AcknowledgeMaxMinutes == 10);
        summary.Alerts.ByDay.Select(d => d.Count).Should().Equal(1 + 1, 0, 0, 1, 0, 0, 0);

        // S-17 out 10 minutes on Monday and on Thursday; DMO-X's copy of the outage is another site's.
        summary.Devices.Single(d => d.Code == "S-17").Should().Match<Ariva.Core.Reports.DeviceUptime>(d => d.OutageMinutes == 20 && d.Outages == 2 && d.UptimePercent == 99.8);

        // Time in each desk state: every arrival desk, as the simulator had it, twice.
        summary.DeskStates.DesksIncluded.Should().BeTrue();
        summary.DeskStates.Desks.Should().HaveCount(22);
        foreach (var desk in summary.DeskStates.Desks)
        {
            desk.Should().Match<WeeklyDesk>(d => d.Checkpoint == "IMM" && d.CheckpointKind == "Immigration" && d.Lanes.Single() == p.Desks[d.Desk].Lane);
            desk.Week.Should().Be(p.Desks[desk.Desk].Week, desk.Desk);
        }

        // A border supervisor gets every border lane and day whole, and no check-in or security lane.
        var oracle = LaneOracle(p.DeskMinutes);
        summary.DeskStates.Lanes.Select(l => l.CheckpointKind).Should().OnlyContain(k => k == "Immigration");
        foreach (var lane in summary.DeskStates.Lanes)
        {
            lane.Should().Match<WeeklyDeskLane>(l => !l.WeekWithheld && !l.DaysWithheld && l.Desks == oracle[l.Lane].Open, lane.Lane);
            lane.Week.Should().Be(oracle[lane.Lane].Week, lane.Lane);
            lane.Days.Should().Equal(oracle[lane.Lane].Days, lane.Lane);
        }

        summary.DeskStates.Lanes.Select(l => l.Lane).Should().BeEquivalentTo(["CIT", "CRW", "RES", "VIS"]);
    }

    [Fact]
    public async Task Week_Should_GiveAnAirportRoleTheAggregatesWithoutDeskRows_When_ATerminalDutyManagerReadsIt()
    {
        var p = await PlantAsync();
        After();

        var border = (await SummaryAsync(p.Border, "DMO", Week)).Data;
        var manager = (await SummaryAsync(p.Manager, "DMO", Week)).Data;

        manager.DeskStates.DesksIncluded.Should().BeFalse();
        manager.DeskStates.Desks.Should().BeEmpty("per-desk figures are border data");

        // Each border cell only where three desks or more had open time: the week by the week's open desks, the days all or none.
        var oracle = LaneOracle(p.DeskMinutes);
        var lanes = manager.DeskStates.Lanes.Where(l => l.CheckpointKind == "Immigration").ToDictionary(l => l.Lane);
        lanes.Keys.Should().BeEquivalentTo(oracle.Keys);
        foreach (var (lane, (open, week, days)) in oracle)
        {
            var shown = lanes[lane];
            var weekWithheld = open < WeeklySummaries.MinDesksForLaneFigures;
            var daysWithheld = weekWithheld || days.Any(d => d.Desks is > 0 and < WeeklySummaries.MinDesksForLaneFigures);
            shown.Should().Match<WeeklyDeskLane>(l => l.WeekWithheld == weekWithheld && l.DaysWithheld == daysWithheld, lane);
            if (weekWithheld)
                shown.Should().Match<WeeklyDeskLane>(l => l.Week == null && l.Desks == null && l.Days.Count == 0, lane);
            else
                shown.Week.Should().Be(week, lane);
            shown.Days.Should().Equal(daysWithheld ? [] : days, lane);
        }

        lanes["CRW"].Should().Match<WeeklyDeskLane>(l => l.WeekWithheld && l.DaysWithheld && l.Week == null && l.Desks == null && l.Days.Count == 0,
            "one desk's figures are that desk's own");
        lanes["VIS"].Should().Match<WeeklyDeskLane>(l => !l.WeekWithheld && l.Desks >= 3);

        // Check-in and security lanes: a duty manager's, as lanes only (one counter, one security lane), never a border role's.
        manager.DeskStates.Lanes.Where(l => l.CheckpointKind is "CheckIn" or "Security").Select(l => (l.CheckpointKind, l.Lane, l.Desks, l.WeekWithheld, l.Week))
            .Should().Equal(("CheckIn", "", 1, false, new DeskStateSeconds(60, 0, 0, 3600, 0, 0)), ("Security", "", 1, false, new DeskStateSeconds(30, 0, 1800, 0, 0, 0)));
        border.DeskStates.Lanes.Should().NotContain(l => l.CheckpointKind == "CheckIn" || l.CheckpointKind == "Security");
        var admin = (await SummaryAsync(p.Admin, "DMO", Week)).Data.DeskStates;
        admin.Lanes.Should().Contain(l => l.CheckpointKind == "CheckIn").And.Contain(l => l.CheckpointKind == "Security");
        admin.Desks.Should().OnlyContain(d => d.CheckpointKind == "Immigration", "no airport desk ever has a row");

        manager.Lanes.Select(l => (l.Zone, l.Passengers, l.P90Minutes)).Should().Equal(border.Lanes.Select(l => (l.Zone, l.Passengers, l.P90Minutes)),
            "lane waits are the same for every reader");
        manager.Alerts.Should().Match<WeeklyAlerts>(a => a.Total == 2 && a.Acknowledged == 1 && a.AcknowledgeP50Minutes == 10, "R-001 belongs to the border supervisors");
        manager.DataQuality.Should().BeEquivalentTo(border.DataQuality);
    }

    [Fact]
    public async Task DeskStates_Should_CountNoMinuteOfAKeyShared_When_ACheckpointCodeIsReusedByAnotherKind()
    {
        var p = await PlantAsync();
        After();
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk_minute WHERE desk_code = 'DMO/IMM/AR-23'")).Should().Be(60, "the shared key's minutes are stored");
        var visDesks = p.Desks.Where(d => d.Value.Lane == "VIS").Select(d => d.Value.Week).ToList();
        var vis = new DeskStateSeconds(visDesks.Sum(w => w.Minutes), visDesks.Sum(w => w.Closed), visDesks.Sum(w => w.Idle), visDesks.Sum(w => w.Serving),
            visDesks.Sum(w => w.Paused), visDesks.Sum(w => w.Unknown));

        foreach (var caller in new[] { p.Border, p.Manager, p.Admin })
        {
            var states = (await SummaryAsync(caller, "DMO", Week)).Data.DeskStates;
            states.Desks.Should().NotContain(d => d.Desk == "AR-23");
            states.Lanes.Single(l => l.CheckpointKind == "Immigration" && l.Lane == "VIS").Week.Should().Be(vis, "the 15 visitors' desks of the simulator, not AR-23's hour");
            states.Lanes.Where(l => l.CheckpointKind is "CheckIn" or "Security").Sum(l => l.Week.Minutes).Should()
                .Be(caller == p.Border ? 0 : 90, "the counter's 60 minutes and the security lane's 30, none of AR-23's");
        }

        var border = (await SummaryAsync(p.Border, "DMO", Week)).Data.DeskStates;
        border.Desks.Should().HaveCount(22);
        foreach (var desk in border.Desks)
            desk.Week.Should().Be(p.Desks[desk.Desk].Week, desk.Desk);
    }

    [Fact]
    public async Task Week_Should_CountOnlyItsOwnSite_When_ANeighbourHoldsTheSameKeysAndCodes()
    {
        var p = await PlantAsync();
        After();

        var dmo = (await SummaryAsync(p.Admin, "DMO", Week)).Data;
        var neighbour = (await SummaryAsync(p.Admin, "DMO-X", Week)).Data;

        neighbour.Lanes.Select(l => l.Zone).Should().BeEquivalentTo(["A-VIS", "CI-C"], "DMO-X has no profile: its zones with minutes only");
        foreach (var lane in neighbour.Lanes)
            lane.Passengers.Should().Be(dmo.Lanes.Single(l => l.Zone == lane.Zone).Passengers, "the same rows under DMO-X's keys");
        neighbour.DeskStates.Desks.Select(d => (d.Desk, d.Week.Minutes)).Should().Equal(dmo.DeskStates.Desks.Select(d => (d.Desk, d.Week.Minutes)));
        neighbour.Availability.Total.AvailableMinutes.Should().Be(0, "DMO-X's ledger says unavailable");
        dmo.Availability.Total.AvailableMinutes.Should().Be(5740);
        neighbour.Alerts.Total.Should().Be(5);
        dmo.Alerts.Total.Should().Be(3);
        neighbour.Devices.Should().BeEmpty("S-17 is DMO's");
        dmo.Lanes.Single(l => l.Zone == "A-VIS").Passengers.Should().Be(2 * p.Passengers.Where(x => x.Key.Zone == "A-VIS").Sum(x => x.Value));

        // A border supervisor of DMO-X only, and roles that do not read reports, get the answer of a site that does not exist.
        (await SummaryAsync(p.Elsewhere, "DMO", Week)).ErrorMessages.Should().Equal(WeeklySummaryErrors.NotFound);
        (await SummaryAsync(p.Elsewhere, "DMO-X", Week)).HasErrors.Should().BeFalse();
        (await SummaryAsync(p.Handler, "DMO", Week)).ErrorMessages.Should().Equal(new[] { WeeklySummaryErrors.NotFound }, "a handler station manager reads no report");
        (await SummaryAsync(p.Observer, "DMO", Week)).ErrorMessages.Should().Equal(WeeklySummaryErrors.NotFound);
        (await SummaryAsync(p.Pending, "DMO", Week)).ErrorMessages.Should().Equal(new[] { WeeklySummaryErrors.NotFound },
            "a border supervisor with a temporary password holds nothing until it is changed, as the permission resolver says");
        (await SummaryAsync(p.Admin, "NOPE", Week)).ErrorMessages.Should().Equal(WeeklySummaryErrors.NotFound);
        (await SummaryAsync(p.Admin, "dmo'--", Week)).ErrorMessages.Should().Equal(WeeklySummaryErrors.NotFound);
        (await SummaryAsync(p.Admin, null, Week)).ErrorMessages.Should().Equal(WeeklySummaryErrors.NotFound);
    }

    [Fact]
    public async Task Week_Should_BeRefused_When_ItIsNotAStartedMondayWithin57WeeksOrTheZoneIsUnknown()
    {
        var p = await PlantAsync();
        After();

        foreach (DateOnly? week in new DateOnly?[] { null, new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 12), new DateOnly(2025, 8, 25) })
            (await SummaryAsync(p.Border, "DMO", week)).ErrorMessages.Should().Equal(new[] { WeeklySummaryErrors.InvalidWeek }, week?.ToString());
        (await SummaryAsync(p.Border, "DMO", new DateOnly(2025, 9, 1))).HasErrors.Should().BeFalse("57 weeks before the current one");
        (await SummaryAsync(p.Border, "DMO", new DateOnly(2026, 10, 5))).Data.Complete.Should().BeFalse("the current week is still running");
        (await SummaryAsync(p.Admin, "XTZ", Week)).ErrorMessages.Should().Equal(new[] { WeeklySummaryErrors.TimeZoneUnknown }, "never summarised in UTC instead");
    }

    [Fact]
    public async Task Reads_Should_BeRefusedRatherThanCutShort_When_TheyPassTheirBound()
    {
        var p = await PlantAsync();
        After();
        var full = (await SummaryAsync(p.Border, "DMO", Week)).Data;
        var deskRows = full.DeskStates.Desks.Sum(d => d.Days.Count(x => x.Seconds.Minutes > 0));
        var roles = new[] { RoleCodes.BorderShiftSupervisor };

        Task<Fluentx.Result<WeeklySummary>> Read(WeeklySummaryLimits limits) => _host.AsCallerAsync(p.Border, s =>
            new WeeklySummaryReader(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<AvailabilityLedgerReader>()) { Limits = limits }
                .ReadAsync("DMO", Week, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai"), roles, _host.Clock.GetUtcNow().UtcDateTime, Ct));

        deskRows.Should().Be(88, "22 desks on Monday, Tuesday, Thursday and Friday");
        foreach (var (limits, what) in new (WeeklySummaryLimits, string)[]
                 {
                     (new WeeklySummaryLimits(MaxLanes: full.Lanes.Count - 1), "lanes"),
                     (new WeeklySummaryLimits(MaxDeskRows: deskRows - 1), "desk rows"),
                     (new WeeklySummaryLimits(MaxAlerts: 2), "alerts"),
                     (new WeeklySummaryLimits(MaxDevices: 0), "devices"),
                     (new WeeklySummaryLimits(MaxOutages: 1), "outages")
                 })
        {
            (await Read(limits)).ErrorMessages.Should().Equal(new[] { WeeklySummaryErrors.TooLarge }, what);
        }

        // The reader refuses a code outside the site pattern itself: as a key prefix in SQL, 'DM_' matches DMO's rows.
        (await _host.AsCallerAsync(p.Border, s => new WeeklySummaryReader(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<AvailabilityLedgerReader>())
            .ReadAsync("DM_", Week, TimeZoneInfo.Utc, roles, _host.Clock.GetUtcNow().UtcDateTime, Ct))).ErrorMessages.Should().Equal(WeeklySummaryErrors.NotFound);

        // And refuses a week the service would refuse: a Tuesday, a week not started.
        foreach (var week in new[] { Week.AddDays(1), Week.AddDays(14) })
        {
            (await _host.AsCallerAsync(p.Border, s => new WeeklySummaryReader(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<AvailabilityLedgerReader>())
                .ReadAsync("DMO", week, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai"), roles, _host.Clock.GetUtcNow().UtcDateTime, Ct))).ErrorMessages
                .Should().Equal(new[] { WeeklySummaryErrors.InvalidWeek }, week.ToString());
        }

        var atBounds = (await Read(new WeeklySummaryLimits(full.Lanes.Count, deskRows, 3, 1, 2))).Data;
        atBounds.Lanes.Should().HaveCount(full.Lanes.Count);
        atBounds.DeskStates.Desks.Should().HaveCount(22);
        atBounds.Alerts.Total.Should().Be(3);
        atBounds.Devices.Single().OutageMinutes.Should().Be(20);
    }

    [Fact]
    public async Task Week_Should_Have169HoursOnTheirLocalDays_When_ItEndsWithTheAutumnChange()
    {
        var p = await PlantAsync();
        _host.Clock.Advance(TimeSpan.FromDays(32));

        var summary = (await SummaryAsync(p.Admin, "XBE", new DateOnly(2026, 10, 19))).Data;

        summary.Should().Match<WeeklySummary>(s => s.TimeZoneId == "Europe/Berlin" && s.Hours == 169 && s.Days[6].Hours == 25);
        var lane = summary.Lanes.Single();
        lane.Days.Select(d => d.Passengers).Should().Equal(20, 0, 0, 0, 0, 20, 60);
        lane.Passengers.Should().Be(100, "Monday 26 October 00:00 local is the next week's");
        lane.Days[6].Hours.Where(h => h.Start == "02:00").Select(h => (h.Waits, h.P90Minutes)).Should().Equal((20L, 4.5), (20L, 10.5));
        lane.Days[6].Hours[^1].Should().Match<WeeklyHour>(h => h.Start == "23:00" && h.Waits == 20);
        var daily = (await _host.AsCallerAsync(p.Admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("XBE", new DateOnly(2026, 10, 25), Ct))).Data;
        lane.Days[6].Hours.Select(h => (h.Start, h.Waits, h.P90Minutes)).Should().Equal(daily.Lanes.Single().Hours.Select(h => (h.Start, h.Waits, h.P90Minutes)));
    }

    [Fact]
    public async Task Week_Should_RunToNow_When_ItIsTheCurrentWeek_And_BeEmpty_When_ItIsBeforeTheData()
    {
        var p = await PlantAsync();

        // Thursday 1 October, 12:00 at the site: the week has run 3,600 + 1,440 minutes; Thursday's outage is still ahead.
        var running = (await SummaryAsync(p.Border, "DMO", Week)).Data;
        running.Complete.Should().BeFalse();
        running.Days.Select(d => d.Started).Should().Equal(true, true, true, true, false, false, false);
        running.Days[3].Ended.Should().BeFalse();
        running.Devices.Single(d => d.Code == "S-17").Should().Match<Ariva.Core.Reports.DeviceUptime>(d => d.OutageMinutes == 10 && d.Outages == 1 && d.UptimePercent == 99.8);

        After();
        var before = (await SummaryAsync(p.Border, "DMO", Week.AddDays(-7))).Data;
        before.Complete.Should().BeTrue();
        before.Lanes.Should().OnlyContain(l => l.Passengers == 0 && l.Peak == null && l.Days.Count == 7);
        before.PeakHours.Should().BeEmpty();
        before.Availability.Total.RecordedMinutes.Should().Be(0);
        before.Availability.Total.Availability.Should().BeNull("no operating minute was recorded");
        before.DeskStates.Lanes.Should().BeEmpty();
        before.Alerts.Total.Should().Be(1, "R-001 of Sunday 27 September, 23:00 at the site");
        before.Devices.Single(d => d.Code == "S-17").UptimePercent.Should().Be(100);
    }
}
