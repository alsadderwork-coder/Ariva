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
    public const string UnknownDialect = "Unknown dialect.";
    public const string UnknownClockSource = "Unknown clock source.";
    public const string UnknownMethod = "Unknown calibration method.";
    public const string UnknownState = "Unknown device state.";

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
    Task<Result<bool>> RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>The BOQ's assumed footprint for a family and mounting height, for the registration form's preview.</summary>
    Result<FootprintViewModel> AssumedFootprint(string family, double mountingHeightMetres);
}
