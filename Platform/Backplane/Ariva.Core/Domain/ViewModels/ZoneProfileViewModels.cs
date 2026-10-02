namespace Ariva.Core.Domain.ViewModels;

/// <summary>One version of a site's zone profile, for the history and the list (ARV-017).</summary>
public sealed record ZoneProfileSummaryViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string Status,
    int? Version,
    int? BasedOnVersion,
    string GeometryHash,
    string CreatedBy,
    DateTime? CreatedOn,
    string PublishedBy,
    DateTime? PublishedOn,
    DateTime? RetiredOn,
    int ZoneCount,
    int LineCount);

public sealed record ZoneViewModel(Guid Id, string Name, string Kind, Guid LevelId, Guid? QueueZoneId, Guid? DeskId, string Polygon, double AreaSquareMetres);

public sealed record LineViewModel(Guid Id, string Name, string Role, Guid? ZoneId, Guid LevelId, double StartX, double StartY, double EndX, double EndY, double LengthMetres);

/// <summary>A version with its geometry.</summary>
public sealed record ZoneProfileViewModel(ZoneProfileSummaryViewModel Profile, IReadOnlyList<ZoneViewModel> Zones, IReadOnlyList<LineViewModel> Lines);

/// <summary>What stops a draft from being published (empty when it can be), and the hash of the geometry to publish.</summary>
public sealed record ZoneProfileValidationViewModel(bool Publishable, IReadOnlyList<string> Problems, string GeometryHash);
