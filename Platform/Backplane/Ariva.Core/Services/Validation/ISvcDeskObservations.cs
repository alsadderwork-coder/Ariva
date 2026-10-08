using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Validation;

/// <summary>
/// Observer logs of border desk states (ARV-104b; formulas F10, F18). The observer's side records one 15-minute batch of
/// per-minute states for desks in the campaign's scope (an Idempotency-Key per batch that belongs to its observer: a resend
/// returns the stored batch, the key with another batch is 409; one state per desk, minute and observer, a second is 409),
/// corrects its own states as new revisions with a reason (audited), and reads its own. A manager reads every observer's
/// states only when it also sees border desks (<c>BorderDesks.View</c>): desk-level data is border data (data boundary), so a
/// terminal duty manager gets an empty page marked <c>DesksIncluded</c> false; the observer's batch, correction and own read
/// answer 403 (<see cref="Domain.Constants.ValidationErrors.DeskObservationsNeedBorderRole"/>) to an account that holds an
/// airport role without <c>BorderDesks.View</c>, even with the Validation observer role (<see cref="Security.BorderDeskAccess"/>,
/// one rule for every desk path). Same rules as manual counts: a site the caller
/// does not reach, a campaign of another site and another observer's observation answer NotFound (CWE-863, CWE-204), the
/// campaign's creator or starter is refused (403), only a running campaign takes states (409, also when a close races).
/// </summary>
public interface ISvcDeskObservations : ISvcScoped
{
    Task<Result<CapturedDeskBatchViewModel>> CaptureAsync(string siteCode, Guid campaignId, CaptureDeskObservationsRequest request, string idempotencyKey,
        CancellationToken ct = default);

    Task<Result<CapturedDeskObservationViewModel>> CorrectAsync(string siteCode, Guid campaignId, Guid observationId, CorrectDeskObservationRequest request,
        string idempotencyKey, CancellationToken ct = default);

    Task<Result<PageViewModel<DeskObservationViewModel>>> OwnObservationsAsync(string siteCode, Guid campaignId, DeskObservationCriteria criteria,
        CancellationToken ct = default);

    Task<Result<DeskObservationPageViewModel>> SearchAsync(string siteCode, Guid campaignId, DeskObservationCriteria criteria, CancellationToken ct = default);
}
