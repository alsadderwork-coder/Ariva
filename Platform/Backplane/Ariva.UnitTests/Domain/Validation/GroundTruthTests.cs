using System.Reflection;
using Ariva.Core;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Validation;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104b: tracer runs and desk observations of a validation campaign. A tracer code is a campaign label of a fixed pattern,
/// never a name; a tracer batch carries the device's clock reading, its offset against the server is measured (refused beyond
/// 5 minutes) and every run is corrected by it; runs and observed minutes are refused outside the planned days, in the future
/// beyond the clock tolerance, after the version's retirement, outside the scope, from the campaign's creator or starter and
/// while it is not running; desks in scope are staffed border desks only; a desk state is corrected by a new revision with a
/// reason; the fingerprint recognises a resent batch whatever the device's clock said; nothing names a person. After the first
/// security review: device times far from the device's clock are refused before the correction (no overflow), an observer
/// records at most 500 runs per campaign, a duplicate is an exact code and join pair, and desk states never reach an account
/// that holds an airport role without BorderDesks.View (<see cref="BorderDeskAccess"/>).
/// </summary>
public sealed class GroundTruthTests
{
    #region Fixtures

    // 2026-10-08 10:00 in Dubai (UTC+4); the planned days are 2026-10-07 and 2026-10-08 local.
    private static readonly DateTime Now = new(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly Guid Manager = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
    private static readonly Guid Observer = Guid.Parse("0199a000-0000-7000-8000-0000000000bb");
    private static readonly Guid OtherObserver = Guid.Parse("0199a000-0000-7000-8000-0000000000cc");
    private const string Key = "tablet-07:batch-0001";

    private sealed record Site(ZoneProfile Version, Zone SnakeA, Zone SnakeB, Line EntryA, Desk D01, Desk D02, Desk EGate, Desk Counter, Desk Retired, Desk Elsewhere);

    private static IReadOnlyList<FloorPoint> Rect(double x, double y, double w, double h) => [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    /// <summary>
    /// A published version with two snakes (A with an entry and an exit, B with an entry and an exit), an immigration checkpoint
    /// with two staffed desks, an e-gate and a desk out of service, a check-in counter, and a staffed desk of another site.
    /// </summary>
    private static Site Planted()
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Dubai").AddTerminal("T1", "T1", "DMO").AddLevel("L0", "Arrivals", 0, 100, 60);
        level.Id = Guid.CreateVersion7();
        var profile = new ZoneProfile("DMO", "Arrivals");
        var snakeA = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));
        var entryA = profile.AddLine("Entry A", LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), snakeA);
        profile.AddLine("Exit A", LineRole.Exit, level, new FloorPoint(30, 22), new FloorPoint(34, 22), snakeA);
        var snakeB = profile.AddZone("Snake B", ZoneKind.Queue, level, Rect(50, 10, 24, 12));
        profile.AddLine("Entry B", LineRole.Entry, level, new FloorPoint(50, 12), new FloorPoint(50, 16), snakeB);
        profile.AddLine("Exit B", LineRole.Exit, level, new FloorPoint(70, 22), new FloorPoint(74, 22), snakeB);
        profile.Id = Guid.CreateVersion7();
        foreach (var zone in profile.Zones)
            zone.Id = Guid.CreateVersion7();
        foreach (var line in profile.Lines)
            line.Id = Guid.CreateVersion7();
        profile.Publish(7, new Dictionary<Guid, Level> { [level.Id!.Value] = level }, "admin", Now.AddDays(-10)).Should().BeEmpty();

        var immigration = level.AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration);
        var d01 = immigration.AddDesk("D01", null, DeskKind.Desk, ["CIT"]);
        var d02 = immigration.AddDesk("D02", null, DeskKind.Desk, ["VIS"]);
        var eGate = immigration.AddDesk("EG01", null, DeskKind.EGate, ["EG"]);
        var retired = immigration.AddDesk("D03", null, DeskKind.Desk, ["CIT"]);
        retired.SetInService(false);
        var counter = level.AddCheckpoint("CHK", "Check-in", CheckpointKind.CheckIn).AddDesk("C01", null, DeskKind.Counter, []);
        var elsewhere = new Airport("OTH", null, "Other", "Asia/Dubai").AddTerminal("T1", "T1", "OTH").AddLevel("L0", "Arrivals", 0, 100, 60)
            .AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration).AddDesk("D01", null, DeskKind.Desk, ["CIT"]);
        foreach (var desk in new[] { d01, d02, eGate, retired, counter, elsewhere })
            desk.Id = Guid.CreateVersion7();
        return new Site(profile, snakeA, snakeB, entryA, d01, d02, eGate, counter, retired, elsewhere);
    }

    private static Guid IdOf(Zone zone) => zone.Id!.Value;

    private static Guid IdOf(Desk desk) => desk.Id!.Value;

    private static ValidationCampaign Plan(Site s, params Desk[] desks)
    {
        var campaign = new ValidationCampaign("Pilot week 1", s.Version, [IdOf(s.SnakeA)], [s.EntryA.Id!.Value], [Today, Today.AddDays(-1)], Today, null, null,
            Manager, Now, desks.Length == 0 ? [s.D01, s.D02] : desks);
        campaign.Id = Guid.CreateVersion7();
        return campaign;
    }

    private static ValidationCampaign Running(Site s)
    {
        var campaign = Plan(s);
        campaign.Start(Manager, Now, ZoneProfileStatus.Published);
        return campaign;
    }

    private static DateTime Utc(int day, int hour, int minute, int second = 0, int millisecond = 0) =>
        new(2026, 10, day, hour, minute, second, millisecond, DateTimeKind.Utc);

    private static TracerRunInput Run(Site s, DateTime joined, DateTime exited, string code = "T-07", bool abandoned = false, Zone zone = null) =>
        new(IdOf(zone ?? s.SnakeA), code, joined, exited, abandoned);

    private static DeskMinutesInput Minutes(Desk desk, params (int Minute, ObservedDeskState State)[] observed)
    {
        var states = new ObservedDeskState?[DeskObservationBatch.MinutesPerBin];
        foreach (var (minute, state) in observed)
            states[minute] = state;
        return new DeskMinutesInput(IdOf(desk), states);
    }

    #endregion

    #region Tracer codes

    [Theory]
    [InlineData("T-07", true)]
    [InlineData("T-00", true)]
    [InlineData("T-123", true)]
    [InlineData("T-7", false)]
    [InlineData("T-1234", false)]
    [InlineData("t-07", false)]
    [InlineData("T07", false)]
    [InlineData("T-07\n", false)]
    [InlineData(" T-07", false)]
    [InlineData("T-٠٧", false)]
    [InlineData("T-０７", false)]
    [InlineData("Ahmad", false)]
    [InlineData("T-07 Ahmad", false)]
    [InlineData("' OR '1'='1", false)]
    [InlineData("<b>T-07</b>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTracerCode_Should_AcceptOnlyTheCampaignLabel_When_Checked(string code, bool valid) =>
        TracerRun.IsTracerCode(code).Should().Be(valid, "a tracer code is T, a hyphen and 2 or 3 ASCII digits, never a name");

    #endregion

    #region Clock offset

    [Theory]
    [InlineData(42_500, 42_500)]
    [InlineData(-1_250, -1_250)]
    [InlineData(0, 0)]
    [InlineData(300_000, 300_000)]
    [InlineData(-300_000, -300_000)]
    public void MeasureOffset_Should_BeTheDeviceClockMinusTheReceipt_When_WithinFiveMinutes(int deviceAheadMs, int expected) =>
        TracerBatch.MeasureOffset(Now.AddMilliseconds(deviceAheadMs), Now).Should().Be(expected);

    [Theory]
    [InlineData(300_001)]
    [InlineData(-300_001)]
    [InlineData(3_600_000)]
    public void MeasureOffset_Should_RefuseTheBatch_When_TheDeviceIsMoreThanFiveMinutesOff(int deviceAheadMs) =>
        TracerBatch.MeasureOffset(Now.AddMilliseconds(deviceAheadMs), Now).Should().BeNull();

    [Fact]
    public void MeasureOffset_Should_RefuseLocalTimes_When_TheKindIsNotUtc() =>
        TracerBatch.MeasureOffset(DateTime.SpecifyKind(Now, DateTimeKind.Local), Now).Should().BeNull();

    [Fact]
    public void ToMillisecond_Should_CutTheTicksBelowAMillisecond_When_Stored() =>
        TracerBatch.ToMillisecond(Now.AddTicks(12_345_678)).Should().Be(Now.AddMilliseconds(1234));

    [Fact]
    public void RecordTracerRuns_Should_CorrectEveryRunByTheMeasuredOffset_When_TheDeviceRunsAhead()
    {
        // F19's sign: the device reads 06:00:42.500 when the server reads 06:00:00.000, so it is 42.5 s ahead; the tracer joined
        // at the device's 05:10:42.500 (05:10:00 on the server) and left at the device's 05:28:12.500 (05:27:30).
        var s = Planted();
        var campaign = Running(s);
        var run = Run(s, Utc(8, 5, 10, 42, 500), Utc(8, 5, 28, 12, 500));

        var (batch, runs) = campaign.RecordTracerRuns([run], Observer, Now.AddMilliseconds(42_500), Now, Dubai, null, Key);

        (batch.CampaignId, batch.SiteCode, batch.ObserverId, batch.ClockOffsetMs, batch.Runs, batch.IdempotencyKey).Should()
            .Be((campaign.Id!.Value, "DMO", Observer, 42_500, 1, Key));
        (batch.DeviceClockUtc, batch.ReceivedUtc).Should().Be((Now.AddMilliseconds(42_500), Now));
        batch.RequestHash.Should().Be(RequestFingerprint.OfTracerRuns(campaign.Id!.Value, [run]));
        var recorded = runs.Should().ContainSingle().Subject;
        (recorded.BatchId, recorded.ZoneId, recorded.TracerCode, recorded.ObserverId, recorded.Abandoned).Should().Be((batch.Id!.Value, IdOf(s.SnakeA), "T-07", Observer, false));
        (recorded.JoinedRawUtc, recorded.ExitedRawUtc, recorded.ClockOffsetMs).Should().Be((Utc(8, 5, 10, 42, 500), Utc(8, 5, 28, 12, 500), 42_500));
        (recorded.JoinedUtc, recorded.ExitedUtc, recorded.Wait).Should().Be((Utc(8, 5, 10), Utc(8, 5, 27, 30), TimeSpan.FromMinutes(17.5)));
        recorded.RecordedUtc.Should().Be(Now);
    }

    [Fact]
    public void TracerBatchProblem_Should_RefuseTheBatch_When_TheOffsetExceedsTheBound()
    {
        var s = Planted();
        var campaign = Running(s);
        var run = Run(s, Utc(8, 5, 10), Utc(8, 5, 20));

        campaign.TracerBatchProblem([run], Observer, Now.AddMinutes(-6), Now, Dubai, null).Should().Be(ValidationErrors.ClockOffsetTooLarge);
        campaign.TracerBatchProblem([run], Observer, Now.AddMinutes(5), Now, Dubai, null).Should().BeNull("exactly the bound is accepted");
        var record = () => campaign.RecordTracerRuns([run], Observer, Now.AddHours(4), Now, Dubai, null, Key);
        record.Should().Throw<InvalidOperationException>().WithMessage(ValidationErrors.ClockOffsetTooLarge, "a device set to local time instead of UTC");
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T01:00:00.000Z", 2_000, ValidationErrors.RunOutsideCampaign)]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:00:00.001Z", 300_000, ValidationErrors.RunOutsideCampaign)]
    [InlineData("9999-12-31T23:00:00.000Z", "9999-12-31T23:59:59.999Z", -2_000, ValidationErrors.TimeInFuture)]
    [InlineData("9999-12-31T23:59:59.000Z", "9999-12-31T23:59:59.999Z", -300_000, ValidationErrors.TimeInFuture)]
    [InlineData("2026-10-08T05:10:00.000Z", "9999-12-31T23:59:59.999Z", -2_000, ValidationErrors.TimeInFuture)]
    [InlineData("0001-01-01T00:00:00.000Z", "2026-10-08T05:20:00.000Z", 2_000, ValidationErrors.RunOutsideCampaign)]
    public void TracerBatchProblem_Should_RefuseWithoutOverflow_When_ARunsDeviceTimesAreFarFromTheDevicesClock(string joined, string exited, int deviceAheadMs,
        string expected)
    {
        // First security review of ARV-104b (CWE-501): well-formed but absurd times used to overflow the correction (500); they
        // are refused before it, with the rule the corrected time would break (no value repeated).
        var s = Planted();
        var campaign = Running(s);
        var run = Run(s, ValidationRules.Instant(joined).GetValueOrDefault(), ValidationRules.Instant(exited).GetValueOrDefault());

        var problem = () => campaign.TracerBatchProblem([run], Observer, Now.AddMilliseconds(deviceAheadMs), Now, Dubai, null);

        problem.Should().NotThrow().Which.Should().Be(expected);
        var record = () => campaign.RecordTracerRuns([run], Observer, Now.AddMilliseconds(deviceAheadMs), Now, Dubai, null, Key);
        record.Should().Throw<InvalidOperationException>().WithMessage(expected);
    }

    [Theory]
    [InlineData(60_000, true)]
    [InlineData(60_001, false)]
    [InlineData(0, true)]
    [InlineData(-2_764_800_000, true)]
    [InlineData(-2_764_800_001, false)]
    public void IsNearDeviceClock_Should_AllowAMinuteAheadAnd32DaysBehind_When_Checked(long offsetMs, bool near) =>
        TracerBatch.IsNearDeviceClock(Now.AddMilliseconds(offsetMs), Now).Should().Be(near);

    [Fact]
    public void IsNearDeviceClock_Should_NeverOverflow_When_EitherValueIsAtTheEdgeOfTheRange()
    {
        var edges = new[] { DateTime.MinValue, DateTime.MaxValue, Now };

        foreach (var time in edges)
        {
            foreach (var clock in edges)
            {
                var check = () => TracerBatch.IsNearDeviceClock(time, clock);
                check.Should().NotThrow().Which.Should().Be(time == clock, $"{time:O} against {clock:O}");
            }
        }
    }

    [Fact]
    public void TracerBatchProblem_Should_AcceptTheWindowsEdges_When_TheOtherRulesHold()
    {
        // Device 2 s ahead: an exit exactly a minute after its clock reading passes the window (and the tolerance after
        // the correction); a join 32 days before it passes the window and is refused as a day outside the campaign.
        var s = Planted();
        var campaign = Running(s);
        var clock = Now.AddSeconds(2);

        campaign.TracerBatchProblem([Run(s, clock.AddMinutes(-10), clock.AddMinutes(1))], Observer, clock, Now, Dubai, null).Should().BeNull();
        campaign.TracerBatchProblem([Run(s, clock.AddDays(-32), clock.AddDays(-32).AddMinutes(10))], Observer, clock, Now, Dubai, null)
            .Should().Be(ValidationErrors.RunOutsideCampaign);
        campaign.TracerBatchProblem([Run(s, clock.AddMinutes(-10), clock.AddMinutes(1).AddMilliseconds(1))], Observer, clock, Now, Dubai, null)
            .Should().Be(ValidationErrors.TimeInFuture);
        campaign.TracerBatchProblem([Run(s, clock.AddDays(-32).AddMilliseconds(-1), clock.AddDays(-32).AddMinutes(10))], Observer, clock, Now, Dubai, null)
            .Should().Be(ValidationErrors.RunOutsideCampaign);
    }

    #endregion

    #region Runs per observer

    [Theory]
    [InlineData(0, 20, true)]
    [InlineData(480, 20, true)]
    [InlineData(499, 1, true)]
    [InlineData(500, 0, true)]
    [InlineData(500, 1, false)]
    [InlineData(481, 20, false)]
    [InlineData(int.MaxValue, 1, false)]
    [InlineData(-1, 1, false)]
    [InlineData(0, -1, false)]
    public void IsWithinObserverCap_Should_KeepAnObserverToFiveHundredRuns_When_ABatchArrives(int recorded, int adding, bool within) =>
        TracerRun.IsWithinObserverCap(recorded, adding).Should().Be(within);

    [Fact]
    public void SameRunAs_Should_MatchOnlyTheExactCodeAndJoinPairs_When_Evaluated()
    {
        // The duplicate check of SvcTracerRuns (first security review of ARV-104b): exact pairs, not every run with a code.
        var s = Planted();
        var campaign = Running(s);
        var (_, stored) = campaign.RecordTracerRuns(
            [Run(s, Utc(8, 5, 10), Utc(8, 5, 20)), Run(s, Utc(8, 5, 11), Utc(8, 5, 21), code: "T-08"), Run(s, Utc(8, 5, 12), Utc(8, 5, 22), code: "T-09")],
            Observer, Now, Now, Dubai, null, Key);

        IReadOnlyList<TracerRunInput> Pairs(params (string Code, DateTime Joined)[] pairs) => [.. pairs.Select(p => Run(s, p.Joined, p.Joined.AddMinutes(5), code: p.Code))];
        IEnumerable<string> Matched(IReadOnlyList<TracerRunInput> pairs) =>
            stored.AsQueryable().Where(Ariva.Infra.Services.Validation.SvcTracerRuns.SameRunAs(pairs)).Select(r => r.TracerCode).OrderBy(c => c, StringComparer.Ordinal);

        Matched(Pairs(("T-07", Utc(8, 5, 10)))).Should().Equal("T-07");
        Matched(Pairs(("T-07", Utc(8, 5, 11)), ("T-08", Utc(8, 5, 10)))).Should().BeEmpty("a code and a join time that belong to different runs are no duplicate");
        Matched(Pairs(("T-07", Utc(8, 5, 10)), ("T-09", Utc(8, 5, 12)), ("T-10", Utc(8, 5, 13)))).Should().Equal("T-07", "T-09");
        Matched(Pairs(("T-07", Utc(8, 5, 10, 0, 1)))).Should().BeEmpty("to the millisecond");
    }

    #endregion

    #region Desk access

    public static TheoryData<string[], bool, bool> DeskAccessCases => new()
    {
        { [], false, false },
        { [RoleCodes.ValidationObserver], false, true },
        { [RoleCodes.BorderShiftSupervisor], true, true },
        { [RoleCodes.SystemAdministrator], true, true },
        { [RoleCodes.TerminalDutyManager], false, false },
        { [RoleCodes.HandlerStationManager], false, false },
        { [RoleCodes.TerminalDutyManager, RoleCodes.ValidationObserver], false, false },
        { [RoleCodes.HandlerStationManager, RoleCodes.ValidationObserver], false, false },
        { [RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], true, true },
        { [RoleCodes.SystemAdministrator, RoleCodes.ValidationObserver], true, true },
        { [RoleCodes.TerminalDutyManager, RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], true, true },
        { ["SomeFutureRole", RoleCodes.ValidationObserver], false, false },
        { [RoleCodes.ValidationObserver, RoleCodes.ValidationObserver, null], false, true }
    };

    [Theory]
    [MemberData(nameof(DeskAccessCases))]
    public void BorderDeskAccess_Should_KeepDesksFromAirportRolesWithoutBorderDesks_When_TheRolesAreRead(string[] roles, bool sees, bool observes) =>
        // First security review of ARV-104b (CWE-863, option (b)): an airport role and the observer role together do not reach
        // desk states; a pure observer (the border's own) does; BorderDesks.View always does.
        BorderDeskAccess.Of(roles).Should().Be(new BorderDeskAccess(sees, observes), string.Join(", ", roles));

    [Fact]
    public void IsAirportSide_Should_NameEveryRoleButBorderAdministratorAndObserver_When_EachRoleIsChecked()
    {
        RoleCodes.All.Where(BorderDeskAccess.IsAirportSide).Should().Equal(RoleCodes.TerminalDutyManager, RoleCodes.HandlerStationManager);
        BorderDeskAccess.Of(null).Should().Be(new BorderDeskAccess(false, false));
        foreach (var role in RoleCodes.All.Where(r => !BorderDeskAccess.IsAirportSide(r) && r != RoleCodes.ValidationObserver))
            RolePermissions.ByRole[role].Should().Contain(Ariva.Core.Global.Defaults.Permissions.ViewBorderDesks, $"{role} is on the border side");
    }

    #endregion

    #region Tracer rules

    public static TheoryData<string, string> TracerCases => new()
    {
        // Server times; Dubai is UTC+4, so the planned local days 2026-10-07 and 2026-10-08 are 2026-10-06T20:00Z to 2026-10-08T20:00Z.
        { "zone not in scope", ValidationErrors.ZoneNotInScope },
        { "joined the day before the first planned day", ValidationErrors.RunOutsideCampaign },
        { "joined on the first local day at midnight", null },
        { "exited more than a minute ahead of the server", ValidationErrors.TimeInFuture },
        { "exited within the minute of tolerance", null },
        { "exited after the version was retired", ValidationErrors.TimeAfterRetirement },
        { "exited as the version was retired", null },
        { "exited before joined", ValidationErrors.InvalidRunTimes },
        { "longer than three hours", ValidationErrors.InvalidRunTimes },
        { "exactly three hours", null },
        { "abandoned", null }
    };

    [Theory]
    [MemberData(nameof(TracerCases))]
    public void TracerRunProblem_Should_DecideByScopeDayAndTime_When_ARunArrives(string situation, string expected)
    {
        var s = Planted();
        var campaign = Running(s);
        var zone = IdOf(s.SnakeA);
        var (joined, exited) = (Utc(8, 5, 10), Utc(8, 5, 25));
        DateTime? retired = null;
        switch (situation)
        {
            case "zone not in scope": zone = IdOf(s.SnakeB); break;
            case "joined the day before the first planned day": (joined, exited) = (Utc(6, 19, 50), Utc(6, 20, 5)); break;
            case "joined on the first local day at midnight": (joined, exited) = (Utc(6, 20, 0), Utc(6, 20, 15)); break;
            case "exited more than a minute ahead of the server": exited = Now.AddSeconds(61); break;
            case "exited within the minute of tolerance": exited = Now.AddSeconds(60); break;
            case "exited after the version was retired": retired = Utc(8, 5, 24); break;
            case "exited as the version was retired": retired = Utc(8, 5, 25); break;
            case "exited before joined": exited = joined.AddSeconds(-1); break;
            case "longer than three hours": (joined, exited) = (Utc(8, 2, 0), Utc(8, 5, 0, 1)); break;
            case "exactly three hours": (joined, exited) = (Utc(8, 2, 0), Utc(8, 5, 0)); break;
        }

        campaign.TracerRunProblem(zone, joined, exited, Observer, Now, Dubai, retired).Should().Be(expected, situation);
    }

    [Fact]
    public void TracerRunProblem_Should_RefuseTheCreatorStarterAndAStoppedCampaign_When_ARunArrives()
    {
        // Same separation of duties as manual counts (owner decision 2026-10-08), and only while the campaign runs.
        var s = Planted();
        var startedByAnother = Plan(s);
        startedByAnother.Start(OtherObserver, Now, ZoneProfileStatus.Published);
        var planned = Plan(s);
        var closed = Running(s);
        closed.Close(Manager, Now);
        var zone = IdOf(s.SnakeA);

        startedByAnother.TracerRunProblem(zone, Utc(8, 5, 10), Utc(8, 5, 25), Manager, Now, Dubai, null).Should().Be(ValidationErrors.OwnCampaign);
        startedByAnother.TracerRunProblem(zone, Utc(8, 5, 10), Utc(8, 5, 25), OtherObserver, Now, Dubai, null).Should().Be(ValidationErrors.OwnCampaign);
        startedByAnother.TracerBatchProblem([Run(s, Utc(8, 5, 10), Utc(8, 5, 25))], OtherObserver, Now, Now, Dubai, null).Should().Be(ValidationErrors.OwnCampaign);
        startedByAnother.TracerRunProblem(zone, Utc(8, 5, 10), Utc(8, 5, 25), Observer, Now, Dubai, null).Should().BeNull();
        planned.TracerRunProblem(zone, Utc(8, 5, 10), Utc(8, 5, 25), Observer, Now, Dubai, null).Should().Be(ValidationErrors.NotStarted);
        closed.TracerRunProblem(zone, Utc(8, 5, 10), Utc(8, 5, 25), Observer, Now, Dubai, null).Should().Be(ValidationErrors.Closed);
    }

    [Fact]
    public void TracerBatchProblem_Should_RefuseTheWholeBatch_When_OneRunBreaksARule()
    {
        var s = Planted();
        var campaign = Running(s);
        var good = Run(s, Utc(8, 5, 10), Utc(8, 5, 25));

        campaign.TracerBatchProblem([good, Run(s, Utc(8, 5, 11), Utc(8, 5, 26), zone: s.SnakeB)], Observer, Now, Now, Dubai, null).Should().Be(ValidationErrors.ZoneNotInScope);
        campaign.TracerBatchProblem([good, good], Observer, Now, Now, Dubai, null).Should().Be(ValidationErrors.InvalidRuns, "the same run twice");
        campaign.TracerBatchProblem([good, Run(s, Utc(8, 5, 11), Utc(8, 5, 26), code: "Ahmad")], Observer, Now, Now, Dubai, null).Should().Be(ValidationErrors.InvalidTracerCode);
        campaign.TracerBatchProblem([], Observer, Now, Now, Dubai, null).Should().Be(ValidationErrors.InvalidRuns);
        campaign.TracerBatchProblem([.. Enumerable.Range(0, TracerBatch.MaxRuns + 1).Select(i => Run(s, Utc(8, 4, i), Utc(8, 5, i)))], Observer, Now, Now, Dubai, null)
            .Should().Be(ValidationErrors.InvalidRuns);
        campaign.TracerBatchProblem([.. Enumerable.Range(0, TracerBatch.MaxRuns).Select(i => Run(s, Utc(8, 4, i), Utc(8, 5, i)))], Observer, Now, Now, Dubai, null)
            .Should().BeNull("20 runs is the limit");
        campaign.TracerBatchProblem([good, Run(s, Utc(8, 5, 11), Utc(8, 5, 26), code: "T-08", abandoned: true)], Observer, Now, Now, Dubai, null).Should().BeNull();
    }

    [Fact]
    public void RecordTracerRuns_Should_Refuse_When_TheCampaignWasNeverSaved()
    {
        var s = Planted();
        var campaign = new ValidationCampaign("Pilot", s.Version, [IdOf(s.SnakeA)], [], [Today], Today, null, null, Manager, Now);

        var record = () => campaign.RecordTracerRuns([Run(s, Utc(8, 5, 10), Utc(8, 5, 25))], Observer, Now, Now, Dubai, null, Key);

        record.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region Desk scope

    [Fact]
    public void Constructor_Should_OrderTheDesksByCheckpointThenCode_When_TheyAreAtTwoCheckpoints()
    {
        // ARV-069a: the constructor was in Stryker's safe mode; the checkpoint order needs two checkpoints to show.
        var s = Planted();
        var e01 = s.D01.Checkpoint.Level.AddCheckpoint("EMI", "Emigration", CheckpointKind.Emigration).AddDesk("E01", null, DeskKind.Desk, ["ALL"]);
        e01.Id = Guid.CreateVersion7();

        var campaign = Plan(s, s.D01, e01);

        campaign.Desks.Select(d => (d.CheckpointCode, d.DeskCode)).Should().Equal(("EMI", "E01"), ("IMM", "D01"));
    }

    [Fact]
    public void Constructor_Should_PutStaffedBorderDesksInScope_When_Planned()
    {
        var s = Planted();

        var campaign = Plan(s, s.D02, s.D01);

        campaign.Desks.Select(d => (d.DeskId, d.CheckpointCode, d.DeskCode, d.SiteCode)).Should().Equal(
            (IdOf(s.D01), "IMM", "D01", "DMO"), (IdOf(s.D02), "IMM", "D02", "DMO"));
        campaign.DeskOf(IdOf(s.D01)).Should().NotBeNull();
        campaign.DeskOf(IdOf(s.EGate)).Should().BeNull();
        campaign.AuditSummary().Should().Contain("desks=2");
    }

    [Fact]
    public void Constructor_Should_TakeNoDesks_When_PlannedAsBeforeArv104b()
    {
        var s = Planted();

        var campaign = new ValidationCampaign("Pilot", s.Version, [IdOf(s.SnakeA)], [], [Today], Today, null, null, Manager, Now);

        campaign.Desks.Should().BeEmpty("a campaign planned without desks takes no desk observations");
        ValidationCampaign.DeskScopeProblem("DMO", null, null).Should().BeNull();
        ValidationCampaign.DeskScopeProblem("DMO", [], []).Should().BeNull();
    }

    public static TheoryData<string> InvalidDeskScopes => ["an e-gate", "a check-in counter", "a desk out of service", "a deleted desk", "a desk of another site",
        "a missing desk", "a repeated desk", "an empty id", "too many desks", "a desk of a deleted checkpoint"];

    [Theory]
    [MemberData(nameof(InvalidDeskScopes))]
    public void DeskScopeProblem_Should_RefuseTheDesks_When_OneIsNotAStaffedBorderDeskOfTheSite(string scope)
    {
        var s = Planted();
        var desks = new List<Desk> { s.D01, s.D02, s.EGate, s.Counter, s.Retired, s.Elsewhere };
        IReadOnlyCollection<Guid> ids = scope switch
        {
            "an e-gate" => [IdOf(s.D01), IdOf(s.EGate)],
            "a check-in counter" => [IdOf(s.Counter)],
            "a desk out of service" => [IdOf(s.Retired)],
            "a desk of another site" => [IdOf(s.Elsewhere)],
            "a missing desk" => [Guid.CreateVersion7()],
            "a repeated desk" => [IdOf(s.D01), IdOf(s.D01)],
            "an empty id" => [Guid.Empty],
            "too many desks" => [.. Enumerable.Range(0, ValidationCampaign.MaxDesks + 1).Select(_ => Guid.CreateVersion7())],
            _ => [IdOf(s.D02)]
        };
        if (scope == "a deleted desk")
            s.D02.SoftDelete("admin", Now);
        if (scope == "a desk of a deleted checkpoint")
            s.D02.Checkpoint.SoftDelete("admin", Now);

        ValidationCampaign.DeskScopeProblem("DMO", ids, desks).Should().Be(ValidationErrors.InvalidDesks, scope);
        var plan = () => new ValidationCampaign("Pilot", s.Version, [IdOf(s.SnakeA)], [], [Today], Today, null, null, Manager, Now,
            [.. desks.Where(d => ids.Contains(d.Id!.Value))]);
        if (scope is not ("a missing desk" or "a repeated desk" or "an empty id" or "too many desks"))
            plan.Should().Throw<ArgumentException>(scope);
    }

    [Fact]
    public void IsBorderDesk_Should_AcceptOnlyStaffedImmigrationOrEmigrationDesks_When_Checked()
    {
        var s = Planted();

        ValidationCampaign.IsBorderDesk(s.D01).Should().BeTrue();
        ValidationCampaign.IsBorderDesk(s.EGate).Should().BeFalse("an e-gate has no officer to observe");
        ValidationCampaign.IsBorderDesk(s.Counter).Should().BeFalse("an airport counter is airport data");
        ValidationCampaign.IsBorderDesk(s.Retired).Should().BeFalse("out of service");
        ValidationCampaign.IsBorderDesk(null).Should().BeFalse();
    }

    #endregion

    #region Desk observations

    public static TheoryData<string, string> DeskCases => new()
    {
        { "desk not in scope", ValidationErrors.DeskNotInScope },
        { "minute of the day before the first planned day", ValidationErrors.BinOutsideCampaign },
        { "first minute of the first local day", null },
        { "minute ending more than a minute from now", ValidationErrors.TimeInFuture },
        { "minute ending within the tolerance", null },
        { "minute ended after the retirement", ValidationErrors.TimeAfterRetirement },
        { "minute ended as the version was retired", null },
        { "minute not whole", ValidationErrors.InvalidObservations }
    };

    [Theory]
    [MemberData(nameof(DeskCases))]
    public void DeskObservationProblem_Should_DecideByScopeDayAndTime_When_AMinuteArrives(string situation, string expected)
    {
        var s = Planted();
        var campaign = Running(s);
        var desk = IdOf(s.D01);
        var minute = Utc(8, 5, 40);
        DateTime? retired = null;
        switch (situation)
        {
            case "desk not in scope": desk = IdOf(s.EGate); break;
            case "minute of the day before the first planned day": minute = Utc(6, 19, 59); break;
            case "first minute of the first local day": minute = Utc(6, 20, 0); break;
            case "minute ending more than a minute from now": minute = Utc(8, 6, 1); break;
            case "minute ending within the tolerance": minute = Utc(8, 6, 0); break;
            case "minute ended after the retirement": retired = Utc(8, 5, 40, 59); break;
            case "minute ended as the version was retired": retired = Utc(8, 5, 41); break;
            case "minute not whole": minute = Utc(8, 5, 40, 30); break;
        }

        campaign.DeskObservationProblem(desk, minute, Observer, Now, Dubai, retired).Should().Be(expected, situation);
    }

    [Fact]
    public void ObserveDesks_Should_RecordEveryObservedMinuteOfTheBin_When_TheBatchIsValid()
    {
        var s = Planted();
        var campaign = Running(s);
        DeskMinutesInput[] desks =
        [
            Minutes(s.D01, (0, ObservedDeskState.Serving), (1, ObservedDeskState.Serving), (14, ObservedDeskState.Idle)),
            Minutes(s.D02, (3, ObservedDeskState.Paused))
        ];

        var (batch, observations) = campaign.ObserveDesks(Utc(8, 5, 30), desks, Observer, Now, Dubai, null, Key);

        (batch.CampaignId, batch.ObserverId, batch.BinStartUtc, batch.Observations, batch.IdempotencyKey, batch.ReceivedUtc).Should()
            .Be((campaign.Id!.Value, Observer, Utc(8, 5, 30), 4, Key, Now));
        batch.RequestHash.Should().Be(RequestFingerprint.OfDeskMinutes(campaign.Id!.Value, Utc(8, 5, 30), desks));
        observations.Select(o => (o.DeskId, o.MinuteUtc, o.State, o.Revision, o.BatchId, o.Reason, o.CorrectsId, o.IdempotencyKey)).Should().Equal(
            (IdOf(s.D01), Utc(8, 5, 30), ObservedDeskState.Serving, 1, batch.Id, (string)null, (Guid?)null, (string)null),
            (IdOf(s.D01), Utc(8, 5, 31), ObservedDeskState.Serving, 1, batch.Id, null, null, null),
            (IdOf(s.D01), Utc(8, 5, 44), ObservedDeskState.Idle, 1, batch.Id, null, null, null),
            (IdOf(s.D02), Utc(8, 5, 33), ObservedDeskState.Paused, 1, batch.Id, null, null, null));
    }

    [Fact]
    public void DeskBatchProblem_Should_RefuseTheWholeBatch_When_OneMinuteOrTheShapeIsWrong()
    {
        var s = Planted();
        var campaign = Running(s);
        var good = Minutes(s.D01, (0, ObservedDeskState.Idle));

        campaign.DeskBatchProblem(Utc(8, 5, 31), [good], Observer, Now, Dubai, null).Should().Be(ValidationErrors.InvalidBin);
        campaign.DeskBatchProblem(Utc(8, 5, 30), [good, Minutes(s.EGate, (0, ObservedDeskState.Idle))], Observer, Now, Dubai, null).Should().Be(ValidationErrors.DeskNotInScope);
        campaign.DeskBatchProblem(Utc(8, 5, 30), [good, good], Observer, Now, Dubai, null).Should().Be(ValidationErrors.InvalidObservations, "a desk twice");
        campaign.DeskBatchProblem(Utc(8, 5, 30), [Minutes(s.D01)], Observer, Now, Dubai, null).Should().Be(ValidationErrors.InvalidObservations, "no minute observed");
        campaign.DeskBatchProblem(Utc(8, 5, 30), [new DeskMinutesInput(IdOf(s.D01), [ObservedDeskState.Idle])], Observer, Now, Dubai, null)
            .Should().Be(ValidationErrors.InvalidObservations, "fewer than 15 states");
        campaign.DeskBatchProblem(Utc(8, 5, 45), [Minutes(s.D01, (14, ObservedDeskState.Idle))], Observer, Now, Dubai, null)
            .Should().BeNull("05:59 ended at 06:00");
        campaign.DeskBatchProblem(Utc(8, 5, 30), [good], Manager, Now, Dubai, null).Should().Be(ValidationErrors.OwnCampaign);
    }

    [Fact]
    public void DeskBatchProblem_Should_AcceptAPartBin_When_OnlyEndedMinutesAreObserved()
    {
        // At 06:00 the 06:00 bin has not ended, but its first minute ends at 06:01, within the minute of tolerance: an observer
        // leaving early sends the minutes it saw.
        var s = Planted();
        var campaign = Running(s);

        campaign.DeskBatchProblem(Utc(8, 6, 0), [Minutes(s.D01, (0, ObservedDeskState.Serving))], Observer, Now, Dubai, null).Should().BeNull();
        campaign.DeskBatchProblem(Utc(8, 6, 0), [Minutes(s.D01, (1, ObservedDeskState.Serving))], Observer, Now, Dubai, null).Should().Be(ValidationErrors.TimeInFuture);
    }

    [Fact]
    public void CorrectDeskObservation_Should_AddTheNextRevisionWithAReason_When_TheObserverCorrectsTheirOwnState()
    {
        var s = Planted();
        var campaign = Running(s);
        var (_, observations) = campaign.ObserveDesks(Utc(8, 5, 30), [Minutes(s.D01, (0, ObservedDeskState.Idle))], Observer, Now, Dubai, null, Key);
        var first = observations[0];
        first.Id = Guid.CreateVersion7();

        var corrected = campaign.CorrectDeskObservation(first, Observer, ObservedDeskState.Serving, "  Tapped the wrong row  ", Now.AddMinutes(2), "tablet-07:fix-0001");

        (corrected.DeskId, corrected.MinuteUtc, corrected.ObserverId, corrected.Revision, corrected.State).Should()
            .Be((IdOf(s.D01), Utc(8, 5, 30), Observer, 2, ObservedDeskState.Serving));
        (corrected.Reason, corrected.CorrectsId, corrected.BatchId, corrected.IdempotencyKey).Should().Be(("Tapped the wrong row", first.Id, (Guid?)null, "tablet-07:fix-0001"));
        corrected.IsSameCorrection(campaign.Id!.Value, first.Id!.Value, ObservedDeskState.Serving, "Tapped the wrong row ").Should().BeTrue();
        corrected.IsSameCorrection(campaign.Id!.Value, first.Id!.Value, ObservedDeskState.Idle, "Tapped the wrong row").Should().BeFalse();
        first.State.Should().Be(ObservedDeskState.Idle, "the corrected revision stays");
    }

    [Fact]
    public void DeskCorrectionProblem_Should_Refuse_When_NotOwnClosedOrWithoutAReason()
    {
        var s = Planted();
        var campaign = Running(s);
        var (_, observations) = campaign.ObserveDesks(Utc(8, 5, 30), [Minutes(s.D01, (0, ObservedDeskState.Idle))], Observer, Now, Dubai, null, Key);
        var first = observations[0];
        first.Id = Guid.CreateVersion7();

        campaign.DeskCorrectionProblem(first, OtherObserver).Should().Be(ValidationErrors.NotFound, "another observer's state answers like a missing one");
        campaign.DeskCorrectionProblem(null, Observer).Should().Be(ValidationErrors.NotFound);
        var noReason = () => campaign.CorrectDeskObservation(first, Observer, ObservedDeskState.Serving, " ", Now);
        noReason.Should().Throw<ArgumentException>();
        campaign.Close(Manager, Now);
        campaign.DeskCorrectionProblem(first, Observer).Should().Be(ValidationErrors.Closed);
    }

    [Theory]
    [InlineData("Closed", ObservedDeskState.Closed)]
    [InlineData("Idle", ObservedDeskState.Idle)]
    [InlineData("Serving", ObservedDeskState.Serving)]
    [InlineData("Paused", ObservedDeskState.Paused)]
    [InlineData("Unknown", null)]
    [InlineData("serving", null)]
    [InlineData("1", null)]
    [InlineData("Serving ", null)]
    [InlineData("<script>", null)]
    [InlineData(null, null)]
    public void StateOf_Should_AcceptOnlyTheFourObservableStates_When_Parsed(string text, ObservedDeskState? expected) =>
        DeskObservation.StateOf(text).Should().Be(expected);

    #endregion

    #region Request rules and fingerprints

    [Fact]
    public async Task TracerRuns_Should_NameTheRuleBroken_When_ARequestIsNotValid()
    {
        var zone = Guid.CreateVersion7();
        TracerRunRequest Good(string code = "T-07") => new(zone, code, "2026-10-08T05:10:00.123Z", "2026-10-08T05:25:00Z", false);
        static async Task<string> FirstError(CaptureTracerRunsRequest request) =>
            (await ValidationRules.TracerRuns().ValidateAllAsync(request)).ErrorMessages?.FirstOrDefault();
        const string clock = "2026-10-08T06:00:00Z";

        (await FirstError(new CaptureTracerRunsRequest(clock, [Good()]))).Should().BeNull();
        (await FirstError(new CaptureTracerRunsRequest("2026-10-08T10:00:00+04:00", [Good()]))).Should().Be(ValidationErrors.InvalidDeviceClock);
        (await FirstError(new CaptureTracerRunsRequest("' OR '1'='1", [Good()]))).Should().Be(ValidationErrors.InvalidDeviceClock);
        (await FirstError(new CaptureTracerRunsRequest(clock, null))).Should().Be(ValidationErrors.InvalidRuns);
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good(), Good()]))).Should().Be(ValidationErrors.InvalidRuns, "the same run twice");
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good() with { Abandoned = null }]))).Should().Be(ValidationErrors.InvalidRuns);
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good() with { ZoneId = Guid.Empty }]))).Should().Be(ValidationErrors.ZoneNotInScope);
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good("Fatima")]))).Should().Be(ValidationErrors.InvalidTracerCode);
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good() with { ExitedUtc = "2026-10-08T05:10:00Z" }]))).Should().Be(ValidationErrors.InvalidRunTimes);
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good() with { JoinedUtc = "<script>alert(1)</script>" }]))).Should().Be(ValidationErrors.InvalidRunTimes);
        (await FirstError(new CaptureTracerRunsRequest(clock, [Good() with { JoinedUtc = "2026-10-08T01:10:00Z" }]))).Should().Be(ValidationErrors.InvalidRunTimes, "over three hours");
        ValidationRules.Runs(new CaptureTracerRunsRequest(clock, [Good()])).Should().ContainSingle().Which.JoinedRawUtc.Should().Be(Utc(8, 5, 10, 0, 123));
        ValidationRules.Instant("2026-10-08T05:10:00.1239999Z").Should().Be(Utc(8, 5, 10, 0, 123), "cut to the millisecond");
    }

    [Fact]
    public async Task DeskObservations_Should_NameTheRuleBroken_When_ARequestIsNotValid()
    {
        var desk = Guid.CreateVersion7();
        static string[] Fifteen(string first) => [first, .. Enumerable.Repeat<string>(null, 14)];
        static async Task<string> FirstError(CaptureDeskObservationsRequest request) =>
            (await ValidationRules.DeskObservations().ValidateAllAsync(request)).ErrorMessages?.FirstOrDefault();
        const string bin = "2026-10-08T05:30:00Z";

        (await FirstError(new CaptureDeskObservationsRequest(bin, [new DeskMinutesRequest(desk, Fifteen("Serving"))]))).Should().BeNull();
        (await FirstError(new CaptureDeskObservationsRequest("2026-10-08T05:31:00Z", [new DeskMinutesRequest(desk, Fifteen("Serving"))]))).Should().Be(ValidationErrors.InvalidBin);
        (await FirstError(new CaptureDeskObservationsRequest(bin, [new DeskMinutesRequest(desk, Fifteen("Unknown"))]))).Should().Be(ValidationErrors.InvalidState);
        (await FirstError(new CaptureDeskObservationsRequest(bin, [new DeskMinutesRequest(desk, Fifteen("<img src=x onerror=alert(1)>"))]))).Should().Be(ValidationErrors.InvalidState);
        (await FirstError(new CaptureDeskObservationsRequest(bin, [new DeskMinutesRequest(desk, ["Serving"])]))).Should().Be(ValidationErrors.InvalidObservations);
        (await FirstError(new CaptureDeskObservationsRequest(bin, [new DeskMinutesRequest(desk, Fifteen(null))]))).Should().Be(ValidationErrors.InvalidObservations);
        (await FirstError(new CaptureDeskObservationsRequest(bin, [new DeskMinutesRequest(desk, Fifteen("Idle")), new DeskMinutesRequest(desk, Fifteen("Idle"))])))
            .Should().Be(ValidationErrors.InvalidObservations, "a desk twice");
        (await FirstError(new CaptureDeskObservationsRequest(bin, null))).Should().Be(ValidationErrors.InvalidObservations);
        (await FirstError(new CaptureDeskObservationsRequest(bin, [.. Enumerable.Range(0, DeskObservationBatch.MaxDesks + 1)
            .Select(_ => new DeskMinutesRequest(Guid.CreateVersion7(), Fifteen("Idle")))]))).Should().Be(ValidationErrors.InvalidObservations);
    }

    [Fact]
    public void Fingerprint_Should_IgnoreTheDeviceClock_When_ABatchIsResent()
    {
        var campaign = Guid.CreateVersion7();
        var zone = Guid.CreateVersion7();
        TracerRunInput[] runs = [new(zone, "T-07", Utc(8, 5, 10), Utc(8, 5, 25), false)];

        RequestFingerprint.OfTracerRuns(campaign, runs).Should().Be(RequestFingerprint.OfTracerRuns(campaign, [runs[0] with { }]));
        RequestFingerprint.OfTracerRuns(campaign, runs).Should().MatchRegex("^[0-9a-f]{64}$");
        RequestFingerprint.OfTracerRuns(campaign, [runs[0] with { Abandoned = true }]).Should().NotBe(RequestFingerprint.OfTracerRuns(campaign, runs));
        RequestFingerprint.OfTracerRuns(campaign, [runs[0] with { JoinedRawUtc = Utc(8, 5, 10, 0, 1) }]).Should().NotBe(RequestFingerprint.OfTracerRuns(campaign, runs));
        RequestFingerprint.OfTracerRuns(Guid.CreateVersion7(), runs).Should().NotBe(RequestFingerprint.OfTracerRuns(campaign, runs));
        var desk = new DeskMinutesInput(Guid.CreateVersion7(), [ObservedDeskState.Idle, .. Enumerable.Repeat<ObservedDeskState?>(null, 14)]);
        RequestFingerprint.OfDeskMinutes(campaign, Utc(8, 5, 30), [desk]).Should().NotBe(RequestFingerprint.OfDeskMinutes(campaign, Utc(8, 5, 45), [desk]));
        RequestFingerprint.OfDeskMinutes(campaign, Utc(8, 5, 30), [desk]).Should()
            .NotBe(RequestFingerprint.OfDeskMinutes(campaign, Utc(8, 5, 30), [desk with { States = [ObservedDeskState.Serving, .. desk.States.Skip(1)] }]));
    }

    [Fact]
    public void Errors_Should_MapToTheirStatus_When_TheApiAnswers()
    {
        ValidationErrors.Forbidden.Should().Contain([ValidationErrors.OwnCampaign, ValidationErrors.DesksNeedBorderRole]);
        ValidationErrors.Conflicts.Should().Contain([ValidationErrors.TimeAfterRetirement, ValidationErrors.RunAlreadyRecorded, ValidationErrors.AlreadyObserved]);
        string[] invalid = [ValidationErrors.TimeInFuture, ValidationErrors.RunOutsideCampaign, ValidationErrors.ClockOffsetTooLarge, ValidationErrors.InvalidTracerCode,
            ValidationErrors.IdempotencyKeyRequired, ValidationErrors.InvalidObservations, ValidationErrors.InvalidState, ValidationErrors.DeskNotInScope, ValidationErrors.ZoneNotInScope];
        invalid.Should().NotIntersectWith(ValidationErrors.Conflicts).And.NotIntersectWith(ValidationErrors.Forbidden, "times outside the campaign or in the future are 400");
    }

    #endregion

    #region Data boundary

    [Fact]
    public void Properties_Should_NameNoPersonBeyondAnArivaUserId_When_RunsAndObservationsArePersisted()
    {
        // Data boundary (ARV-104b): tracers are campaign labels, observers Ariva user ids (Guid); no name, user name, contact,
        // staff number or document is persisted.
        var types = new[] { typeof(ValidationCampaignDesk), typeof(TracerBatch), typeof(TracerRun), typeof(DeskObservationBatch), typeof(DeskObservation) };
        var persisted = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.GetSetMethod(nonPublic: true) is not null)
            .Select(property => (Type: t.Name, property.Name, property.PropertyType))).ToList();
        string[] personal = ["UserName", "DisplayName", "FirstName", "LastName", "FullName", "Name", "Email", "Phone", "Document", "Passport", "Nationality", "Birth", "Badge",
            "Staff", "Officer", "Traveller", "Passenger", "Mrz", "Pnr"];

        persisted.Where(p => personal.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase))).Should().BeEmpty("no name of anyone, a tracer is a label");
        persisted.Where(p => p.Name == "ObserverId").Should().OnlyContain(p => p.PropertyType == typeof(Guid), "people are Ariva user ids").And.HaveCount(4);
        persisted.Should().NotContain(p => p.Name == "CreatedBy" || p.Name == "ModifiedBy", "no user names from the audit base class");
        typeof(TracerRun).GetProperty(nameof(TracerRun.TracerCode))!.GetCustomAttribute<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()!.Length.Should().Be(5);
    }

    #endregion
}
