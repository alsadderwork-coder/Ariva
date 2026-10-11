using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ariva.IntegrationTests.Border;

/// <summary>
/// ARV-149 on PostgreSQL: the desk feed gives each AMAN desk without staff or service zones its lane context once a minute
/// (F10 rows 5a to 5f): the lane cycle time per person of the queue zones it serves (AMAN's intervals of every desk serving
/// them) and the people in those zones from their latest live minute (<c>queue_minute</c>). Such a desk is Serving while its
/// transactions keep the rhythm or people wait, Paused after a silence of k x s with people waiting, Idle with nobody
/// waiting; a desk with a staff zone keeps the rows of before; a desk whose queue is not known falls back to T1; a queue
/// length out of bounds is left out and logged with the site and the count only (CWE-501); each site's contexts come from
/// its own desks, intervals and queues only, whatever codes another site shares (CWE-863); a read that reaches its row cap
/// gives no contexts (CWE-120); a context minute read back ahead of the clock counts as absent (CWE-501).
/// </summary>
public sealed partial class ImmigrationIntakeTests
{
    /// <summary>
    /// Sites for the transaction rhythm (once per database), each with a published profile:
    /// <list type="bullet">
    /// <item>XS6: an immigration checkpoint IMM with AMAN desks R1 (Visitors, no zone), R2 (Visitors, with a staff zone) and
    /// R3 (Residents, no zone), mapped to XR1 to XR3, and the queue zones Q-VIS and Q-RES of those lanes on the desks' level;</item>
    /// <item>XS7: the same checkpoint, desk, AMAN and zone codes on a level of its own, R2 without a zone (CWE-863);</item>
    /// <item>XS8: IMM with one AMAN desk R1 (Visitors, XR1) and the queue zone Q-VIS (the row cap and the context minute).</item>
    /// </list>
    /// </summary>
    private Task RhythmSiteAsync() => ExecuteAsMigrationAsync("""
        DO $rhythm$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM zone_profile WHERE id = 'a1490000-0000-0000-0000-000000000020') THEN
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XS6', 'Transaction rhythm probe');
                INSERT INTO terminal (id, airport_id, code, name, site_code)
                     SELECT 'a1490000-0000-0000-0000-000000000001', id, 'XT6', 'Probe terminal 6', 'XS6' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
                INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                     VALUES ('a1490000-0000-0000-0000-000000000002', 'a1490000-0000-0000-0000-000000000001', 'XS6', 'L0', 'Arrivals', 0, 100, 100);
                INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
                     VALUES ('a1490000-0000-0000-0000-000000000003', 'a1490000-0000-0000-0000-000000000002', 'XS6', 'IMM', 'Immigration', 'Immigration');
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                    ('a1490000-0000-0000-0000-000000000011', 'a1490000-0000-0000-0000-000000000003', 'XS6', 'R1', 'Visitors 1', 'Desk', 'VIS'),
                    ('a1490000-0000-0000-0000-000000000012', 'a1490000-0000-0000-0000-000000000003', 'XS6', 'R2', 'Visitors 2', 'Desk', 'VIS'),
                    ('a1490000-0000-0000-0000-000000000013', 'a1490000-0000-0000-0000-000000000003', 'XS6', 'R3', 'Residents 1', 'Desk', 'RES');
                INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code) VALUES
                    (gen_random_uuid(), 'Aman', 'XR1', 'a1490000-0000-0000-0000-000000000011', 'XS6'),
                    (gen_random_uuid(), 'Aman', 'XR2', 'a1490000-0000-0000-0000-000000000012', 'XS6'),
                    (gen_random_uuid(), 'Aman', 'XR3', 'a1490000-0000-0000-0000-000000000013', 'XS6');
                INSERT INTO zone_profile (id, site_code, name, status) VALUES ('a1490000-0000-0000-0000-000000000020', 'XS6', 'Rhythm', 'Draft');
                INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon, lane_category) VALUES
                    ('a1490000-0000-0000-0000-000000000021', 'a1490000-0000-0000-0000-000000000020', 'Q-VIS', 'Queue', 'a1490000-0000-0000-0000-000000000002', NULL, NULL, 'probe', 'VIS'),
                    ('a1490000-0000-0000-0000-000000000022', 'a1490000-0000-0000-0000-000000000020', 'Q-RES', 'Queue', 'a1490000-0000-0000-0000-000000000002', NULL, NULL, 'probe', 'RES');
                INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                    ('a1490000-0000-0000-0000-000000000031', 'a1490000-0000-0000-0000-000000000020', 'R2 staff', 'Staff', 'a1490000-0000-0000-0000-000000000002',
                     'a1490000-0000-0000-0000-000000000021', 'a1490000-0000-0000-0000-000000000012', 'probe');
                UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('c', 64), published_on = now() WHERE id = 'a1490000-0000-0000-0000-000000000020';
            END IF;

            IF NOT EXISTS (SELECT 1 FROM zone_profile WHERE id = 'a1490000-0000-0000-0000-000000000120') THEN
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XS7', 'Transaction rhythm neighbour');
                INSERT INTO terminal (id, airport_id, code, name, site_code)
                     SELECT 'a1490000-0000-0000-0000-000000000101', id, 'XT7', 'Probe terminal 7', 'XS7' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
                INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                     VALUES ('a1490000-0000-0000-0000-000000000102', 'a1490000-0000-0000-0000-000000000101', 'XS7', 'L0', 'Arrivals', 0, 100, 100);
                INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
                     VALUES ('a1490000-0000-0000-0000-000000000103', 'a1490000-0000-0000-0000-000000000102', 'XS7', 'IMM', 'Immigration', 'Immigration');
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                    ('a1490000-0000-0000-0000-000000000111', 'a1490000-0000-0000-0000-000000000103', 'XS7', 'R1', 'Visitors 1', 'Desk', 'VIS'),
                    ('a1490000-0000-0000-0000-000000000112', 'a1490000-0000-0000-0000-000000000103', 'XS7', 'R2', 'Visitors 2', 'Desk', 'VIS'),
                    ('a1490000-0000-0000-0000-000000000113', 'a1490000-0000-0000-0000-000000000103', 'XS7', 'R3', 'Residents 1', 'Desk', 'RES');
                INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code) VALUES
                    (gen_random_uuid(), 'Aman', 'XR1', 'a1490000-0000-0000-0000-000000000111', 'XS7'),
                    (gen_random_uuid(), 'Aman', 'XR2', 'a1490000-0000-0000-0000-000000000112', 'XS7'),
                    (gen_random_uuid(), 'Aman', 'XR3', 'a1490000-0000-0000-0000-000000000113', 'XS7');
                INSERT INTO zone_profile (id, site_code, name, status) VALUES ('a1490000-0000-0000-0000-000000000120', 'XS7', 'Rhythm neighbour', 'Draft');
                INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon, lane_category) VALUES
                    ('a1490000-0000-0000-0000-000000000121', 'a1490000-0000-0000-0000-000000000120', 'Q-VIS', 'Queue', 'a1490000-0000-0000-0000-000000000102', NULL, NULL, 'probe', 'VIS'),
                    ('a1490000-0000-0000-0000-000000000122', 'a1490000-0000-0000-0000-000000000120', 'Q-RES', 'Queue', 'a1490000-0000-0000-0000-000000000102', NULL, NULL, 'probe', 'RES');
                UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('d', 64), published_on = now() WHERE id = 'a1490000-0000-0000-0000-000000000120';
            END IF;

            IF NOT EXISTS (SELECT 1 FROM zone_profile WHERE id = 'a1490000-0000-0000-0000-000000000220') THEN
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'XS8', 'Transaction rhythm cap');
                INSERT INTO terminal (id, airport_id, code, name, site_code)
                     SELECT 'a1490000-0000-0000-0000-000000000201', id, 'XT8', 'Probe terminal 8', 'XS8' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
                INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                     VALUES ('a1490000-0000-0000-0000-000000000202', 'a1490000-0000-0000-0000-000000000201', 'XS8', 'L0', 'Arrivals', 0, 100, 100);
                INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
                     VALUES ('a1490000-0000-0000-0000-000000000203', 'a1490000-0000-0000-0000-000000000202', 'XS8', 'IMM', 'Immigration', 'Immigration');
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                    ('a1490000-0000-0000-0000-000000000211', 'a1490000-0000-0000-0000-000000000203', 'XS8', 'R1', 'Visitors 1', 'Desk', 'VIS');
                INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code) VALUES
                    (gen_random_uuid(), 'Aman', 'XR1', 'a1490000-0000-0000-0000-000000000211', 'XS8');
                INSERT INTO zone_profile (id, site_code, name, status) VALUES ('a1490000-0000-0000-0000-000000000220', 'XS8', 'Rhythm cap', 'Draft');
                INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon, lane_category) VALUES
                    ('a1490000-0000-0000-0000-000000000221', 'a1490000-0000-0000-0000-000000000220', 'Q-VIS', 'Queue', 'a1490000-0000-0000-0000-000000000202', NULL, NULL, 'probe', 'VIS');
                UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('e', 64), published_on = now() WHERE id = 'a1490000-0000-0000-0000-000000000220';
            END IF;
        END
        $rhythm$;
        """);

