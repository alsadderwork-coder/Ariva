using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Validation;

/// <summary>
/// Timed tracers of a validation campaign (ARV-104b; formulas F18, F19). The observer's side records a batch of runs from the
/// capturing device: the device sends its own clock reading with the batch, the server measures the offset (device minus
/// server, refused beyond 5 minutes) and stores each run's device times, the offset and the corrected times. A batch needs an
/// Idempotency-Key that belongs to its observer: a resend returns the stored batch, the key with another batch is 409. The
/// observer reads its own runs; a manager (<c>Validation.View</c>) reads every observer's. Same rules as manual counts: a site
/// the caller does not reach and a campaign of another site answer NotFound (CWE-863, CWE-204), the campaign's creator or
/// starter is refused (403), only a running campaign takes runs (409, also when a close races the batch).
/// </summary>
public interface ISvcTracerRuns : ISvcScoped
{
    Task<Result<CapturedTracerBatchViewModel>> CaptureAsync(string siteCode, Guid campaignId, CaptureTracerRunsRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<Result<PageViewModel<TracerRunViewModel>>> OwnRunsAsync(string siteCode, Guid campaignId, TracerRunCriteria criteria, CancellationToken ct = default);

    Task<Result<PageViewModel<TracerRunViewModel>>> SearchAsync(string siteCode, Guid campaignId, TracerRunCriteria criteria, CancellationToken ct = default);
}
