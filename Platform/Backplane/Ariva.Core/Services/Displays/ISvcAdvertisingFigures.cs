using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>
/// One line of the proof of play figures (ARV-144): what it counts (<c>Key</c>: a campaign "advertiser: campaign", an
/// advertiser, a display's code or a local day "yyyy-MM-dd"), the plays and those that played their whole slot, the airtime
/// on screen and the airtime the plans scheduled, in seconds, and the share of the scheduled airtime delivered (null when
/// none was scheduled).
/// </summary>
public sealed record AdvertisingFigureViewModel(string Key, int Plays, int CompletedPlays, double AirtimeSeconds, double ScheduledSeconds, double? DeliveredShare);

/// <summary>One advertisement on one display on one local day: the figures' finest grain.</summary>
public sealed record AdvertisingFigureRowViewModel(
    DateOnly Day,
    string DisplayCode,
    Guid MediaId,
    string Advertiser,
    string Campaign,
    int Plays,
    int CompletedPlays,
    double AirtimeSeconds,
    double ScheduledSeconds,
    double? DeliveredShare);

/// <summary>The proof of play figures of a site over local days <c>From</c> to <c>To</c>, by campaign, advertiser, display and day.</summary>
public sealed record AdvertisingFiguresViewModel(
    string SiteCode,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    IReadOnlyList<AdvertisingFigureViewModel> ByCampaign,
    IReadOnlyList<AdvertisingFigureViewModel> ByAdvertiser,
    IReadOnlyList<AdvertisingFigureViewModel> ByDisplay,
    IReadOnlyList<AdvertisingFigureViewModel> ByDay,
    IReadOnlyList<AdvertisingFigureRowViewModel> Rows);

/// <summary>The answers of the figures that are not plain validation.</summary>
public static class AdvertisingFiguresErrors
{
    public const string Range = "Figures cover 1 to 31 local days of the site, the first not after the last, none after today and none more than 3650 days ago.";
}

/// <summary>
/// The proof of play figures of a site (ARV-144), within the caller's sites and only for a caller whose roles view advertising
/// (<c>Advertising.View</c>; either refusal answers like a site that does not exist). The report built on them, its endpoint,
/// its own permission and its CSV export are ARV-146: this is the service it reads, and it checks the caller itself so that no
/// later caller reaches the figures without the permission. The scheduled airtime comes from the plans as they are when the
/// figures are read, over the displays that play advertising (split, set to advertising, enabled).
/// </summary>
public interface ISvcAdvertisingFigures : ISvcScoped
{
    Task<Result<AdvertisingFiguresViewModel>> ReadAsync(string siteCode, DateOnly from, DateOnly to, CancellationToken ct = default);
}
