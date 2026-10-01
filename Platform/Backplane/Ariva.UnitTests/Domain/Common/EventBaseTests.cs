using FluentAssertions;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>ARV-004: event identity, time and partition key (ADR-0018).</summary>
public sealed class EventBaseTests
{
    [Fact]
    public void Constructor_Should_SetVersion7IdAndUtcTime_When_EventIsCreated()
    {
        var before = DateTime.UtcNow;

        var opened = new SampleZoneOpened();

        opened.Id.Version.Should().Be(7);
        opened.OccurredOn.Kind.Should().Be(DateTimeKind.Utc);
        opened.OccurredOn.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        opened.Version.Should().Be(1);
    }

    [Fact]
    public void EventType_Should_BeClassName_When_Read()
    {
        new SampleZoneOpened().EventType.Should().Be(nameof(SampleZoneOpened));
    }

    [Fact]
    public void GetPartitionKey_Should_ReturnEntityKey_When_Called()
    {
        new SampleZoneOpened { ZoneCode = "T1-ARR-A" }.GetPartitionKey().Should().Be("T1-ARR-A");
    }

    [Fact]
    public void Constructor_Should_GiveDistinctIds_When_TwoEventsAreCreated()
    {
        new SampleZoneOpened().Id.Should().NotBe(new SampleZoneOpened().Id);
    }
}
