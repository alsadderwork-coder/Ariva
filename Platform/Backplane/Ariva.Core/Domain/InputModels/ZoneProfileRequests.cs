using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

// Zone profile requests (ARV-017). Geometry is validated by the aggregate (simple polygons inside the level, lines on
// edges); the attributes keep the payload bounded. Polygons are "x y,x y,..." in metres of the level, at most 200 points.

/// <summary>A new draft for a site, copied from the site's published version when there is one.</summary>
public sealed record CreateZoneProfileDraftRequest(
    [Required, MaxLength(17)] string SiteCode,
    [MaxLength(200)] string Name);

public sealed record RenameZoneProfileRequest([Required, MaxLength(200)] string Name);

public sealed record AddZoneRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(32)] string Kind,
    [Required] Guid? LevelId,
    [Required, MaxLength(Entities.Zone.PolygonLength)] string Polygon,
    Guid? QueueZoneId = null,
    Guid? DeskId = null,
    [MaxLength(4)] string LaneCategory = null);

/// <summary>Renames and reshapes a zone; it stays on its level.</summary>
/// <summary>A zone's new name and shape; <c>LaneCategory</c> null keeps its lane, an empty string clears it (ARV-057).</summary>
public sealed record UpdateZoneRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(Entities.Zone.PolygonLength)] string Polygon,
    [MaxLength(4)] string LaneCategory = null);

public sealed record AddLineRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(32)] string Role,
    [Required] Guid? LevelId,
    [Range(-1, 2001)] double StartX,
    [Range(-1, 2001)] double StartY,
    [Range(-1, 2001)] double EndX,
    [Range(-1, 2001)] double EndY,
    Guid? ZoneId = null);

/// <summary>The geometry hash the publisher reviewed, from <c>GET {id}/validation</c>; publishing refuses a draft that changed since.</summary>
public sealed record PublishZoneProfileRequest([Required, RegularExpression("^[0-9a-f]{64}$")] string GeometryHash);
