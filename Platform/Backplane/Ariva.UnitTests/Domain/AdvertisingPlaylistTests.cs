using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-143a: a playlist is a named, ordered list of 1 to 40 items, each an advertisement or an instruction slide of its
/// site, with its time on screen and its weight; a retired playlist is kept. The audit trail keeps the whole playlist.
/// </summary>
public sealed class AdvertisingPlaylistTests
{
    private const string Site = "AUH-TA";

    private static AdvertisingPlaylistItem Item(AdvertisingItemKind kind = AdvertisingItemKind.Media, int seconds = 10, int weight = 1, Guid? id = null) =>
        new(kind, id ?? Guid.CreateVersion7(), seconds, weight);

    private static AdvertisingPlaylistValues Values(string name = "Morning bank", params AdvertisingPlaylistItem[] items) =>
        new(name, items.Length == 0 ? [Item(), Item(AdvertisingItemKind.Slide, 8, 2)] : items);

    [Fact]
    public void Constructor_Should_KeepTheItemsInOrderAndTheNameTrimmed_When_TheValuesAreValid()
    {
        var first = Item(seconds: 15, weight: 3);
        var second = Item(AdvertisingItemKind.Slide, 5);

        var playlist = new AdvertisingPlaylist(Site, Values("  Morning bank  ", first, second));

        playlist.Name.Should().Be("Morning bank");
        playlist.ItemList.Should().Equal(first, second);
        playlist.Status.Should().Be(InstructionStatus.Active);
        playlist.Values().Should().BeEquivalentTo(new AdvertisingPlaylistValues("Morning bank", [first, second]));
    }

    [Theory]
    [InlineData("no name")]
    [InlineData("a name with a line break")]
    [InlineData("a name too long")]
    [InlineData("no item")]
    [InlineData("41 items")]
    [InlineData("an item without an id")]
    [InlineData("an item of no kind")]
    [InlineData("a weight of 0")]
    [InlineData("a weight of 11")]
    [InlineData("no time on screen")]
    [InlineData("longer than any video")]
    public void Values_Should_BeRefused_When_ARuleIsBroken(string reason)
    {
        var values = reason switch
        {
            "no name" => Values(" "),
            "a name with a line break" => Values("Morning\nbank"),
            "a name too long" => Values(new string('n', AdvertisingPlaylist.MaxNameLength + 1)),
            "no item" => new AdvertisingPlaylistValues("Morning bank", []),
            "41 items" => new AdvertisingPlaylistValues("Morning bank", [.. Enumerable.Range(0, 41).Select(_ => Item())]),
            "an item without an id" => Values("Morning bank", Item(id: Guid.Empty)),
            "an item of no kind" => Values("Morning bank", Item((AdvertisingItemKind)7)),
            "a weight of 0" => Values("Morning bank", Item(weight: 0)),
            "a weight of 11" => Values("Morning bank", Item(weight: 11)),
            "no time on screen" => Values("Morning bank", Item(seconds: 0)),
            _ => Values("Morning bank", Item(seconds: AdvertisingMedia.MaxVideoSeconds + 1))
        };

        values.Problems().Should().NotBeEmpty(reason);
        ((Action)(() => new AdvertisingPlaylist(Site, values))).Should().Throw<ArgumentException>(reason);
    }

    [Fact]
    public void Retire_Should_KeepThePlaylist_When_ItIsRestoredLater()
    {
        var playlist = new AdvertisingPlaylist(Site, Values());

        playlist.Retire();
        playlist.IsActive.Should().BeFalse();
        playlist.AuditSummary().Should().Contain("\"status\":\"Retired\"");
        playlist.Restore();
        playlist.IsActive.Should().BeTrue();
    }

    [Fact]
    public void AuditSummary_Should_KeepEveryItem_When_APlaylistIsAtItsLongest()
    {
        var items = Enumerable.Range(0, AdvertisingPlaylist.MaxItems)
            .Select(_ => Item(seconds: AdvertisingMedia.MaxVideoSeconds, weight: AdvertisingPlaylist.MaxWeight)).ToArray();
        var playlist = new AdvertisingPlaylist("XXXXXXXX-XXXXXXXX", Values(new string('n', AdvertisingPlaylist.MaxNameLength), items));

        var summary = playlist.AuditSummary();

        summary.Length.Should().BeLessThanOrEqualTo(AuditEntry.SummaryLength, "the whole playlist stays in one audit entry");
        foreach (var item in items)
            summary.Should().Contain($"M:{item.Id:N}:{item.Seconds}:{item.Weight}");
        Values(new string('"', AdvertisingPlaylist.MaxNameLength), items).Problems()
            .Should().Equal([AdvertisingPlaylistValues.TooLongToAudit], "a name of quotes, escaped, would not fit with 40 items");
    }
}
