using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Topology;

/// <summary>Errors of the zone profile workflow; the controller maps them to status codes.</summary>
public static class ZoneProfileErrors
{
    public const string NotADraft = "A published or retired zone profile never changes; create a draft from it.";
    public const string DraftExists = "The site already has a draft; edit or discard it.";
    public const string NotPublishable = "The draft cannot be published yet; see the problems.";
    public const string ChangedSinceReview = "The draft changed since it was validated; validate it again and publish the geometry you reviewed.";
    public const string PolygonFormat = "A polygon is \"x y,x y,...\" in metres, with 3 to 200 points.";

    /// <summary>Errors that mean "the state does not allow it" (409).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { NotADraft, DraftExists, ChangedSinceReview };
}

/// <summary>
/// The zone profile workflow (ARV-017): create a draft from the site's published version, edit its zones and lines,
/// validate, publish (a new version that replaces the published one and raises ZoneProfilePublished through the
/// outbox), discard a draft. Published and retired versions never change. Limited to the caller's sites; every change is
/// audited.
/// </summary>
public interface ISvcZoneProfiles : ISvcScoped
{
    /// <summary>Every version and the draft of a site, newest first (the history).</summary>
    Task<Result<IReadOnlyList<ZoneProfileSummaryViewModel>>> HistoryAsync(string siteCode, CancellationToken ct = default);

    Task<Result<ZoneProfileViewModel>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<ZoneProfileViewModel>> CreateDraftAsync(CreateZoneProfileDraftRequest request, CancellationToken ct = default);

    Task<Result<ZoneProfileViewModel>> RenameAsync(Guid id, RenameZoneProfileRequest request, CancellationToken ct = default);

    Task<Result<ZoneViewModel>> AddZoneAsync(Guid id, AddZoneRequest request, CancellationToken ct = default);

    Task<Result<ZoneViewModel>> UpdateZoneAsync(Guid id, Guid zoneId, UpdateZoneRequest request, CancellationToken ct = default);

    Task<Result<bool>> RemoveZoneAsync(Guid id, Guid zoneId, CancellationToken ct = default);

    Task<Result<LineViewModel>> AddLineAsync(Guid id, AddLineRequest request, CancellationToken ct = default);

    Task<Result<bool>> RemoveLineAsync(Guid id, Guid lineId, CancellationToken ct = default);

    Task<Result<ZoneProfileValidationViewModel>> ValidateAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Publishes the draft if its geometry still has the hash the publisher reviewed (from the validation); on problems
    /// returns <see cref="ZoneProfileErrors.NotPublishable"/> followed by them.
    /// </summary>
    Task<Result<ZoneProfileSummaryViewModel>> PublishAsync(Guid id, PublishZoneProfileRequest request, CancellationToken ct = default);

    Task<Result<bool>> DiscardAsync(Guid id, CancellationToken ct = default);
}
