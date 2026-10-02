using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

// Device registry requests (ARV-021). Kinds are names (StereoVision, HttpsPush, Xovis, Ntp); the entity validates
// every value again. A footprint is either length and width or a radius (LiDAR); without one the BOQ's assumed
// footprint for the mounting height is used and labelled as an estimate. The declarative dialect names a mapping from
// the catalog shipped with Ariva (ARV-024); the other dialects name none.

/// <summary>Where a sensor hangs and what it covers.</summary>
public sealed record DevicePlacement(
    [Required] Guid? LevelId,
    [Range(-1, 2001)] double X,
    [Range(-1, 2001)] double Y,
    [Range(0, 100)] double MountingHeightMetres,
    [Range(0, 360)] double OrientationDegrees,
    [Required, MaxLength(200)] string QueueZoneName,
    [Range(0, 1000)] double? FootprintLengthMetres = null,
    [Range(0, 1000)] double? FootprintWidthMetres = null,
    [Range(0, 1000)] double? FootprintRadiusMetres = null);

public sealed record RegisterDeviceRequest(
    [Required, MaxLength(16)] string Code,
    [Required, MaxLength(32)] string Family,
    [Required, MaxLength(100)] string Model,
    [Required, MaxLength(32)] string Transport,
    [Required, MaxLength(32)] string Dialect,
    [Required, MaxLength(8)] string ClockSource,
    [Required] DevicePlacement Placement,
    [MaxLength(64)] string MappingName = null);

public sealed record UpdateDeviceRequest(
    [Required, MaxLength(100)] string Model,
    [Required, MaxLength(32)] string Transport,
    [Required, MaxLength(32)] string Dialect,
    [Required, MaxLength(8)] string ClockSource,
    [MaxLength(64)] string MappingName = null);

/// <summary>
/// Where a device may push from (CIDR blocks; an empty list for anywhere) and the client certificate it must present
/// (SHA-256; an empty string for none). Both are required, so a request that only means to change the networks cannot
/// drop the certificate pin by leaving it out.
/// </summary>
public sealed record SetDeviceAccessRequest(
    [Required, MaxLength(16)] IReadOnlyList<string> AllowedSources,
    [Required(AllowEmptyStrings = true), MaxLength(95)] string ClientCertificateSha256);

public sealed record RecordCalibrationRequest(
    [Required, MaxLength(32)] string Method,
    [Range(50, 5000)] int SampleSize,
    [Range(0, 100)] double CountingAccuracyPercent,
    [Range(0, 30)] double WaitTimeErrorMinutes,
    [Range(50, 100)] double? ThresholdPercent = null,
    [MaxLength(1000)] string Notes = null);
