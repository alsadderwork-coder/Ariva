using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A playlist of a site's advertising region (ARV-143a, wiki 11 section 9): an ordered list of items, each an advertisement
/// of the site's library or an instruction slide of the site, with how long it stays on screen and its weight (its share
/// of airtime). Schedules (<see cref="AdvertisingSchedule"/>) assign it to displays; what plays is decided at each refresh
/// (an advertisement only once approved and within its dates, a slide only while active), so a playlist can name a draft.
/// A retired playlist is kept and its schedules stop applying. Its name is plain text, checked here and only shown as text.
/// Script 0060 holds the same rules in the database, the items checked against the site's own library and slides.
/// </summary>
public class AdvertisingPlaylist : BaseAuditableEntity<AdvertisingPlaylist>, ISiteBound
{
    public const int MaxNameLength = 120;
    /// <summary>The most items a playlist holds: 40 keep the whole playlist in one audit entry with their ids in hex.</summary>
    public const int MaxItems = 40;
    public const int MaxPlaylistsPerSite = 100;
    public const int MinSeconds = 5;
    public const int MaxSeconds = 60;
    public const int MinWeight = 1;
    public const int MaxWeight = 10;

    /// <summary>The stored items' room (script 0060): 40 items of about 90 characters each.</summary>
    public const int MaxItemsJsonLength = 8000;

    protected AdvertisingPlaylist()
    {
    }

    public AdvertisingPlaylist(string siteCode, AdvertisingPlaylistValues values)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        SiteCode = siteCode;
        Status = InstructionStatus.Active;
        Set(values);
    }

    public virtual string SiteCode { get; protected set; }

    [MaxLength(MaxNameLength)]
    public virtual string Name { get; protected set; }

    /// <summary>The items in playing order as JSON: <c>[{"kind":"Media","id":"...","seconds":10,"weight":1}]</c>.</summary>
    [MaxLength(MaxItemsJsonLength)]
    public virtual string Items { get; protected set; }

    /// <summary>Active, or Retired: a retired playlist is kept with its history and its schedules stop applying.</summary>
    public virtual InstructionStatus Status { get; protected set; }

    public virtual bool IsActive => Status == InstructionStatus.Active;

    public virtual IReadOnlyList<AdvertisingPlaylistItem> ItemList =>
        Items is null ? [] : JsonSerializer.Deserialize<List<AdvertisingPlaylistItem>>(Items, Display.Json) ?? [];

    public virtual AdvertisingPlaylistValues Values() => new(Name, ItemList);

    public virtual void Set(AdvertisingPlaylistValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = values.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(values));
        Name = values.Name.Trim();
        Items = ItemsJson(values.Items);
    }

    public virtual void Retire() => Status = InstructionStatus.Retired;

    public virtual void Restore() => Status = InstructionStatus.Active;

    /// <summary>What the audit trail keeps of a playlist: its name, state and every item (an entry keeps 2,000 characters).</summary>
    public virtual string AuditSummary() => SummaryOf(SiteCode, Name, Status, ItemList);

    internal static string SummaryOf(string siteCode, string name, InstructionStatus status, IEnumerable<AdvertisingPlaylistItem> items) =>
        JsonSerializer.Serialize(new
        {
            SiteCode = siteCode, Name = name?.Trim(), Status = status.ToString(),
            // Kind, id, seconds and weight, without names: the shortest form that keeps every item.
            Items = (items ?? []).Select(i => $"{(i.Kind == AdvertisingItemKind.Media ? "M" : "S")}:{i.Id:N}:{i.Seconds}:{i.Weight}")
        }, Display.Json);

    internal static string ItemsJson(IEnumerable<AdvertisingPlaylistItem> items) => JsonSerializer.Serialize((items ?? []).ToList(), Display.Json);

    /// <summary>
    /// One cycle of a playlist as a display plays it (ARV-143b): the items' indexes in playing order, each item as many times
    /// as its weight, so its share of airtime is its weight times its slot. The repeats are spread through the cycle by
    /// smooth weighted round robin (the item furthest behind its share plays next; the earlier item on a tie), so weights of
    /// 2 and 1 play A, B, A rather than A, A, B. A cycle holds at most 40 items of weight 10: 400 places.
    /// </summary>
    public static IReadOnlyList<int> PlayingOrder(IReadOnlyList<int> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count > MaxItems || weights.Any(w => w is < MinWeight or > MaxWeight))
            throw new ArgumentException($"A playlist plays 0 to {MaxItems} items of weight {MinWeight} to {MaxWeight}.", nameof(weights));
        var total = weights.Sum();
        var credit = new int[weights.Count];
        var order = new List<int>(total);
        for (var place = 0; place < total; place++)
        {
            var next = 0;
            for (var i = 0; i < weights.Count; i++)
            {
                credit[i] += weights[i];
                if (credit[i] > credit[next])
                    next = i;
            }

            credit[next] -= total;
            order.Add(next);
        }

        return order;
    }
}

/// <summary>
/// One item of a playlist (ARV-143a): an advertisement of the site's library (<see cref="AdvertisingItemKind.Media"/>) or an
/// instruction slide of the site (<see cref="AdvertisingItemKind.Slide"/>), how many seconds it stays on screen, and its
/// weight (an item of weight 2 gets twice the airtime of one of weight 1, ARV-143b).
/// </summary>
public sealed record AdvertisingPlaylistItem(AdvertisingItemKind Kind, Guid Id, int Seconds, int Weight = 1);

/// <summary>A playlist's settings as checked values (ARV-143a); the items' ids are checked against the site by the service.</summary>
public sealed record AdvertisingPlaylistValues(string Name, IReadOnlyList<AdvertisingPlaylistItem> Items)
{
    public const string TooLongToAudit = "The playlist is too long to keep whole in the audit trail; use a shorter name or fewer items.";

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > AdvertisingPlaylist.MaxNameLength || !DisplayText.IsClean(Name.Trim()))
            problems.Add($"A playlist is named, in at most {AdvertisingPlaylist.MaxNameLength} characters on one line.");
        var items = Items ?? [];
        if (items.Count is 0 or > AdvertisingPlaylist.MaxItems)
            problems.Add($"A playlist has 1 to {AdvertisingPlaylist.MaxItems} items.");
        else if (items.Any(i => i is null || !Enum.IsDefined(i.Kind) || i.Id == Guid.Empty))
            problems.Add("Every item is an advertisement or an instruction slide of the site.");
        else
        {
            if (items.Any(i => i.Weight is < AdvertisingPlaylist.MinWeight or > AdvertisingPlaylist.MaxWeight))
                problems.Add($"An item's weight is {AdvertisingPlaylist.MinWeight} to {AdvertisingPlaylist.MaxWeight}.");
            // A video's slot is its own length (set by the service from the stored item), from 1 second up to the ceiling every
            // installation keeps; every other item stays 5 to 60 seconds.
            if (items.Any(i => i.Seconds is < 1 or > AdvertisingMedia.MaxVideoSeconds))
                problems.Add($"An item stays on screen {AdvertisingPlaylist.MinSeconds} to {AdvertisingPlaylist.MaxSeconds} seconds (a video its own length).");
        }

        if (problems.Count == 0 && AdvertisingPlaylist.SummaryOf(new string('X', 17), Name, InstructionStatus.Retired, items).Length > AuditEntry.SummaryLength)
            problems.Add(TooLongToAudit);
        return problems;
    }
}
