using Ariva.Core.Domain.Common;
using Ariva.Core.Domain.Components;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>Minimal entity used to exercise the base classes; not a real Ariva entity.</summary>
public class SampleZone : BaseSoftDeletableEntity<SampleZone>
{
    public virtual string Code { get; set; }

    public static string Key(params object[] parts) => GenerateHashKey(parts);
}

/// <summary>A second entity type, to prove equality never crosses types.</summary>
public class SampleDesk : BaseAuditableEntity<SampleDesk>
{
}

/// <summary>Minimal event keyed by zone.</summary>
public sealed class SampleZoneOpened : EventBase
{
    public string ZoneCode { get; set; } = "Z1";

    public override string GetPartitionKey() => ZoneCode;
}
