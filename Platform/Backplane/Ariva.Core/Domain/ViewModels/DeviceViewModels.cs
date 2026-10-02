namespace Ariva.Core.Domain.ViewModels;

/// <summary>A coverage footprint with where it came from; an assumed one says so in <see cref="Note"/>.</summary>
public sealed record FootprintViewModel(double? LengthMetres, double? WidthMetres, double? RadiusMetres, string Source, string Text, string Note);

/// <summary>A registered device (ARV-021). The credential is never part of it, only its prefix and when it was issued.</summary>
public sealed record DeviceViewModel(
    Guid Id,
    string Code,
    string SiteCode,
    string Family,
    string Model,
    string Transport,
    string Dialect,
    string ClockSource,
    string State,
    Guid LevelId,
    double X,
    double Y,
    double MountingHeightMetres,
    double OrientationDegrees,
    FootprintViewModel Footprint,
    string QueueZoneName,
    string CredentialPrefix,
    DateTime? CredentialIssuedOn,
    DateTime? LastCalibratedOn,
    bool? LastCalibrationPassed,
    DateTime? RetiredOn,
    DateTime? CreatedOn,
    IReadOnlyList<string> AllowedSources = null,
    string ClientCertificateSha256 = null,
    string MappingName = null);

/// <summary>A declarative mapping shipped with Ariva (ARV-024): its name, what it reads and the vendor documentation it follows.</summary>
public sealed record DeviceMappingViewModel(string Name, string Title, string Source, IReadOnlyList<string> Kinds);

/// <summary>
/// What the device authentication handler needs about a presented credential's device (ARV-022): never sent to a
/// client. Cached briefly under the "devices" tag, which every registry change evicts.
/// </summary>
public sealed record DeviceCredentialRecord(
    Guid DeviceId,
    string Code,
    string SiteCode,
    string QueueZoneName,
    string State,
    string CredentialHash,
    IReadOnlyList<string> AllowedSources,
    string ClientCertificateSha256,
    string Dialect = "Canonical",
    double X = 0,
    double Y = 0,
    double OrientationDegrees = 0,
    string MappingName = null,
    string Transport = "HttpsPush");

/// <summary>
/// The published geometry of a device's queue zone (ARV-022): the queue zone, the zones that hang off it and their
/// lines, with the profile version and hash, so a device or gateway names lines and zones the way Ariva does.
/// </summary>
public sealed record DeviceZoneViewModel(
    string SiteCode,
    string QueueZoneName,
    int Version,
    string GeometryHash,
    IReadOnlyList<ZoneViewModel> Zones,
    IReadOnlyList<LineViewModel> Lines);

/// <summary>Who the authenticated device is, with the server's clock for the device's offset estimate.</summary>
public sealed record DeviceSelfViewModel(string Code, string SiteCode, string QueueZoneName, string State, DateTime ServerTimeUtc);

/// <summary>
/// The answer to a registration or a credential rotation: the device and its credential, shown this once. Ariva keeps
/// only a hash; a lost credential is replaced, never recovered.
/// </summary>
public sealed record DeviceCredentialViewModel(DeviceViewModel Device, string Credential);

public sealed record CalibrationViewModel(
    Guid Id,
    Guid DeviceId,
    string Method,
    int SampleSize,
    double CountingAccuracyPercent,
    double WaitTimeErrorMinutes,
    double ThresholdPercent,
    bool Passed,
    string Notes,
    DateTime PerformedOn,
    string PerformedBy,
    string DeviceState);

/// <summary>
/// A device's health (ARV-025): its state, when Ariva last heard from it and how long ago, and what it last reported
/// (online flag, frame rate, temperature) with Ariva's clock estimate. Null fields mean nothing was heard yet.
/// </summary>
public sealed record DeviceHealthViewModel(
    Guid Id,
    string Code,
    string SiteCode,
    string QueueZoneName,
    string State,
    DateTime? LastSeenOn,
    double? SecondsSinceSeen,
    bool? ReportedOnline,
    double? FrameRate,
    double? TemperatureCelsius,
    double? ClockOffsetMilliseconds,
    string ClockState);

/// <summary>A queue zone's sensing health (ARV-025).</summary>
public sealed record ZoneHealthViewModel(string SiteCode, string QueueZoneName, string State, int Devices, int DevicesOffline, int DevicesDegraded, DateTime ChangedOn);

/// <summary>
/// The health of a site's devices and zones, with the heartbeat timeout that marks a device offline. At most 2,000 of
/// each, offline and degraded first; <see cref="Truncated"/> says when there were more.
/// </summary>
public sealed record DeviceHealthOverviewViewModel(int HeartbeatTimeoutSeconds, IReadOnlyList<DeviceHealthViewModel> Devices, IReadOnlyList<ZoneHealthViewModel> Zones,
    bool Truncated = false);
