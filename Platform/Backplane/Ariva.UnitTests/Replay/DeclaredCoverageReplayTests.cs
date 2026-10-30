using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Infra.Messaging;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// ARV-259e5 on the reference evening (seed 9303): S-17 is out from 18:20 to 18:30 while S-15 is heard throughout. With S-15 counting
/// both Visitors lines and S-17 none, nothing changes: the golden hash holds. With S-15 counting the entry and S-17 alone the exit, the
/// exit has no live covering device while S-17 is out: only the 18:15 bin (Unknown instead of Degraded) and the live minutes 18:20 to
/// 18:29 (Unknown) change. The archived form of the declared evening replays to the same hash (GoldenReplayArchiveTests).
/// </summary>
public sealed class DeclaredCoverageReplayTests
{
    private const string Entry = "A-VIS entry";
    private const string Exit = "A-VIS exit";

    private static string Json(object value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement Golden() =>
        JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Replay/replay-golden.json"))).RootElement;

    private static readonly Lazy<(ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs)> Undeclared = new(() => ReferenceReplay.Run());

    [Fact]
    public void Replay_Should_KeepTheGoldenHash_When_S15CountsBothLinesAndS17None()
    {
        var golden = Golden();
        var declared = ReferenceReplay.Declared(d => d switch { "S-15" => [Entry, Exit], "S-17" => [], _ => null });
        declared.Health.Should().Contain(r => r.DeviceCode == "S-17" && r.CoveredLines != null && r.CoveredLines.Count == 0);

        var (hashes, outputs) = ReferenceReplay.Run(source: declared);

        hashes.OutputHead.Should().Be(golden.GetProperty("outputHead").GetString(), "S-15, heard throughout, covers both lines");
        hashes.Outputs.Should().Be(golden.GetProperty("outputs").GetInt64());
        outputs["DMO/A-VIS"].SelectMany(o => o.Refusals).Should().BeEmpty("every declared name is a line of the zone");
    }

    [Fact]
    public void Replay_Should_ChangeOnlyThe1815BinAndTheLiveMinutes1820To1829_When_S17AloneCountsTheExit()
    {
        var (hashes, outputs) = ReferenceReplay.Run(source: ReferenceReplay.Declared(ReferenceReplay.ExitAlone));
        var (goldenHashes, golden) = Undeclared.Value;
        ZoneOutputs Of(Dictionary<string, List<ZoneOutputs>> run, string zone) => run["DMO/" + zone].Single();
        var (before, after) = (Of(golden, "A-VIS"), Of(outputs, "A-VIS"));

        hashes.OutputHead.Should().NotBe(goldenHashes.OutputHead);
        Json(Of(outputs, "CI-C")).Should().Be(Json(Of(golden, "CI-C")), "island C is another zone");

        // Live: the same minutes and figures; Unknown exactly where S-17's outage made them degraded, within 18:20 to 18:29 (a minute
        // published before its silence passed the limit stays as it was published).
        var unknown = after.Live.Where(l => l.Unknown).Select(l => Clock(l.MinuteUtc)).ToList();
        unknown.Should().NotBeEmpty().And.OnlyContain(m => string.CompareOrdinal(m, "18:20") >= 0 && string.CompareOrdinal(m, "18:30") < 0).And.Contain("18:29");
        unknown.Should().Equal(before.Live.Where(l => l.LengthDegraded).Select(l => Clock(l.MinuteUtc)));
        Json(after.Live.Select(l => l with { Unknown = false })).Should().Be(Json(before.Live));

        // Bins: the 18:15 bin is Unknown where it was Degraded (S-17's outage); every other bin, and every revision, as before.
        var at1815 = ReferenceReplay.WallOf(1095);
        after.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == at1815).Should().NotBeEmpty().And.OnlyContain(b => b.Quality == BinQuality.Unknown);
        before.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == at1815).Should().OnlyContain(b => b.Quality == BinQuality.Degraded);
        after.Bins.Where(b => b.Quality == BinQuality.Unknown).Select(b => b.StartUtc).Distinct().Should().Equal(at1815);
        Json(after.Bins.Where(b => b.StartUtc != at1815)).Should().Be(Json(before.Bins.Where(b => b.StartUtc != at1815)));
        after.Bins.Count.Should().Be(before.Bins.Count);

        // Nothing else: outages, recomputations, line counts, health, overflow and desk readings as before.
        Json(after with { Live = [], Bins = [] }).Should().Be(Json(before with { Live = [], Bins = [] }));
    }

    [Fact]
    public void Replay_Should_GiveTheSameHash_When_RunAgain()
    {
        var first = ReferenceReplay.Run(source: ReferenceReplay.Declared(ReferenceReplay.ExitAlone)).Hashes;

        ReferenceReplay.Run(source: ReferenceReplay.Declared(ReferenceReplay.ExitAlone)).Hashes.Should().Be(first, "declarations are records like any other");
    }
}
