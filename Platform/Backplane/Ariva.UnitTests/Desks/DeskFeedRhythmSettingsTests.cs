using Ariva.Infra.Border;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Logging;

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

    // case, the stream's default lateness, push interval and idle tick, a site's own lateness and push interval (null: none), expected D in seconds
    public static TheoryData<string, int, int, int, int?, int?, int> LengthDues => new()
    {
        { "the defaults: 90 + 60 + 15 + 15", 90, 60, 15, null, null, 180 },
        { "the site's own lateness and push interval", 90, 60, 15, 300, 120, 450 },
        { "the site's own lateness only", 90, 60, 15, 120, null, 210 },
        { "no lateness, no push, no idle tick: the write alone", 0, 0, 0, null, null, 15 },
        { "the largest the stream allows", 1_800, 600, 3_600, null, null, 6_015 }
    };

    [Theory]
    [MemberData(nameof(LengthDues))]
    public void QueueLengthDue_Should_FollowTheStreamsTimingForTheSite_When_ComputedForALaneContext(string because, int lateness, int push, int idleTick,
        int? siteLateness, int? sitePush, int expected)
    {
        // ARV-149a: D, how long after a minute ends the stream has written its length, from the stream's settings of the same host.
        var stream = new StreamSettings { LatenessSeconds = lateness, PushIntervalSeconds = push, IdleTickSeconds = idleTick };
        if (siteLateness.HasValue || sitePush.HasValue)
            stream.Sites["XS1"] = new StreamSiteSettings { LatenessSeconds = siteLateness, PushIntervalSeconds = sitePush };

        DeskFeed.QueueLengthDue(stream, "XS1").Should().Be(TimeSpan.FromSeconds(expected), because);
        DeskFeed.QueueLengthDue(stream, "XS2").Should().Be(TimeSpan.FromSeconds(lateness + push + idleTick + DeskFeed.QueueLengthWriteSeconds), "another site takes the defaults");
    }

    [Fact]
    public void QueueLengthDue_Should_TakeTheStreamsDefaults_When_TheHostHasNoStreamSettings()
    {
        DeskFeed.QueueLengthDue(null, "XS1").Should().Be(TimeSpan.FromSeconds(180));
        DeskFeed.QueueLengthDue(null, null).Should().Be(TimeSpan.FromSeconds(180));
        // A value below 0 (which the stream's checks refuse at start) counts as 0 rather than taking from the others.
        DeskFeed.QueueLengthDue(new StreamSettings { LatenessSeconds = -60, PushIntervalSeconds = 60, IdleTickSeconds = 15 }, "XS1")
            .Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void QueueLengthsNeverDue_Should_NameTheSitesWhoseLengthsCanNeverBeOverdue_When_TheirStreamTimingIsLong()
    {
        // ARV-149a (R-f of its security re-check): a minute is fresh back to 5 minutes before the engine's watermark (the read less
        // the feed's lateness) and overdue a minute and D after its start, so D must be at most 240 s plus the feed's lateness
        // (330 s by default); beyond it a site's missing lengths are never overdue and its zones stay not known, uncounted.
        DeskFeed.QueueLengthDueWindow(new DeskFeedSettings()).Should().Be(TimeSpan.FromSeconds(330));
        DeskFeed.QueueLengthDueWindow(new DeskFeedSettings { LatenessSeconds = 0 }).Should().Be(TimeSpan.FromSeconds(240));
        DeskFeed.QueueLengthDueWindow(null).Should().Be(TimeSpan.FromSeconds(330));
        var stream = new StreamSettings();
        stream.Sites["XS3"] = new StreamSiteSettings { LatenessSeconds = 1_800, PushIntervalSeconds = 600 };
        stream.Sites["XS1"] = new StreamSiteSettings { LatenessSeconds = 240 };
        stream.Sites["XS2"] = new StreamSiteSettings { LatenessSeconds = 241 };
        stream.Sites["not a code"] = new StreamSiteSettings { LatenessSeconds = 1_800 };

        // XS1's D is 330 s, at the window (a fresh minute can still fall due); XS2's 331 s and XS3's 2,430 s cannot. A key that is
        // not a site code is never named (the stream refuses it at start).
        DeskFeed.QueueLengthsNeverDue(stream, new DeskFeedSettings()).Should().Equal(
            ("XS2", TimeSpan.FromSeconds(331), TimeSpan.FromSeconds(330)), ("XS3", TimeSpan.FromSeconds(2_430), TimeSpan.FromSeconds(330)));
        DeskFeed.QueueLengthsNeverDue(stream, new DeskFeedSettings { LatenessSeconds = 101 }).Select(f => f.Site).Should()
            .Equal(new[] { "XS3" }, "a longer feed lateness widens the window to 341 s");
        DeskFeed.QueueLengthsNeverDue(new StreamSettings(), new DeskFeedSettings()).Should().BeEmpty("the defaults' 180 s are within 330 s");
        DeskFeed.QueueLengthsNeverDue(null, null).Should().BeEmpty();
        DeskFeed.QueueLengthsNeverDue(new StreamSettings { LatenessSeconds = 600 }, new DeskFeedSettings()).Should()
            .Equal(new[] { ((string)null, TimeSpan.FromSeconds(690), TimeSpan.FromSeconds(330)) }, "the defaults themselves: every site without timing of its own");
    }

    [Fact]
    public void WarnOfQueueLengthsNeverDue_Should_NameOnlyTheSiteAndTheTwoNumbers_When_TheFeedStarts()
    {
        // ARV-149a (R-f): one warning per such site when the feed starts, with the site code, D and the window only.
        var stream = new StreamSettings { LatenessSeconds = 600 };
        stream.Sites["XS2"] = new StreamSiteSettings { LatenessSeconds = 300 };
        stream.Sites["XS1"] = new StreamSiteSettings { LatenessSeconds = 90 };
        var log = new WarningLog();
        var feed = new DeskFeed(new DatabaseSettings(), TimeProvider.System, new DeskFeedSettings(), log, stream);

        feed.WarnOfQueueLengthsNeverDue().Should().Be(2);

        log.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Warning);
        log.Entries[0].Values.Keys.Should().BeEquivalentTo(new[] { "DueSeconds", "WindowSeconds", "{OriginalFormat}" }, "the defaults' warning names no site");
        (log.Entries[0].Values["DueSeconds"], log.Entries[0].Values["WindowSeconds"]).Should().Be(((object)690L, (object)330L));
        log.Entries[1].Values.Keys.Should().BeEquivalentTo(new[] { "Site", "DueSeconds", "WindowSeconds", "{OriginalFormat}" });
        (log.Entries[1].Values["Site"], log.Entries[1].Values["DueSeconds"], log.Entries[1].Values["WindowSeconds"]).Should().Be(((object)"XS2", (object)390L, (object)330L));
        feed.QueueLengthDueFor("XS2").Should().Be(TimeSpan.FromSeconds(390));
        feed.QueueLengthDueFor("XS1").Should().Be(TimeSpan.FromSeconds(180));

        // The defaults: nothing to say.
        var quiet = new WarningLog();
        new DeskFeed(new DatabaseSettings(), TimeProvider.System, new DeskFeedSettings(), quiet).WarnOfQueueLengthsNeverDue().Should().Be(0);
        quiet.Entries.Should().BeEmpty();
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object> Values);

    // Keeps the feed's log lines with their structured values, to prove what a warning carries.
    private sealed class WarningLog : ILogger<DeskFeed>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = state is IReadOnlyList<KeyValuePair<string, object>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) : [];
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }
}
