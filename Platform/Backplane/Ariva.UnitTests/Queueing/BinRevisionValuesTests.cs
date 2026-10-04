using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ADR-0007, F6: a recomputed final bin is a new revision only when a value that matters changed. Each compared value
/// changes the answer on its own; the revision number, its reason and the late-event count do not (ARV-069 triage).
/// </summary>
public sealed class BinRevisionValuesTests
{
    private static readonly WaitSummary Waits = new(10, 6.5, 6, 11, 12, 0.9, [(10, 4L), (20, 6L)]);

    private static readonly BinResult Bin = new("DMO/A-VIS", new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15), 1,
        BinStatus.Final, BinQuality.Good, 12, 11, Waits, 1, 0, 0, 0, 0, 1, 0, 3, null);

    public static TheoryData<string, BinResult> Changed => new()
    {
        { "zone", Bin with { QueueZone = "DMO/A-CIT" } },
        { "start", Bin with { StartUtc = Bin.StartUtc.AddMinutes(15) } },
        { "length", Bin with { Length = TimeSpan.FromMinutes(30) } },
        { "status", Bin with { Status = BinStatus.Provisional } },
        { "quality", Bin with { Quality = BinQuality.Degraded } },
        { "entries", Bin with { Entries = 13 } },
        { "exits", Bin with { Exits = 10 } },
        { "abandoned", Bin with { Abandoned = 2 } },
        { "fragmented", Bin with { Fragmented = 1 } },
        { "censored", Bin with { Censored = 1 } },
        { "reanchored", Bin with { Reanchored = 1 } },
        { "rejected", Bin with { Rejected = 1 } },
        { "open", Bin with { Open = 0 } },
        { "profile version", Bin with { ZoneProfileVersion = 4 } },
        { "waits", Bin with { Waits = Waits with { Waits = 11 } } },
        { "mean", Bin with { Waits = Waits with { MeanMinutes = 7 } } },
        { "p50", Bin with { Waits = Waits with { P50Minutes = 5 } } },
        { "p90", Bin with { Waits = Waits with { P90Minutes = 10 } } },
        { "p95", Bin with { Waits = Waits with { P95Minutes = 13 } } },
        { "share", Bin with { Waits = Waits with { ShareWithinTarget = 0.8 } } },
        { "histogram", Bin with { Waits = Waits with { Histogram = [(10, 4L), (21, 6L)] } } }
    };

    [Theory]
    [MemberData(nameof(Changed))]
    public void SameValues_Should_BeFalse_When_AValueThatMattersChanged(string what, BinResult changed)
    {
        Bin.SameValues(changed).Should().BeFalse("a different {0} is a new revision", what);
        changed.SameValues(Bin).Should().BeFalse();
    }

    [Fact]
    public void SameValues_Should_IgnoreTheRevisionItsReasonAndLateEvents_AndRefuseNothing()
    {
        Bin.SameValues(Bin with { Revision = 2, RevisionReason = "late events", LateEvents = 40, Waits = Waits with { Histogram = [.. Waits.Histogram] } })
            .Should().BeTrue();
        Bin.SameValues(null).Should().BeFalse();
    }
}
