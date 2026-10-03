using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;
using Ariva.Core.Flights;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Flights;

/// <summary>
/// ARV-041: the flight model. Feed data is checked before it touches the model (CWE-501): codes have their shape, times
/// are UTC and within what a feed can mean, names are exact, counts are bounded. Messages apply by their own time, so a
/// leg ends the same whatever order its messages arrive in; the status follows from the milestones; a repeated message
/// changes nothing. Allocations keep unmapped counter codes apart. The stale-feed rule judges silence against flights due.
/// </summary>
public sealed class FlightTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Scheduled = new(2026, 10, 1, 14, 5, 0, DateTimeKind.Utc);

    private static FlightLegData Arrival(string key = "DM214-20261001-A") =>
        new(key, "DM", "214", null, "Arrival", Scheduled, Origin: "BEY", Destination: "DMO", Terminal: "T1", Stand: "B12", Gate: "B12", AircraftType: "A320",
            Seats: 180, PaxEstimate: 162, Status: "Scheduled", Codeshares: ["XR1214"]);

    private static FlightLegData Departure(string key = "XR331-20261001-D") =>
        new(key, "XR", "331", null, "Departure", Scheduled, Origin: "DMO", Destination: "DXB", Terminal: "T1", Seats: 180);

    private static FlightLegValues Values(FlightLegData data)
    {
        var (values, errors) = FlightRules.Check(data, Now);
        errors.Should().BeEmpty();
        return values;
    }

    private static FlightLeg Leg(FlightLegData data, DateTime source) => new("DMO", Values(data), "aidx", source, Now);

    [Fact]
    public void Check_Should_NormaliseAGoodLeg_When_ItHasEveryField()
    {
        var (values, errors) = FlightRules.Check(Arrival() with { Carrier = " dm ", Terminal = "t1", Codeshares = ["xr1214", "XR1214", "QL9"] }, Now);

        errors.Should().BeEmpty();
        values.Should().Match<FlightLegValues>(v => v.Carrier == "DM" && v.Terminal == "T1" && v.Direction == FlightDirection.Arrival && v.Status == FlightStatus.Scheduled);
        values.Codeshares.Should().Equal("XR1214", "QL9");
    }

    public static TheoryData<string, FlightLegData> BadLegs => new()
    {
        { "key with a space", Arrival("DM 214") },
        { "key with a line break", Arrival("DM214\r\nX") },
        { "key too long", Arrival(new string('K', 65)) },
        { "carrier of four", Arrival() with { Carrier = "DMXX" } },
        { "carrier with a symbol", Arrival() with { Carrier = "D'" } },
        { "number of five digits", Arrival() with { Number = "12345" } },
        { "number with letters", Arrival() with { Number = "21A" } },
        { "suffix of two", Arrival() with { Suffix = "AB" } },
        { "direction in lower case", Arrival() with { Direction = "arrival" } },
        { "direction as a number", Arrival() with { Direction = "0" } },
        { "direction joined", Arrival() with { Direction = "Arrival,Departure" } },
        { "status padded", Arrival() with { Status = " Cancelled" } },
        { "status unknown", Arrival() with { Status = "Delayed" } },
        { "no schedule", Arrival() with { ScheduledUtc = null } },
        { "local schedule", Arrival() with { ScheduledUtc = DateTime.SpecifyKind(Scheduled, DateTimeKind.Local) } },
        { "unspecified schedule", Arrival() with { ScheduledUtc = DateTime.SpecifyKind(Scheduled, DateTimeKind.Unspecified) } },
        { "schedule a week ago", Arrival() with { ScheduledUtc = Now.AddDays(-7) } },
        { "schedule in two years", Arrival() with { ScheduledUtc = Now.AddDays(800) } },
        { "estimate days early", Arrival() with { EstimatedUtc = Scheduled.AddDays(-2) } },
        { "actual far late", Arrival() with { ActualUtc = Scheduled.AddDays(4) } },
        { "arrival with off-block", Arrival() with { OffBlockUtc = Scheduled } },
        { "departure with on-block", Departure() with { OnBlockUtc = Scheduled } },
        { "origin of four", Arrival() with { Origin = "OJAI" } },
        { "destination with digits", Arrival() with { Destination = "D1O" } },
        { "stand with a space", Arrival() with { Stand = "B 12" } },
        { "gate with markup", Arrival() with { Gate = "<b>" } },
        { "aircraft of five", Arrival() with { AircraftType = "A320N" } },
        { "negative seats", Arrival() with { Seats = -1 } },
        { "too many passengers", Arrival() with { PaxEstimate = 5000 } },
        { "codeshare without number", Arrival() with { Codeshares = ["XR"] } },
        { "too many codeshares", Arrival() with { Codeshares = [.. Enumerable.Range(1, 21).Select(i => $"XR{i}")] } }
    };

    [Theory]
    [MemberData(nameof(BadLegs))]
    public void Check_Should_RefuseTheLeg_When_AFieldIsNotWhatAFeedCanMean(string why, FlightLegData data)
    {
        var (values, errors) = FlightRules.Check(data, Now);

        values.Should().BeNull(why);
        errors.Should().NotBeEmpty(why);
    }

    public static TheoryData<string, FlightLegData> EdgeLegs
    {
        get
        {
            var min = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            var max = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            var data = new TheoryData<string, FlightLegData>
            {
                { "schedule at the minimum with an estimate", Arrival() with { ScheduledUtc = min, EstimatedUtc = Now } },
                { "schedule and estimate at the maximum", Arrival() with { ScheduledUtc = max, EstimatedUtc = max } }
            };
            foreach (var edge in new[] { min, max })
            {
                data.Add($"estimate {edge:yyyy}", Arrival() with { EstimatedUtc = edge });
                data.Add($"actual {edge:yyyy}", Arrival() with { ActualUtc = edge });
                data.Add($"on-block {edge:yyyy}", Arrival() with { OnBlockUtc = edge });
                data.Add($"off-block {edge:yyyy}", Departure() with { OffBlockUtc = edge });
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EdgeLegs))]
    public void Check_Should_ReturnErrorsNotThrow_When_ATimeIsAtTheEdgeOfTheCalendar(string why, FlightLegData data)
    {
        var check = () => FlightRules.Check(data, Now);

        check.Should().NotThrow(why).Which.Errors.Should().NotBeEmpty(why);
    }

    [Fact]
    public void Times_Should_KeepMicroseconds_When_Checked()
    {
        var precise = Scheduled.AddTicks(7);
        Values(Arrival() with { ScheduledUtc = precise, EstimatedUtc = precise.AddTicks(3) }).Should()
            .Match<FlightLegValues>(v => v.ScheduledUtc == Scheduled && v.EstimatedUtc == Scheduled.AddTicks(10) && v.ScheduledUtc.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public void Leg_Should_EndTheSame_When_ItsMessagesArriveInAnyOrder()
    {
        var t1 = Now.AddMinutes(-30);
        var t2 = Now.AddMinutes(-20);
        var t3 = Now.AddMinutes(-10);
        var first = Arrival() with { EstimatedUtc = Scheduled.AddMinutes(7), Stand = "B12" };
        var second = Arrival() with { EstimatedUtc = Scheduled.AddMinutes(12), Stand = "B14" };
        var third = Arrival() with { Status = "Cancelled" };
        var messages = new[] { (first, t1), (second, t2), (third, t3) };

        string Final(IEnumerable<(FlightLegData Data, DateTime Source)> order)
        {
            var list = order.ToList();
            var leg = Leg(list[0].Data, list[0].Source);
            foreach (var (data, source) in list.Skip(1))
                leg.Apply(Values(data), "aidx", source, Now);
            return $"{leg.EstimatedUtc:O}|{leg.Stand}|{leg.Status}|{leg.Cancelled}";
        }

        var expected = $"{Scheduled.AddMinutes(12):O}|B12|Cancelled|True";
        // The newest schedule is the third message (no stand change in it: Stand B12 from Arrival()); the newest estimate is the second.
        foreach (var order in new[] { new[] { 0, 1, 2 }, [2, 1, 0], [1, 2, 0], [0, 2, 1], [2, 0, 1], [1, 0, 2] })
            Final(order.Select(i => messages[i])).Should().Be(expected, string.Join(",", order));
    }

    [Fact]
    public void Leg_Should_KeepTheNewestMilestoneAndFillAMissingOne_When_AnOlderMessageArrivesLate()
    {
        var leg = Leg(Arrival(), Now.AddMinutes(-60));
        leg.Record(FlightEventType.OnBlock, Scheduled.AddMinutes(20), "aidx", Now.AddMinutes(-5), Now).Should().BeTrue();

        // An older message filling the landing nobody reported yet is taken; an older on-block is not.
        leg.Record(FlightEventType.Landed, Scheduled.AddMinutes(12), "aidx", Now.AddMinutes(-10), Now).Should().BeTrue();
        leg.Record(FlightEventType.OnBlock, Scheduled.AddMinutes(17), "aidx", Now.AddMinutes(-9), Now).Should().BeFalse();

        leg.Should().Match<FlightLeg>(l => l.OnBlockUtc == Scheduled.AddMinutes(20) && l.ActualUtc == Scheduled.AddMinutes(12) && l.Status == FlightStatus.OnBlock);
        leg.Record(FlightEventType.OnBlock, Scheduled.AddMinutes(21), "acris", Now.AddMinutes(-1), Now).Should().BeTrue("a newer correction wins");
        leg.OnBlockUtc.Should().Be(Scheduled.AddMinutes(21));
        leg.Feed.Should().Be("acris");
    }

    [Fact]
    public void Status_Should_FollowTheMilestones_When_TheyAreKnown()
    {
        var arrival = Leg(Arrival() with { Status = null }, Now.AddMinutes(-60));
        arrival.Status.Should().Be(FlightStatus.Scheduled);
        arrival.Record(FlightEventType.Estimated, Scheduled.AddMinutes(5), "aidx", Now.AddMinutes(-50), Now);
        arrival.Status.Should().Be(FlightStatus.Estimated);
        arrival.Record(FlightEventType.Landed, Scheduled.AddMinutes(4), "aidx", Now.AddMinutes(-40), Now);
        arrival.Status.Should().Be(FlightStatus.Landed);

        var departure = Leg(Departure(), Now.AddMinutes(-60));
        foreach (var (type, status) in new[]
                 {
                     (FlightEventType.GateOpen, FlightStatus.GateOpen), (FlightEventType.BoardingStart, FlightStatus.Boarding), (FlightEventType.OffBlock, FlightStatus.OffBlock)
                 })
        {
            departure.Record(type, Scheduled.AddMinutes(-30), "aidx", Now.AddMinutes(-30), Now);
            departure.Status.Should().Be(status);
        }

        departure.Record(FlightEventType.Diverted, Scheduled, "aidx", Now.AddMinutes(-20), Now);
        departure.Status.Should().Be(FlightStatus.Diverted);
        departure.Apply(Values(Departure() with { Status = "Scheduled" }), "aidx", Now.AddMinutes(-10), Now);
        departure.Status.Should().Be(FlightStatus.OffBlock, "a newer message stating another status reinstates it");
        departure.Apply(Values(Departure() with { Status = "Diverted" }), "aidx", Now.AddMinutes(-25), Now);
        departure.Diverted.Should().BeFalse("an older diversion does not undo the newer reinstatement");
    }

    [Fact]
    public void Leg_Should_RefuseAMilestoneOfTheOtherDirectionOrDirectionChange_When_Told()
    {
        var arrival = Leg(Arrival(), Now.AddMinutes(-60));
        var boarding = () => arrival.Record(FlightEventType.BoardingStart, Scheduled, "aidx", Now, Now);
        boarding.Should().Throw<InvalidOperationException>();
        var departure = Leg(Departure(), Now.AddMinutes(-60));
        var onBlock = () => departure.Record(FlightEventType.OnBlock, Scheduled, "aidx", Now, Now);
        onBlock.Should().Throw<InvalidOperationException>();
        var flipped = () => arrival.Apply(Values(Arrival() with { Direction = "Departure" }), "aidx", Now, Now);
        flipped.Should().Throw<InvalidOperationException>();
        var farAway = () => arrival.Record(FlightEventType.Landed, Scheduled.AddDays(5), "aidx", Now, Now);
        farAway.Should().Throw<ArgumentOutOfRangeException>();
        var local = () => arrival.Record(FlightEventType.Landed, Scheduled, "aidx", DateTime.SpecifyKind(Now, DateTimeKind.Local), Now);
        local.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Leg_Should_RaiseFlightChangedOnlyForAChange_When_AMessageRepeatsWhatIsKnown()
    {
        var leg = Leg(Arrival(), Now.AddMinutes(-60));
        leg.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<FlightChanged>()
            .Which.Should().Match<FlightChanged>(e => e.SiteCode == "DMO" && e.FlightKey == "DM214-20261001-A" && e.Status == "Scheduled" && e.GetPartitionKey() == "DMO/DM214-20261001-A");

        leg.Apply(Values(Arrival()), "aidx", Now.AddMinutes(-30), Now).Should().BeFalse("nothing changed");
        leg.Record(FlightEventType.Estimated, Scheduled.AddMinutes(9), "aidx", Now.AddMinutes(-20), Now).Should().BeTrue();
        leg.DomainEvents.Should().HaveCount(2);
        leg.DomainEvents.OfType<FlightChanged>().Last().EstimatedUtc.Should().Be(Scheduled.AddMinutes(9));
    }

    [Fact]
    public void Leg_Should_LetAScheduleFileFillOnlyWhatALiveFeedHasNotReported_When_BothSendTheSchedule()
    {
        var live = Leg(Arrival(), Now.AddDays(-40));
        live.Apply(Values(Arrival() with { Stand = null, Gate = null, Terminal = "T2" }), "ssim", Now, Now, fallback: true).Should()
            .BeFalse("a schedule file never changes a leg a live feed set, however old the live message");
        live.Should().Match<FlightLeg>(l => l.Stand == "B12" && l.Terminal == "T1" && l.ScheduleFeed == "aidx" && !l.ScheduleFallback);

        var scheduled = new FlightLeg("DMO", Values(Arrival("QR1-20261001-A") with { Carrier = "QR", Number = "1", Stand = null, Gate = null }), "ssim", Now, Now,
            fallback: true);
        scheduled.Should().Match<FlightLeg>(l => l.ScheduleFeed == "ssim" && l.ScheduleFallback);
        scheduled.Apply(Values(Arrival("QR1-20261001-A") with { Carrier = "QR", Number = "1", Terminal = "T3", Stand = null, Gate = null }), "ssim", Now.AddMinutes(1),
            Now, fallback: true).Should().BeTrue("a newer schedule file replaces an older one");
        scheduled.Terminal.Should().Be("T3");

        scheduled.Apply(Values(Arrival("QR1-20261001-A") with { Carrier = "QR", Number = "1" }), "aidx", Now.AddDays(-2), Now).Should()
            .BeTrue("a live feed replaces a schedule file's values, even with an older message");
        scheduled.Should().Match<FlightLeg>(l => l.Stand == "B12" && l.Terminal == "T1" && l.ScheduleFeed == "aidx" && !l.ScheduleFallback);
        scheduled.Apply(Values(Arrival("QR1-20261001-A") with { Carrier = "QR", Number = "1", Terminal = "T3" }), "ssim", Now.AddDays(1), Now, fallback: true)
            .Should().BeFalse("once a live feed has reported the leg");
        scheduled.Terminal.Should().Be("T1");
    }

    [Fact]
    public void Events_Should_BeCheckedByExactName_When_Parsed()
    {
        FlightRules.Check(new FlightEventData("DM214-20261001-A", "OnBlock", Scheduled)).Errors.Should().BeEmpty();
        FlightRules.Check(new FlightEventData("DM214-20261001-A", "onblock", Scheduled)).Errors.Should().NotBeEmpty();
        FlightRules.Check(new FlightEventData("DM214-20261001-A", "2", Scheduled)).Errors.Should().NotBeEmpty();
        FlightRules.Check(new FlightEventData("DM214-20261001-A", "OnBlock", DateTime.SpecifyKind(Scheduled, DateTimeKind.Unspecified))).Errors.Should().NotBeEmpty();
        FlightRules.Check(new FlightEventData(null, "OnBlock", Scheduled)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Allocation_Should_KeepUnmappedCountersApartAndIgnoreOlderMessages_When_Applied()
    {
        var (values, errors) = FlightRules.Check(new CounterAllocationData("XR331-20261001-D", "ci-c", ["c01", "C02", "C02", "Z99"], Scheduled.AddHours(-3),
            Scheduled.AddMinutes(-45), "hb"));
        errors.Should().BeEmpty();
        values.Should().Match<CounterAllocationValues>(v => v.CheckpointCode == "CI-C" && v.HandlerCode == "HB");
        values.CounterCodes.Should().Equal("C01", "C02", "Z99");

        var leg = Leg(Departure(), Now.AddMinutes(-60));
        leg.Id ??= Guid.NewGuid();
        var allocation = new CounterAllocation(leg, "CI-C");
        allocation.Apply(values, ["C-01", "C-02"], ["Z99"], "api", Now.AddMinutes(-10), Now).Should().BeTrue();
        allocation.Should().Match<CounterAllocation>(a => a.DeskCodes == "C-01 C-02" && a.UnresolvedCodes == "Z99");
        allocation.Apply(values with { CounterCodes = ["C01"] }, ["C-01"], [], "api", Now.AddMinutes(-20), Now).Should().BeFalse("older");
        allocation.Apply(values, ["C-01", "C-02"], ["Z99"], "api", Now, Now).Should().BeFalse("the same");

        var arrival = Leg(Arrival(), Now);
        arrival.Id = Guid.NewGuid();
        var wrong = () => new CounterAllocation(arrival, "CI-C");
        wrong.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null, "CI-C", 2, true)]
    [InlineData("XR331", "CI C", 2, true)]
    [InlineData("XR331", "CI-C", 0, true)]
    [InlineData("XR331", "CI-C", 101, true)]
    [InlineData("XR331", "CI-C", 2, false)]
    public void Allocation_Should_BeRefused_When_ItsFieldsAreNotWhatAFeedCanMean(string key, string checkpoint, int counters, bool refused)
    {
        var data = new CounterAllocationData(key, checkpoint, [.. Enumerable.Range(1, counters).Select(i => $"C{i:00}")], Scheduled.AddHours(-3), Scheduled.AddMinutes(-45));
        FlightRules.Check(data).Errors.Should().HaveCount(refused ? 1 : 0);
        FlightRules.Check(data with { CloseUtc = data.OpenUtc }).Errors.Should().NotBeEmpty("close after open");
        FlightRules.Check(data with { CloseUtc = data.OpenUtc!.Value.AddHours(25) }).Errors.Should().NotBeEmpty("at most a day");
        FlightRules.Check(data with { CounterCodes = ["C01\r\nX"] }).Errors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(5, 3, FeedState.Fresh)]
    [InlineData(25, 3, FeedState.Stale)]
    [InlineData(25, 0, FeedState.Idle)]
    [InlineData(20, 1, FeedState.Fresh)]
    [InlineData(null, 1, FeedState.Stale)]
    [InlineData(null, 0, FeedState.Idle)]
    public void FeedFreshness_Should_BeStaleOnlyWhileFlightsAreDue_When_TheFeedIsSilent(int? silentMinutes, int flightsDue, FeedState expected)
    {
        DateTime? last = silentMinutes is { } minutes ? Now.AddMinutes(-minutes) : null;
        FeedFreshnessRule.Evaluate(last, Now, TimeSpan.FromMinutes(20), flightsDue).Should().Be(expected);
    }

    [Fact]
    public void Feed_Should_BeOneLowerCaseName_When_Normalised()
    {
        FlightRules.NormalizeFeed(" AIDX ").Should().Be("aidx");
        FlightRules.NormalizeFeed("api-v1").Should().Be("api-v1");
        FlightRules.NormalizeFeed("a").Should().BeNull();
        FlightRules.NormalizeFeed("ai dx").Should().BeNull();
        FlightRules.NormalizeFeed("aidx\n").Should().Be("aidx", "trimmed");
        FlightRules.NormalizeFeed("ai_dx").Should().BeNull();
        FlightRules.IsPlausibleSource(Now.AddMinutes(6), Now).Should().BeFalse();
        FlightRules.IsPlausibleSource(Now.AddDays(-31), Now).Should().BeFalse();
        FlightRules.IsPlausibleSource(DateTime.SpecifyKind(Now, DateTimeKind.Local), Now).Should().BeFalse();
        FlightRules.IsPlausibleSource(Now.AddMinutes(-1), Now).Should().BeTrue();
    }
}
