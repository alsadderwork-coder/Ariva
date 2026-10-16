using Ariva.Core.Alerting;
using Ariva.Infra.Alerting;
using FluentAssertions;
using Row = Ariva.Infra.Staffing.StaffingReadBack.StaffingRow;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-181b2 (F16, CWE-501): the value an open desks below recommendation rule judges. Per lane minute the job computed, the
/// recommendation of the current interval (interval 0) minus the desks open now by desk state, at least 0; no value when either
/// is unknown, the open count is degraded, the row is not the minute's own interval 0, fails the staffing API's read-back
/// check, or has a gap. A minute without a value neither counts toward nor breaks a run.
/// </summary>
public sealed class StaffingAlertSeriesTests
{
    private static readonly DateTime Minute = new(2026, 10, 15, 10, 9, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(6, 4, false, 2.0)]
    [InlineData(4, 6, false, 0.0)]
    [InlineData(0, 0, false, 0.0)]
    [InlineData(7, 0, false, 7.0)]
    [InlineData(1000, 0, false, 1000.0)]
    [InlineData(4, 4, false, 0.0)]
    public void DesksBelow_Should_BeTheShortfallAtLeastZero_When_BothCountsAreKnown(int recommended, int openNow, bool degraded, double expected) =>
        StaffingAlertSeries.DesksBelow(recommended, openNow, degraded).Should().Be(expected);

    [Theory]
    [InlineData(null, 4, false)]
    [InlineData(6, null, false)]
    [InlineData(null, null, false)]
    [InlineData(6, 4, true)]
    [InlineData(4, 6, true)]
    public void DesksBelow_Should_HaveNoValue_When_ACountIsUnknownOrTheOpenCountIsDegraded(int? recommended, int? openNow, bool degraded) =>
        StaffingAlertSeries.DesksBelow(recommended, openNow, degraded).Should().BeNull();

    /// <summary>
    /// Interval 0 of 10:09 as the engine writes it: 6 desks recommended for the wait of 6 in service, 4 open now (measured), a
    /// queue of 40; the interval runs 6 minutes to 10:15.
    /// </summary>
    private static Row Interval0(int? recommended = 6, int? openNow = 4) => new()
    {
        ZoneKey = "XYZ/A-VIS",
        MinuteUtc = Minute,
        IntervalIndex = 0,
        Lane = "VIS",
        StartUtc = Minute,
        Minutes = 6,
        Arrivals = 30,
        Recommended = recommended,
        Bound = recommended is null or 0 ? "None" : "Wait",
        AtLimit = recommended == 6,
        Shortfall = null,
        WaitRecommended = recommended is null ? null : 8,
        WaitOpen = openNow is null || recommended is null ? null : 14,
        NoServiceOpen = false,
        Gap = recommended is null ? "FlightWithoutPassengers" : "None",
        LaneGap = "None",
        QueueNow = 40,
        OpenNow = openNow,
        OpenNowDegraded = false,
        Desks = 6,
        TargetMinutes = 10,
        BusyCap = 0.85,
        ThroughputPerDesk = 0.5,
        ThroughputSource = "Measured"
    };

    [Fact]
    public void StaffingMinute_Should_JudgeTheShortfall_When_TheRowIsThePlausibleInterval0OfItsMinute()
    {
        var minute = AlertInputs.StaffingMinute(Interval0());

        minute.Should().Be(new AlertMinute(Minute, 2));
        minute.MinuteUtc.Kind.Should().Be(DateTimeKind.Utc);
        AlertInputs.StaffingMinute(Interval0(recommended: 3)).Value.Should().Be(0, "more desks open than recommended is no shortfall");
    }

    // The rows without a value, by name (the row type is internal to Ariva.Infra, so the theory takes the name).
    private static readonly Dictionary<string, Func<Row>> NoValueRows = new(StringComparer.Ordinal)
    {
        ["a later interval"] = () => With(r => r.IntervalIndex = 1),
        ["interval 0 starting after its minute"] = () => With(r => r.StartUtc = Minute.AddMinutes(6)),
        ["a gap in the interval"] = () => Interval0(recommended: null),
        ["the open count unknown"] = () => Interval0(openNow: null),
        ["the open count degraded"] = () => With(r => r.OpenNowDegraded = true),
        ["a recommendation above the lane's desks (read-back check)"] = () => With(r =>
        {
            r.Recommended = 7;
            r.AtLimit = false;
        }),
        ["a lane code Ariva never writes (read-back check)"] = () => With(r => r.Lane = "vis"),
        ["a gap that is not a name Ariva writes (read-back check)"] = () => With(r => r.Gap = "<script>"),
        ["a recommendation beside a gap (read-back check)"] = () => With(r => r.Gap = "BacklogUnknown"),
        ["a minute that is not whole (read-back check)"] = () => With(r =>
        {
            r.MinuteUtc = Minute.AddSeconds(30);
            r.StartUtc = Minute.AddSeconds(30);
        }),
        ["a busy cap that is not a number (read-back check)"] = () => With(r => r.BusyCap = double.NaN),
        ["a lane-wide gap beside a recommendation (read-back check)"] = () => With(r => r.LaneGap = "NoThroughput")
    };

    public static TheoryData<string> NoValue() => [.. NoValueRows.Keys];

    [Theory]
    [MemberData(nameof(NoValue))]
    public void StaffingMinute_Should_HaveNoValue_When_TheRowIsNotAPlausibleInterval0WithoutAGap(string why)
    {
        var row = NoValueRows[why]();

        var minute = AlertInputs.StaffingMinute(row);

        minute.Value.Should().BeNull(why);
        minute.MinuteUtc.Should().Be(row.MinuteUtc, "the minute is still taken, without a value, so it neither counts toward nor breaks a run");
    }

    [Fact]
    public void StaffingMinute_Should_SkipMinutesWithoutAValue_When_TheEvaluatorFolds()
    {
        var rule = new Core.Domain.Entities.AlertRuleValues("Desks short", ["A-VIS"], Core.Domain.Enums.AlertMetric.DesksBelowPlan,
            Core.Domain.Enums.AlertComparator.GreaterOrEqual, 2, null, null, 3, 2, Core.Domain.Enums.AlertSeverity.Warning, Core.RoleCodes.BorderShiftSupervisor,
            null, null, null, false, true);
        var minutes = new[]
        {
            AlertInputs.StaffingMinute(At(0, Interval0())), AlertInputs.StaffingMinute(At(1, With(r => r.OpenNowDegraded = true))),
            AlertInputs.StaffingMinute(At(2, Interval0())), AlertInputs.StaffingMinute(At(3, Interval0(recommended: null))),
            AlertInputs.StaffingMinute(At(4, Interval0(recommended: 6, openNow: 3)))
        };

        var (_, transitions) = AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes);

        transitions.Should().ContainSingle().Which.Should().Match<AlertTransition>(t => t.Kind == AlertTransitionKind.Raised && t.MinuteUtc == Minute.AddMinutes(4) &&
                                                                                       t.Value == 3, "three valued minutes in a row at 2 or more, the skipped ones between");
    }

    private static Row At(int minutes, Row row)
    {
        row.MinuteUtc = Minute.AddMinutes(minutes);
        row.StartUtc = row.MinuteUtc;
        row.Minutes = Math.Max(1, 6 - minutes);
        return row;
    }

    private static Row With(Action<Row> change)
    {
        var row = Interval0();
        change(row);
        return row;
    }
}
