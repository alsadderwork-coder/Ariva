using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// ARV-036: the golden replay of the reference evening (seed 9303) from the archive's form of Ingest's output. It
/// reproduces the scripted events: the arrivals Visitors nowcast passes 15 minutes at 18:05; S-17 is out from 18:20 until
/// it is heard again at 18:31 and the Visitors zone is degraded meanwhile; Handler B's island C breaches in the 19:00,
/// 19:15 and 19:30 bins. The same records give the same output hash on every run, the hash is the reviewed golden value,
/// and the exported replay is tamper-evident.
/// </summary>
public sealed class GoldenReplayTests
{
    private static readonly Lazy<(ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs, string Export)> Golden = new(() =>
    {
        using var export = new StringWriter();
        var (hashes, outputs) = ReferenceReplay.Run(export);
        return (hashes, outputs, export.ToString());
    });

    private static string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static ZoneOutputs Of(string zone) => Golden.Value.Outputs["DMO/" + zone][0];

    [Fact]
    public void Replay_Should_PassFifteenMinutesAt1805_When_TheVisitorsWaveArrives()
    {
        var live = Of("A-VIS").Live;

        var first = live.First(l => l.NowcastMinutes > 15);

        Clock(first.MinuteUtc).Should().Be("18:05");
        live.Should().OnlyContain(l => l.MinuteUtc >= ReferenceReplay.From.AddMinutes(-1) && l.MinuteUtc < ReferenceReplay.To + ZoneReplay.Settle(new ZoneProcessorSettings()));
    }

    [Fact]
    public void Replay_Should_DegradeTheVisitorsZone_When_S17IsOfflineFrom1820To1830()
    {
        var visitors = Of("A-VIS");

        visitors.Outages.Should().ContainSingle().Which.Should().Be(
            new DeviceOutage("DMO/A-VIS", "S-17", ReferenceReplay.WallOf(1100), ReferenceReplay.WallOf(1111)),
            "S-17 last reported for 18:19 at 18:20 and next for 18:30 at 18:31");
        var degraded = visitors.Live.Where(l => l.LengthDegraded).Select(l => Clock(l.MinuteUtc)).ToList();
        degraded.Should().NotBeEmpty();
        degraded.Should().OnlyContain(m => string.CompareOrdinal(m, "18:20") >= 0 && string.CompareOrdinal(m, "18:30") <= 0,
            "the zone is degraded live only while S-17 is out");
        degraded.Last().Should().Be("18:30");
        var marked = visitors.Bins.Where(b => b.Status == BinStatus.Final && b.Quality == BinQuality.Degraded).Select(b => Clock(b.StartUtc)).Distinct().ToList();
        marked.Should().Equal("18:15", "18:30");
        Of("CI-C").Outages.Should().BeEmpty();
    }

    [Fact]
    public void Replay_Should_BreachIslandC_When_HandlerBIsShortOfCountersFrom1910()
    {
        var finals = Of("CI-C").Bins.Where(b => b.Status == BinStatus.Final).GroupBy(b => b.StartUtc).Select(g => g.Last()).ToList();

        finals.Where(b => b.Waits.P90Minutes > 15).Select(b => Clock(b.StartUtc)).Should().Equal("19:00", "19:15", "19:30");
        finals.Where(b => b.StartUtc < ReferenceReplay.To).Should().HaveCount(14, "every bin of the range becomes final");
    }

    [Theory]
    [InlineData("A-VIS")]
    [InlineData("CI-C")]
    public void Replay_Should_CountEveryLineMinuteAsTheZoneMinutes_When_TheEveningIsReplayed(string zone)
    {
        var outputs = Of(zone);
        var minutes = outputs.Minutes.GroupBy(m => m.StartUtc).ToDictionary(g => g.Key, g => g.Last());

        // ARV-113: each line's closed minutes, once each, from Ariva's own counts; the entry line's ins and the exit line's
        // outs are the zone's entries and exits of the same minute (the reference evening has no duplicate crossings).
        outputs.Lines.Should().NotBeEmpty();
        outputs.Lines.Should().OnlyContain(l => l.Source == LineCountSource.Ariva);
        outputs.Lines.GroupBy(l => (l.LineName, l.MinuteUtc)).Should().OnlyContain(g => g.Count() == 1, "a closed line minute is released once");
        outputs.Lines.Select(l => (l.LineName, l.Role)).Distinct().Should().BeEquivalentTo([(zone + " entry", QueueLineRole.Entry), (zone + " exit", QueueLineRole.Exit)]);
        var entries = outputs.Lines.Where(l => l.Role == QueueLineRole.Entry).ToDictionary(l => l.MinuteUtc, l => l.In);
        var exits = outputs.Lines.Where(l => l.Role == QueueLineRole.Exit).ToDictionary(l => l.MinuteUtc, l => l.Out);
        foreach (var (start, minute) in minutes)
        {
            entries.GetValueOrDefault(start).Should().Be(minute.Entries, $"the entry line's ins at {Clock(start)} are the zone's entries");
            exits.GetValueOrDefault(start).Should().Be(minute.Exits, $"the exit line's outs at {Clock(start)} are the zone's exits");
        }

        entries.Values.Sum().Should().Be(minutes.Values.Sum(m => m.Entries)).And.BePositive();
        exits.Values.Sum().Should().Be(minutes.Values.Sum(m => m.Exits)).And.BePositive();
    }

