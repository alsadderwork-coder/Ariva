using Ariva.Core.Messaging;

namespace Ariva.Core.Domain.Events;

/// <summary>
/// A device was registered, moved, calibrated, given a new credential, changed health or retired (ARV-021). Keyed by
/// site, so one site's registry changes arrive in order. Ingest refreshes the device it authenticates (ARV-022) and
/// Stream the devices that cover each zone. Carries no credential material.
/// </summary>
[KafkaTopic(KafkaTopics.DeviceRegistryChanged)]
public sealed class DeviceRegistryChanged : EventBase
{
    public Guid DeviceId { get; set; }
    public string SiteCode { get; set; }
    public string Code { get; set; }

    /// <summary>Registered, Moved, CredentialRotated, CalibrationPassed, CalibrationFailed, HealthOnline, HealthDegraded, HealthOffline or Retired.</summary>
    public string Change { get; set; }

    /// <summary>The device's state after the change.</summary>
    public string State { get; set; }

    public string QueueZoneName { get; set; }

    public override string GetPartitionKey() => SiteCode;
}