    [Fact]
    public async Task DeskFeed_Should_FollowTheTransactionRhythm_When_ADeskHasAmansSourcesAlone()
    {
        await SeedAsync();
        await RhythmSiteAsync();
        _host.Clock.Advance(TimeSpan.FromDays(60));
        var log = new FeedLog();
        var feed = new Ariva.Infra.Border.DeskFeed(_host.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>(), _host.Clock,
            new Ariva.Infra.Border.DeskFeedSettings(), log);
        var t0 = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
        _host.Clock.Advance(t0 - Now);
        (await feed.SiteAsync("XS6", Ct)).Should().BeTrue("the engine starts now");
        (await feed.SiteAsync("XS7", Ct)).Should().BeTrue();
        var id = 0;
        DateTimeOffset At(int m, int s = 0) => new(t0.AddMinutes(m).AddSeconds(s));
        DeskIntervalStats Interval(string site, string desk, string lane, int m, int transactions, double service = 90) =>
            new(site, desk, At(m), 60, transactions, transactions, transactions > 0 ? service : 0, transactions > 0 ? service : 0, transactions > 0 ? service : 0, lane,
                $"x6-{++id}");

        // XS6: all three desks log in at 0:20 and AMAN reports every desk's interval each minute (so its feed lives); R1
        // completes a traveller in the intervals of minutes 1 and 3 (90 s each). People wait in Q-VIS in minutes 0 to 9,
        // nobody after; Q-RES has no live minute until minute 12, whose length is beyond any queue.
        // XS7, the same minutes with the same checkpoint, desk, AMAN and zone codes (CWE-863): R1 completes one traveller at
        // 3,000 s in the interval of minute 1 and R2 none; nobody waits in its Q-VIS in minutes 0 to 9, people from 10 on.
        for (var m = 0; m < 15; m++)
        {
            _host.Clock.Advance(t0.AddMinutes(m + 1).AddSeconds(5) - Now);
            foreach (var site in new[] { "XS6", "XS7" })
            {
                if (m == 0)
                {
                    foreach (var desk in new[] { "XR1", "XR2", "XR3" })
                        (await Apply<DeskSessionChanged>(i => i.ApplyDeskSessionsAsync(ImmigrationScope.ForSite(site), "aman-kafka",
                            [new DeskSessionChanged(site, desk, DeskSessionState.Opened, desk == "XR3" ? "RES" : "VIS", At(0, 20), $"x6-{++id}")], Ct))).Should().OnlyContain(r => r.Applied);
                }

                var r1 = site == "XS6" ? Interval(site, "XR1", "VIS", m, m is 1 or 3 ? 1 : 0) : Interval(site, "XR1", "VIS", m, m == 1 ? 1 : 0, service: 3_000);
                (await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite(site), "aman-kafka",
                    [r1, Interval(site, "XR2", "VIS", m, 0), Interval(site, "XR3", "RES", m, 0)], Ct))).Should().OnlyContain(r => r.Applied);
            }

