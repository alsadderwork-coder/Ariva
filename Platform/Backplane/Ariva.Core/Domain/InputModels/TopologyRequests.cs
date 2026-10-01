using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

// Topology requests (ARV-014). Codes and kinds are validated by the entities as well; the attributes here only keep
// the payload small and well formed. Site codes and parent ids are bound once, at creation, and never changed.

public sealed record CreateAirportRequest(
    [Required, MaxLength(3), RegularExpression("^[A-Z]{3}$")] string IataCode,
    [MaxLength(4)] string IcaoCode,
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(64)] string TimeZoneId);

public sealed record UpdateAirportRequest(
    [MaxLength(4)] string IcaoCode,
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(64)] string TimeZoneId);

public sealed record CreateTerminalRequest(
    [Required] Guid? AirportId,
    [Required, MaxLength(16)] string Code,
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(17)] string SiteCode);

public sealed record UpdateTerminalRequest([Required, MaxLength(200)] string Name);

public sealed record CreateLevelRequest(
    [Required] Guid? TerminalId,
    [Required, MaxLength(16)] string Code,
    [Required, MaxLength(200)] string Name,
    [Range(-10, 50)] int FloorNumber,
    [Range(0.1, 2000)] double WidthMetres,
    [Range(0.1, 2000)] double DepthMetres);

public sealed record UpdateLevelRequest(
    [Required, MaxLength(200)] string Name,
    [Range(-10, 50)] int FloorNumber,
    [Range(0.1, 2000)] double WidthMetres,
    [Range(0.1, 2000)] double DepthMetres);

public sealed record CreateCheckpointRequest(
    [Required] Guid? LevelId,
    [Required, MaxLength(16)] string Code,
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(32)] string Kind);

public sealed record UpdateCheckpointRequest([Required, MaxLength(200)] string Name);

public sealed record CreateDeskRequest(
    [Required] Guid? CheckpointId,
    [Required, MaxLength(16)] string Code,
    [MaxLength(200)] string Name,
    [Required, MaxLength(32)] string Kind,
    [MaxLength(8)] IReadOnlyList<string> LaneCategories = null);

public sealed record UpdateDeskRequest(
    [MaxLength(200)] string Name,
    [MaxLength(8)] IReadOnlyList<string> LaneCategories = null,
    bool InService = true);

/// <summary>A numbered range of desks (ARV-015): prefix plus From..To padded to Width digits, at most 200.</summary>
public sealed record CreateDeskRangeRequest(
    [Required] Guid? CheckpointId,
    [MaxLength(12)] string Prefix,
    [Range(0, 9999)] int From,
    [Range(0, 9999)] int To,
    [Range(1, 4)] int Width,
    [Required, MaxLength(32)] string Kind,
    [MaxLength(8)] IReadOnlyList<string> LaneCategories = null);

/// <summary>Another system's code for a desk (ARV-015).</summary>
public sealed record CreateDeskCodeMappingRequest(
    [Required, MaxLength(16)] string System,
    [Required, MaxLength(32)] string ExternalCode,
    [Required] Guid? DeskId);

/// <summary>Points an existing code at another desk of the same site.</summary>
public sealed record UpdateDeskCodeMappingRequest([Required] Guid? DeskId);
