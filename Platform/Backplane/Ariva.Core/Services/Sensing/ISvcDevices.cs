using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Sensing;

public static class DeviceErrors
{
    public const string ZoneNotFound = "The queue zone is not in the site's published zone profile or its draft.";
    public const string ZoneOnAnotherLevel = "The queue zone is on another level than the device.";
    public const string OutOfReach = "The device's footprint does not reach its queue zone; move it over the zone.";
    public const string Retired = "The device is retired.";
    public const string NotRemovable = "Only a device that was never calibrated can be removed; retire it instead.";
    public const string UnknownFamily = "Unknown device family.";
    public const string UnknownTransport = "Unknown transport.";
    public const string UnsupportedTransport = "Ariva does not support this transport yet: register the device with HTTPS push or MQTT.";

    /// <summary>
    /// The transports Ariva.Api.Ingest takes data from (HTTPS push, ARV-023; MQTT, ARV-024). The others are named by the
    /// sensor support model for later releases; a device registered with one could never send data, so it is refused.
    /// </summary>
    public static readonly IReadOnlySet<Ariva.Core.Domain.Enums.DeviceTransport> SupportedTransports =
        new HashSet<Ariva.Core.Domain.Enums.DeviceTransport> { Ariva.Core.Domain.Enums.DeviceTransport.HttpsPush, Ariva.Core.Domain.Enums.DeviceTransport.Mqtt };
    public const string UnknownDialect = "Unknown dialect.";
    public const string UnknownClockSource = "Unknown clock source.";
    public const string UnknownMethod = "Unknown calibration method.";
    public const string UnknownState = "Unknown device state.";
    public const string UnknownMapping = "The declarative dialect needs a mapping from the catalog shipped with Ariva (GET devices/mappings); other dialects take none.";

    /// <summary>Errors that mean the request conflicts with the device's state (409).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { Retired, NotRemovable };
}

/// <summary>
/// The device registry (ARV-021): register a device with a credential shown once, change its details or placement
/// (a move sends it back to commissioning), rotate its credential, record calibrations (a pass sets it online),
/// retire it, and remove one that was never calibrated. Site-scoped: another site's device answers like one that does
/// not exist. Every change is audited and raises DeviceRegistryChanged.
/// </summary>
public interface ISvcDevices : ISvcScoped
{
    Task<Result<PageViewModel<DeviceViewModel>>> SearchAsync(DeviceCriteria criteria, CancellationToken ct = default);
    Task<Result<DeviceViewModel>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<DeviceCredentialViewModel>> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default);
    Task<Result<DeviceViewModel>> UpdateAsync(Guid id, UpdateDeviceRequest request, CancellationToken ct = default);
    Task<Result<DeviceViewModel>> MoveAsync(Guid id, DevicePlacement request, CancellationToken ct = default);
    Task<Result<DeviceCredentialViewModel>> RotateCredentialAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<CalibrationViewModel>>> CalibrationsAsync(Guid id, CancellationToken ct = default);
    Task<Result<CalibrationViewModel>> RecordCalibrationAsync(Guid id, RecordCalibrationRequest request, CancellationToken ct = default);
    Task<Result<DeviceViewModel>> RetireAsync(Guid id, CancellationToken ct = default);

    /// <summary>Where the device may push from and the client certificate it must present (ARV-022).</summary>
    Task<Result<DeviceViewModel>> SetAccessAsync(Guid id, SetDeviceAccessRequest request, CancellationToken ct = default);
    Task<Result<bool>> RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>The BOQ's assumed footprint for a family and mounting height, for the registration form's preview.</summary>
    Result<FootprintViewModel> AssumedFootprint(string family, double mountingHeightMetres);
}

/// <summary>
/// The device side of the registry (ARV-022), used by Ariva.Api.Ingest without a user: find the device behind a
/// presented credential prefix, and read the published geometry of a device's zone. Not site-scoped by a user; the
/// device's own site and zone are the scope.
/// </summary>
public interface ISvcDeviceGateway : ISvcScoped
{
    /// <summary>The live device whose credential starts with <paramref name="prefix"/>, or null. Cached for a minute at most.</summary>
    Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>The published queue zone of that name in the site, with the zones that hang off it and their lines.</summary>
    Task<Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default);
}

/// <summary>
/// Where mapped sensing batches go (ARV-023): Kafka through <see cref="ISvcMessageBus"/> in the hosts; a test can
/// capture them instead. A failure means the push was not stored and the device should send it again (503).
/// </summary>
public interface ISensingSink
{
    Task PublishAsync(IReadOnlyList<Ariva.Core.Domain.Contracts.IEvent> events, CancellationToken ct = default);
}

/// <summary>
/// The declarative mappings shipped with Ariva (ARV-024), read and checked at start-up. A device on the declarative
/// dialect names one; there is no way to add a mapping at run time, so a mapping is reviewed like code.
/// </summary>
public interface IDeviceMappingCatalog
{
    /// <summary>The mappings by name, with a title and the vendor documentation they follow.</summary>
    IReadOnlyList<DeviceMappingViewModel> Mappings { get; }

    bool Contains(string name);
}
