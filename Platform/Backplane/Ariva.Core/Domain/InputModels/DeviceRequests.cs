using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

// Device registry requests (ARV-021). Kinds are names (StereoVision, HttpsPush, Xovis, Ntp); the entity validates
// every value again. A footprint is either length and width or a radius (LiDAR); without one the BOQ's assumed
// footprint for the mounting height is used and labelled as an estimate.

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
    [Required] DevicePlacement Placement);

public sealed record UpdateDeviceRequest(
    [Required, MaxLength(100)] string Model,
    [Required, MaxLength(32)] string Transport,
    [Required, MaxLength(32)] string Dialect,
    [Required, MaxLength(8)] string ClockSource);

public sealed record RecordCalibrationRequest(
    [Required, MaxLength(32)] string Method,
    [Range(50, 5000)] int SampleSize,
    [Range(0, 100)] double CountingAccuracyPercent,
    [Range(0, 30)] double WaitTimeErrorMinutes,
    [Range(50, 100)] double? ThresholdPercent = null,
    [MaxLength(1000)] string Notes = null);
