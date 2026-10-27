using Ariva.Infra.Border;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Border;

/// <summary>
/// ARV-257: each border site's AMAN feed age, from the heartbeat the desk feed stores per site and lists at the start of each
/// read, tagged with the site only. Each age is the one at the listing, so a replica never reports a site older than it was;
/// a heartbeat that stays where it was (a read that fails or is cut) shows as an age grown at the next listing; a listing
/// replaces the last; a replica whose listings stop reports nothing after the report window; a code that is not a site code
/// and sites past the cap are not reported.
/// </summary>
public sealed class AmanFeedAgesTests
{
    #region Fields

    private static readonly DateTime T0 = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    #endregion

    #region Tests

    [Fact]
    public void Ages_Should_GiveTheSecondsFromEachSitesHeartbeatToTheListing_When_TheSitesAreListed()
    {
        var clock = new Clock(T0);
        var ages = new AmanFeedAges(clock, new DeskFeedSettings());

        ages.Listed([("DMO", T0.AddMinutes(-2)), ("AMM", T0.AddMinutes(-1))], T0);
        clock.Now = T0.AddSeconds(30);

        ages.Ages().Should().BeEquivalentTo([("DMO", 120.0), ("AMM", 60.0)], "the age at the listing, never one the replica has not seen");
    }

    [Fact]
    public void Ages_Should_GrowFromListingToListing_When_TheStoredHeartbeatStays()
    {
        var clock = new Clock(T0);
        var ages = new AmanFeedAges(clock, new DeskFeedSettings { PollSeconds = 15 });
        var heartbeat = T0.AddMinutes(-1);

        for (var s = 0; s <= 300; s += 15)
        {
            clock.Now = T0.AddSeconds(s);
            ages.Listed([("DMO", heartbeat)], clock.Now);
        }

        ages.Ages().Should().ContainSingle().Which.Should().Be(("DMO", 360.0),
            "a read that fails or is cut leaves the stored heartbeat where it was, so the age grows and the alert fires");
    }

    [Fact]
    public void Ages_Should_CountFromTheFirstListingHere_When_TheFeedWasNeverHeard()
    {
        var clock = new Clock(T0);
        var ages = new AmanFeedAges(clock, new DeskFeedSettings());

        ages.Listed([("DMO", null)], T0);
        clock.Now = T0.AddSeconds(20);
        ages.Listed([("DMO", null), ("AMM", null)], clock.Now);
        clock.Now = T0.AddSeconds(40);
        ages.Listed([("DMO", null), ("AMM", null)], clock.Now);

        ages.Ages().Should().BeEquivalentTo([("DMO", 40.0), ("AMM", 20.0)], "never heard: counted from this replica's first listing of the site");
    }

    [Fact]
    public void Listed_Should_KeepTheHeartbeatItKnew_When_AListingBringsNone()
    {
        var clock = new Clock(T0);
        var ages = new AmanFeedAges(clock, new DeskFeedSettings());
        ages.Listed([("DMO", null)], T0.AddHours(-1));
        ages.Listed([("DMO", T0.AddMinutes(-1))], T0);

        clock.Now = T0.AddSeconds(15);
        ages.Listed([("DMO", null)], clock.Now);

        ages.Ages().Single().Seconds.Should().Be(75,
            "the site's desk feed state deleted (wiki 10, 4.8i) leaves the heartbeat null: AMAN was still last heard then, not at the first listing an hour ago");
    }

    [Fact]
    public void Listed_Should_StopReportingASite_When_ItIsNoLongerListed()
    {
        var clock = new Clock(T0);
        var ages = new AmanFeedAges(clock, new DeskFeedSettings());
        ages.Listed([("DMO", null), ("AMM", T0)], T0);

        clock.Now = T0.AddSeconds(15);
        ages.Listed([("AMM", T0)], clock.Now);
        clock.Now = T0.AddSeconds(30);
        ages.Listed([("AMM", T0), ("DMO", null)], clock.Now);

        ages.Ages().Should().BeEquivalentTo([("AMM", 30.0), ("DMO", 0.0)],
            "a site whose AMAN mappings are gone is dropped, and a site listed again starts again from that listing");
    }

    [Fact]
    public void Ages_Should_ReportNothing_When_TheLatestListingIsOlderThanTheWindow()
    {
        var clock = new Clock(T0);
        var ages = new AmanFeedAges(clock, new DeskFeedSettings { PollSeconds = 15 });

        ages.Ages().Should().BeEmpty("nothing listed yet");
        ages.Listed([("DMO", T0)], T0);

        ages.ReportWindow.Should().Be(TimeSpan.FromSeconds(60), "two polls of 15 s and half a minute");
        clock.Now = T0.AddSeconds(60);
        ages.Ages().Should().ContainSingle("listed 60 s ago: still reported");
        clock.Now = T0.AddSeconds(61);
        ages.Ages().Should().BeEmpty("this replica's listings stopped (the database out of reach, the feed stopped): the alert's absence rule takes over");
    }

    [Fact]
    public void Ages_Should_NeverBeNegative_When_AHeartbeatIsAheadOfTheListing()
    {
        var ages = new AmanFeedAges(new Clock(T0), new DeskFeedSettings());

        ages.Listed([("DMO", T0.AddMinutes(1))], T0);

        ages.Ages().Single().Seconds.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dmo")]
    [InlineData("DMO\n")]
    [InlineData("TOOLONGSITECODE1")]
    public void Listed_Should_ReportNothing_When_TheCodeIsNotASiteCode(string site)
    {
        var ages = new AmanFeedAges(new Clock(T0), new DeskFeedSettings());

        ages.Listed([(site, T0)], T0);

        ages.Ages().Should().BeEmpty();
    }

    [Fact]
    public void Listed_Should_ReportEachSiteOnceAndAtMostTheCap_When_MoreSitesAreListed()
    {
        var ages = new AmanFeedAges(new Clock(T0), new DeskFeedSettings());
        var codes = Enumerable.Range(0, AmanFeedAges.MaxSites + 5).Select(i => $"S{i:D4}").ToList();

        ages.Listed([("S0000", T0.AddMinutes(-1)), .. codes.Select(c => (c, (DateTime?)T0))], T0);

        ages.Ages().Should().HaveCount(AmanFeedAges.MaxSites);
        ages.Ages().Should().Contain(("S0000", 60.0), "a site listed twice is taken once, at its first row");
        ages.Ages().Select(a => a.Site).Should().NotContain(codes[^1], "sites past the cap are not reported");
    }

    [Fact]
    public void Listed_Should_RefuseNoSites_When_GivenNone()
    {
        var ages = new AmanFeedAges(new Clock(T0), new DeskFeedSettings());

        var act = () => ages.Listed(null, T0);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Gauge_Should_ReportEachSitesAgeTaggedWithTheSiteOnly_When_Collected()
    {
        using var metrics = new BorderMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        var ages = new AmanFeedAges(new Clock(T0), new DeskFeedSettings(), metrics);
        ages.Listed([("DMO", T0.AddMinutes(-3))], T0);

        var measured = reader.Observe(BorderMetrics.FeedAgeName);

        measured.Should().ContainSingle();
        measured[0].Value.Should().Be(180);
        measured[0].Tags.Should().Equal(new Dictionary<string, string> { ["site"] = "DMO" });
    }

    #endregion

    #region Helpers

    /// <summary>A clock set by hand.</summary>
    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    #endregion
}
