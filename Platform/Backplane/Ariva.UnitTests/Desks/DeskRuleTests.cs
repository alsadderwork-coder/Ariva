using Ariva.Core.Desks;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-033: the desk state rule of formula F10 at a moment, with its documented cases (T1 = 3, T2 = 10 minutes), the
/// precedence of the signals, staleness per source and the fallback when a source goes silent.
/// </summary>
public sealed class DeskRuleTests
{
    private static readonly DateTime T = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DeskStateSettings Settings = new() { PauseAfter = TimeSpan.FromMinutes(3), CloseAfter = TimeSpan.FromMinutes(10) };
    private static readonly DeskProfile Aman = new("D01", "VIS", HasTransactions: true, HasSession: true, HasStaffZone: true, HasServiceZone: true);
    private static readonly DeskProfile SensorsOnly = new("D02", "VIS", HasTransactions: false, HasSession: false, HasStaffZone: true, HasServiceZone: true);
    private static readonly DeskProfile NoStaffZone = new("D03", "VIS", HasTransactions: true, HasSession: true, HasStaffZone: false, HasServiceZone: false);

    // Every configured source heard just now (so none is stale), logged in 30 minutes ago.
    private static DeskMemory LoggedIn(DeskProfile desk, int? staff = 1, int? service = 0, DateTime? staffEmptySince = null, DateTime? lastTransaction = null) => new()
    {
        TransactionsHeard = desk.HasTransactions ? T : null,
        SessionHeard = desk.HasSession ? T : null,
        StaffHeard = desk.HasStaffZone ? T : null,
        ServiceHeard = desk.HasServiceZone ? T : null,
        Session = desk.HasSession ? DeskSessionSignal.Opened : null,
        SessionSince = desk.HasSession ? T.AddMinutes(-30) : null,
        SessionSignalAt = desk.HasSession ? T.AddMinutes(-30) : null,
        StaffCount = desk.HasStaffZone ? staff : null,
        StaffEmptySince = staff == 0 ? staffEmptySince : null,
        ServiceCount = desk.HasServiceZone ? service : null,
        LastTransaction = lastTransaction
    };

    private static DeskStatus At(DeskProfile desk, DeskMemory memory) => DeskRule.Evaluate(desk, memory, T, Settings).Status;

    [Fact]
    public void Rule_Should_BeIdle_When_StaffArePresentAndTheLastTransactionEndedFourMinutesAgo()
    {
        At(Aman, LoggedIn(Aman, staff: 1, lastTransaction: T.AddMinutes(-4))).Should().Be(DeskStatus.Idle, "staff presence is activity");
    }

    [Fact]
    public void Rule_Should_BePaused_When_TheStaffZoneIsEmptyForFourMinutesWithoutATransaction()
    {
        At(Aman, LoggedIn(Aman, staff: 0, staffEmptySince: T.AddMinutes(-4))).Should().Be(DeskStatus.Paused);
    }

    [Fact]
    public void Rule_Should_BeClosed_When_TheStaffZoneIsEmptyForElevenMinutes()
    {
        At(Aman, LoggedIn(Aman, staff: 0, staffEmptySince: T.AddMinutes(-11))).Should().Be(DeskStatus.Closed);
    }

    [Fact]
    public void Rule_Should_BeClosed_When_ALogoutIsReceivedWhateverTheSensorsShow()
    {
        var memory = LoggedIn(Aman, staff: 2, service: 1) with { Session = DeskSessionSignal.Closed, TransactionSince = T.AddSeconds(-20) };

        At(Aman, memory).Should().Be(DeskStatus.Closed);
    }

    [Fact]
    public void Rule_Should_BeServing_When_ATransactionStartedAndHasNotEnded()
    {
        At(Aman, LoggedIn(Aman, staff: 0, staffEmptySince: T.AddMinutes(-11)) with { TransactionSince = T.AddMinutes(-1) })
            .Should().Be(DeskStatus.Serving, "rank 1 outranks an empty staff zone");
    }

    [Fact]
    public void Rule_Should_BeUnknown_When_EverySourceIsSilentBeyondTStale()
    {
        var silent = LoggedIn(Aman) with
        {
            TransactionsHeard = T.AddMinutes(-3), SessionHeard = T.AddMinutes(-3), StaffHeard = T.AddMinutes(-2), ServiceHeard = T.AddMinutes(-5)
        };

        var e = DeskRule.Evaluate(Aman, silent, T, Settings);
        e.Status.Should().Be(DeskStatus.Unknown);
        e.Open.Should().BeFalse("Unknown is excluded from throughput");
        DeskRule.Evaluate(Aman, DeskMemory.Empty, T, Settings).Status.Should().Be(DeskStatus.Unknown, "nothing heard yet");
    }

