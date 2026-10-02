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
    DateTime? CreatedOn);

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