    [Theory]
    [InlineData("A-VIS")]
    [InlineData("CI-C")]
    public void Replay_Should_CheckTheHealthOfEveryBin_When_TheEveningIsReplayed(string zone)
    {
        var outputs = Of(zone);

        // ARV-114a: one health row with every bin result, for the same bin, revision and status, with its counts.
        outputs.Health.Should().HaveSameCount(outputs.Bins);
        outputs.Health.Zip(outputs.Bins).Should().OnlyContain(p => p.First.StartUtc == p.Second.StartUtc && p.First.Revision == p.Second.Revision &&
            p.First.Status == p.Second.Status && p.First.Entries == p.Second.Entries && p.First.Exits == p.Second.Exits);
        var finals = outputs.Health.Where(h => h.Status == BinStatus.Final && h.StartUtc < ReferenceReplay.To).GroupBy(h => h.StartUtc).Select(g => g.Last()).ToList();
        finals.Should().HaveCount(14);

        // The emulator's occupancy is always its entries minus its exits and every passenger is a track that exits, so the
        // evening conserves people, completes every track and keeps within the snake's capacity (A-VIS 190, CI-C 70).
        var measured = finals.Where(h => h.ConservationResidual is not null).ToList();
        measured.Should().HaveCount(13, "the first bin starts before the first occupancy reading");
        measured.Should().OnlyContain(h => h.ConservationResidual == 0);
        finals.Where(h => h.Entries > 0).Should().OnlyContain(h => h.TrackCompletionRate == 1 && h.TracksEntered == h.Entries && h.TracksOpen == 0 && h.TracksCensored == 0);
        outputs.Health.Where(h => h.Entries == 0).Should().NotBeEmpty().And.OnlyContain(h => h.TrackCompletionRate == null && h.TracksOpen == 0,
            "a bin without entries (the settling bins after the range at least) has no rate");
        finals.Should().OnlyContain(h => h.MinutesOutsideCapacity == 0 && h.CapacityMinutes == h.OccupancyMinutes);
        finals.Skip(1).Should().OnlyContain(h => h.OccupancyMinutes == 15);
    }

    [Fact]
    public void Replay_Should_GiveTheVisitorsWaveItsHealth_When_The1800BinIsFinal()
    {
        var bin = Of("A-VIS").Health.Last(h => Clock(h.StartUtc) == "18:00" && h.Status == BinStatus.Final);

        bin.Should().BeEquivalentTo(new
        {
            Entries = 243L, Exits = 108L, OccupancyStart = 10, OccupancyEnd = 145, ConservationResidual = 0L, TracksEntered = 243L, TracksExited = 243L,
            TrackCompletionRate = 1.0, OccupancyMinutes = 15, CapacityMinutes = 15, MinutesOutsideCapacity = 0, ZoneProfileVersion = 12
        });
    }

