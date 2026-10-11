using Ariva.Infra.Border;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>ARV-149: the desk feed's settings of the transaction rhythm (<c>Border:DeskFeed</c>), checked at start.</summary>
public sealed class DeskFeedRhythmSettingsTests
{
    [Fact]
    public void Settings_Should_TakeTheProposedRhythm_When_NotConfigured()
    {
        var settings = new DeskFeedSettings();

        settings.RhythmPauseMultiple.Should().Be(4);
        settings.RhythmWindowMinutes.Should().Be(15);
        settings.Problems().Should().BeEmpty();
        settings.Engine.RhythmPauseMultiple.Should().Be(4, "the engine takes the feed's multiple");
        new DeskFeedSettings { RhythmPauseMultiple = 7.5 }.Engine.RhythmPauseMultiple.Should().Be(7.5);
        settings.SensorEngine.RhythmPauseMultiple.Should().Be(4, "the sensor-only engine has no desk that follows the rhythm, whatever its settings");
    }

    [Theory]
    [InlineData(1.99, 15, "RhythmPauseMultiple")]
    [InlineData(20.01, 15, "RhythmPauseMultiple")]
    [InlineData(double.NaN, 15, "RhythmPauseMultiple")]
    [InlineData(double.PositiveInfinity, 15, "RhythmPauseMultiple")]
    [InlineData(4, 4, "RhythmWindowMinutes")]
    [InlineData(4, 61, "RhythmWindowMinutes")]
    public void Settings_Should_BeRefused_When_TheRhythmIsOutOfBounds(double multiple, int window, string setting)
    {
        var problems = new DeskFeedSettings { RhythmPauseMultiple = multiple, RhythmWindowMinutes = window }.Problems().ToList();

        problems.Should().ContainSingle().Which.Should().Contain(setting);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(20, 60)]
    public void Settings_Should_BeAccepted_When_TheRhythmIsAtItsBounds(double multiple, int window)
    {
        new DeskFeedSettings { RhythmPauseMultiple = multiple, RhythmWindowMinutes = window }.Problems().Should().BeEmpty();
    }
}
