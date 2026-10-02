using Ariva.Core.Messaging;

namespace Ariva.Core.Domain.Events;

/// <summary>
/// A zone profile version was published and is now the site's active geometry (ARV-017). Keyed by site, on a compacted
/// topic, so the latest value per site is the active profile. Stream and Cronz load the geometry by
/// <see cref="ProfileId"/> and check it against <see cref="GeometryHash"/>.
/// </summary>
[KafkaTopic(KafkaTopics.TopologyZoneProfileActivated)]
public sealed class ZoneProfilePublished : EventBase
{
    public Guid ProfileId { get; set; }
    public string SiteCode { get; set; }
    public int Version { get; set; }

    /// <summary>The version this one replaced, null for the site's first.</summary>
    public int? ReplacesVersion { get; set; }

    public string GeometryHash { get; set; }
    public string PublishedBy { get; set; }
    public int ZoneCount { get; set; }
    public int LineCount { get; set; }

    public override string GetPartitionKey() => SiteCode;
}