            await QueueMinuteAsync("XS6/Q-VIS", t0.AddMinutes(m), m < 10 ? 4 : 0);
            await QueueMinuteAsync("XS7/Q-VIS", t0.AddMinutes(m), m < 10 ? 0 : 3);
            if (m == 12)
                await QueueMinuteAsync("XS6/Q-RES", t0.AddMinutes(m), Ariva.Core.Desks.DeskLaneContext.MaxWaiting + 1);
            (await feed.SiteAsync("XS6", Ct)).Should().BeTrue();
            (await feed.SiteAsync("XS7", Ct)).Should().BeTrue();
        }

        _host.Clock.Advance(t0.AddMinutes(17) - Now);
        (await feed.SiteAsync("XS6", Ct)).Should().BeTrue();
        (await feed.SiteAsync("XS7", Ct)).Should().BeTrue();

        var minutes = await RowsAsync("""
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60,
                   concat_ws('|', closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, sensor_derived_seconds, degraded)
              FROM desk_minute WHERE (desk_code LIKE 'XS6/%' OR desk_code LIKE 'XS7/%') AND minute_utc >= @from AND minute_utc < @to
            """, r => (r.GetString(0), r.GetString(1)), t0, t0.AddMinutes(15));
        string Minute(string site, string desk, int m) => minutes[$"{site}/IMM/{desk}@{m}"];

        // XS6's R1: Serving while people wait (5b) and its transactions keep the rhythm (5a); Paused 6 minutes (4 x 1.5)
        // after the interval ending at 4 with people still waiting (5c); Idle once nobody waits (5d). Each read's contexts
        // apply from the engine's watermark (the read's time less the 90-second lateness) with the latest queue minute before
        // it: the read at 12:05 carries the empty minute 10 and applies from 10:35. XS7's 3,000-second service time would
        // keep it from pausing (P would be 30 minutes) and XS7's queue would keep it Serving from 11 on.
        for (var m = 1; m < 10; m++)
            Minute("XS6", "R1", m).Should().Be("0|0|60|0|0|0|f", "XS6 R1 serving in minute {0}", m);
        Minute("XS6", "R1", 10).Should().Be("0|25|0|35|0|0|f", "XS6 R1 silent for P with people waiting, then nobody waiting");
        for (var m = 11; m < 15; m++)
            Minute("XS6", "R1", m).Should().Be("0|60|0|0|0|0|f", "XS6 R1 logged in with nobody waiting in minute {0}", m);

        // XS6's R2 has a staff zone (never heard): the rows of before, Paused T1 after its login without a transaction, whatever the queue.
        for (var m = 4; m < 15; m++)
            Minute("XS6", "R2", m).Should().Be("0|0|0|60|0|0|f", "XS6 R2 in minute {0}", m);
        // R3's queue is not known at either site (no live minute; at XS6 then one out of bounds): T1 after its login, as before.
        for (var m = 4; m < 15; m++)
        {
            Minute("XS6", "R3", m).Should().Be("0|0|0|60|0|0|f", "XS6 R3 in minute {0}", m);
            Minute("XS7", "R3", m).Should().Be("0|0|0|60|0|0|f", "XS7 R3 in minute {0}", m);
        }

        // XS7, the converse (CWE-863): its own s, 3,000 s, keeps R1 Serving through the rhythm window from the interval ending
        // at 2 (with XS6's 90 s it would be Idle from 4.5); its own queue leaves R2 Idle in minutes 1 to 9 (XS6's people
        // would make it Serving) and Serving from 10:35, when its minute 10 with people applies (XS6's empty queue would
        // leave it Idle).
        Minute("XS7", "R1", 1).Should().Be("0|60|0|0|0|0|f", "XS7 R1 logged in with nobody waiting before its first transaction");
        for (var m = 2; m < 15; m++)
            Minute("XS7", "R1", m).Should().Be("0|0|60|0|0|0|f", "XS7 R1 in its own rhythm in minute {0}", m);
        for (var m = 1; m < 10; m++)
            Minute("XS7", "R2", m).Should().Be("0|60|0|0|0|0|f", "XS7 R2 with nobody waiting in its own queue in minute {0}", m);
        Minute("XS7", "R2", 10).Should().Be("0|35|25|0|0|0|f", "XS7's people waiting apply from 10:35");
        for (var m = 11; m < 15; m++)
            Minute("XS7", "R2", m).Should().Be("0|0|60|0|0|0|f", "XS7 R2 with people waiting in its own queue in minute {0}", m);

        // The minute of the latest contexts is kept with the engine, so each minute gives them once.
        (await _host.ReadAsync<string>("SELECT state->>'contextUtc' FROM desk_feed_state WHERE site_code = 'XS6'")).Should().NotBeNullOrEmpty();

        // CWE-501: the queue length out of bounds was left out, with a warning that names the site and the count only. It is
        // read again, and left out again, by each minute's contexts while it is the zone's latest fresh minute; XS7 has none.
        log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("outside their bounds", StringComparison.Ordinal)).Should().NotBeEmpty()
            .And.OnlyContain(e => e.Values.Count == 3 && Equals(e.Values["Site"], "XS6") && Equals(e.Values["Count"], 1) &&
                !e.Message.Contains("Q-RES", StringComparison.Ordinal) && !e.Message.Contains("R3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeskFeed_Should_GiveNoLaneContextAndWarnOnce_When_ARead_ReachesItsRowCap()
    {
        await SeedAsync();
        await RhythmSiteAsync();
        _host.Clock.Advance(TimeSpan.FromDays(90));
        var log = new FeedLog();
        var feed = new Ariva.Infra.Border.DeskFeed(_host.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>(), _host.Clock,
            new Ariva.Infra.Border.DeskFeedSettings(), log);
        var t0 = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
        _host.Clock.Advance(t0 - Now);
        (await feed.SiteAsync("XS8", Ct)).Should().BeTrue("the engine starts now");
        var id = 0;
        DateTimeOffset At(int m, int s = 0) => new(t0.AddMinutes(m).AddSeconds(s));
        IEnumerable<FeedLogEntry> Capped() => log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("lane context rows", StringComparison.Ordinal));
        DateTime? before = null;

        // R1 logs in at 0:20 and completes nothing; nobody waits. The reads of minutes 4 and 5 meet 30,000 AMAN intervals of
        // the matched desk in the rhythm window (received a day ago, so the AMAN read does not take them).
        for (var m = 0; m < 9; m++)
        {
            _host.Clock.Advance(t0.AddMinutes(m + 1).AddSeconds(5) - Now);
            if (m == 0)
            {
                (await Apply<DeskSessionChanged>(i => i.ApplyDeskSessionsAsync(ImmigrationScope.ForSite("XS8"), "aman-kafka",
                    [new DeskSessionChanged("XS8", "XR1", DeskSessionState.Opened, "VIS", At(0, 20), $"x8-{++id}")], Ct))).Should().OnlyContain(r => r.Applied);
            }

            (await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("XS8"), "aman-kafka",
                [new DeskIntervalStats("XS8", "XR1", At(m), 60, 0, 0, 0, 0, 0, "VIS", $"x8-{++id}")], Ct))).Should().OnlyContain(r => r.Applied);
            await QueueMinuteAsync("XS8/Q-VIS", t0.AddMinutes(m), 0);
            if (m == 4)
                await CapIntervalsAsync(t0.AddMinutes(3), plant: true);
            if (m == 6)
                await CapIntervalsAsync(t0.AddMinutes(3), plant: false);
            (await feed.SiteAsync("XS8", Ct)).Should().BeTrue();

            if (m == 3)
                before = await LaneContextAtAsync("XS8", "XS8/IMM/R1");
            if (m == 4)
            {
                // CWE-120: the capped read gives no context (the desk keeps the one before) and exactly one warning, with the
                // site and the cap only.
                (await LaneContextAtAsync("XS8", "XS8/IMM/R1")).Should().Be(before).And.Be(t0.AddMinutes(2).AddSeconds(35));
                Capped().Should().ContainSingle().Which.Should().Match<FeedLogEntry>(e =>
                    e.Values.Count == 3 && Equals(e.Values["Site"], "XS8") && Equals(e.Values["Max"], 30_000) && !e.Message.Contains("R1", StringComparison.Ordinal));
            }
        }

        // One warning per capped read (minutes 4 and 5); the context of 2:35 goes stale at 4:35 and the desk follows rows 5
        // and 7 (silent since its login: Paused) until the read of minute 6 brings a context again at 5:35 (nobody waiting: Idle).
        Capped().Should().HaveCount(2).And.OnlyContain(e => e.Values.Count == 3 && Equals(e.Values["Site"], "XS8") && Equals(e.Values["Max"], 30_000));
        var minutes = await RowsAsync("""
            SELECT desk_code || '@' || extract(epoch FROM minute_utc - @from)::int / 60,
                   concat_ws('|', closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, sensor_derived_seconds, degraded)
              FROM desk_minute WHERE desk_code LIKE 'XS8/%' AND minute_utc >= @from AND minute_utc < @to
            """, r => (r.GetString(0), r.GetString(1)), t0, t0.AddMinutes(7));
        minutes["XS8/IMM/R1@3"].Should().Be("0|60|0|0|0|0|f", "logged in with nobody waiting");
        minutes["XS8/IMM/R1@4"].Should().Be("0|35|0|25|0|0|f", "no context since the read of minute 3: rows 5 and 7 from 4:35");
        minutes["XS8/IMM/R1@5"].Should().Be("0|25|0|35|0|0|f", "a context again from 5:35");
        minutes["XS8/IMM/R1@6"].Should().Be("0|60|0|0|0|0|f");

        // CWE-501: a context minute read back ahead of the clock counts as absent (else it would hold the contexts back until
        // then): the read gives its contexts and keeps its own minute, with a warning that names the site only.
        await ExecuteAsMigrationAsync("""UPDATE desk_feed_state SET state = jsonb_set(state, '{contextUtc}', '"2999-01-01T00:00:00Z"') WHERE site_code = 'XS8'""");
        _host.Clock.Advance(t0.AddMinutes(10).AddSeconds(5) - Now);
        (await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("XS8"), "aman-kafka",
            [new DeskIntervalStats("XS8", "XR1", At(9), 60, 0, 0, 0, 0, 0, "VIS", $"x8-{++id}")], Ct))).Should().OnlyContain(r => r.Applied);
        (await feed.SiteAsync("XS8", Ct)).Should().BeTrue();
        (await _host.ReadAsync<string>("SELECT state->>'contextUtc' FROM desk_feed_state WHERE site_code = 'XS8'")).Should().NotStartWith("2999");
        (await LaneContextAtAsync("XS8", "XS8/IMM/R1")).Should().Be(t0.AddMinutes(8).AddSeconds(35), "the read's contexts apply from its watermark");
        log.Entries.Where(e => e.Message.Contains("ahead of the clock", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Match<FeedLogEntry>(e => e.Level == LogLevel.Warning && e.Values.Count == 2 && Equals(e.Values["Site"], "XS8"));
    }

    // 30,000 AMAN intervals of XS8's R1 in the minutes before `latest`, received a day before it (planted), or removed.
    private async Task CapIntervalsAsync(DateTime latest, bool plant)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = plant
            ? new NpgsqlCommand("""
                INSERT INTO border_desk_interval (id, site_code, desk_code, desk_id, interval_start_utc, transactions_processed, documents_processed,
                                                  mean_service_seconds, p90_service_seconds, mean_cycle_seconds, lane_category, feed, source_event_id, received_utc)
                SELECT gen_random_uuid(), 'XS8', 'XR1', d.id, @latest - make_interval(mins => k % 10), 0, 0, 0, 0, 0, 'VIS', 'aman-kafka', 'x8-cap-' || k,
                       @latest - interval '1 day'
                  FROM generate_series(1, 30000) AS k, desk d
                 WHERE d.site_code = 'XS8' AND d.code = 'R1' AND d.deleted_on IS NULL
                """, connection)
            : new NpgsqlCommand("DELETE FROM border_desk_interval WHERE site_code = 'XS8' AND source_event_id LIKE 'x8-cap-%'", connection);
        command.Parameters.AddWithValue("latest", latest);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(30_000);
    }

    // The time of the latest lane context a desk's engine took, from the desk feed's saved state.
    private async Task<DateTime?> LaneContextAtAsync(string site, string deskKey)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT (d->'memory'->>'laneContextAt')::timestamptz
              FROM desk_feed_state s, jsonb_array_elements(s.state->'engine'->'desks') AS d
             WHERE s.site_code = @site AND d->>'deskCode' = @desk
            """, connection);
        command.Parameters.AddWithValue("site", site);
        command.Parameters.AddWithValue("desk", deskKey);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
    }

    private async Task QueueMinuteAsync(string zoneKey, DateTime minute, int length)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, length_from_sensors, length_degraded, updated_on)
            VALUES (@zone, @minute, 1, @length, true, false, now())
            ON CONFLICT (zone_key, minute_utc) DO UPDATE SET queue_length = EXCLUDED.queue_length
            """, connection);
        command.Parameters.AddWithValue("zone", zoneKey);
        command.Parameters.AddWithValue("minute", minute);
        command.Parameters.AddWithValue("length", length);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record FeedLogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object> Values);

    // Keeps the desk feed's log lines with their structured values, to prove what a warning carries.
    private sealed class FeedLog : ILogger<Ariva.Infra.Border.DeskFeed>
    {
        private readonly List<FeedLogEntry> _entries = [];

        public IReadOnlyList<FeedLogEntry> Entries
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
                _entries.Add(new FeedLogEntry(logLevel, formatter(state, exception), values));
        }
    }
}