    [Fact]
    public void Replay_Should_CountMinutesAboveCapacity_When_IslandCIsGivenASmallerCapacity()
    {
        var geometry = ReferenceReplay.GeometryOf("CI-C") with { Capacities = new Dictionary<string, int>(StringComparer.Ordinal) { ["CI-C"] = 40 } };
        var health = new List<ZoneHealthBin>();
        var capturing = new ReplayLedger(ReferenceReplay.Manifest(new ZoneProcessorSettings()), new CapturingTextWriter(health));

        ZoneReplay.Run(capturing, "DMO/CI-C", geometry, ReferenceReplay.ProfileVersion, new ZoneProcessorSettings(), ReferenceReplay.InputsOf("CI-C"),
            ReferenceReplay.From, ReferenceReplay.To);

        var finals = health.Where(h => h.Status == BinStatus.Final).GroupBy(h => h.StartUtc).ToDictionary(g => Clock(g.Key), g => g.Last());
        finals["19:15"].MinutesOutsideCapacity.Should().BePositive("Handler B's island holds up to 59 people then, above 40");
        finals["19:30"].MinutesOutsideCapacity.Should().BePositive();
        finals["18:00"].MinutesOutsideCapacity.Should().Be(0);
        finals.Values.Should().OnlyContain(h => h.MinutesOutsideCapacity <= h.CapacityMinutes && h.CapacityMinutes <= h.OccupancyMinutes);
    }

    [Fact]
    public void Replay_Should_KeepEveryEarlierOutput_When_TheHealthRowsAreLeftOut()
    {
        // ARV-114a appends the health rows to each drain; leaving them out must give back the ARV-113 golden exactly, so no
        // earlier output changed.
        var without = new ReplayLedger(ReferenceReplay.Manifest(new ZoneProcessorSettings()));
        foreach (var line in Golden.Value.Export.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "out" && root.GetProperty("type").GetString() != "health")
                without.Output(root.GetProperty("zone").GetString(), root.GetProperty("type").GetString(), root.GetProperty("record").Clone());
        }

        var hashes = without.End();

