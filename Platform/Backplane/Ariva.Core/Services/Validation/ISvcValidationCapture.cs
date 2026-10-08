using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Validation;

/// <summary>
/// Manual count capture (ARV-104a): the observer's side of a validation campaign. It lists the running campaigns of a site
/// with their lines, records the caller's count of a line for one 15-minute bin (one per line, bin and observer), corrects
/// the caller's own counts as new revisions with a reason, and lists the caller's own counts; never anyone else's. The
/// observer is the caller's Ariva user id. A site the caller does not reach, a campaign of another site and another
/// observer's count answer NotFound (CWE-863, CWE-204). An optional Idempotency-Key returns the stored count when a
/// request is sent again.
/// </summary>
public interface ISvcValidationCapture : ISvcScoped
{
    /// <summary>The running campaigns of a site, at most this many.</summary>
    const int MaxRunningListed = 50;

    Task<Result<IReadOnlyList<CaptureCampaignViewModel>>> RunningAsync(string siteCode, CancellationToken ct = default);

    Task<Result<PageViewModel<ManualCountViewModel>>> OwnCountsAsync(string siteCode, Guid campaignId, ManualCountCriteria criteria, CancellationToken ct = default);

    Task<Result<CapturedCountViewModel>> CaptureAsync(string siteCode, Guid campaignId, CaptureManualCountRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<Result<CapturedCountViewModel>> CorrectAsync(string siteCode, Guid campaignId, Guid countId, CorrectManualCountRequest request, string idempotencyKey,
        CancellationToken ct = default);
}
