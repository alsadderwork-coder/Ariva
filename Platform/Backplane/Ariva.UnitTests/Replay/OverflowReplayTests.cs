using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Infra.Messaging;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// ARV-115: the overflow scenario case (<see cref="ScenarioConfig.OverflowEvening"/>: seed 9303's evening with the Visitors
/// snake holding 60 people, its scripted events unchanged) replayed through Ingest and the stream's engine. The Visitors
/// queue spills into the A-OV band in both evening waves; the replay records the band's minutes and each change once, the
/// same records always give the same output hash (locked in replay-overflow.json), and the stream (snapshots taken and
/// restored through JSON as it goes, outputs drained at random checkpoints) writes exactly the replay's overflow rows and
/// changes. The reference evening itself never fills a band, so its golden hash is unchanged.
/// </summary>
public sealed class OverflowReplayTests
{
    private static readonly Lazy<(ReplayHashes Hashes, ZoneOutputs Outputs)> Overflow = new(() =>
    {
        var (hashes, outputs) = ReferenceReplay.RunOverflow();
        return (hashes, outputs["DMO/A-VIS"][0]);
    });

    private static string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Replay_Should_FillTheVisitorsBandInBothWaves_When_TheSnakeHoldsSixtyPeople()
    {
        var (_, outputs) = Overflow.Value;
        var minutes = outputs.Overflow;
        var changes = outputs.OverflowChanges;

        minutes.Should().OnlyContain(m => m.BandName == "A-OV");
        minutes.Select(m => m.MinuteUtc).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
        minutes.Should().HaveCount(209, "S-25 reports the band at the end of every minute, 17:01 to 20:29 within the range");
        changes.Select(c => c.Kind).Should().Equal(OverflowChangeKind.Occupied, OverflowChangeKind.Emptied, OverflowChangeKind.Occupied, OverflowChangeKind.Emptied);
        changes.Select(c => Clock(c.MinuteUtc)).Should().Equal("18:03", "18:32", "19:33", "19:53");
        changes[1].OccupiedSinceUtc.Should().Be(changes[0].MinuteUtc);
        changes[1].PeakOccupancy.Should().Be(minutes.Where(m => m.MinuteUtc >= changes[0].MinuteUtc && m.MinuteUtc < changes[1].MinuteUtc).Max(m => m.MaxOccupancy));
        changes[1].PeakOccupancy.Should().Be(148 - (int)ScenarioConfig.OverflowEveningVisitorsCapacity, "the first wave peaks at 148 people, 88 above the snake");

        // Each change sits where the minutes turn: occupied after an unoccupied minute, empty after an occupied one.
        foreach (var change in changes)
        {
            var minute = minutes.Single(m => m.MinuteUtc == change.MinuteUtc);
            var before = minutes.Last(m => m.MinuteUtc < change.MinuteUtc);
            minute.Occupied.Should().Be(change.Kind == OverflowChangeKind.Occupied);
            before.Occupied.Should().Be(change.Kind == OverflowChangeKind.Emptied);
        }

        // Overflow minutes per 15-minute bin (TC-19, Proposed: a minute with any occupancy).
        var perBin = minutes.Where(m => m.Occupied).GroupBy(m => Clock(m.MinuteUtc.AddMinutes(-(m.MinuteUtc.Minute % 15)))).ToDictionary(g => g.Key, g => g.Count());
        perBin.Should().Equal(new Dictionary<string, int> { ["18:00"] = 12, ["18:15"] = 15, ["18:30"] = 2, ["19:30"] = 12, ["19:45"] = 8 });
        perBin.Values.Sum().Should().Be(minutes.Count(m => m.Occupied)).And.Be(29 + 20, "18:03 to 18:31 and 19:33 to 19:52");
    }

    [Fact]
    public void Replay_Should_GiveTheLockedOutputHash_When_RunAgain()
    {
        var again = ReferenceReplay.RunOverflow();
        var locked = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Replay/replay-overflow.json"))).RootElement;

        again.Hashes.Should().Be(Overflow.Value.Hashes, "the same records always give the same hashes");
        Overflow.Value.Hashes.OutputHead.Should().Be(locked.GetProperty("outputHead").GetString(),
            "the overflow scenario case is locked like the golden replay; a change is reviewed and the value updated with the human's approval");
        Overflow.Value.Hashes.Outputs.Should().Be(locked.GetProperty("outputs").GetInt64());
    }

    [Fact]
    public void Stream_Should_WriteTheReplaysOverflowRowsAndChanges_When_RestoredFromSnapshotsAsItGoes()
    {
        var settings = new ZoneProcessorSettings();
        var geometry = ReferenceReplay.GeometryOf("A-VIS");
        var inputs = ReferenceReplay.InputsOf("A-VIS", ReferenceReplay.OverflowIngested);
        var seen = 0;
        var zone = new ZoneProcessor("DMO/A-VIS", geometry, ReferenceReplay.ProfileVersion, settings);
        var written = new List<ZoneOutputs>();
        foreach (var input in inputs)
        {
            zone.Offer(input.ToBatch(), ReferenceReplay.To);
            // Checkpoints at uneven intervals (every 2nd, 5th or 7th input in turn), so they fall mid-minute and mid-spell.
            if (++seen % new[] { 2, 5, 7 }[seen % 3] != 0)
                continue;
            // A checkpoint: the outputs and the state written together, then the zone rebuilt from the stored state.
            var outputs = zone.Peek();
            var json = JsonSerializer.Serialize(zone.Capture(), EventCatalog.Json);
            zone.Acknowledge(outputs);
            written.Add(outputs);
            zone = ZoneProcessor.Restore("DMO/A-VIS", geometry, ReferenceReplay.ProfileVersion, settings,
                JsonSerializer.Deserialize<ZoneProcessorState>(json, EventCatalog.Json)!, ReferenceReplay.To.AddDays(1));
        }

        zone.Finish(ReferenceReplay.To, ReferenceReplay.To + ZoneReplay.Settle(settings));
        written.Add(zone.Drain());

        var replay = Overflow.Value.Outputs;
        written.SelectMany(o => o.Overflow).Should().Equal(replay.Overflow);
        written.SelectMany(o => o.OverflowChanges).Should().Equal(replay.OverflowChanges);
        written.SelectMany(o => o.Live).Should().Equal(replay.Live, "the queue length still sums the snake and the band");
    }

    [Fact]
    public void Replay_Should_MeasureTheQueueFromBothZones_When_TheBandReports()
    {
        var (_, outputs) = Overflow.Value;

        var peak = outputs.Live.Single(l => Clock(l.MinuteUtc) == "18:15");
        peak.LengthMeasured.Should().BeTrue("the snake and its band both report, so the queue length comes from the sensors");
        peak.QueueLength.Should().Be(outputs.Overflow.Single(m => Clock(m.MinuteUtc) == "18:15").MaxOccupancy + (int)ScenarioConfig.OverflowEveningVisitorsCapacity);
    }
}