    [Fact]
    public void Rule_Should_FallBackToTheSensors_When_TheLoginFeedIsStale()
    {
        // F10 with the AMAN feed down (overview failure modes): the staff zone decides, and the result is flagged.
        var memory = LoggedIn(Aman, staff: 1, service: 1) with { Session = DeskSessionSignal.Closed, SessionHeard = T.AddMinutes(-5), TransactionsHeard = T.AddMinutes(-5) };

        var e = DeskRule.Evaluate(Aman, memory, T, Settings);
        e.Status.Should().Be(DeskStatus.Serving, "staff present and a passenger at the desk, no live login source");
        e.SensorDerived.Should().BeTrue();
        e.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Rule_Should_BeServingWeakly_When_OnlySensorsSeeStaffAndAPassenger()
    {
        var e = DeskRule.Evaluate(SensorsOnly, LoggedIn(SensorsOnly, staff: 1, service: 1), T, Settings);

        e.Status.Should().Be(DeskStatus.Serving);
        e.SensorDerived.Should().BeTrue();
        e.Degraded.Should().BeFalse("every configured source is live");
        At(SensorsOnly, LoggedIn(SensorsOnly, staff: 1, service: 0)).Should().Be(DeskStatus.Idle);
        At(SensorsOnly, LoggedIn(SensorsOnly, staff: 0, service: 1, staffEmptySince: T.AddMinutes(-1))).Should().Be(DeskStatus.Closed, "a passenger walking up proves nothing");
    }

    [Fact]
    public void Rule_Should_RecordPresenceWithoutProcessing_When_StaffArePresentButNotLoggedIn()
    {
        var memory = LoggedIn(Aman, staff: 1, service: 1) with { Session = null, SessionSince = null };

        var e = DeskRule.Evaluate(Aman, memory, T, Settings);
        e.Status.Should().Be(DeskStatus.Closed);
        e.PresentNotProcessing.Should().BeTrue();
    }

    [Fact]
    public void Rule_Should_GiveRows4And5TheirGrace_When_ASensorDeskLosesItsStaffReading()
    {
        DeskMemory Left(int minutesAgo) => LoggedIn(SensorsOnly, staff: 0, service: 1, staffEmptySince: T.AddMinutes(-minutesAgo)) with { StaffLeft = true };

        At(SensorsOnly, Left(0)).Should().Be(DeskStatus.Idle, "a dropout of a few seconds does not close the desk");
        At(SensorsOnly, Left(4)).Should().Be(DeskStatus.Paused);
        At(SensorsOnly, Left(11)).Should().Be(DeskStatus.Closed);
    }

    [Fact]
    public void Rule_Should_FollowTheTransactions_When_ADeskHasNoOtherSource()
    {
        var txOnly = new DeskProfile("T01", "CHK", HasTransactions: true, HasSession: false, HasStaffZone: false, HasServiceZone: false);
        DeskMemory After(int minutes) => new() { TransactionsHeard = T, LastTransaction = T.AddMinutes(-minutes) };

        At(txOnly, After(1)).Should().Be(DeskStatus.Idle);
        At(txOnly, After(4)).Should().Be(DeskStatus.Paused);
        At(txOnly, After(11)).Should().Be(DeskStatus.Closed);
        At(txOnly, After(11) with { TransactionSince = T.AddSeconds(-30) }).Should().Be(DeskStatus.Serving);
    }

    [Fact]
    public void Rule_Should_RecordPresence_When_StaffRemainAfterALogout()
    {
        var e = DeskRule.Evaluate(Aman, LoggedIn(Aman, staff: 1) with { Session = DeskSessionSignal.Closed }, T, Settings);

        e.Status.Should().Be(DeskStatus.Closed);
        e.PresentNotProcessing.Should().BeTrue();
    }

    [Fact]
    public void Rule_Should_BePaused_When_TheLoginSourceReportsABreak()
    {
        At(Aman, LoggedIn(Aman, staff: 1) with { Session = DeskSessionSignal.Paused }).Should().Be(DeskStatus.Paused);
        At(Aman, LoggedIn(Aman, staff: 1) with { Session = DeskSessionSignal.Paused, TransactionSince = T.AddSeconds(-5) })
            .Should().Be(DeskStatus.Serving, "a transaction in progress is exact");
    }

    [Fact]
    public void Rule_Should_UseTransactionsOnly_When_NoStaffZoneSensorExists()
    {
        At(NoStaffZone, LoggedIn(NoStaffZone, lastTransaction: T.AddMinutes(-4))).Should().Be(DeskStatus.Paused);
        At(NoStaffZone, LoggedIn(NoStaffZone, lastTransaction: T.AddMinutes(-2))).Should().Be(DeskStatus.Idle);
        At(NoStaffZone, LoggedIn(NoStaffZone) with { SessionSince = T.AddMinutes(-1) }).Should().Be(DeskStatus.Idle, "a fresh login is not Paused at once");
        At(NoStaffZone, LoggedIn(NoStaffZone) with { TransactionsHeard = T.AddMinutes(-5) })
            .Should().Be(DeskStatus.Idle, "without a live activity source, a login is Idle");
    }

    [Fact]
    public void Rule_Should_NotCloseOrPause_When_TransactionsContinueWithAnEmptyStaffZone()
    {
        At(Aman, LoggedIn(Aman, staff: 0, staffEmptySince: T.AddMinutes(-11), lastTransaction: T.AddMinutes(-1))).Should().Be(DeskStatus.Idle);
    }

    [Fact]
    public void Rule_Should_IgnoreAnOpenTransaction_When_ItsEndWasLost()
    {
        At(Aman, LoggedIn(Aman, staff: 1) with { TransactionSince = T.AddMinutes(-31) }).Should().Be(DeskStatus.Idle);
    }

    [Fact]
    public void NextChange_Should_GiveTheEarliestThreshold_When_TimePasses()
    {
        var memory = LoggedIn(Aman, staff: 0, staffEmptySince: T.AddMinutes(-1));

        DeskRule.NextChange(Aman, memory, T, Settings).Should().Be(T.AddMinutes(2), "T1 from the staff zone emptying at 17:59 comes before T_stale");
        DeskRule.NextChange(Aman, memory, T.AddMinutes(2), Settings).Should().Be(T.AddMinutes(9), "then T2 from the same emptying");
        DeskRule.NextChange(Aman, DeskMemory.Empty, T, Settings).Should().BeNull();
    }

    // ARV-069a: NextChange was in Stryker's safe mode; each threshold it considers is tested on its own here, so dropping
    // one is noticed. Every memory holds a single instant at T, and the answer is the first threshold after afterUtc.
    public static TheoryData<string, DeskMemory, int, int?, int> Thresholds => new()
    {
        // case, memory, minutes after T, sensor-only T1 in minutes (null: none), expected minutes after T
        { "staff zone empty, sensor-only T1", new DeskMemory { StaffEmptySince = T }, 0, 1, 1 },
        { "last transaction, sensor-only T1", new DeskMemory { LastTransaction = T }, 0, 1, 1 },
        { "last transaction, T1", new DeskMemory { LastTransaction = T }, 0, null, 3 },
        { "last transaction, T2 once T1 passed", new DeskMemory { LastTransaction = T }, 3, null, 10 },
        { "logged in, T1", new DeskMemory { SessionSince = T }, 0, null, 3 },
        { "logged in, T2 once T1 passed", new DeskMemory { SessionSince = T }, 3, null, 10 },
        { "transaction open, its longest duration", new DeskMemory { TransactionSince = T }, 0, null, 30 },
        { "staff zone empty, T2 once the sensor-only T1 passed", new DeskMemory { StaffEmptySince = T }, 1, 1, 3 }
    };

    [Theory]
    [MemberData(nameof(Thresholds))]
    public void NextChange_Should_GiveEachThreshold_When_ItIsTheOnlyOneAhead(string because, DeskMemory memory, int afterMinutes, int? sensorPauseMinutes, int expectedMinutes)
    {
        var settings = Settings with { SensorPauseAfter = sensorPauseMinutes is { } m ? TimeSpan.FromMinutes(m) : null };

        DeskRule.NextChange(Aman, memory, T.AddMinutes(afterMinutes), settings).Should().Be(T.AddMinutes(expectedMinutes), because);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(5, 4)]
    [InlineData(3, 600)]
    public void Settings_Should_BeValidated_When_TheThresholdsAreOutOfOrder(int t1Minutes, int t2Minutes)
    {
        new DeskStateSettings { PauseAfter = TimeSpan.FromMinutes(t1Minutes), CloseAfter = TimeSpan.FromMinutes(t2Minutes) }.Problems().Should().NotBeEmpty();
    }
}
