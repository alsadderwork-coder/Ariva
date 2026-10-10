using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Validation;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// Manual count capture (ARV-104a, <see cref="ISvcValidationCapture"/>, script 0047). The site comes first (NotFound outside the
/// caller's sites), then the Idempotency-Key and the request (<see cref="ValidationRules"/>), then the campaign, whether the
/// caller may count for it at all (the account that created or started it may not, 403: separation of duties, owner
/// decision 2026-10-08) and its rules (<see cref="ValidationCampaign.CaptureProblem"/>). One count per line, bin and
/// observer: a second answers 409 (the unique index decides a concurrent pair); a correction is the next revision of the
/// caller's own latest count, never an edit, and is audited. An Idempotency-Key belongs to the observer who sent it: the
/// replay looks among the caller's own counts only, so another observer's identical key is a new request (CWE-863). A count
/// lands only in a running campaign: the insert trigger re-checks under a share lock, so a count racing a close either
/// commits before the close or answers 409. The running list names a campaign's border desks only to a caller who may observe
/// them (<see cref="Ariva.Core.Security.BorderDeskAccess"/>, ARV-104b).
/// </summary>
internal sealed class SvcValidationCapture(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit,
    ReportReader reader, CallerRoles callerRoles)
    : ValidationServiceBase(unitOfWork, currentUser, timeProvider, siteScope, reader), ISvcValidationCapture
{
    #region Reads

    public async Task<Result<IReadOnlyList<CaptureCampaignViewModel>>> RunningAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<IReadOnlyList<CaptureCampaignViewModel>>(ValidationErrors.NotFound);
        var running = await QueryAsNoTracking<ValidationCampaign>()
            .Where(c => c.SiteCode == siteCode && c.Status == ValidationCampaignStatus.Running)
            .OrderByDescending(c => c.StartedUtc).ThenBy(c => c.Id)
            .Take(ISvcValidationCapture.MaxRunningListed)
            .ToListAsync(ct);
        var zone = await TimeZoneAsync(siteCode, ct);
        // Border data: the desks to log only for a caller who may observe them (BorderDeskAccess, CWE-863, data boundary); an
        // account with an airport role and no BorderDesks.View sees none, even with the observer role.
        var desksIncluded = (await DeskAccessAsync(callerRoles, ct)).Observes;
        return new Result<IReadOnlyList<CaptureCampaignViewModel>>(
        [
            .. running.Select(c => new CaptureCampaignViewModel(c.Id.GetValueOrDefault(), c.SiteCode, c.Name, zone.Id, [.. c.Days.Select(ValidationCampaign.FormatDay)],
                (int)ValidationCampaign.BinLength.TotalMinutes,
                [.. c.Lines.OrderBy(l => l.LineName, StringComparer.Ordinal).Select(l => new CaptureLineViewModel(l.LineId, l.LineName, l.LineRole.ToString(), l.QueueZoneName))],
                [.. c.Zones.OrderBy(z => z.ZoneName, StringComparer.Ordinal).Select(z => new CaptureZoneViewModel(z.ZoneId, z.ZoneName))],
                desksIncluded,
                desksIncluded
                    ? [.. c.Desks.OrderBy(d => d.CheckpointCode, StringComparer.Ordinal).ThenBy(d => d.DeskCode, StringComparer.Ordinal)
                        .Select(d => new CaptureDeskViewModel(d.DeskId, d.CheckpointCode, d.DeskCode))]
                    : [],
                (int)TracerBatch.MaxClockOffset.TotalSeconds))
        ]);
    }

    public async Task<Result<PageViewModel<ManualCountViewModel>>> OwnCountsAsync(string siteCode, Guid campaignId, ManualCountCriteria criteria, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } caller)
            return Result.Error<PageViewModel<ManualCountViewModel>>(ValidationErrors.NotFound);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<PageViewModel<ManualCountViewModel>>(ValidationErrors.NotFound);
        criteria ??= new ManualCountCriteria();
        var valid = await ValidationRules.Counts().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<PageViewModel<ManualCountViewModel>>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidSort);
        // The caller's own counts only, whatever observer the query names.
        return new Result<PageViewModel<ManualCountViewModel>>(await CountsAsync(campaign, criteria, caller, ct));
    }

    #endregion

    #region Capture and correction

    public async Task<Result<CapturedCountViewModel>> CaptureAsync(string siteCode, Guid campaignId, CaptureManualCountRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } observer)
            return Result.Error<CapturedCountViewModel>(ValidationErrors.NotFound);
        if (idempotencyKey is not null && !ManualCount.IsIdempotencyKey(idempotencyKey))
            return Result.Error<CapturedCountViewModel>(ValidationErrors.InvalidIdempotencyKey);
        request ??= new CaptureManualCountRequest(null, null, null, null);
        var valid = await ValidationRules.Capture().ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<CapturedCountViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidBin);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<CapturedCountViewModel>(ValidationErrors.NotFound);
        // Before the replay too: the campaign's creator or starter gets one answer whatever key it sends.
        if (campaign.ObserverProblem(observer) is { } excluded)
            return Result.Error<CapturedCountViewModel>(excluded);

        var lineId = request.LineId.GetValueOrDefault();
        var binStart = ValidationRules.BinStart(request.BinStartUtc).GetValueOrDefault();
        var crossingsIn = request.CrossingsIn.GetValueOrDefault();
        var crossingsOut = request.CrossingsOut.GetValueOrDefault();
        if (idempotencyKey is not null && await ReplayAsync(observer, idempotencyKey, ct) is { } stored)
        {
            return stored.IsSameRequest(campaign.Id.GetValueOrDefault(), lineId, binStart, crossingsIn, crossingsOut, null, null)
                ? new Result<CapturedCountViewModel>(new CapturedCountViewModel(View(campaign, stored, await IsCurrentAsync(stored, ct)), Replayed: true))
                : Result.Error<CapturedCountViewModel>(ValidationErrors.KeyReused);
        }

        var (_, retiredOn) = await ProfileStateAsync(campaign.ProfileId, ct);
        var zone = await TimeZoneAsync(siteCode, ct);
        if (campaign.CaptureProblem(lineId, binStart, observer, UtcNow, zone, retiredOn) is { } problem)
            return Result.Error<CapturedCountViewModel>(problem);
        var id = campaign.Id.GetValueOrDefault();
        if (await QueryAsNoTracking<ManualCount>().AnyAsync(c => c.CampaignId == id && c.LineId == lineId && c.BinStartUtc == binStart && c.ObserverId == observer, ct))
            return Result.Error<CapturedCountViewModel>(ValidationErrors.AlreadyCaptured);

        var count = campaign.Capture(lineId, binStart, observer, crossingsIn, crossingsOut, UtcNow, zone, retiredOn, idempotencyKey);
        await SaveAsync(count, ct);
        if (await FlushRefusedAsync(ct) is { } refused)
            return Result.Error<CapturedCountViewModel>(Refusal(refused, ValidationErrors.AlreadyCaptured));
        return new Result<CapturedCountViewModel>(new CapturedCountViewModel(View(campaign, count, current: true), Replayed: false));
    }

    public async Task<Result<CapturedCountViewModel>> CorrectAsync(string siteCode, Guid campaignId, Guid countId, CorrectManualCountRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } observer)
            return Result.Error<CapturedCountViewModel>(ValidationErrors.NotFound);
        if (idempotencyKey is not null && !ManualCount.IsIdempotencyKey(idempotencyKey))
            return Result.Error<CapturedCountViewModel>(ValidationErrors.InvalidIdempotencyKey);
        request ??= new CorrectManualCountRequest(null, null, null);
        var valid = await ValidationRules.Correct().ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<CapturedCountViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidReason);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null || countId == Guid.Empty)
            return Result.Error<CapturedCountViewModel>(ValidationErrors.NotFound);

        var id = campaign.Id.GetValueOrDefault();
        var current = await Query<ManualCount>().FirstOrDefaultAsync(c => c.Id == countId && c.CampaignId == id && c.SiteCode == siteCode, ct);
        // Another observer's count answers like a missing one, also to a resent request.
        if (current is null || current.ObserverId != observer)
            return Result.Error<CapturedCountViewModel>(ValidationErrors.NotFound);
        if (campaign.ObserverProblem(observer) is { } excluded)
            return Result.Error<CapturedCountViewModel>(excluded);
        var crossingsIn = request.CrossingsIn.GetValueOrDefault();
        var crossingsOut = request.CrossingsOut.GetValueOrDefault();
        if (idempotencyKey is not null && await ReplayAsync(observer, idempotencyKey, ct) is { } stored)
        {
            return stored.IsSameRequest(id, current.LineId, current.BinStartUtc, crossingsIn, crossingsOut, countId, request.Reason)
                ? new Result<CapturedCountViewModel>(new CapturedCountViewModel(View(campaign, stored, await IsCurrentAsync(stored, ct)), Replayed: true))
                : Result.Error<CapturedCountViewModel>(ValidationErrors.KeyReused);
        }

        if (campaign.CorrectionProblem(current, observer) is { } problem)
            return Result.Error<CapturedCountViewModel>(problem);
        if (!await IsCurrentAsync(current, ct))
            return Result.Error<CapturedCountViewModel>(ValidationErrors.NotLatest);

        var corrected = campaign.Correct(current, observer, crossingsIn, crossingsOut, request.Reason, UtcNow, idempotencyKey);
        await SaveAsync(corrected, ct);
        if (await FlushRefusedAsync(ct) is { } refused)
            return Result.Error<CapturedCountViewModel>(Refusal(refused, ValidationErrors.NotLatest));
        await audit.RecordAsync(CorrectedAction, nameof(ManualCount), corrected.Id, siteCode, AuditSummary(campaign, current), AuditSummary(campaign, corrected), ct);
        await FlushAsync(ct);
        return new Result<CapturedCountViewModel>(new CapturedCountViewModel(View(campaign, corrected, current: true), Replayed: false));
    }

    #endregion

    #region Helpers

    /// <summary>Stable audit action code; never rename it once shipped.</summary>
    internal const string CorrectedAction = "ManualCount.Corrected";

    /// <summary>
    /// The count the caller already made under this key, if any (in any campaign: a key names one request). Only the caller's
    /// own counts: a key is unique per observer (ux_manual_count_idempotency on observer_id and the key), so another observer
    /// sending the same key makes a count of its own and never reads or replays this one (CWE-863).
    /// </summary>
    private Task<ManualCount> ReplayAsync(Guid observer, string key, CancellationToken ct) =>
        QueryAsNoTracking<ManualCount>().FirstOrDefaultAsync(c => c.ObserverId == observer && c.IdempotencyKey == key, ct);

    private async Task<bool> IsCurrentAsync(ManualCount count, CancellationToken ct)
    {
        Guid? id = count.Id;
        return !await QueryAsNoTracking<ManualCount>().AnyAsync(n => n.CorrectsId == id, ct);
    }

    /// <summary>
    /// The answer to a refused insert: the campaign closed meanwhile (the trigger, 23001), the same key sent twice at once
    /// (send it again for the stored count), or the same line, bin and revision written concurrently.
    /// </summary>
    private static string Refusal((string State, string Constraint) refused, string duplicate) =>
        refused.State == RestrictViolation ? ValidationErrors.Closed
        : refused.Constraint == "ux_manual_count_idempotency" ? ValidationErrors.Concurrent
        : duplicate;

    #endregion
}
