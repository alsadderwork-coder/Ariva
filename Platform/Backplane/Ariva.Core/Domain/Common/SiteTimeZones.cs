using System.Diagnostics.CodeAnalysis;
using TimeZoneConverter;

namespace Ariva.Core.Domain.Common;

/// <summary>
/// Airport time zones are IANA ids (Asia/Amman): the web lists them, and local days, calendars and reports use them.
/// The hosts run with invariant globalization, where .NET on Windows cannot map an IANA id to the registry's zone (that
/// needs ICU), while Linux reads it from tzdata. TimeZoneConverter carries the mapping itself, so the same stored id
/// resolves on the Linux images and on a Windows developer or demo machine.
/// </summary>
public static class SiteTimeZones
{
    /// <summary>Longer than any IANA or Windows id; a longer value is refused before any lookup.</summary>
    public const int MaxLength = 64;

    private static readonly HashSet<string> Iana = new(TZConvert.KnownIanaTimeZoneNames, StringComparer.Ordinal);

    /// <summary>
    /// True for an IANA id this host resolves: the only form an airport stores, so the web, every host and the database
    /// read the same id. A Windows id (Arabian Standard Time), a different case or an unknown name is refused.
    /// </summary>
    public static bool IsIana([NotNullWhen(true)] string? id) => id is not null && Iana.Contains(id) && TryFind(id, out _);

    /// <summary>
    /// The zone of a stored id: IANA, or a Windows id a Windows machine may have stored before <see cref="IsIana"/>
    /// checked new values. False when the id is unknown or the operating system has no data for it.
    /// </summary>
    public static bool TryFind(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        return !string.IsNullOrWhiteSpace(id) && id.Length <= MaxLength && TZConvert.TryGetTimeZoneInfo(id, out zone);
    }
}
