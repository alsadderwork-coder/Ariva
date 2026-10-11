using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

// Site configuration bundles (ARV-122a, docs/product/decisions.md). The body of an import is one bundle document; each
// part is the base64 of its own bytes (UTF-8 JSON for the configuration parts, the raw bytes of each floor plan file) and
// the manifest holds the SHA-256 of each part's decoded bytes. Nothing in a bundle is an id: references between parts
// are codes and names, and every row is checked by SiteConfigurationRules and the entities before anything is stored.

/// <summary>
/// The bundle an export answers and an import takes. The attributes keep the envelope small; the parts' sizes, names and
/// hashes are checked by <c>SiteConfigurationBundles.Open</c> before any part is decoded (CWE-120, CWE-501).
/// </summary>
public sealed record ImportSiteConfigurationRequest(
    int? FormatVersion,
    [MaxLength(17)] string SourceSite,
    [MaxLength(20)] string ExportedAtUtc,
    [MaxLength(Services.Topology.SiteConfigurationLimits.MaxParts + 1)] Dictionary<string, string> Manifest,
    [MaxLength(Services.Topology.SiteConfigurationLimits.MaxParts + 1)] Dictionary<string, string> Parts);

/// <summary>The topology part: terminals, levels, checkpoints and desks, each list present even when empty.</summary>
public sealed record TopologyPart(
    IReadOnlyList<TerminalPartRow> Terminals,
    IReadOnlyList<LevelPartRow> Levels,
    IReadOnlyList<CheckpointPartRow> Checkpoints,
    IReadOnlyList<DeskPartRow> Desks);

/// <summary>A terminal, its airport by IATA code (the airport must exist on the target; it is never created).</summary>
public sealed record TerminalPartRow(string Airport, string Code, string Name);

/// <summary>A level of a terminal named by its code.</summary>
public sealed record LevelPartRow(string Terminal, string Code, string Name, int FloorNumber, double WidthMetres, double DepthMetres);

/// <summary>A checkpoint on a level named by its terminal and level codes; its own code is unique in the site.</summary>
public sealed record CheckpointPartRow(string Terminal, string Level, string Code, string Name, string Kind);

/// <summary>A desk of a checkpoint named by the checkpoint's code.</summary>
public sealed record DeskPartRow(string Checkpoint, string Code, string Name, string Kind, IReadOnlyList<string> LaneCategories, bool InService);

/// <summary>Another system's code for a desk named by its checkpoint and desk codes.</summary>
public sealed record DeskCodeMappingPartRow(string System, string ExternalCode, string Checkpoint, string Desk);

/// <summary>
/// The published zone profile of the source site (the zoneProfile part holds none or one). Its F22 v1 hash is the
/// source's record only (it names levels and desks by ids, so no import reproduces it); the portable hash must be
/// reproduced by the imported draft.
/// </summary>
public sealed record ZoneProfilePartRow(
    string Name,
    int? Version,
    string GeometryHash,
    string PortableGeometryHash,
    IReadOnlyList<ZonePartRow> Zones,
    IReadOnlyList<LinePartRow> Lines);

/// <summary>A zone: its level by terminal and level codes, its queue zone by name, its desk by checkpoint and desk codes.</summary>
public sealed record ZonePartRow(
    string Name,
    string Kind,
    string Terminal,
    string Level,
    string Polygon,
    string QueueZone,
    string Checkpoint,
    string Desk,
    string LaneCategory,
    int? PhysicalCapacity);

/// <summary>A line: its zone by name (none for a count line on its own), its level by terminal and level codes.</summary>
public sealed record LinePartRow(string Name, string Role, string Zone, string Terminal, string Level, double StartX, double StartY, double EndX, double EndY);

/// <summary>
/// A floor plan's metadata; its file is the part <c>floorPlanFile.N</c>, N being the row's position from 1. The type is
/// read from the file's bytes, never from the bundle.
/// </summary>
public sealed record FloorPlanPartRow(string Terminal, string Level, string FileName, double MetresPerPixel, double OriginX, double OriginY);
