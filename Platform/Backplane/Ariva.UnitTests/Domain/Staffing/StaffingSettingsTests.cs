using Ariva.Core.Staffing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Domain.Staffing;

/// <summary>
/// ARV-181a: the <c>Staffing</c> settings: the defaults (target 10 minutes, busy cap 0.85, no fallback cycle time shipped),
/// each value within its bounds, site and lane keys in their shapes (CWE-501: configuration is input too), the resolution of
/// a lane's target, cap and fallback (site lane, site, deployment lane, deployment), and binding from configuration.
/// </summary>
public sealed class StaffingSettingsTests
{
    [Fact]
    public void Defaults_Should_BeValid_When_NothingIsConfigured()
    {
        var settings = new StaffingSettings();

        settings.Problems().Should().BeEmpty();
        settings.TargetFor("DMO", "VIS").Should().Be(10);
        settings.BusyCapFor("DMO").Should().Be(0.85);
        settings.FallbackCycleSecondsFor("DMO", "VIS").Should().BeNull("no fallback cycle time is shipped");
        settings.FallbackCycleSeconds.Should().BeEmpty();
    }

    public static TheoryData<StaffingSettings, string> OutOfBounds => new()
    {
        { new StaffingSettings { TargetMinutes = 0.5 }, "Staffing:TargetMinutes" },
        { new StaffingSettings { TargetMinutes = 121 }, "Staffing:TargetMinutes" },
        { new StaffingSettings { TargetMinutes = double.NaN }, "Staffing:TargetMinutes" },
        { new StaffingSettings { BusyCap = 0.49 }, "Staffing:BusyCap" },
        { new StaffingSettings { BusyCap = 1.01 }, "Staffing:BusyCap" },
        { new StaffingSettings { LaneTargetMinutes = new(StringComparer.Ordinal) { ["vis"] = 10 } }, "Staffing:LaneTargetMinutes" },
        { new StaffingSettings { LaneTargetMinutes = new(StringComparer.Ordinal) { ["VIS"] = 200 } }, "Staffing:LaneTargetMinutes:VIS" },
        { new StaffingSettings { FallbackCycleSeconds = new(StringComparer.Ordinal) { ["VIS"] = 2 } }, "Staffing:FallbackCycleSeconds:VIS" },
        { new StaffingSettings { FallbackCycleSeconds = new(StringComparer.Ordinal) { ["VISITOR"] = 95 } }, "Staffing:FallbackCycleSeconds" },
        { new StaffingSettings { ThroughputWindowMinutes = 14 }, "Staffing:ThroughputWindowMinutes" },
        { new StaffingSettings { MinOpenDeskMinutes = 0 }, "Staffing:MinOpenDeskMinutes" },
        { new StaffingSettings { MinExits = 0 }, "Staffing:MinExits" },
        { new StaffingSettings { CycleMinMinutes = 0.01 }, "Staffing:CycleMinMinutes" },
        { new StaffingSettings { CycleMaxMinutes = 0.2 }, "Staffing:CycleMaxMinutes" },
        { new StaffingSettings { QueueFreshMinutes = 0 }, "Staffing:QueueFreshMinutes" },
        { new StaffingSettings { OpenFreshMinutes = 31 }, "Staffing:OpenFreshMinutes" },
        { new StaffingSettings { StaleMinutes = 61 }, "Staffing:StaleMinutes" },
        { new StaffingSettings { MaxDeskMinuteRows = 999 }, "Staffing:MaxDeskMinuteRows" },
        { new StaffingSettings { MaxLaneDeskRows = 0 }, "Staffing:MaxLaneDeskRows" },
        { new StaffingSettings { MaxLaneDeskRows = 100_001 }, "Staffing:MaxLaneDeskRows" },
        { new StaffingSettings { MaxSitesPerRun = 0 }, "Staffing:MaxSitesPerRun" },
        { new StaffingSettings { Sites = new(StringComparer.Ordinal) { ["dmo"] = new StaffingSiteSettings() } }, "Staffing:Sites" },
        { new StaffingSettings { Sites = new(StringComparer.Ordinal) { ["DMO"] = new StaffingSiteSettings { BusyCap = 0.3 } } }, "Staffing:Sites:DMO:BusyCap" },
        { new StaffingSettings { Sites = new(StringComparer.Ordinal) { ["DMO"] = new StaffingSiteSettings { TargetMinutes = 0 } } }, "Staffing:Sites:DMO:TargetMinutes" },
        {
            new StaffingSettings { Sites = new(StringComparer.Ordinal) { ["DMO"] = new StaffingSiteSettings { FallbackCycleSeconds = new(StringComparer.Ordinal) { ["CIT"] = 601 } } } },
            "Staffing:Sites:DMO:FallbackCycleSeconds:CIT"
        }
    };

