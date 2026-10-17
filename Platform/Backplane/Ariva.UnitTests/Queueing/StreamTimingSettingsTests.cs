using Ariva.Core.Queueing;
using Ariva.Infra.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-148: the allowed lateness and the push interval of the stream, by default and per site (<c>Stream:Sites</c>), as
/// the live stream and a replay of the site apply them to the zone settings, and the checks a host starts with.
/// </summary>
public sealed class StreamTimingSettingsTests
{
    private static StreamSettings Bind(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))).Build()
            .GetSection(StreamSettings.SectionName).Get<StreamSettings>() ?? new StreamSettings();

    [Fact]
    public void Defaults_Should_BeTheEnginesDefaults_When_NothingIsConfigured()
    {
        var settings = new StreamSettings();
        var engine = new QueueEngineSettings();

        settings.Problems().Should().BeEmpty();
        TimeSpan.FromSeconds(settings.LatenessSeconds).Should().Be(engine.Lateness).And.Be(TimeSpan.FromSeconds(90));
        TimeSpan.FromSeconds(settings.PushIntervalSeconds).Should().Be(engine.PushInterval).And.Be(TimeSpan.FromSeconds(60));
        settings.ZoneSettingsFor("DMO", new ZoneProcessorSettings()).Should().Be(new ZoneProcessorSettings(), "the defaults change nothing");
        ZoneReplayHash(settings.ZoneSettingsFor("DMO", new ZoneProcessorSettings())).Should().Be(ZoneReplayHash(new ZoneProcessorSettings()));
    }

    private static string ZoneReplayHash(ZoneProcessorSettings settings) => Ariva.Core.Queueing.Replay.ZoneReplay.SettingsHash(settings);

    [Fact]
    public void ZoneSettingsFor_Should_GiveASiteItsOwnLatenessAndPushInterval_When_Configured()
    {
        var settings = Bind(("Stream:LatenessSeconds", "120"), ("Stream:Sites:AUH-TA:LatenessSeconds", "150"),
            ("Stream:Sites:AUH-TA:PushIntervalSeconds", "90"), ("Stream:Sites:DXB:PushIntervalSeconds", "30"));
        var baseline = new ZoneProcessorSettings { ExitWindowMinutes = 7 };

        settings.Problems().Should().BeEmpty();
        var auh = settings.ZoneSettingsFor("AUH-TA", baseline);
        var dxb = settings.ZoneSettingsFor("DXB", baseline);
        var dmo = settings.ZoneSettingsFor("DMO", baseline);

        (auh.Engine.Lateness, auh.Engine.PushInterval).Should().Be((TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(90)));
        (dxb.Engine.Lateness, dxb.Engine.PushInterval).Should().Be((TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30)), "a value left out takes the default");
        (dmo.Engine.Lateness, dmo.Engine.PushInterval).Should().Be((TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(60)));
        auh.ExitWindowMinutes.Should().Be(7, "only the timing changes");
        auh.Engine.MaxBufferedEvents.Should().Be(baseline.Engine.MaxBufferedEvents);
        ZoneReplayHash(auh).Should().NotBe(ZoneReplayHash(dmo), "a replay names the settings it ran under");
        settings.ZoneSettingsFor(null, baseline).Engine.Lateness.Should().Be(TimeSpan.FromSeconds(120));
    }

    public static TheoryData<string, (string Key, string Value)[], string> Refused => new()
    {
        { "a lateness below the push interval", [("Stream:LatenessSeconds", "50")], "Stream:LatenessSeconds is at least its PushIntervalSeconds" },
        { "a site's lateness below its push interval", [("Stream:Sites:DMO:LatenessSeconds", "30")], "Stream:Sites:DMO:LatenessSeconds is at least" },
        { "a site's push interval above its default lateness", [("Stream:Sites:DMO:PushIntervalSeconds", "120")], "Stream:Sites:DMO:LatenessSeconds is at least" },
        { "a negative lateness", [("Stream:LatenessSeconds", "-1")], "Stream:LatenessSeconds is from 0 to 1,800" },
        { "a lateness over half an hour", [("Stream:Sites:DMO:LatenessSeconds", "1801")], "Stream:Sites:DMO:LatenessSeconds is from 0 to 1,800" },
        { "a push interval over ten minutes", [("Stream:PushIntervalSeconds", "601"), ("Stream:LatenessSeconds", "1800")], "Stream:PushIntervalSeconds is from 0 to 600" },
        { "a negative push interval", [("Stream:Sites:DMO:PushIntervalSeconds", "-1")], "Stream:Sites:DMO:PushIntervalSeconds is from 0 to 600" },
        { "a key that is not a site code", [("Stream:Sites:dmo:LatenessSeconds", "90")], "Stream:Sites has a key that is not a site code" },
        { "a key with a path in it", [("Stream:Sites:DMO..X:LatenessSeconds", "90")], "Stream:Sites has a key that is not a site code" }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Problems_Should_RefuseTheTiming_When_ItIsOutOfRange(string because, (string Key, string Value)[] values, string problem)
    {
        Bind(values).Problems().Should().ContainSingle().Which.Should().StartWith(problem, because);
    }

    [Fact]
    public void Problems_Should_AcceptTheEdges_When_TheyAreInRange()
    {
        Bind(("Stream:LatenessSeconds", "0"), ("Stream:PushIntervalSeconds", "0")).Problems().Should().BeEmpty("the receive clock, the rule before ARV-148");
        Bind(("Stream:LatenessSeconds", "1800"), ("Stream:PushIntervalSeconds", "600")).Problems().Should().BeEmpty();
        Bind(("Stream:Sites:DMO:LatenessSeconds", "60")).Problems().Should().BeEmpty("exactly the push interval");
        Bind(("Stream:Sites:AB-12345678:LatenessSeconds", "60")).Problems().Should().BeEmpty("the longest site code");
    }

    private static (string Key, string Value)[] Pairs(string[] values) =>
        [.. values.Select(v => v.Split('=', 2)).Select(p => (p[0], p[1]))];

    [Theory]
    [InlineData("Stream:Sites:DMO: DeviceSilenceSeconds", "Stream:Sites:DMO:PushIntervalSeconds=180", "Stream:Sites:DMO:LatenessSeconds=300")]
    [InlineData("Stream: DeviceSilenceSeconds", "Stream:PushIntervalSeconds=600", "Stream:LatenessSeconds=1800")]
    public void ZoneSettingsProblems_Should_RefuseASite_When_ItsPushIntervalReachesTheDeviceSilenceLimit(string problem, params string[] values)
    {
        // A device that pushes only every 3 minutes or less often would be out between its pushes (the silence limit is
        // 180 seconds): the stream's own ranges allow it, the zone settings refuse it, and the host checks every site's at
        // start rather than when the site's first zone is created.
        var settings = Bind(Pairs(values));

        settings.Problems().Should().BeEmpty("the stream's own ranges allow it");
        settings.ZoneSettingsProblems(new ZoneProcessorSettings()).Should().ContainSingle().Which.Should().StartWith(problem);
    }

    [Fact]
    public void ZoneSettingsProblems_Should_AcceptEverySite_When_ItsPushIntervalIsBelowTheSilenceLimit()
    {
        Bind(("Stream:Sites:DMO:PushIntervalSeconds", "179"), ("Stream:Sites:DMO:LatenessSeconds", "300"))
            .ZoneSettingsProblems(new ZoneProcessorSettings()).Should().BeEmpty();
        new StreamSettings().ZoneSettingsProblems(new ZoneProcessorSettings()).Should().BeEmpty("the defaults");

        var badKey = Bind(("Stream:Sites:dmo:PushIntervalSeconds", "600"), ("Stream:Sites:dmo:LatenessSeconds", "1800"));
        badKey.ZoneSettingsProblems(new ZoneProcessorSettings()).Should().BeEmpty("a key that is not a site code is skipped here");
        badKey.Problems().Should().ContainSingle().Which.Should().Contain("not a site code", "and refused by the stream's own checks");
    }

    [Theory]
    [InlineData("Stream:Sites:dmo:LatenessSeconds=90")]
    [InlineData("Stream:LatenessSeconds=30")]
    [InlineData("Stream:Sites:DMO:PushIntervalSeconds=180", "Stream:Sites:DMO:LatenessSeconds=300")]
    public void AddArivaQueueStream_Should_RefuseToStart_When_TheTimingIsNotValid(params string[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Pairs(values).Select(p => new KeyValuePair<string, string>(p.Key, p.Value))).Build();

        var add = () => Ariva.Di.Extensions.StreamExtensions.AddArivaQueueStream(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), configuration);

        add.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddArivaQueueStream_Should_RegisterTheSitesTiming_When_ItIsValid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([
            new KeyValuePair<string, string>("Stream:Sites:DMO:PushIntervalSeconds", "30"),
            new KeyValuePair<string, string>("Stream:Sites:DMO:LatenessSeconds", "45")
        ]).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        Ariva.Di.Extensions.StreamExtensions.AddArivaQueueStream(services, configuration);

        services.Should().ContainSingle(d => d.ServiceType == typeof(StreamSettings))
            .Which.ImplementationInstance.Should().BeOfType<StreamSettings>()
            .Which.TimingOf("DMO").Should().Be((45, 30));
    }

    [Fact]
    public void Problems_Should_RefuseTheSites_When_MoreThanTheBoundAreNamed()
    {
        var settings = new StreamSettings
        {
            Sites = Enumerable.Range(0, StreamSettings.MaxSites + 1).ToDictionary(i => $"S{i:D4}", _ => new StreamSiteSettings(), StringComparer.Ordinal)
        };

        settings.Problems().Should().ContainSingle().Which.Should().Contain("at most 500 sites");
        new StreamSettings { Sites = settings.Sites.Take(StreamSettings.MaxSites).ToDictionary(StringComparer.Ordinal) }.Problems().Should().BeEmpty();
    }
}
