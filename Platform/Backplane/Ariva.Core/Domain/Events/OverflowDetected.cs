using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Messaging;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;

namespace Ariva.Core.Domain.Events;

/// <summary>
/// An overflow band of a queue zone became occupied, emptied or unknown (ARV-115), as the queue state engine decided it
/// per closed minute (<see cref="OverflowBands"/>): once when the band's first minute with occupancy follows an empty,
/// unknown or unheard spell (<c>Occupied</c>), once when its first minute with readings and no occupancy follows an
/// occupied or unknown one (<c>Emptied</c>), and once when it stays silent beyond the occupancy freshness window after it
/// was occupied or empty (<c>Unknown</c>: neither occupied nor empty until it reports again).
/// Ariva.Api.Stream writes it to the outbox in the checkpoint transaction that stores the band's minute (ADR-0018), keyed
/// by the zone key (<c>&lt;site&gt;/&lt;queue zone name&gt;</c>) so a zone's changes stay in order. The id is derived from
/// the zone, the band, the minute, the change and the profile version, so a replay or a repeated checkpoint writes the
/// same event once and a consumer's inbox recognises it. Aggregates only: names, a minute and people counts, never an
/// identity (data boundary).
/// </summary>
[KafkaTopic(KafkaTopics.FlowOverflowDetected)]
public sealed class OverflowDetected : EventBase
{
    public string SiteCode { get; set; }
    public string QueueZoneName { get; set; }

    /// <summary>The overflow band (a zone of kind Overflow in the published profile).</summary>
    public string BandName { get; set; }

    /// <summary>Occupied, Emptied or Unknown (written as the name).</summary>
    public OverflowChangeKind State { get; set; }

    /// <summary>The minute (UTC) the change belongs to: the band's first occupied minute, its first empty one, or its first unknown one.</summary>
    public DateTime MinuteUtc { get; set; }

    /// <summary>On Emptied or Unknown after an occupied spell, the spell's first minute; null otherwise.</summary>
    public DateTime? OccupiedSinceUtc { get; set; }

    /// <summary>The most people the band held: in the minute it became occupied, or over the occupied spell that ended (0 when none ended).</summary>
    public int PeakOccupancy { get; set; }

    /// <summary>The zone profile version that produced the minute.</summary>
    public int ZoneProfileVersion { get; set; }

    public override string GetPartitionKey() => ZoneKeys.For(SiteCode, QueueZoneName);

    /// <summary>The event of a band change of zone <paramref name="zoneKey"/> (the site is the key's part before the first slash).</summary>
    public static OverflowDetected From(string zoneKey, string queueZoneName, int profileVersion, OverflowChange change)
    {
        ArgumentException.ThrowIfNullOrEmpty(zoneKey);
        ArgumentException.ThrowIfNullOrEmpty(queueZoneName);
        ArgumentNullException.ThrowIfNull(change);
        var slash = zoneKey.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !string.Equals(zoneKey[(slash + 1)..], queueZoneName, StringComparison.Ordinal))
            throw new ArgumentException("The zone key is <site>/<queue zone name> of this queue zone.", nameof(zoneKey));
        var minute = DateTime.SpecifyKind(change.MinuteUtc, DateTimeKind.Utc);
        return new OverflowDetected
        {
            Id = IdOf(zoneKey, change.BandName, minute, change.Kind, profileVersion),
            OccurredOn = minute,
            SiteCode = zoneKey[..slash],
            QueueZoneName = queueZoneName,
            BandName = change.BandName,
            State = change.Kind,
            MinuteUtc = minute,
            OccupiedSinceUtc = change.OccupiedSinceUtc is { } since ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : null,
            PeakOccupancy = change.PeakOccupancy,
            ZoneProfileVersion = profileVersion
        };
    }

    /// <summary>
    /// A name-based id (RFC 9562 version 8 layout over SHA-256): the same zone, band, minute, change and profile version
    /// always give the same id, and any other gives another.
    /// </summary>
    public static Guid IdOf(string zoneKey, string bandName, DateTime minuteUtc, OverflowChangeKind kind, int profileVersion)
    {
        var text = string.Join('\n', "ariva-overflow-detected/1", zoneKey, bandName,
            minuteUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), kind.ToString(),
            profileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}
