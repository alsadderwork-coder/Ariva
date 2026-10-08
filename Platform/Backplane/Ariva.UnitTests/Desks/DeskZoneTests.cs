using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-116: staff and service zone readings from the zone processor to the desk engine. Only counts cross (no track id
/// reaches a desk sample or a desk minute); the filter in between and the engine stay bounded under a flood of readings;
/// snapshots continue exactly and refuse hostile values; a reading flagged by the sensing pipeline makes the desk's state
/// Degraded; AMAN's session keeps its higher rank; a desk without a live login source pauses after the sensor T1.
/// </summary>
public sealed class DeskZoneTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private const string Desk = "DMO/IMM/AR-08";
    private const string OtherDesk = "DMO/IMM/AR-09";

    private static readonly IReadOnlyDictionary<string, DeskZoneLink> Links = new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal)
    {
        ["AR-08 staff"] = new(Desk, DeskSource.StaffZone),
        ["AR-08 service"] = new(Desk, DeskSource.ServiceZone),
        ["AR-09 staff"] = new(OtherDesk, DeskSource.StaffZone)
    };

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    #region DeskZoneReadings

    [Fact]
    public void Accept_Should_PassTheDeskKeyRoleTimeAndCountOnly_When_TheZoneNamesADesk()
    {
        var readings = new DeskZoneReadings(Links);

        var staff = readings.Accept("AR-08 staff", 1, At(0), degraded: false);
        var service = readings.Accept("AR-08 service", 0, At(0), degraded: true);

        staff.Should().Be(new DeskZoneSample(Desk, DeskSource.StaffZone, At(0), 1, false));
        service.Should().Be(new DeskZoneSample(Desk, DeskSource.ServiceZone, At(0), 0, true));
        readings.Accept("A-VIS", 3, At(1), false).Should().BeNull("a queue zone is not a desk zone");
        readings.Accept(null, 3, At(1), false).Should().BeNull();
        readings.Links("AR-08 staff").Should().BeTrue();
        readings.Links("A-VIS").Should().BeFalse();
    }

    [Fact]
    public void DeskZoneSample_Should_HoldNoIdentity_When_ItsShapeIsInspected()
    {
        // Data boundary: a desk key, a role, a time, a count and a flag; nothing that could carry a track, officer,
        // traveller or document identity.
        typeof(DeskZoneSample).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(nameof(DeskZoneSample.DeskKey), nameof(DeskZoneSample.Source), nameof(DeskZoneSample.TimeUtc), nameof(DeskZoneSample.Count),
                nameof(DeskZoneSample.Degraded));
        typeof(DeskMinute).GetProperties().Select(p => p.Name).Where(n => n.Contains("Track", StringComparison.OrdinalIgnoreCase) ||
                                                                         n.Contains("Officer", StringComparison.OrdinalIgnoreCase) ||
                                                                         n.Contains("Traveller", StringComparison.OrdinalIgnoreCase) ||
                                                                         n.Contains("Person", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    [Fact]
    public void Accept_Should_DropAReading_When_ItIsNotLaterThanTheLatestPassedOn()
    {
        var readings = new DeskZoneReadings(Links);
        readings.Accept("AR-08 staff", 1, At(10), false).Should().NotBeNull();

        readings.Accept("AR-08 staff", 0, At(10), false).Should().BeNull("the same moment again");
        readings.Accept("AR-08 staff", 0, At(5), false).Should().BeNull("older than the latest");
        readings.Accept("AR-08 service", 1, At(5), false).Should().NotBeNull("another role has its own order");

        readings.Counters.Superseded.Should().Be(2);
    }

    [Fact]
    public void Accept_Should_DropAnUnchangedReading_When_TheLatestIsYoungerThanTheKeepAlive()
    {
        var readings = new DeskZoneReadings(Links);
        readings.Accept("AR-08 staff", 1, At(0), false).Should().NotBeNull();

        readings.Accept("AR-08 staff", 1, At(14.9), false).Should().BeNull("nothing new within 15 seconds");
        readings.Accept("AR-08 staff", 1, At(15), false).Should().NotBeNull("the keep-alive keeps the source heard");
        readings.Accept("AR-08 staff", 0, At(16), false).Should().NotBeNull("a change passes at once");
        readings.Accept("AR-08 staff", 0, At(17), true).Should().NotBeNull("so does a change of flag");

        readings.Counters.Should().Be(new DeskZoneReadingCounters(Passed: 4, Unchanged: 1, Superseded: 0, Capped: 0, Invalid: 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    [InlineData(int.MaxValue)]
    public void Accept_Should_RefuseACount_When_ItIsNoDesksCount(int count)
    {
        var readings = new DeskZoneReadings(Links);

        readings.Accept("AR-08 staff", count, At(0), false).Should().BeNull();
        readings.Counters.Invalid.Should().Be(1);
        readings.Capture().Should().BeEmpty("a refused reading leaves no memory");
    }

    [Fact]
    public void Accept_Should_PassAtMostSixtyPerDeskZoneAndMinute_When_ADeviceFloodsReadings()
    {
        var readings = new DeskZoneReadings(Links);
        var passed = new List<DeskZoneSample>();

        // 100,000 alternating readings in one minute (every change is news), then one in the next minute.
        for (var i = 0; i < 100_000; i++)
        {
            if (readings.Accept("AR-08 staff", i % 2, T0.AddTicks(1 + i * 5_000L), false) is { } s)
                passed.Add(s);
        }

        var next = readings.Accept("AR-08 staff", 1, T0.AddMinutes(1).AddSeconds(1), false);

        passed.Should().HaveCount(DeskZoneReadings.MaxPerDeskZoneMinute);
        // Past the bound the latest passed on stays as it was, so a repeat of its count within 15 seconds counts as unchanged.
        readings.Counters.Capped.Should().BePositive();
        (readings.Counters.Capped + readings.Counters.Unchanged).Should().Be(100_000 - DeskZoneReadings.MaxPerDeskZoneMinute);
        next.Should().NotBeNull("the next minute's first reading brings the state back");
        readings.Capture().Should().HaveCount(1, "the memory is one entry per desk zone heard");
    }

    [Fact]
    public void Constructor_Should_Refuse_When_TheLinksAreNotOneZonePerDeskRoleOrNotDeskZones()
    {
        var twice = new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal) { ["a"] = new(Desk, DeskSource.StaffZone), ["b"] = new(Desk, DeskSource.StaffZone) };
        var session = new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal) { ["a"] = new(Desk, DeskSource.Session) };
        var overlong = new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal) { ["a"] = new("DMO/" + new string('X', 61), DeskSource.StaffZone) };

        ((Action)(() => _ = new DeskZoneReadings(twice))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new DeskZoneReadings(session))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new DeskZoneReadings(overlong))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new DeskZoneReadings(null))).Should().NotThrow("no links: nothing is a desk zone");
    }

    [Fact]
    public void Restore_Should_ContinueExactly_When_TheMemoryGoesThroughJson()
    {
        var steps = Enumerable.Range(0, 400).Select(i => (Zone: i % 3 == 0 ? "AR-08 service" : i % 3 == 1 ? "AR-08 staff" : "AR-09 staff", Count: i / 7 % 2, At: At(i * 0.7))).ToList();
        var whole = new DeskZoneReadings(Links);
        var expected = steps.Select(s => whole.Accept(s.Zone, s.Count, s.At, false)).ToList();

        var first = new DeskZoneReadings(Links);
        var actual = steps.Take(150).Select(s => first.Accept(s.Zone, s.Count, s.At, false)).ToList();
        var json = JsonSerializer.Serialize(first.Capture());
        var second = DeskZoneReadings.Restore(Links, JsonSerializer.Deserialize<List<DeskZoneMemoState>>(json), At(1_000));
        actual.AddRange(steps.Skip(150).Select(s => second.Accept(s.Zone, s.Count, s.At, false)));

        actual.Should().Equal(expected);
    }

    public static TheoryData<string, DeskZoneMemoState[]> HostileMemos => new()
    {
        { "count above a desk's", [new(Desk, DeskSource.StaffZone, T0, 51, false, 1)] },
        { "negative count", [new(Desk, DeskSource.StaffZone, T0, -1, false, 1)] },
        { "no reading in its minute", [new(Desk, DeskSource.StaffZone, T0, 1, false, 0)] },
        { "more than the minute's bound", [new(Desk, DeskSource.StaffZone, T0, 1, false, 61)] },
        { "before 2000", [new(Desk, DeskSource.StaffZone, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, false, 1)] },
        { "more than a day after the clock", [new(Desk, DeskSource.StaffZone, T0.AddDays(3), 1, false, 1)] },
        { "twice", [new(Desk, DeskSource.StaffZone, T0, 1, false, 1), new(Desk, DeskSource.StaffZone, T0.AddSeconds(1), 1, false, 1)] },
        { "an empty entry", [null] }
    };

    [Theory]
    [MemberData(nameof(HostileMemos))]
    public void Restore_Should_Refuse_When_TheMemoryIsHostile(string why, DeskZoneMemoState[] memo)
    {
        var act = () => DeskZoneReadings.Restore(Links, memo, T0.AddHours(1));

        act.Should().Throw<InvalidDataException>(why);
    }

    [Fact]
    public void Restore_Should_RefuseTooManyEntriesAndForgetUnlinkedOnes_When_TheSnapshotSaysSo()
    {
        var flood = Enumerable.Range(0, DeskZoneReadings.MaxLinks + 1).Select(i => new DeskZoneMemoState($"DMO/X/{i}", DeskSource.StaffZone, T0, 1, false, 1)).ToList();
        var renamed = new List<DeskZoneMemoState> { new("DMO/IMM/AR-99", DeskSource.StaffZone, T0, 1, false, 1), new(Desk, DeskSource.StaffZone, T0, 1, false, 1) };

        ((Action)(() => DeskZoneReadings.Restore(Links, flood, T0))).Should().Throw<InvalidDataException>();
        var restored = DeskZoneReadings.Restore(Links, renamed, T0);
        restored.Capture().Should().ContainSingle().Which.DeskKey.Should().Be(Desk);
        restored.Accept("AR-08 staff", 1, T0.AddSeconds(1), false).Should().BeNull("the restored memory holds the latest reading");
    }

    #endregion

    #region Zone processor

    private static QueueZoneGeometry Geometry() =>
        new("A-VIS", new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" }, new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal))
        {
            DeskZones = Links
        };

    private static T Stamp<T>(T batch, DateTime received) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.SiteCode = "DMO";
        batch.QueueZoneName = "A-VIS";
        batch.DeviceCode = "S-18";
        batch.DeviceId = Guid.Parse("0199a000-0000-7000-8000-000000000018");
        batch.Commissioned = true;
        batch.ReceivedUtc = received;
        return batch;
    }

    private static ZoneOccupancyBatch Occupancy(DateTime received, params (string Zone, int Count, DateTime At, SensedFlags Flags)[] readings) =>
        Stamp(new ZoneOccupancyBatch { Occupancy = [.. readings.Select(r => new Sensed<ZoneOccupancy>(new ZoneOccupancy(r.Zone, r.Count, r.At), r.At, r.Flags))] }, received);

    [Fact]
    public void ZoneProcessor_Should_SendDeskZoneCountsToTheDesksAndNotToTheQueue_When_ABatchHoldsBoth()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);
        var queueOnly = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);

        zone.Offer(Occupancy(At(70), ("A-VIS", 7, At(60), SensedFlags.None), ("AR-08 staff", 1, At(60), SensedFlags.None),
            ("AR-08 service", 1, At(60), SensedFlags.Corrected)), At(70));
        queueOnly.Offer(Occupancy(At(70), ("A-VIS", 7, At(60), SensedFlags.None)), At(70));
        zone.Tick(At(95));
        queueOnly.Tick(At(95));
        var outputs = zone.Drain();
        var expected = queueOnly.Drain();

        outputs.DeskReadings.Should().Equal(
            new DeskZoneSample(Desk, DeskSource.StaffZone, At(60), 1, false),
            new DeskZoneSample(Desk, DeskSource.ServiceZone, At(60), 1, true));
        outputs.Live.Should().Equal(expected.Live, "the desk zones' people are not in the queue");
        outputs.Live[^1].Should().Match<QueueLiveMinute>(l => l.QueueLength == 7 && l.LengthMeasured);
        outputs.Minutes.Should().BeEquivalentTo(expected.Minutes);
        zone.Counters.Invalid.Should().Be(0);
    }

    [Fact]
    public void ZoneProcessor_Should_LetNoTrackIdReachTheDesks_When_TrackedCrossingsAndDeskZonesArriveTogether()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);
        string[] tracks = ["S-18/track-1", "S-18/track-2"];

        zone.Offer(Stamp(new VendorLineCrossingBatch
        {
            Crossings = [.. tracks.Select((t, i) => new Sensed<LineCrossing>(new LineCrossing("A-VIS entry", CrossingDirection.In, t, At(30 + i)), At(30 + i), SensedFlags.None))]
        }, At(40)), At(40));
        zone.Offer(Stamp(new TrackSampleBatch
        {
            Samples = [.. tracks.Select(t => new Sensed<TrackPosition>(new TrackPosition(t, 1, 1, 1.7, At(35)), At(35), SensedFlags.None))]
        }, At(41)), At(41));
        zone.Offer(Occupancy(At(70), ("AR-08 staff", 1, At(60), SensedFlags.None), ("AR-09 staff", 0, At(60), SensedFlags.None)), At(70));
        var outputs = zone.Drain();

        outputs.DeskReadings.Should().HaveCount(2);
        var json = JsonSerializer.Serialize(outputs.DeskReadings, ReplayLedger.Json);
        foreach (var track in tracks.Append("track-1").Append("track-2"))
            json.Should().NotContain(track);
        JsonSerializer.Serialize(zone.Capture().DeskZones).Should().NotContain("track");
    }

    [Fact]
    public void ZoneProcessor_Should_StayBoundedPerDeskZoneAndMinute_When_ADeviceFloodsDeskReadings()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);

        // 30 batches of 3,000 alternating staff readings each, all in the same minute.
        for (var b = 0; b < 30; b++)
        {
            var readings = Enumerable.Range(0, ZoneProcessor.MaxEventsPerBatch)
                .Select(i => ("AR-08 staff", i % 2, T0.AddTicks(1 + (b * ZoneProcessor.MaxEventsPerBatch + i) * 1_000L), SensedFlags.None)).ToArray();
            zone.Offer(Occupancy(At(59), readings), At(59));
        }

        var outputs = zone.Drain();
        outputs.DeskReadings.Should().HaveCount(DeskZoneReadings.MaxPerDeskZoneMinute);
        (zone.DeskZoneCounters.Capped + zone.DeskZoneCounters.Unchanged).Should().Be(30 * ZoneProcessor.MaxEventsPerBatch - DeskZoneReadings.MaxPerDeskZoneMinute);
        zone.Full.Should().BeFalse("the pending outputs stay far below the zone's bound");
        zone.Capture().DeskZones.Should().HaveCount(1);
    }

    [Fact]
    public void ZoneProcessor_Should_ContinueExactly_When_RestoredFromASnapshotWithDeskZones()
    {
        var batches = Enumerable.Range(0, 40).Select(i => Occupancy(At(i * 20 + 5), ("AR-08 staff", i / 3 % 2, At(i * 20), SensedFlags.None),
            ("AR-08 service", i / 5 % 2, At(i * 20), SensedFlags.None))).ToList();
        var whole = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);
        foreach (var b in batches)
            whole.Offer(b, b.ReceivedUtc);
        var expected = whole.Drain().DeskReadings;

        var first = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);
        foreach (var b in batches.Take(17))
            first.Offer(b, b.ReceivedUtc);
        var actual = first.Drain().DeskReadings.ToList();
        var json = JsonSerializer.Serialize(first.Capture());
        var state = JsonSerializer.Deserialize<ZoneProcessorState>(json);
        state.Version.Should().Be(ZoneProcessorState.CurrentVersion, "version 7 added the desk zones (ARV-116); 8 the engine's pending empty-queue check (ARV-114d)");
        var second = ZoneProcessor.Restore("DMO/A-VIS", Geometry(), 12, null, state, At(2_000));
        foreach (var b in batches.Skip(17))
            second.Offer(b, b.ReceivedUtc);
        actual.AddRange(second.Drain().DeskReadings);

        actual.Should().Equal(expected);
    }

    [Fact]
    public void ZoneProcessor_Should_RestoreAVersion6Snapshot_When_ItHasNoDeskZoneMemory()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);
        zone.Offer(Occupancy(At(70), ("AR-08 staff", 1, At(60), SensedFlags.None)), At(70));
        zone.Drain();
        var older = zone.Capture() with { Version = 6, DeskZones = null };

        var restored = ZoneProcessor.Restore("DMO/A-VIS", Geometry(), 12, null, JsonSerializer.Deserialize<ZoneProcessorState>(JsonSerializer.Serialize(older)), At(200));
        restored.Offer(Occupancy(At(75), ("AR-08 staff", 1, At(61), SensedFlags.None)), At(75));

        restored.Drain().DeskReadings.Should().ContainSingle("without memory the next reading passes");
    }

    [Fact]
    public void ZoneProcessor_Should_RefuseASnapshot_When_ItsDeskZoneMemoryIsHostile()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry(), 12);
        zone.Offer(Occupancy(At(70), ("AR-08 staff", 1, At(60), SensedFlags.None)), At(70));
        var hostile = zone.Capture() with { DeskZones = [new(Desk, DeskSource.StaffZone, At(60), 5_000, false, 1)] };

        var act = () => ZoneProcessor.Restore("DMO/A-VIS", Geometry(), 12, null, hostile, At(200));

        act.Should().Throw<InvalidDataException>();
    }

    #endregion

    #region Desk engine

    private static DeskProfile SensorDesk(string key = Desk) => new(key, "VIS", HasTransactions: false, HasSession: false, HasStaffZone: true, HasServiceZone: true);

    private static DeskProfile AmanDesk(string key = Desk) => new(key, "VIS", HasTransactions: true, HasSession: true, HasStaffZone: true, HasServiceZone: true);

    private static readonly DeskStateSettings Feed = new Ariva.Infra.Border.DeskFeedSettings().Engine with { Lateness = TimeSpan.Zero };

    [Fact]
    public void Engine_Should_ServeIdlePauseAndCloseFromTheZonesAlone_When_TheDeskHasNoLoginSource()
    {
        var engine = new DeskStateEngine([SensorDesk()], T0, Feed);
        void Read(double s, DeskSource zone, int count) => engine.Offer(new DeskZoneReading(Desk, At(s), zone, count), At(s));
        DeskStatus StatusAt(double s)
        {
            engine.Advance(At(s));
            return engine.Status(Desk).Status;
        }

        Read(0, DeskSource.StaffZone, 1);
        Read(0, DeskSource.ServiceZone, 1);
        StatusAt(1).Should().Be(DeskStatus.Serving, "row 6: staff present and a passenger at the desk");
        engine.Status(Desk).SensorDerived.Should().BeTrue();
        Read(30, DeskSource.ServiceZone, 0);
        StatusAt(31).Should().Be(DeskStatus.Idle);
        for (var s = 45; s <= 600; s += 15)
            Read(s, DeskSource.ServiceZone, 0);

        Read(60, DeskSource.StaffZone, 0);
        for (var s = 75; s <= 840; s += 15)
            Read(s, DeskSource.StaffZone, 0);
        StatusAt(119).Should().Be(DeskStatus.Idle, "a sensor dropout under the sensor T1 (60 s) keeps the desk open");
        StatusAt(121).Should().Be(DeskStatus.Paused, "staff zone empty for the sensor T1");
        StatusAt(661).Should().Be(DeskStatus.Closed, "row 4: empty for T2");
    }

    [Fact]
    public void Engine_Should_KeepTheThreeMinuteT1_When_TheDeskIsLoggedIn()
    {
        var engine = new DeskStateEngine([AmanDesk()], T0, Feed);
        void Beat(double s)
        {
            engine.Offer(new DeskHeartbeat(Desk, At(s), DeskSource.Session), At(s));
            engine.Offer(new DeskHeartbeat(Desk, At(s), DeskSource.Transactions), At(s));
            engine.Offer(new DeskZoneReading(Desk, At(s), DeskSource.StaffZone, s < 60 ? 1 : 0), At(s));
            engine.Offer(new DeskZoneReading(Desk, At(s), DeskSource.ServiceZone, 0), At(s));
        }

        engine.Offer(new DeskSessionChangedSignal(Desk, At(0), DeskSessionSignal.Opened), At(0));
        for (var s = 0; s <= 300; s += 15)
            Beat(s);

        engine.Advance(At(200));
        engine.Status(Desk).Status.Should().Be(DeskStatus.Idle, "logged in, T1 is 3 minutes whatever the sensor T1");
        engine.Advance(At(241));
        engine.Status(Desk).Status.Should().Be(DeskStatus.Paused);
    }

    [Fact]
    public void Engine_Should_KeepAmansHigherRank_When_TheZonesSaySomethingElse()
    {
        var engine = new DeskStateEngine([AmanDesk()], T0, Feed);
        void Live(double s, int staff, int service)
        {
            engine.Offer(new DeskHeartbeat(Desk, At(s), DeskSource.Session), At(s));
            engine.Offer(new DeskHeartbeat(Desk, At(s), DeskSource.Transactions), At(s));
            engine.Offer(new DeskZoneReading(Desk, At(s), DeskSource.StaffZone, staff), At(s));
            engine.Offer(new DeskZoneReading(Desk, At(s), DeskSource.ServiceZone, service), At(s));
        }

        engine.Offer(new DeskSessionChangedSignal(Desk, At(0), DeskSessionSignal.Closed), At(0));
        Live(0, 1, 1);
        engine.Advance(At(1));
        engine.Status(Desk).Should().Be(new DeskEvaluation(DeskStatus.Closed, false, true, false), "a logout proves Closed; staff present is recorded");

        engine.Offer(new DeskSessionChangedSignal(Desk, At(10), DeskSessionSignal.Opened), At(10));
        Live(10, 1, 1);
        engine.Advance(At(11));
        engine.Status(Desk).Should().Be(new DeskEvaluation(DeskStatus.Idle, false, false, false), "logged in: the service zone alone does not make it Serving (row 6 is for desks without a login)");

        engine.Offer(new DeskTransactionStarted(Desk, At(20)), At(20));
        Live(20, 1, 0);
        engine.Advance(At(21));
        engine.Status(Desk).Should().Be(new DeskEvaluation(DeskStatus.Serving, false, false, false), "a transaction proves Serving, exactly");
    }

    [Fact]
    public void Engine_Should_RecordSensorSecondsAndDegradedMinutes_When_ReadingsAreFlaggedOrStop()
    {
        var engine = new DeskStateEngine([SensorDesk(), SensorDesk(OtherDesk)], T0, Feed);
        // AR-08: staff present all along, its service zone readings flagged (a corrected clock) in the second minute.
        // AR-09: staff present in the first minute, then silent.
        for (var s = 0; s < 300; s += 15)
        {
            engine.Offer(new DeskZoneReading(Desk, At(s), DeskSource.StaffZone, 1), At(s));
            engine.Offer(new DeskZoneReading(Desk, At(s), DeskSource.ServiceZone, 0, Degraded: s is >= 60 and < 120), At(s));
            if (s < 60)
            {
                engine.Offer(new DeskZoneReading(OtherDesk, At(s), DeskSource.StaffZone, 1), At(s));
                engine.Offer(new DeskZoneReading(OtherDesk, At(s), DeskSource.ServiceZone, 0), At(s));
            }
        }

        var minutes = engine.Advance(At(300)).Minutes;
        var desk = minutes.Where(m => m.DeskCode == Desk).ToList();
        var other = minutes.Where(m => m.DeskCode == OtherDesk).ToList();

        desk.Should().OnlyContain(m => m.Idle == TimeSpan.FromMinutes(1) && m.SensorDerived == TimeSpan.FromMinutes(1), "sensor-derived seconds are recorded");
        desk.Select(m => m.Degraded).Should().Equal(false, true, false, false, false);
        // Last heard at 00:45: T_stale (2 minutes) passes at 02:45, so minute 2 is part Unknown and minutes 3 and 4 wholly.
        other[0].Degraded.Should().BeFalse();
        other[2].Unknown.Should().Be(TimeSpan.FromSeconds(15));
        other.Skip(3).Should().OnlyContain(m => m.Unknown == TimeSpan.FromMinutes(1) && m.Degraded);
        engine.Lane("VIS").Unknown.Should().Be(1);
    }

    [Fact]
    public void Engine_Should_NotDegradeAnAmanDesk_When_ItsZonesHaveNeverBeenHeard()
    {
        // A profile may draw a service zone to link a queue to its desk without a sensor over it: never heard, it is no
        // source. Once heard, a silent zone is stale like any other source and degrades the desk's state.
        var engine = new DeskStateEngine([AmanDesk()], T0, Feed);
        void Aman(double s)
        {
            engine.Offer(new DeskHeartbeat(Desk, At(s), DeskSource.Session), At(s));
            engine.Offer(new DeskTransactionsCompleted(Desk, At(s), 1), At(s));
        }

        engine.Offer(new DeskSessionChangedSignal(Desk, At(0), DeskSessionSignal.Opened), At(0));
        for (var s = 0; s <= 600; s += 30)
            Aman(s);
        engine.Offer(new DeskZoneReading(Desk, At(300), DeskSource.StaffZone, 1), At(300));

        engine.Advance(At(290));
        engine.Status(Desk).Should().Be(new DeskEvaluation(DeskStatus.Idle, false, false, false), "zones never heard are not sources");
        engine.Advance(At(400));
        engine.Status(Desk).Degraded.Should().BeFalse("the staff zone is live");
        engine.Advance(At(430));
        engine.Status(Desk).Should().Be(new DeskEvaluation(DeskStatus.Idle, false, false, true), "the staff zone heard at 05:00 is stale from 07:00");
    }

    [Fact]
    public void Engine_Should_StayBoundedPerDeskAndLetOtherDesksThrough_When_OneDeskFloodsZoneReadings()
    {
        var settings = Feed with { MaxBufferedSignals = 2_000, MaxBufferedSignalsPerDesk = 500 };
        var engine = new DeskStateEngine([SensorDesk(), SensorDesk(OtherDesk)], T0, settings);
        var reference = At(600);

        for (var i = 0; i < 50_000; i++)
            engine.Offer(new DeskZoneReading(Desk, At(300 + i * 0.001), DeskSource.StaffZone, i % 2), reference);
        engine.Offer(new DeskZoneReading(OtherDesk, At(300), DeskSource.StaffZone, 1), reference);
        engine.Offer(new DeskZoneReading(OtherDesk, At(300), DeskSource.ServiceZone, 0), reference);

        var pending = engine.Capture().Desks.ToDictionary(d => d.DeskCode, d => d.Pending.Count);
        pending[Desk].Should().BeLessThanOrEqualTo(500, "a desk holds at most its own cap");
        pending.Values.Sum().Should().BeLessThanOrEqualTo(2_000, "and the engine its own");
        pending[OtherDesk].Should().Be(2, "a flood on one desk does not crowd out the others");
        engine.Counters.BufferFull.Should().Be(50_000 - pending[Desk]);
        engine.Advance(At(301));
        engine.Status(OtherDesk).Status.Should().Be(DeskStatus.Idle);
    }

    [Fact]
    public void Restore_Should_Refuse_When_ABufferedZoneReadingIsOneTheEngineWouldRefuse()
    {
        var engine = new DeskStateEngine([new DeskProfile(Desk, "VIS", true, true, false, false), SensorDesk(OtherDesk)], T0, Feed);
        engine.Offer(new DeskZoneReading(OtherDesk, At(30), DeskSource.StaffZone, 1), At(0));
        var state = engine.Capture();
        DeskEngineState With(string desk, DeskSignalState signal) => state with
        {
            Desks = [.. state.Desks.Select(d => d.DeskCode == desk ? d with { Pending = [new DeskPendingState(signal, false, 0)] } : d)]
        };

        // A zone reading for a desk without zones, a count above a desk's, and a reading whose role is a session.
        var noZone = With(Desk, new DeskSignalState("zone", Desk, At(30), DeskSource.StaffZone, Count: 1));
        var crowd = With(OtherDesk, new DeskSignalState("zone", OtherDesk, At(30), DeskSource.StaffZone, Count: 500));
        var session = With(OtherDesk, new DeskSignalState("zone", OtherDesk, At(30), DeskSource.Session, Count: 1));

        foreach (var hostile in new[] { noZone, crowd, session })
            ((Action)(() => DeskStateEngine.Restore(engine.Capture().Desks.Select(d => d.DeskCode == Desk ? new DeskProfile(Desk, "VIS", true, true, false, false) : SensorDesk(OtherDesk)),
                Feed, hostile))).Should().Throw<InvalidDataException>();
        DeskStateEngine.Restore([new DeskProfile(Desk, "VIS", true, true, false, false), SensorDesk(OtherDesk)], Feed, state).Capture()
            .Should().BeEquivalentTo(state, "a valid snapshot restores as it was");
    }

    [Fact]
    public void Restore_Should_KeepTheDegradedFlag_When_AFlaggedReadingIsBuffered()
    {
        var engine = new DeskStateEngine([SensorDesk()], T0, Feed with { Lateness = TimeSpan.FromSeconds(90) });
        engine.Offer(new DeskZoneReading(Desk, At(30), DeskSource.StaffZone, 1, Degraded: true), At(30));

        var json = JsonSerializer.Serialize(engine.Capture());
        var restored = DeskStateEngine.Restore([SensorDesk()], Feed with { Lateness = TimeSpan.FromSeconds(90) }, JsonSerializer.Deserialize<DeskEngineState>(json));
        restored.Advance(At(150));

        restored.Status(Desk).Degraded.Should().BeTrue();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(181)]
    public void Settings_Should_RefuseASensorT1_When_ItIsOutOfRange(int seconds)
    {
        new DeskStateSettings { SensorPauseAfter = TimeSpan.FromSeconds(seconds) }.Problems().Should().ContainSingle(p => p.Contains("SensorPauseAfter", StringComparison.Ordinal));
        new DeskStateSettings { SensorPauseAfter = TimeSpan.FromSeconds(60) }.Problems().Should().BeEmpty();
        new DeskStateSettings().PauseAfterFor(loginLive: false).Should().Be(TimeSpan.FromMinutes(3), "unset, the sensor T1 is T1");
    }

    #endregion
}