        hashes.OutputHead.Should().Be("8d13749079c0c0ca347dcd8c9e41f6708a7360b7145ad61af32c4d49cf0e0a2c");
        hashes.Outputs.Should().Be(2716);
    }

    [Fact]
    public void Replay_Should_KeepItsGoldenHashUnchanged_When_NoBandIsEverOccupied()
    {
        // ARV-115: the reference evening's snakes never fill (A-VIS peaks at 145 of 190), and its devices are the queue
        // leads only, so no band reports: no overflow output joins the chain and the golden hash stays as approved.
        Of("A-VIS").Overflow.Should().BeEmpty();
        Of("A-VIS").OverflowChanges.Should().BeEmpty();
        Of("CI-C").Overflow.Should().BeEmpty();
        Golden.Value.Export.Should().NotContain("\"type\":\"overflow");
    }

    /// <summary>Reads the health rows of a replay back from its export.</summary>
    private sealed class CapturingTextWriter(List<ZoneHealthBin> health) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(string value)
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "out" && root.GetProperty("type").GetString() == "health")
                health.Add(JsonSerializer.Deserialize<ZoneHealthBin>(root.GetProperty("record").GetRawText(), ReplayLedger.Json)!);
        }
    }

    [Fact]
    public void Replay_Should_GiveTheGoldenOutputHash_When_RunAgain()
    {
        var again = ReferenceReplay.Run();
        var golden = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Replay/replay-golden.json"))).RootElement;

        again.Hashes.Should().Be(Golden.Value.Hashes, "the same records always give the same hashes");
        Golden.Value.Hashes.OutputHead.Should().Be(golden.GetProperty("outputHead").GetString(),
            "the engine's outputs for the reference evening are locked; a change is reviewed and the golden value updated with the human's approval");
        Golden.Value.Hashes.Outputs.Should().Be(golden.GetProperty("outputs").GetInt64());
    }

    [Fact]
    public void Replay_Should_GiveTheSameOutputHash_When_TheEveningIsIngestedAgain()
    {
        var again = ReferenceReplay.Run(source: ReferenceReplay.IngestAgain());

        again.Hashes.OutputHead.Should().Be(Golden.Value.Hashes.OutputHead, "health reports get new ids, which only break ties between devices");
        again.Hashes.InputHead.Should().NotBe(Golden.Value.Hashes.InputHead, "the inputs carry the reports' ids");
    }

    [Fact]
    public void Replay_Should_ChangeItsOutputHash_When_TheSettingsChange()
    {
        var other = ReferenceReplay.Run(settings: new ZoneProcessorSettings { DeviceSilenceSeconds = 600 });

        other.Hashes.OutputHead.Should().NotBe(Golden.Value.Hashes.OutputHead, "S-17's ten silent minutes no longer count as an outage");
        other.Hashes.Genesis.Should().NotBe(Golden.Value.Hashes.Genesis, "the settings are part of the manifest");
    }

    [Fact]
    public void Export_Should_Verify_When_Untouched()
    {
        var verified = ReplayLedger.Verify(new StringReader(Golden.Value.Export));

        verified.Problems.Should().BeEmpty();
        verified.Valid.Should().BeTrue();
        verified.Hashes.Should().Be(Golden.Value.Hashes);
        verified.Manifest.Zones.Should().Equal("A-VIS", "CI-C");
    }

    [Fact]
    public void Export_Should_FailVerification_When_ALineIsTooLong()
    {
        var lines = Golden.Value.Export.Split('\n');
        var hostile = lines[0] + "\n" + new string('x', ReplayLedger.MaxLineLength + 10) + "\n";

        var verified = ReplayLedger.Verify(new StringReader(hostile));

        verified.Valid.Should().BeFalse();
        verified.Problems.Should().ContainSingle(p => p.Contains("longer", StringComparison.Ordinal));
    }

    [Fact]
    public void Inputs_Should_KeepBatchesApart_When_RowsShareABatchIdButNotTheBatch()
    {
        var rows = ReferenceReplay.Ingested.Batches.Where(b => b.QueueZoneName == "A-VIS").Take(2).SelectMany(ReferenceReplay.Rows).ToList();
        var forged = rows.Select((r, i) => i == 0 ? r : r with { BatchId = rows[0].BatchId }).ToList();

        var batches = ArchivedBatch.Group(forged);

        batches.Should().HaveCountGreaterThan(1, "rows of another kind or receive time stay in their own batch");
        batches.Should().OnlyContain(b => b.Events.All(e => e.Kind == b.Kind));
        var convert = () => batches.Select(b => b.ToBatch()).ToList();
        convert.Should().NotThrow();
    }

    [Fact]
    public void Export_Should_FailVerification_When_ALineIsChangedDroppedAddedOrMoved()
    {
        var lines = Golden.Value.Export.TrimEnd('\n').Split('\n');
        var output = Array.FindIndex(lines, l => l.Contains("\"type\":\"bin\"", StringComparison.Ordinal));
        var input = Array.FindIndex(lines, l => l.StartsWith("{\"kind\":\"in\"", StringComparison.Ordinal));
        string Join(IEnumerable<string> l) => string.Join('\n', l) + "\n";

        var changed = lines.ToArray();
        changed[output] = changed[output].Replace("\"entries\":", "\"entries\":1", StringComparison.Ordinal);
        var edited = lines.ToArray();
        edited[input] = edited[input].Replace("S-15", "S-16", StringComparison.Ordinal);
        var dropped = lines.Where((_, i) => i != output).ToArray();
        var added = lines.Take(output).Append(lines[output]).Concat(lines.Skip(output)).ToArray();
        var moved = lines.ToArray();
        (moved[output], moved[output + 1]) = (moved[output + 1], moved[output]);
        var truncated = lines.Take(lines.Length - 1).ToArray();
        var endForged = lines.ToArray();
        endForged[^1] = endForged[^1].Replace(Golden.Value.Hashes.OutputHead, new string('0', 64), StringComparison.Ordinal);
        var manifest = lines.ToArray();
        manifest[0] = manifest[0].Replace("\"profileVersion\":12", "\"profileVersion\":13", StringComparison.Ordinal);

        // A forged record placed before the genuine one (a reader taking the first sees it), and an unhashed note.
        var duplicate = lines.ToArray();
        duplicate[output] = duplicate[output].Replace("\"record\":{", "\"record\":{\"entries\":0},\"record\":{", StringComparison.Ordinal);
        var note = lines.ToArray();
        note[output] = note[output].Replace("\"chain\":", "\"note\":\"auditor approved\",\"chain\":", StringComparison.Ordinal);
        var manifestNote = lines.ToArray();
        manifestNote[0] = manifestNote[0].Replace("{\"kind\":\"manifest\",", "{\"kind\":\"manifest\",\"note\":1,", StringComparison.Ordinal);

        foreach (var (name, tampered) in new[]
                 {
                     ("changed output", changed), ("changed input", edited), ("dropped", dropped), ("added", added), ("moved", moved),
                     ("truncated", truncated), ("forged end", endForged), ("changed manifest", manifest), ("duplicate record", duplicate),
                     ("extra property", note), ("extra manifest property", manifestNote)
                 })
        {
            var verified = ReplayLedger.Verify(new StringReader(Join(tampered)));
            verified.Valid.Should().BeFalse(name);
            verified.Problems.Should().NotBeEmpty(name);
        }
    }
}
