namespace Ariva.Core.Domain.ViewModels;

/// <summary>A coverage footprint with where it came from; an assumed one says so in <see cref="Note"/>.</summary>
public sealed record FootprintViewModel(double? LengthMetres, double? WidthMetres, double? RadiusMetres, string Source, string Text, string Note);

/// <summary>
/// A registered device (ARV-021). The credential is never part of it, only its prefix and when it was issued.
/// <see cref="LineCoverage"/> and <see cref="CoveredLines"/> are the lines it counts (ARV-259e4); <see cref="LinesNotInZone"/>
/// the lines it lists that its zone, in the site's published zone profile, no longer holds as entry or exit lines (a
/// publish dropped or renamed them: a warning, never a refusal).
/// </summary>
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
    string MappingName = null,
    string LineCoverage = "All",
    IReadOnlyList<string> CoveredLines = null,
    IReadOnlyList<string> LinesNotInZone = null);

/// <summary>A device after its lines were declared (ARV-259e4), with the warnings (a listed line its footprint does not reach).</summary>
public sealed record DeviceCoverageViewModel(DeviceViewModel Device, IReadOnlyList<string> Warnings);

/// <summary>A declarative mapping shipped with Ariva (ARV-024): its name, what it reads and the vendor documentation it follows.</summary>
public sealed record DeviceMappingViewModel(string Name, string Title, string Source, IReadOnlyList<string> Kinds);

/// <summary>
/// What the device authentication handler needs about a presented credential's device (ARV-022): never sent to a
/// client. Cached briefly under the "devices" tag, which every registry change evicts. <see cref="CoveredLines"/> is the
/// lines the device declares it counts (ARV-259e5: null for All, empty for None, the names for Listed), which Ingest stamps
/// on its health reports.
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
    string Transport = "HttpsPush",
    IReadOnlyList<string> CoveredLines = null);

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

/// <summary>A queue zone's entry and exit lines no commissioned device declares it counts (ARV-259e4), in ordinal order.</summary>
public sealed record UncoveredLinesViewModel(string SiteCode, string QueueZoneName, IReadOnlyList<string> Lines);

/// <summary>
/// The health of a site's devices and zones, with the heartbeat timeout that marks a device offline. At most 2,000 of
/// each, offline and degraded first; <see cref="Truncated"/> says when there were more. <see cref="UncoveredLines"/>
/// (ARV-259e4) lists the queue zones of the published zone profiles with an entry or exit line no commissioned device
/// declares it counts, at most 2,000.
/// </summary>
public sealed record DeviceHealthOverviewViewModel(int HeartbeatTimeoutSeconds, IReadOnlyList<DeviceHealthViewModel> Devices, IReadOnlyList<ZoneHealthViewModel> Zones,
    bool Truncated = false, IReadOnlyList<UncoveredLinesViewModel> UncoveredLines = null);
