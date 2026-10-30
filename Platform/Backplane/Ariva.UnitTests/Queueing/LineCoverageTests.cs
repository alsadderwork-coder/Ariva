using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.UnitTests.Replay;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-259e1 (formulas F11): a queue zone whose entry or exit line has no live covering device is Unknown. Until devices declare
/// their lines every device the zone tracks covers every entry and exit line, so the zone is Unknown while every device it tracks
/// is out (no measurement at all): its live minutes are Unknown (and degraded), its bins are marked Unknown over the spell, and the
/// spell survives a snapshot. One device out of two leaves the zone Degraded, as before.
/// </summary>
public sealed class LineCoverageTests
{
    private const string ZoneKey = "DMO/A-VIS";
    private static readonly DateTime Start = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string> { "A-VIS entry" }, new HashSet<string> { "A-VIS exit" },
        new HashSet<string>(), new HashSet<string>());

    private static string Json(object value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static T Stamp<T>(T batch, string device, DateTime received, bool commissioned = true) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.DeviceId = Guid.NewGuid();
        batch.DeviceCode = device;
        batch.SiteCode = "DMO";
        batch.QueueZoneName = "A-VIS";
        batch.Commissioned = commissioned;
        batch.ReceivedUtc = received;
        return batch;
    }

    private static ZoneOccupancyBatch Occupancy(string device, DateTime received, SensedFlags flags = SensedFlags.None, bool commissioned = true) =>
        Stamp(new ZoneOccupancyBatch { Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-VIS", 3, received), received, flags)] }, device, received, commissioned);

    private static DeviceStatusBatch Status(string device, DateTime received, bool online = true) =>
        Stamp(new DeviceStatusBatch { Online = online }, device, received);

    /// <summary>
    /// Minute by minute: each device in <paramref name="devices"/> sends a reading unless its rule says it is silent that minute, and
    /// the zone is ticked at the minute as the stream host ticks an idle zone, so a zone whose devices are all silent still moves on.
    /// </summary>
    private static ZoneProcessor Play(IReadOnlyList<(string Device, Func<int, bool> Silent)> devices, int minutes = 40, ZoneProcessorSettings settings = null,
        Action<ZoneProcessor, int> each = null)
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12, settings);
        for (var m = 0; m < minutes; m++)
        {
            var at = Start.AddMinutes(m);
            foreach (var (device, silent) in devices)
            {
                if (!silent(m))
                    zone.Offer(Occupancy(device, at), DateTime.MaxValue);
            }

            each?.Invoke(zone, m);
            zone.Tick(at);
        }

        zone.Finish(Start.AddMinutes(minutes), Start.AddMinutes(minutes + 200));
        return zone;
    }

    private static List<int> UnknownMinutes(ZoneOutputs outputs) =>
        [.. outputs.Live.Where(l => l.Unknown).Select(l => (int)(l.MinuteUtc - Start).TotalMinutes)];

    private static BinQuality FinalBin(ZoneOutputs outputs, int minute) =>
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == Start.AddMinutes(minute)).Select(b => b.Quality).Last();

    private static readonly Func<int, bool> Never = _ => false;

    #region F11

    public static TheoryData<string, Func<int, bool>, Func<int, bool>, int?, int?, BinQuality, BinQuality> Cases => new()
    {
        // Name, S-15 silent, S-17 silent, the spell [from, to) in minutes (null: never Unknown), the 18:00 and 18:15 bins.
        { "both devices heard throughout", Never, Never, null, null, BinQuality.Good, BinQuality.Good },
        { "one of two devices out", Never, m => m is > 10 and < 21, null, null, BinQuality.Degraded, BinQuality.Degraded },
        { "the only device silent", _ => true, m => m is > 10 and < 21, 10, 20, BinQuality.Unknown, BinQuality.Unknown },
        { "two devices out at overlapping times", m => m is > 10 and < 26, m => m is > 15 and < 31, 15, 25, BinQuality.Degraded, BinQuality.Unknown },
        { "two devices out at different times", m => m is > 5 and < 11, m => m is > 20 and < 26, null, null, BinQuality.Degraded, BinQuality.Degraded }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Zone_Should_BeUnknownOnlyWhileNoDeviceIsLive_When_ItsDevicesGoOut(string name, Func<int, bool> silent15, Func<int, bool> silent17, int? from, int? to,
        BinQuality bin1800, BinQuality bin1815)
    {
        var outputs = Play([("S-15", silent15), ("S-17", silent17)]).Drain();

        var unknown = UnknownMinutes(outputs);
        if (from is null)
            unknown.Should().BeEmpty(name);
        else
        {
            // Live minutes turn Unknown once the last device's silence passes the limit; those published before stay as they were.
            unknown.Should().NotBeEmpty(name).And.OnlyContain(m => m >= from && m < to, name).And.Contain(to!.Value - 1, name);
            outputs.Live.Where(l => l.Unknown).Should().OnlyContain(l => l.LengthDegraded && (l.NowcastDegraded || l.NowcastMinutes == null), name);
        }

        FinalBin(outputs, 0).Should().Be(bin1800, name);
        FinalBin(outputs, 15).Should().Be(bin1815, name);
        FinalBin(outputs, 30).Should().Be(BinQuality.Good, name);
    }

    [Fact]
    public void Zone_Should_BeUnknownAtOnce_When_ItsOnlyDeviceReportsItselfOffline()
    {
        var outputs = Play([("S-17", m => m is > 10 and < 21)], each: (zone, m) =>
        {
            if (m == 11)
                zone.Offer(Status("S-17", Start.AddMinutes(11), online: false), DateTime.MaxValue);
        }).Drain();

        outputs.Outages.Should().ContainSingle().Which.FromUtc.Should().Be(Start.AddMinutes(11), "out from the minute it said so");
        UnknownMinutes(outputs).Should().NotBeEmpty().And.OnlyContain(m => m >= 11 && m < 20).And.Contain(12, "no silence to wait for");
        FinalBin(outputs, 0).Should().Be(BinQuality.Unknown);
    }

    [Fact]
    public void Zone_Should_EndTheSpell_When_ANewDeviceIsHeard()
    {
        // S-17 falls silent after 18:10 for good; S-20 is heard for the first time at 18:20 with a push that covers 18:19 (ARV-148).
        var outputs = Play([("S-17", m => m > 10), ("S-20", m => m < 20)]).Drain();

        UnknownMinutes(outputs).Should().NotBeEmpty().And.OnlyContain(m => m >= 10 && m < 19).And.Contain(18);
        FinalBin(outputs, 15).Should().Be(BinQuality.Unknown);
        FinalBin(outputs, 30).Should().Be(BinQuality.Degraded, "S-17 is still out, but S-20 covers the lines");
    }

    [Fact]
    public void Zone_Should_StayUnknown_When_OnlyAnUncommissionedDeviceIsHeard()
    {
        // ARV-023: a device in commissioning checks its installation; it never counts, so it never covers a line either.
        var outputs = Play([("S-17", m => m is > 10 and < 21)], each: (zone, m) =>
            zone.Offer(Occupancy("S-30", Start.AddMinutes(m), commissioned: false), DateTime.MaxValue)).Drain();

        UnknownMinutes(outputs).Should().Contain(19);
        FinalBin(outputs, 15).Should().Be(BinQuality.Unknown);
    }

    [Fact]
    public void Zone_Should_BeDegradedNotUnknown_When_EveryDeviceIsLiveAndAnInputIsFlagged()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m < 40; m++)
        {
            var at = Start.AddMinutes(m);
            zone.Offer(Occupancy("S-15", at, m is > 10 and < 15 ? SensedFlags.Corrected : SensedFlags.None), DateTime.MaxValue);
            zone.Tick(at);
        }

        zone.Finish(Start.AddMinutes(40), Start.AddMinutes(240));
        var outputs = zone.Drain();

        outputs.Live.Should().Contain(l => l.LengthDegraded).And.OnlyContain(l => !l.Unknown);
        outputs.Bins.Should().OnlyContain(b => b.Quality != BinQuality.Unknown);
    }

    [Fact]
    public void Zone_Should_KeepTheSpell_When_ItsOnlyDeviceIsForgotten()
    {
        // A device silent beyond the forget limit is no longer tracked; a zone that tracks no device has no measurement at all.
        var settings = new ZoneProcessorSettings { DeviceForgetHours = 1 };
        var inUse = new Dictionary<int, (bool Unknown, bool InUse)>();
        var zone = Play([("S-17", m => m is > 10 and < 100)], minutes: 90, settings, each: (z, m) => inUse[m] = (z.UnknownNow, z.UnknownInUse));
        zone.UnknownNow.Should().BeFalse("the replay's end closed the spell");
        // ARV-259e3: the Stream's gauge counts the zone while it still tracks its device, and no longer once it is forgotten: a zone
        // whose devices have all been silent for the forget limit is taken as out of use, though it stays Unknown.
        inUse[30].Should().Be((true, true), "out, and still tracked");
        inUse[85].Should().Be((true, false), "forgotten at 19:10: Unknown, but no longer counted");
        inUse[5].Should().Be((false, false), "live");
        var outputs = zone.Drain();

        outputs.Outages.Should().ContainSingle().Which.ToUtc.Should().Be(Start.AddMinutes(70), "forgotten an hour after it was last heard");
        // The last live minute the range publishes is 19:26 (its watermark at 19:30); the settle publishes only its own last minute.
        UnknownMinutes(outputs).Should().Contain([19, 69, 70, 86], "Unknown before and after it is forgotten, to the range's end");
        outputs.Live.Where(l => l.MinuteUtc >= Start.AddMinutes(90)).Should().OnlyContain(l => !l.Unknown, "nothing is invented while the zone settles");
    }

    [Fact]
    public void UnknownNow_Should_FollowTheSpell_When_TheOnlyDeviceGoesOutAndComesBack()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        zone.Offer(Occupancy("S-17", Start), DateTime.MaxValue);
        zone.UnknownNow.Should().BeFalse();

        zone.Tick(Start.AddMinutes(4));
        zone.UnknownNow.Should().BeTrue("silent beyond 180 seconds");

        zone.Offer(Occupancy("S-17", Start.AddMinutes(5)), DateTime.MaxValue);
        zone.UnknownNow.Should().BeFalse();
    }

    #endregion

    #region Finality, replay and order

    [Fact]
    public void Zone_Should_AskOnceForEachBinFinalBeforeTheSpell_When_ItsOnlyDeviceFallsSilent()
    {
        // S-17 last heard at 18:14:50: the 18:00 bin is final at 18:17:40 (its watermark past 18:15), before the silence passes the
        // limit at 18:18, so the spell's first mark asks for a recomputation of it, once for Unknown beside the outage's own request
        // for Degraded; later marks ask for nothing again.
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m <= 14; m++)
        {
            zone.Offer(Occupancy("S-17", Start.AddMinutes(m)), DateTime.MaxValue);
            zone.Tick(Start.AddMinutes(m));
        }

        zone.Offer(Occupancy("S-17", Start.AddMinutes(14).AddSeconds(50)), DateTime.MaxValue);
        foreach (var at in new[] { 15.0, 16, 17, 17 + 40 / 60.0 })
            zone.Tick(Start.AddMinutes(at));
        zone.Peek().Bins.Should().Contain(b => b.StartUtc == Start && b.Status == BinStatus.Final && b.Quality == BinQuality.Good, "final before the silence");
        for (var m = 18; m < 80; m++)
        {
            if (m >= 60)
                zone.Offer(Occupancy("S-17", Start.AddMinutes(m)), DateTime.MaxValue);
            zone.Tick(Start.AddMinutes(m));
        }

        zone.Finish(Start.AddMinutes(80), Start.AddMinutes(280));
        var outputs = zone.Drain();

        var asked = outputs.Recomputations.Where(r => r.FromUtc == Start).Select(r => r.Reason).ToList();
        asked.Should().BeEquivalentTo(["the zone was Degraded after the bin was final", "the zone was Unknown after the bin was final"]);
        outputs.Recomputations.GroupBy(r => (r.FromUtc, r.Reason)).Should().OnlyContain(g => g.Count() == 1);
        FinalBin(outputs, 15).Should().Be(BinQuality.Unknown);
    }

    [Fact]
    public void Revisions_Should_AddAnUnknownRevision_When_TheRecomputationFindsTheSpellBeforeTheBinIsFinal()
    {
        // F6: the live run above made the 18:00 bin final (Good) before S-17's silence passed the limit. The recomputation over the
        // archive replays the records alone (ZoneReplay steps the zone at its records' receive times, never between them), so the
        // silence is seen at the next record, before that bin can be final: it is Unknown there, and BinRevisions writes r + 1.
        IEnumerable<ZoneOccupancyBatch> Records() =>
            Enumerable.Range(0, 15).Select(m => Occupancy("S-17", Start.AddMinutes(m)))
                .Append(Occupancy("S-17", Start.AddMinutes(14).AddSeconds(50)))
                .Concat(Enumerable.Range(60, 20).Select(m => Occupancy("S-17", Start.AddMinutes(m))));
        BinResult[] Recompute()
        {
            var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
            foreach (var batch in Records())
                zone.Offer(batch, DateTime.MaxValue);
            zone.Finish(Start.AddMinutes(80), Start.AddMinutes(280));
            return [.. zone.Drain().Bins];
        }

        var live = new ZoneProcessor(ZoneKey, Geometry, 12);
        foreach (var batch in Records())
        {
            live.Offer(batch, DateTime.MaxValue);
            live.Tick(batch.ReceivedUtc);
            if (batch.ReceivedUtc == Start.AddMinutes(14).AddSeconds(50))
            {
                foreach (var at in new[] { 15.0, 16, 17, 17 + 40 / 60.0 })
                    live.Tick(Start.AddMinutes(at));
                for (var m = 18; m < 60; m++)
                    live.Tick(Start.AddMinutes(m));
            }
        }

        live.Finish(Start.AddMinutes(80), Start.AddMinutes(280));
        var published = live.Drain();
        published.Recomputations.Should().Contain(r => r.FromUtc == Start && r.Reason == "the zone was Unknown after the bin was final");
        var before = published.Bins.Where(b => b.StartUtc == Start && b.Status == BinStatus.Final).MaxBy(b => b.Revision);
        before.Quality.Should().Be(BinQuality.Good, "final before the silence passed the limit");
        var recomputed = Recompute();

        var revisions = BinRevisions.Next(published.Bins, recomputed, "the zone was Unknown after the bin was final");

        revisions.Should().ContainSingle(b => b.StartUtc == Start).Which.Should().Match<BinResult>(b =>
            b.Quality == BinQuality.Unknown && b.Revision == before.Revision + 1 && b.RevisionReason == "the zone was Unknown after the bin was final");
        revisions.Should().NotContain(b => b.StartUtc > Start && b.StartUtc < Start.AddMinutes(60) && b.Quality != BinQuality.Unknown,
            "the bins of the spell were Unknown live already, and stay so");
        BinRevisions.Next(recomputed, Recompute(), "the same records again").Should().BeEmpty("a recomputation is deterministic");
    }

    [Fact]
    public void Replay_Should_EndAnOpenSpellAtTheEnd_When_TheRangeEndsWhileNoDeviceIsLive()
    {
        var zone = Play([("S-17", m => m > 30)]);
        var outputs = zone.Drain();

        UnknownMinutes(outputs).Should().NotBeEmpty().And.OnlyContain(m => m >= 30 && m < 40);
        outputs.Live.Where(l => l.MinuteUtc >= Start.AddMinutes(40)).Should().OnlyContain(l => !l.Unknown, "devices are not watched after the end");
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == Start.AddMinutes(30)).Should().OnlyContain(b => b.Quality == BinQuality.Unknown);
        zone.UnknownNow.Should().BeFalse();
    }

    [Fact]
    public void Zone_Should_GiveTheSameOutputs_When_TheRecordsOfAReceiveTimeComeInAnotherOrder()
    {
        (string, Func<int, bool>) s15 = ("S-15", m => m is > 10 and < 26), s17 = ("S-17", m => m is > 15 and < 31);

        var first = Play([s15, s17]).Drain();
        var second = Play([s17, s15]).Drain();

        Json(second.Live).Should().Be(Json(first.Live));
        Json(second.Bins).Should().Be(Json(first.Bins));
        Json(second.Recomputations).Should().Be(Json(first.Recomputations));
    }

    [Fact]
    public void Replay_Should_MarkThe1815BinUnknown_When_BothVisitorDevicesAreSilentFrom1820To1830()
    {
        // The reference evening (seed 9303) with S-15 silent too while S-17 is: A-VIS has no measurement at all for those minutes.
        var settings = new ZoneProcessorSettings();
        var zone = new ZoneProcessor("DMO/A-VIS", ReferenceReplay.GeometryOf("A-VIS"), ReferenceReplay.ProfileVersion, settings);
        var silenced = 0;
        foreach (var input in ReferenceReplay.InputsOf("A-VIS"))
        {
            var batch = input.ToBatch();
            if (batch.DeviceCode == "S-15" && batch.ReceivedUtc > ReferenceReplay.WallOf(1100) && batch.ReceivedUtc < ReferenceReplay.WallOf(1111))
            {
                silenced++;
                continue;
            }

            zone.Offer(batch, ReferenceReplay.To);
        }

        zone.Finish(ReferenceReplay.To, ReferenceReplay.To + ZoneReplay.Settle(settings));
        var outputs = zone.Drain();

        silenced.Should().BePositive();
        var unknown = outputs.Live.Where(l => l.Unknown).Select(l => l.MinuteUtc.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        unknown.Should().NotBeEmpty().And.OnlyContain(m => string.CompareOrdinal(m, "18:20") >= 0 && string.CompareOrdinal(m, "18:30") < 0).And.Contain("18:29");
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.Quality == BinQuality.Unknown).Select(b => b.StartUtc).Distinct()
            .Should().Equal(ReferenceReplay.WallOf(1095));
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == ReferenceReplay.WallOf(1110)).Should().OnlyContain(b => b.Quality == BinQuality.Good);
    }

    [Fact]
    public void LiveMinute_Should_LeaveUnknownOutOfItsJson_When_ItIsFalse()
    {
        var minute = new QueueLiveMinute(ZoneKey, Start, 3, true, false, 4, 1, null, false);

        Json(minute).Should().NotContain("unknown");
        JsonSerializer.Serialize(minute, ReplayLedger.Json).Should().NotContainEquivalentOf("unknown");
        Json(minute with { Unknown = true }).Should().Contain("\"unknown\":true");
        JsonSerializer.Deserialize<QueueLiveMinute>(Json(minute with { Unknown = true }), EventCatalog.Json)!.Unknown.Should().BeTrue();
    }

    #endregion

    #region Snapshots

    /// <summary>The only device S-17 silent from 18:11 to 18:20, captured during the spell and restored, at 18:15.</summary>
    private static (ZoneProcessor Zone, ZoneProcessorState State) DuringASpell()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m <= 15; m++)
        {
            var at = Start.AddMinutes(m);
            if (m <= 10)
                zone.Offer(Occupancy("S-17", at), DateTime.MaxValue);
            zone.Tick(at);
        }

        zone.Drain();
        return (zone, JsonSerializer.Deserialize<ZoneProcessorState>(Json(zone.Capture()), EventCatalog.Json)!);
    }

    [Fact]
    public void Coverage_Should_ContinueIdentically_When_RestoredFromASnapshotDuringASpell()
    {
        var straight = Play([("S-17", m => m is > 10 and < 21)]).Drain();

        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        var outputs = new List<ZoneOutputs>();
        for (var m = 0; m < 40; m++)
        {
            var at = Start.AddMinutes(m);
            if (m is <= 10 or >= 21)
                zone.Offer(Occupancy("S-17", at), DateTime.MaxValue);
            zone.Tick(at);
            if (m is 15 or 21)
            {
                outputs.Add(zone.Drain());
                var state = JsonSerializer.Deserialize<ZoneProcessorState>(Json(zone.Capture()), EventCatalog.Json)!;
                if (m == 15)
                    state.Coverage.Should().HaveCount(2, "both lines are without a live covering device");
                zone = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, Start.AddDays(1));
            }
        }

        zone.Finish(Start.AddMinutes(40), Start.AddMinutes(240));
        outputs.Add(zone.Drain());

        Json(outputs.SelectMany(o => o.Live)).Should().Be(Json(straight.Live));
        Json(outputs.SelectMany(o => o.Bins)).Should().Be(Json(straight.Bins));
        Json(outputs.SelectMany(o => o.Recomputations)).Should().Be(Json(straight.Recomputations));
    }

    [Fact]
    public void Coverage_Should_ContinueIdentically_When_ADeviceFirstHeardOfflineDuringTheSpellIsInTheSnapshot()
    {
        // S-17 is out from 18:10; S-30 is heard for the first time at 18:16, reporting itself offline, so it goes out after the spell
        // started (a replacement sensor commissioned during the outage). The engine writes that state itself, so a restore from it
        // must continue exactly as the run that was never stopped (ARV-259e1 review, round 2).
        void Each(ZoneProcessor zone, int m)
        {
            if (m == 16)
                zone.Offer(Status("S-30", Start.AddMinutes(m), online: false), DateTime.MaxValue);
        }

        var straight = Play([("S-17", m => m is > 10 and < 21)], each: Each).Drain();

        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        var outputs = new List<ZoneOutputs>();
        for (var m = 0; m < 40; m++)
        {
            var at = Start.AddMinutes(m);
            if (m is <= 10 or >= 21)
                zone.Offer(Occupancy("S-17", at), DateTime.MaxValue);
            Each(zone, m);
            zone.Tick(at);
            if (m == 17)
            {
                outputs.Add(zone.Drain());
                var state = JsonSerializer.Deserialize<ZoneProcessorState>(Json(zone.Capture()), EventCatalog.Json)!;
                state.Coverage.Should().HaveCount(2).And.OnlyContain(c => c.UncoveredSinceUtc == Start.AddMinutes(10));
                state.Devices.Should().Contain(d => d.DeviceCode == "S-30" && d.OutSinceUtc == Start.AddMinutes(16), "out after the spell started");
                zone = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, Start.AddDays(1));
            }
        }

        zone.Finish(Start.AddMinutes(40), Start.AddMinutes(240));
        outputs.Add(zone.Drain());

        UnknownMinutes(straight).Should().NotBeEmpty();
        Json(outputs.SelectMany(o => o.Live)).Should().Be(Json(straight.Live));
        Json(outputs.SelectMany(o => o.Bins)).Should().Be(Json(straight.Bins));
        Json(outputs.SelectMany(o => o.Recomputations)).Should().Be(Json(straight.Recomputations));
    }

    /// <summary>The state as a build before version 14 would have written it: its Unknown marks were the outage's Degraded ones.</summary>
    private static ZoneProcessorState MarkedAsBefore14(ZoneProcessorState state) => state.WrittenBefore14() with
    {
        Version = 13,
        Bins = state.Bins with
        {
            Marks = [.. state.Bins.Marks.Select(m => m.Quality == BinQuality.Unknown ? m with { Quality = BinQuality.Degraded } : m)],
            Open = [.. state.Bins.Open.Select(b => b.Marked == BinQuality.Unknown ? b with { Marked = BinQuality.Degraded } : b)]
        }
    };

    [Fact]
    public void Restore_Should_DeriveTheSpellFromTheDevices_When_TheSnapshotWasWrittenBeforeVersion14()
    {
        var (_, state) = DuringASpell();
        var json = JsonSerializer.SerializeToNode(MarkedAsBefore14(state), EventCatalog.Json)!.AsObject();
        json.Remove("coverage");
        json.Remove("recentUncovered");

        var restored = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), json.Deserialize<ZoneProcessorState>(EventCatalog.Json)!, Start.AddDays(1));

        restored.UnknownNow.Should().BeTrue("its only device is out");
        var capture = restored.Capture();
        capture.Version.Should().Be(ZoneProcessorState.CurrentVersion);
        capture.Coverage.Should().OnlyContain(c => c.UncoveredSinceUtc == Start.AddMinutes(10) && c.MarkedThroughUtc == null)
            .And.HaveCount(2, "derived from the device's outage, marked from its start at the next step");
        restored.Tick(Start.AddMinutes(16));
        restored.Capture().Coverage.Should().OnlyContain(c => c.MarkedThroughUtc == Start.AddMinutes(15));
    }

    [Fact]
    public void Restore_Should_RefuseTheSnapshot_When_ItsCoverageDisagreesWithTheZone()
    {
        // Checked by LineCoverage.Restore against the zone's lines and its restored devices; each case passes SnapshotChecks, so the
        // refusal's reason is the one named.
        var (zone, state) = DuringASpell();
        var notAfter = Start.AddDays(1);
        ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, notAfter).UnknownNow.Should().BeTrue();
        var (entry, exit) = (state.Coverage[0], state.Coverage[1]);
        entry.MarkedThroughUtc.Should().NotBeNull("the spell was marked at the step before the snapshot");
        var live = new ZoneProcessor(ZoneKey, Geometry, 12);
        live.Offer(Occupancy("S-17", Start), DateTime.MaxValue);
        var covered = live.Capture();
        const string Disagree = "*do not agree with its devices*";
        const string NotTheZones = "*not one of the zone's entry or exit lines, or names one twice*";
        const string NotOneSpell = "*do not share one spell*";

        foreach (var (bad, reason, because) in new (ZoneProcessorState, string, string)[]
                 {
                     (state with { Coverage = [] }, Disagree, "every device is out, so the lines have no coverage: a snapshot cannot hide it"),
                     (state with { Coverage = [entry] }, Disagree, "nor hide one of them"),
                     (covered with { Coverage = [entry with { UncoveredSinceUtc = Start, MarkedThroughUtc = null }, exit with { UncoveredSinceUtc = Start, MarkedThroughUtc = null }] },
                         Disagree, "a live device covers the lines: a snapshot cannot make them uncovered"),
                     (state with { Coverage = [entry, exit, entry] }, NotTheZones, "a line named twice"),
                     (state with { Coverage = [entry, exit with { LineName = "A-VIS count" }] }, NotTheZones, "a line that is not an entry or exit line"),
                     (state with { Coverage = [entry, exit with { MarkedThroughUtc = null }] }, NotOneSpell, "every line is marked through the same minute"),
                     (state with { Coverage = [entry, exit with { UncoveredSinceUtc = entry.UncoveredSinceUtc.AddMinutes(1) }] }, NotOneSpell,
                         "every line loses its coverage at the same moment"),
                     (state with { Devices = [], RecentOutages = [], Coverage = [entry] }, NotOneSpell, "nor, in a zone that tracks no device, only some lines"),
                     (state with { RecentUncovered = [new RecentUncoveredState("elsewhere", Start, Start.AddMinutes(5))] }, "*names a recent spell of a line that is not*",
                         "a spell of another line")
                 })
        {
            SnapshotChecks.Of(bad, ZoneKey, "A-VIS", 12, notAfter).Should().BeEmpty(because);
            var restore = () => ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because).WithMessage(reason, because);
        }

        zone.UnknownNow.Should().BeTrue();
    }

    [Fact]
    public void SnapshotChecks_Should_RefuseTheSnapshot_When_ItsCoverageIsOutOfBoundsOrOfAnEarlierVersion()
    {
        // Checked by SnapshotChecks before anything is restored; each case is refused for the one reason named, and nothing else.
        var (_, state) = DuringASpell();
        var notAfter = Start.AddDays(1);
        SnapshotChecks.Of(state, ZoneKey, "A-VIS", 12, notAfter).Should().BeEmpty();
        var (entry, exit) = (state.Coverage[0], state.Coverage[1]);
        var device = state.Devices.Single();
        var reference = state.ReferenceUtc;
        var before = MarkedAsBefore14(state);
        SnapshotChecks.Of(before, ZoneKey, "A-VIS", 12, notAfter).Should().BeEmpty();
        before.Bins.Open.Should().NotBeEmpty();
        const string Implausible = "1 values of the snapshot are not plausible (negative counts, implausible times or empty items).";
        const string TooMany = "The snapshot holds more lines without coverage than a zone keeps.";

        foreach (var (bad, problem, because) in new (ZoneProcessorState, string, string)[]
                 {
                     (before with { Coverage = state.Coverage }, "A snapshot before version 14 carries no lines without coverage.", "lines without coverage before version 14"),
                     (before with { RecentUncovered = [new RecentUncoveredState("A-VIS exit", Start, Start.AddMinutes(5))] },
                         "A snapshot before version 14 carries no lines without coverage.", "an ended spell before version 14"),
                     (before with { Bins = before.Bins with { Marks = [new BinMarkState(Start, Start.AddMinutes(5), BinQuality.Unknown)] } },
                         "A snapshot before version 14 carries no Unknown bin mark.", "an Unknown mark before version 14"),
                     (before with { Bins = before.Bins with { Open = [before.Bins.Open[0] with { Marked = BinQuality.Unknown }, .. before.Bins.Open.Skip(1)] } },
                         "A snapshot before version 14 carries no Unknown bin mark.", "an open bin marked Unknown before version 14"),
                     (state with { Coverage = [.. Enumerable.Range(0, SnapshotChecks.MaxCoveredLines + 1).Select(i => entry with { LineName = $"line {i}" })] },
                         TooMany, "more open lines than a profile keeps"),
                     (state with { RecentUncovered = [.. Enumerable.Repeat(new RecentUncoveredState("A-VIS exit", Start, Start.AddMinutes(5)), SnapshotChecks.MaxCoveredLines * 4 + 1)] },
                         TooMany, "more ended spells than a zone keeps"),
                     (state with { Coverage = [entry, exit with { LineName = new string('x', 201) }] }, Implausible, "a name no profile keeps"),
                     (state with { Coverage = [entry, exit with { MarkedThroughUtc = exit.UncoveredSinceUtc.AddMinutes(-1) }] }, Implausible, "marked before it started"),
                     (state with { Coverage = [entry, exit with { UncoveredSinceUtc = reference.AddMinutes(1), MarkedThroughUtc = null }] }, Implausible,
                         "started after the zone's clock"),
                     (state with { Coverage = [entry, exit with { MarkedThroughUtc = reference.AddMinutes(1) }] }, Implausible,
                         "marked past the zone's clock: every later mark of the spell, and its last, would mark nothing"),
                     (state with { RecentUncovered = [new RecentUncoveredState("A-VIS exit", Start.AddMinutes(5), Start.AddMinutes(5))] }, Implausible, "a spell of no length"),
                     (state with { RecentUncovered = [new RecentUncoveredState("A-VIS exit", Start, reference.AddMinutes(1).AddTicks(1))] }, Implausible,
                         "ended more than a minute past the zone's clock: it would make live minutes Unknown while the lines are covered"),
                     (state with { Devices = [device with { MarkedThroughUtc = reference.AddMinutes(1) }] }, Implausible, "an outage marked past the zone's clock"),
                     (state with { RecentOutages = [new RecentOutageState("S-17", Start, reference.AddMinutes(1).AddTicks(1))] }, Implausible,
                         "an outage ended more than a minute past the zone's clock")
                 })
        {
            SnapshotChecks.Of(bad, ZoneKey, "A-VIS", 12, notAfter).Should().Equal([problem], because);
            var restore = () => ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because).WithMessage("*" + problem, because);
        }

        // The latest a spell or an outage ends: a minute past the zone's clock (one reported and ended at the same receive time).
        SnapshotChecks.Of(state with { RecentUncovered = [new RecentUncoveredState("A-VIS exit", Start, reference.AddMinutes(1))] }, ZoneKey, "A-VIS", 12, notAfter)
            .Should().BeEmpty();
        SnapshotChecks.Of(state with { RecentOutages = [new RecentOutageState("S-17", Start, reference.AddMinutes(1))] }, ZoneKey, "A-VIS", 12, notAfter)
            .Should().BeEmpty();
    }

    #endregion
}
