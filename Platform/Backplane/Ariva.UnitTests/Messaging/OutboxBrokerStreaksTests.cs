using Ariva.Infra.Messaging.Outbox;
using FluentAssertions;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-245: a row the broker keeps refusing (no attempt counts against it since ARV-165) is reported once, when its streak of
/// broker-unavailable failures reaches the configured length, and the streak ends when the row is sent or fails for a reason
/// of its own. The followed rows are bounded in time and number.
/// </summary>
public sealed class OutboxBrokerStreaksTests
{
    private static readonly DateTime Start = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Streak = TimeSpan.FromMinutes(15);

    [Fact]
    public void BrokerUnavailable_Should_ReportOnceAtTheStreaksLength_When_TheRowKeepsFailing()
    {
        var streaks = new OutboxBrokerStreaks(Streak);

        // Every 5 seconds, as the broker retry tries it, until past the streak.
        var reports = new List<(TimeSpan At, TimeSpan Length)>();
        for (var at = TimeSpan.Zero; at <= Streak + TimeSpan.FromMinutes(5); at += TimeSpan.FromSeconds(5))
        {
            if (streaks.BrokerUnavailable(7, Start + at) is { } length)
                reports.Add((at, length));
        }

        reports.Should().ContainSingle("a row is reported once").Which.Should().Be((Streak, Streak), "at the first failure that reaches the streak");
    }

    [Fact]
    public void BrokerUnavailable_Should_NotReport_When_TheStreakIsShorterThanItsLength()
    {
        var streaks = new OutboxBrokerStreaks(Streak);

        streaks.BrokerUnavailable(7, Start).Should().BeNull();
        streaks.BrokerUnavailable(7, Start + Streak - TimeSpan.FromSeconds(1)).Should().BeNull("one second short of the streak");
        streaks.BrokerUnavailable(7, Start + Streak).Should().Be(Streak);
    }

    [Fact]
    public void Ended_Should_StartTheStreakAfresh_When_TheRowWasSentOrFailedForItsOwnReason()
    {
        var streaks = new OutboxBrokerStreaks(Streak);
        streaks.BrokerUnavailable(7, Start);
        streaks.BrokerUnavailable(7, Start + TimeSpan.FromMinutes(10));

        streaks.Ended(7);

        streaks.BrokerUnavailable(7, Start + TimeSpan.FromMinutes(16)).Should().BeNull("the streak starts again at this failure");
        streaks.BrokerUnavailable(7, Start + TimeSpan.FromMinutes(30)).Should().BeNull("14 minutes into the new streak");
        streaks.BrokerUnavailable(7, Start + TimeSpan.FromMinutes(31)).Should().Be(Streak);
    }

    [Fact]
    public void Ended_Should_LetARowBeReportedAgain_When_ItsNextStreakReachesTheLength()
    {
        var streaks = new OutboxBrokerStreaks(Streak);
        streaks.BrokerUnavailable(7, Start);
        streaks.BrokerUnavailable(7, Start + Streak).Should().Be(Streak);

        streaks.Ended(7);
        streaks.BrokerUnavailable(7, Start + TimeSpan.FromHours(1));

        streaks.BrokerUnavailable(7, Start + TimeSpan.FromHours(1) + Streak).Should().Be(Streak, "a new streak is a new report");
    }

    [Fact]
    public void BrokerUnavailable_Should_FollowEachRowOnItsOwn_When_SeveralRowsFail()
    {
        var streaks = new OutboxBrokerStreaks(Streak);
        streaks.BrokerUnavailable(1, Start);
        streaks.BrokerUnavailable(2, Start + TimeSpan.FromMinutes(5));

        streaks.BrokerUnavailable(1, Start + Streak).Should().Be(Streak);
        streaks.BrokerUnavailable(2, Start + Streak).Should().BeNull("row 2 has failed for 10 minutes");
        streaks.BrokerUnavailable(2, Start + Streak + TimeSpan.FromMinutes(5)).Should().Be(Streak);
    }

    [Fact]
    public void BrokerUnavailable_Should_DropAStreak_When_ItsRowWasNotSeenForTheForgetTime()
    {
        // Another relay sent the row, or this one stopped leading: a streak not noted again for an hour is dropped, and a later
        // failure of the row starts a new one.
        var streaks = new OutboxBrokerStreaks(Streak);
        streaks.BrokerUnavailable(7, Start);
        streaks.BrokerUnavailable(8, Start + OutboxBrokerStreaks.ForgetAfter + TimeSpan.FromSeconds(1));

        streaks.Count.Should().Be(1, "row 7 was not seen for longer than the forget time");
        streaks.BrokerUnavailable(7, Start + OutboxBrokerStreaks.ForgetAfter + TimeSpan.FromSeconds(2)).Should().BeNull("its streak starts again");
    }

    [Fact]
    public void BrokerUnavailable_Should_FollowAtMostMaxRows_When_ManyRowsFail()
    {
        var streaks = new OutboxBrokerStreaks(TimeSpan.Zero);
        for (var seq = 0L; seq < OutboxBrokerStreaks.MaxRows; seq++)
            streaks.BrokerUnavailable(seq, Start).Should().Be(TimeSpan.Zero);

        streaks.BrokerUnavailable(OutboxBrokerStreaks.MaxRows, Start).Should().BeNull("a row past the bound is not followed");
        streaks.Count.Should().Be(OutboxBrokerStreaks.MaxRows);

        streaks.Ended(0);
        streaks.BrokerUnavailable(OutboxBrokerStreaks.MaxRows, Start).Should().Be(TimeSpan.Zero, "room again once a streak ended");
    }
}
