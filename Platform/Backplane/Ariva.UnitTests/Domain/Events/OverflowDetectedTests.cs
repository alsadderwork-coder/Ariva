using System.Text.Json;
using Ariva.Core.Domain.Events;
using Ariva.Core.Messaging;
using Ariva.Core.Queueing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Events;

/// <summary>
/// ARV-115: OverflowDetected, the stream's event for a band that became occupied or emptied. Keyed by the zone key (site
/// and queue zone) on ariva.flow.overflow-detected.v1, an id derived from what it says (a replay writes the same event
/// once), and aggregates only (names, a minute, people counts; data boundary).
/// </summary>
public sealed class OverflowDetectedTests
{
    private static readonly DateTime Minute = new(2026, 9, 28, 18, 2, 0, DateTimeKind.Utc);

    [Fact]
    public void From_Should_CarryTheChangeKeyedBySiteAndQueueZone_When_ABandEmpties()
    {
        var change = new OverflowChange("A-OV", Minute.AddMinutes(29), OverflowChangeKind.Emptied, 85, Minute);

        var e = OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, change);

        e.Should().BeEquivalentTo(new
        {
            SiteCode = "DMO", QueueZoneName = "A-VIS", BandName = "A-OV", State = OverflowChangeKind.Emptied, MinuteUtc = Minute.AddMinutes(29), OccupiedSinceUtc = (DateTime?)Minute,
            PeakOccupancy = 85, ZoneProfileVersion = 12, OccurredOn = Minute.AddMinutes(29)
        });
        e.GetPartitionKey().Should().Be("DMO/A-VIS");
        new EventCatalog([typeof(OverflowDetected).Assembly]).TopicOf(typeof(OverflowDetected)).Should().Be(KafkaTopics.FlowOverflowDetected);
    }

    [Fact]
    public void Id_Should_BeTheSame_When_TheSameChangeIsWrittenAgain()
    {
        var change = new OverflowChange("A-OV", Minute, OverflowChangeKind.Occupied, 3, null);

        var first = OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, change);
        var again = OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, change);

        again.Id.Should().Be(first.Id, "a replay or a repeated checkpoint writes the same event, which the outbox keeps once");
        first.Id.Version.Should().Be(8, "a name-based id in the RFC 9562 version 8 layout");
        new[]
        {
            OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, change with { Kind = OverflowChangeKind.Emptied }).Id,
            OverflowDetected.From("DMO/A-VIS", "A-VIS", 13, change).Id,
            OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, change with { BandName = "A-OV2" }).Id,
            OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, change with { MinuteUtc = Minute.AddMinutes(1) }).Id,
            OverflowDetected.From("AUH/A-VIS", "A-VIS", 12, change).Id
        }.Should().OnlyHaveUniqueItems().And.NotContain(first.Id);
    }

    [Theory]
    [InlineData("A-VIS", "A-VIS")]
    [InlineData("/A-VIS", "A-VIS")]
    [InlineData("DMO/A-VIS", "D-VIS")]
    public void From_Should_RefuseTheZoneKey_When_ItIsNotTheSitesKeyOfTheQueueZone(string zoneKey, string queueZone)
    {
        var from = () => OverflowDetected.From(zoneKey, queueZone, 12, new OverflowChange("A-OV", Minute, OverflowChangeKind.Occupied, 3, null));

        from.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Payload_Should_CarryAggregatesOnly_When_Serialised()
    {
        var e = OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, new OverflowChange("A-OV", Minute, OverflowChangeKind.Occupied, 3, null));

        var names = JsonDocument.Parse(JsonSerializer.Serialize(e, EventCatalog.Json)).RootElement.EnumerateObject().Select(p => p.Name).ToList();

        // Data boundary (docs/domain/data-boundary.md): no officer, traveller, document or track identifier.
        names.Should().BeEquivalentTo("id", "occurredOn", "eventType", "version", "correlationId", "causationId", "siteCode", "queueZoneName", "bandName", "state",
            "minuteUtc", "occupiedSinceUtc", "peakOccupancy", "zoneProfileVersion");
    }

    [Fact]
    public void Payload_Should_WriteTheStateByName_When_TheBandWentUnknown()
    {
        var e = OverflowDetected.From("DMO/A-VIS", "A-VIS", 12, new OverflowChange("A-OV", Minute.AddMinutes(5), OverflowChangeKind.Unknown, 7, Minute));

        var json = JsonDocument.Parse(JsonSerializer.Serialize(e, EventCatalog.Json)).RootElement;

        json.GetProperty("state").GetString().Should().Be("Unknown");
        json.GetProperty("occupiedSinceUtc").GetDateTime().Should().Be(Minute);
        json.GetProperty("peakOccupancy").GetInt32().Should().Be(7);
        e.Id.Should().NotBe(OverflowDetected.IdOf("DMO/A-VIS", "A-OV", Minute.AddMinutes(5), OverflowChangeKind.Emptied, 12), "the change is part of the id");
        JsonSerializer.Deserialize<OverflowDetected>(json.GetRawText(), EventCatalog.Json)!.State.Should().Be(OverflowChangeKind.Unknown);
    }
}
