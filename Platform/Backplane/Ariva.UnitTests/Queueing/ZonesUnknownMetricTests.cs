using Ariva.Infra.Streaming;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-259e3 (formulas F11): <c>ariva.stream.zones_unknown</c>, the queue zones a Stream instance holds that are Unknown now, by
/// site. Each instance reports its own zones, so the instances of a group add up: every site it holds a zone of, 0 when none is
/// Unknown, at most <see cref="StreamMetrics.MaxSites"/>; a measurement past its freshness, or from a worker that stopped, is not
/// reported (the worker's measurement is in the integration tests, <c>QueueStreamCoverageTests</c>).
/// </summary>
public sealed class ZonesUnknownMetricTests
{
    [Fact]
    public void UnknownBySite_Should_CountEachSitesUnknownZones_When_SomeAreAndSomeAreNot()
    {
        StreamMetrics.UnknownBySite([("DMO", true), ("AUH-TA", false), ("DMO", false), ("DMO", true), ("AUH-TA", false), ("BAH", true)])
            .Should().Equal([("AUH-TA", 0), ("BAH", 1), ("DMO", 2)], "every site held, 0 when none of its zones is Unknown, in code order");
        StreamMetrics.UnknownBySite([("DMO", false), (null, true), ("", true)]).Should().Equal([("DMO", 0)], "a zone without a site is left out");
        StreamMetrics.UnknownBySite([("DMO", false), ("dmo", true), ("passport 123", true), ("DMO/x", true), ("DMO\u202E", true), (new string('A', 40), true)])
            .Should().Equal([("DMO", 0)], "a tag carries a site code only, whatever reaches the reading");
        StreamMetrics.UnknownBySite([]).Should().BeEmpty("no zone held, no site");
        StreamMetrics.UnknownBySite([("AUH-TA", true), ("AUH", false)]).Should().Equal([("AUH", 0), ("AUH-TA", 1)], "sites are compared by code, ordinally");
        FluentActions.Invoking(() => StreamMetrics.UnknownBySite(null)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UnknownBySite_Should_KeepTheSitesWithTheMostUnknownZones_When_TheyAreMoreThanItsBound()
    {
        // A replica holding zones of more sites than a reading carries: the sites with Unknown zones are kept first (a site left
        // out has none, or fewer), then by code; the reading is in code order.
        var zones = Enumerable.Range(0, StreamMetrics.MaxSites + 20).Select(i => ($"S{i:000}", false))
            .Append(("S119", true)).Append(("S119", true)).Append(("S110", true)).ToList();

        var sites = StreamMetrics.UnknownBySite(zones);

        sites.Should().HaveCount(StreamMetrics.MaxSites).And.BeInAscendingOrder(s => s.Site, StringComparer.Ordinal);
        sites.Should().Contain(("S119", 2)).And.Contain(("S110", 1), "the sites with Unknown zones come first");
        sites.Where(s => s.Count == 0).Select(s => s.Site).Should().Equal(Enumerable.Range(0, StreamMetrics.MaxSites - 2).Select(i => $"S{i:000}"),
            "then the others by code");
    }

    [Fact]
    public void ZonesUnknown_Should_ReportEachSiteForItsWindow_When_TheWorkerHasMeasured()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new StreamMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(StreamMetrics.ZonesUnknownName).Should().BeEmpty("nothing before the first measurement");

        metrics.ZonesUnknown([("DMO", true), ("DMO", false), ("AUH-TA", false)], TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(60));

        reader.Observe(StreamMetrics.ZonesUnknownName).Select(m => (m.Tags["site"], m.Value)).Should().BeEquivalentTo([("DMO", 1d), ("AUH-TA", 0d)],
            "a site whose zones are all measured reports 0, so a sum over the replicas sees every site");
        reader.Observe(StreamMetrics.ZonesUnknownName).Should().AllSatisfy(m => m.Tags.Keys.Should().Equal("site"));

        clock.Advance(TimeSpan.FromSeconds(1));
        reader.Observe(StreamMetrics.ZonesUnknownName).Should().BeEmpty("a worker that stopped measuring (a stuck loop) reads as missing past its window");

        metrics.ZonesUnknown([("DMO", false)], TimeSpan.FromSeconds(60));
        reader.Observe(StreamMetrics.ZonesUnknownName).Should().ContainSingle().Which.Value.Should().Be(0, "the last measurement replaces the one before");
        metrics.ZonesUnknown([], TimeSpan.FromSeconds(60));
        reader.Observe(StreamMetrics.ZonesUnknownName).Should().BeEmpty("a worker that holds no zone (its partitions revoked) reports no site");
        metrics.ZonesUnknown([("DMO", true)], TimeSpan.FromSeconds(60));
        metrics.Clear();
        reader.Observe(StreamMetrics.ZonesUnknownName).Should().BeEmpty("the worker stopped");
        FluentActions.Invoking(() => metrics.ZonesUnknown(null, TimeSpan.FromSeconds(60))).Should().Throw<ArgumentNullException>();
    }
}
