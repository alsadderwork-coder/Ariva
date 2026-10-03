using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Topology;

/// <summary>Errors the topology services return; the controllers map them to status codes.</summary>
public static class TopologyErrors
{
    public const string NotFound = "The record does not exist.";
    public const string Duplicate = "A record with that code already exists here.";
    public const string HasChildren = "Delete the records under it first.";
    public const string UnknownKind = "Unknown kind.";
    public const string UnknownSite = "Unknown site.";
    public const string InvalidCriteria = "The search criteria are not valid.";
    public const string DeploymentWide = "Only an administrator with access to every site can change airports.";
    public const string TooLarge = "The file is larger than 20 MB.";
    public const string UnsupportedFile = "Upload a PNG, JPEG or SVG image.";
    public const string UnsafeFileName = "The file name must not contain a path.";

    /// <summary>Errors that mean "already exists or in use" (409).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { Duplicate, HasChildren };
}

/// <summary>
/// Topology administration (ARV-014): airports, terminals, levels, checkpoints and desks. Reads and writes below the
/// airport are limited to the caller's sites, and a record outside them answers like one that does not exist. Every
/// change is audited. Deletes are soft and refused while live children exist.
/// </summary>
public interface ISvcTopology : ISvcScoped
{
    Task<Result<PageViewModel<AirportViewModel>>> SearchAirportsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<AirportViewModel>> GetAirportAsync(Guid id, CancellationToken ct = default);
    Task<Result<AirportViewModel>> CreateAirportAsync(CreateAirportRequest request, CancellationToken ct = default);
    Task<Result<AirportViewModel>> UpdateAirportAsync(Guid id, UpdateAirportRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteAirportAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<TerminalViewModel>>> SearchTerminalsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<TerminalViewModel>> GetTerminalAsync(Guid id, CancellationToken ct = default);
    Task<Result<TerminalViewModel>> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default);
    Task<Result<TerminalViewModel>> UpdateTerminalAsync(Guid id, UpdateTerminalRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteTerminalAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<LevelViewModel>>> SearchLevelsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<LevelViewModel>> GetLevelAsync(Guid id, CancellationToken ct = default);
    Task<Result<LevelViewModel>> CreateLevelAsync(CreateLevelRequest request, CancellationToken ct = default);
    Task<Result<LevelViewModel>> UpdateLevelAsync(Guid id, UpdateLevelRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteLevelAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<CheckpointViewModel>>> SearchCheckpointsAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<CheckpointViewModel>> GetCheckpointAsync(Guid id, CancellationToken ct = default);
    Task<Result<CheckpointViewModel>> CreateCheckpointAsync(CreateCheckpointRequest request, CancellationToken ct = default);
    Task<Result<CheckpointViewModel>> UpdateCheckpointAsync(Guid id, UpdateCheckpointRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteCheckpointAsync(Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<DeskViewModel>>> SearchDesksAsync(TopologyCriteria criteria, CancellationToken ct = default);
    Task<Result<DeskViewModel>> GetDeskAsync(Guid id, CancellationToken ct = default);
    Task<Result<DeskViewModel>> CreateDeskAsync(CreateDeskRequest request, CancellationToken ct = default);

    /// <summary>Creates a numbered range of desks in one transaction: all of them or none (ARV-015).</summary>
    Task<Result<IReadOnlyList<DeskViewModel>>> CreateDeskRangeAsync(CreateDeskRangeRequest request, CancellationToken ct = default);
    Task<Result<DeskViewModel>> UpdateDeskAsync(Guid id, UpdateDeskRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteDeskAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Desk code mappings (ARV-015): another system's code for a desk, unique per system and site, one per desk and system.
/// Site-scoped like the desks; changes are audited.
/// </summary>
public interface ISvcDeskCodeMappings : ISvcScoped
{
    Task<Result<PageViewModel<DeskCodeMappingViewModel>>> SearchAsync(DeskCodeMappingCriteria criteria, CancellationToken ct = default);
    Task<Result<DeskCodeMappingViewModel>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<DeskCodeMappingViewModel>> CreateAsync(CreateDeskCodeMappingRequest request, CancellationToken ct = default);
    Task<Result<DeskCodeMappingViewModel>> UpdateAsync(Guid id, UpdateDeskCodeMappingRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The live desk a system's code names at a site, or null when it is not mapped (feed consumers park such
    /// records). Cached with the topology tag; not scoped to a caller, for background consumers.
    /// </summary>
    Task<Guid?> ResolveAsync(Ariva.Core.Domain.Enums.ExternalSystem system, string siteCode, string externalCode, CancellationToken ct = default);
}

/// <summary>
/// Floor plans (ARV-018): one per level, site-scoped like the level. Uploads are sniffed (PNG, JPEG or SVG), limited to
/// 20 MB, SVG is sanitised, and the file is stored under a generated key.
/// </summary>
public interface ISvcFloorPlans : ISvcScoped
{
    Task<Result<FloorPlanViewModel>> GetAsync(Guid levelId, CancellationToken ct = default);

    /// <summary>The current plans of a site's live levels (ARV-055): which levels have one, without asking level by level.</summary>
    Task<Result<IReadOnlyList<FloorPlanViewModel>>> ListAsync(string siteCode, CancellationToken ct = default);

    Task<Result<FloorPlanContent>> OpenAsync(Guid levelId, CancellationToken ct = default);

    Task<Result<FloorPlanViewModel>> UploadAsync(Guid levelId, FloorPlanUpload upload, CancellationToken ct = default);

    Task<Result<FloorPlanViewModel>> CalibrateAsync(Guid levelId, CalibrateFloorPlanRequest request, CancellationToken ct = default);

    Task<Result<bool>> DeleteAsync(Guid levelId, CancellationToken ct = default);
}