    [Theory]
    [MemberData(nameof(OutOfBounds))]
    public void Problems_Should_NameTheSetting_When_AValueIsOutOfItsBounds(StaffingSettings settings, string name)
    {
        settings.Problems().Should().Contain(p => p.StartsWith(name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20_000)]
    [InlineData(100_000)]
    public void Problems_Should_AcceptTheLaneDeskBound_When_WithinItsRange(int rows)
    {
        new StaffingSettings { MaxLaneDeskRows = rows }.Problems().Should().BeEmpty();
    }

    [Theory]
    [InlineData(60, 3, 100, 6_400)]
    [InlineData(240, 15, 100, 25_600)]
    [InlineData(15, 1, 1, 17)]
    [InlineData(60, 3, 0, 0)]
    public void MaxQueueRows_Should_BeTheLanesTimesTheMinutesRead_When_Asked(int window, int fresh, int lanes, int rows)
    {
        // CWE-400: one row per zone and minute (queue_minute's key) over the throughput window, its freshness and the
        // current minute; at most 100 lanes x 256 minutes = 25,600 (the largest settings).
        var settings = new StaffingSettings { ThroughputWindowMinutes = window, QueueFreshMinutes = fresh };

        settings.QueueReadMinutes.Should().Be(window + fresh + 1);
        settings.MaxQueueRows(lanes).Should().Be(rows);
    }

    [Fact]
    public void MaxQueueRows_Should_StayWithinItsBound_When_TheSettingsAreAtTheirLargest()
    {
        var largest = new StaffingSettings { ThroughputWindowMinutes = 240, QueueFreshMinutes = 15 };

        largest.Problems().Should().BeEmpty();
        largest.MaxQueueRows(Ariva.Core.Services.Border.ISvcStaffingRecommendations.MaxLanes).Should().Be(25_600);
        new StaffingSettings { ThroughputWindowMinutes = 241 }.Problems().Should().NotBeEmpty("the window is at most 240 minutes");
        new StaffingSettings { QueueFreshMinutes = 16 }.Problems().Should().NotBeEmpty("the freshness is at most 15 minutes");
        new StaffingSettings().MaxQueueRows(-1).Should().Be(0);
    }

    [Fact]
    public void Problems_Should_RefuseTooManySitesOrLaneValues_When_Configured()
    {
        var sites = Enumerable.Range(0, StaffingSettings.MaxSites + 1).ToDictionary(i => $"S{i:000}", _ => new StaffingSiteSettings(), StringComparer.Ordinal);
        var lanes = Enumerable.Range(0, StaffingSettings.MaxLaneValues + 1).ToDictionary(i => new string((char)('A' + i / 26), 1) + (char)('A' + i % 26), _ => 10.0,
            StringComparer.Ordinal);

        new StaffingSettings { Sites = sites }.Problems().Should().Contain(p => p.Contains("at most 500 sites", StringComparison.Ordinal));
        new StaffingSettings { LaneTargetMinutes = lanes }.Problems().Should().Contain(p => p.Contains("at most 50 lane values", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolution_Should_TakeTheSitesLaneThenTheSiteThenTheDeploymentsLaneThenTheDeployment_When_Asked()
    {
        var settings = new StaffingSettings
        {
            TargetMinutes = 10,
            BusyCap = 0.85,
            LaneTargetMinutes = new(StringComparer.Ordinal) { ["CRW"] = 5, ["RES"] = 8 },
            FallbackCycleSeconds = new(StringComparer.Ordinal) { ["VIS"] = 95, ["CIT"] = 30 },
            Sites = new(StringComparer.Ordinal)
            {
                ["DMO"] = new StaffingSiteSettings
                {
                    TargetMinutes = 12,
                    LaneTargetMinutes = new(StringComparer.Ordinal) { ["VIS"] = 15 },
                    BusyCap = 0.9,
                    FallbackCycleSeconds = new(StringComparer.Ordinal) { ["VIS"] = 100 }
                },
                ["AMM"] = new StaffingSiteSettings { LaneTargetMinutes = new(StringComparer.Ordinal) { ["VIS"] = 20 } }
            }
        };

        settings.TargetFor("DMO", "VIS").Should().Be(15);
        settings.TargetFor("DMO", "CRW").Should().Be(12, "the site's own value comes before the deployment's lane value");
        settings.TargetFor("AMM", "CRW").Should().Be(5);
        settings.TargetFor("AMM", "CIT").Should().Be(10);
        settings.TargetFor("ZZZ", "RES").Should().Be(8);
        settings.TargetFor(null, null).Should().Be(10);
        settings.BusyCapFor("DMO").Should().Be(0.9);
        settings.BusyCapFor("AMM").Should().Be(0.85);
        settings.FallbackCycleSecondsFor("DMO", "VIS").Should().Be(100);
        settings.FallbackCycleSecondsFor("DMO", "CIT").Should().Be(30);
        settings.FallbackCycleSecondsFor("AMM", "RES").Should().BeNull();
        settings.FallbackCycleSecondsFor("AMM", null).Should().BeNull();
        settings.Problems().Should().BeEmpty();
    }

    [Fact]
    public void Binding_Should_ReadSiteAndLaneValues_When_Configured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Staffing:TargetMinutes"] = "12",
            ["Staffing:BusyCap"] = "0.8",
            ["Staffing:Sites:DMO:BusyCap"] = "0.9",
            ["Staffing:Sites:DMO:LaneTargetMinutes:VIS"] = "15",
            ["Staffing:Sites:DMO:FallbackCycleSeconds:VIS"] = "95"
        }).Build();

        var settings = configuration.GetSection(StaffingSettings.SectionName).Get<StaffingSettings>();

        settings.Problems().Should().BeEmpty();
        settings.TargetFor("DMO", "VIS").Should().Be(15);
        settings.TargetFor("DMO", "CIT").Should().Be(12);
        settings.BusyCapFor("DMO").Should().Be(0.9);
        settings.BusyCapFor("AMM").Should().Be(0.8);
        settings.FallbackCycleSecondsFor("DMO", "VIS").Should().Be(95);
        new StaffingSettings().TargetFor("DMO", "VIS").Should().Be(10, "binding changed no shared default");
    }
}
