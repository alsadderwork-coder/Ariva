using System.Text.Json;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Infra.Border;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-116: the desk feed's side of the staff and service zone readings: stored rows become engine signals by the exact
/// role name only (CWE-501), the read position works for them as for AMAN's records, a state saved before has no zone
/// position and still loads, and the sensor T1 setting is bounded.
/// </summary>
public sealed class DeskFeedZoneTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("StaffZone", DeskSource.StaffZone)]
    [InlineData("ServiceZone", DeskSource.ServiceZone)]
    public void Signal_Should_GiveAZoneReading_When_TheStoredRoleIsAZone(string role, DeskSource expected)
    {
        var row = new DeskZoneRow(Guid.NewGuid(), T0, "DMO/IMM/AR-08", role, T0.AddSeconds(-30), 1, true);

        row.Signal().Should().Be(new DeskZoneReading("DMO/IMM/AR-08", T0.AddSeconds(-30), expected, 1, true));
    }

    [Theory]
    [InlineData("Session")]
    [InlineData("Transactions")]
    [InlineData("staffzone")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData(null)]
    public void Signal_Should_GiveNothing_When_TheStoredRoleIsNotExactlyAZoneRole(string role)
    {
        new DeskZoneRow(Guid.NewGuid(), T0, "DMO/IMM/AR-08", role, T0, 1, false).Signal().Should().BeNull();
    }

    [Fact]
    public void Take_Should_TakeEachRowOnceAcrossOverlappingReads_When_GivenTheRowsIdAndWriteTime()
    {
        var rows = Enumerable.Range(0, 10).Select(i => new DeskZoneRow(Guid.CreateVersion7(), T0.AddSeconds(i * 20), "DMO/IMM/AR-08", "StaffZone", T0, i % 2, false)).ToList();
        var cursor = AmanFeedCursor.Start(T0);

        var (first, next) = cursor.Take(rows.Take(6).ToList(), r => r.Id, r => r.WrittenUtc);
        // The next read reaches back two minutes and returns the rows already taken again, with the new ones.
        var (second, last) = next.Take(rows.Skip(2).ToList(), r => r.Id, r => r.WrittenUtc);

        first.Should().Equal(rows.Take(6));
        second.Should().Equal(rows.Skip(6));
        last.PositionUtc.Should().Be(rows[^1].WrittenUtc);
        last.ReadFromUtc.Should().Be(rows[^1].WrittenUtc - AmanFeedCursor.Overlap);
    }

    [Fact]
    public void Take_Should_GiveWhatTheAmanRecordsTakeGave_When_UsedForAmanRecords()
    {
        var records = Enumerable.Range(0, 30).Select(i => new AmanFeedRecord(AmanRecordKind.DeskInterval, Guid.CreateVersion7(), T0.AddSeconds(i * 7), "k", null, T0)).ToList();
        var cursor = new AmanFeedCursor(T0.AddSeconds(50), [new AmanTakenRecord(records[3].Id, records[3].ReceivedUtc)]);

        var a = cursor.Take(records, 10);
        var b = cursor.Take(records, r => r.Id, r => r.ReceivedUtc, 10);

        b.Fresh.Should().Equal(a.Fresh);
        b.Next.Should().BeEquivalentTo(a.Next);
    }

    [Fact]
    public void State_Should_LoadWithoutAZonePosition_When_SavedBeforeArv116()
    {
        var saved = new DeskFeedState(DeskFeedState.CurrentVersion, new DeskStateEngine([], T0).Capture(), AmanFeedCursor.Start(T0), T0);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(saved, EventCatalog.Json));
        var withoutZones = new Dictionary<string, JsonElement>(document.RootElement.EnumerateObject()
            .Where(p => !p.Name.Equals(nameof(DeskFeedState.ZoneCursor), StringComparison.OrdinalIgnoreCase)).Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone())));

        var loaded = JsonSerializer.Deserialize<DeskFeedState>(JsonSerializer.Serialize(withoutZones), EventCatalog.Json);

        loaded.ZoneCursor.Should().BeNull("the feed then starts the readings at the time of the read");
        loaded.Cursor.Should().BeEquivalentTo(saved.Cursor);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(60, true)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    public void Settings_Should_BoundTheSensorT1_When_Configured(int seconds, bool valid)
    {
        var settings = new DeskFeedSettings { SensorPauseSeconds = seconds };

        settings.Problems().Any().Should().Be(!valid);
        if (valid)
            settings.Engine.Problems().Should().BeEmpty();
        new DeskFeedSettings().Engine.SensorPauseAfter.Should().Be(TimeSpan.FromSeconds(60), "Proposed, pending the owner");
        new DeskFeedSettings().Engine.PauseAfter.Should().Be(TimeSpan.FromMinutes(3), "T1 with a login source is unchanged");
    }
}
