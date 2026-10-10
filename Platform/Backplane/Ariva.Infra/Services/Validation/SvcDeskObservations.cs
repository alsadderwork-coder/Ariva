using System.Globalization;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Validation;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// Desk observer logs (ARV-104b, <see cref="ISvcDeskObservations"/>, script 0048), in ARV-104a's order: the site first
/// (NotFound outside the caller's sites), then the Idempotency-Key (required for a batch, optional for a correction) and the
/// request, then the campaign and whether the caller may capture for it at all (its creator or starter may not, 403, checked
/// before any replay). A key belongs to its observer (the replay looks among the caller's own batches and corrections only,
/// CWE-863). One state per desk, minute and observer: a minute already observed answers 409 (the unique index decides a
/// concurrent pair); a correction is the next revision of the caller's own latest state, never an edit, and is audited.
/// Desk-level data is border data, decided by one rule (<see cref="BorderDeskAccess"/>, through
/// <see cref="ValidationServiceBase.DeskAccessAsync"/>) right after the site check and before any row is read: the manager's
/// read answers only a caller who also sees border desks (<c>BorderDesks.View</c>), and an empty page marked
/// <c>DesksIncluded</c> false to anyone else; the observer's batch, correction and own read answer 403 to an account that
/// holds an airport role without <c>BorderDesks.View</c>, even with the Validation observer role (a pure observer, the border's
/// own, keeps them). Script 0048's insert triggers re-check the running campaign under a share lock, so a batch racing a close
/// commits before it or answers 409.
/// </summary>
internal sealed class SvcDeskObservations(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit,
    ReportReader reader, CallerRoles callerRoles)
    : ValidationServiceBase(unitOfWork, currentUser, timeProvider, siteScope, reader), ISvcDeskObservations
{
    #region Capture and correction

    public async Task<Result<CapturedDeskBatchViewModel>> CaptureAsync(string siteCode, Guid campaignId, CaptureDeskObservationsRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } observer)
            return Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.NotFound);
        if (!(await DeskAccessAsync(callerRoles, ct)).Observes)
            return Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.DeskObservationsNeedBorderRole);
        if (idempotencyKey is null)
            return Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.IdempotencyKeyRequired);
        if (!ManualCount.IsIdempotencyKey(idempotencyKey))
            return Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.InvalidIdempotencyKey);
        request ??= new CaptureDeskObservationsRequest(null, null);
        var valid = await ValidationRules.DeskObservations().ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<CapturedDeskBatchViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidObservations);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.NotFound);
        if (campaign.ObserverProblem(observer) is { } excluded)
            return Result.Error<CapturedDeskBatchViewModel>(excluded);

        var binStart = ValidationRules.BinStart(request.BinStartUtc).GetValueOrDefault();
        var desks = ValidationRules.DeskMinutes(request);
        var id = campaign.Id.GetValueOrDefault();
        var fingerprint = RequestFingerprint.OfDeskMinutes(id, binStart, desks);
        var stored = await QueryAsNoTracking<DeskObservationBatch>().FirstOrDefaultAsync(b => b.ObserverId == observer && b.IdempotencyKey == idempotencyKey, ct);
        if (stored is not null)
        {
            return stored.CampaignId == id && string.Equals(stored.RequestHash, fingerprint, StringComparison.Ordinal)
                ? new Result<CapturedDeskBatchViewModel>(new CapturedDeskBatchViewModel(await BatchViewAsync(campaign, stored, ct), Replayed: true))
                : Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.KeyReused);
        }

        var (_, retiredOn) = await ProfileStateAsync(campaign.ProfileId, ct);
        var zone = await TimeZoneAsync(siteCode, ct);
        if (campaign.DeskBatchProblem(binStart, desks, observer, UtcNow, zone, retiredOn) is { } problem)
            return Result.Error<CapturedDeskBatchViewModel>(problem);
        var deskIds = desks.Select(d => d.DeskId).ToList();
        var binEnd = binStart + ValidationCampaign.BinLength;
        var earlier = await QueryAsNoTracking<DeskObservation>()
            .Where(o => o.CampaignId == id && o.ObserverId == observer && o.Revision == 1 && o.MinuteUtc >= binStart && o.MinuteUtc < binEnd && deskIds.Contains(o.DeskId))
            .Select(o => new { o.DeskId, o.MinuteUtc })
            .ToListAsync(ct);
        var observed = ValidationCampaign.Observed(binStart, desks).Select(o => (o.DeskId, o.MinuteUtc)).ToHashSet();
        if (earlier.Any(e => observed.Contains((e.DeskId, DateTime.SpecifyKind(e.MinuteUtc, DateTimeKind.Utc)))))
            return Result.Error<CapturedDeskBatchViewModel>(ValidationErrors.AlreadyObserved);

        var (batch, observations) = campaign.ObserveDesks(binStart, desks, observer, UtcNow, zone, retiredOn, idempotencyKey);
        await SaveAsync(batch, ct);
        foreach (var observation in observations)
            await SaveAsync(observation, ct);
        if (await FlushRefusedAsync(ct) is { } refused)
            return Result.Error<CapturedDeskBatchViewModel>(Refusal(refused, ValidationErrors.AlreadyObserved, "ux_desk_observation_batch_idempotency"));
        return new Result<CapturedDeskBatchViewModel>(new CapturedDeskBatchViewModel(View(campaign, batch, observations), Replayed: false));
    }

    public async Task<Result<CapturedDeskObservationViewModel>> CorrectAsync(string siteCode, Guid campaignId, Guid observationId, CorrectDeskObservationRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } observer)
            return Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.NotFound);
        if (!(await DeskAccessAsync(callerRoles, ct)).Observes)
            return Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.DeskObservationsNeedBorderRole);
        if (idempotencyKey is not null && !ManualCount.IsIdempotencyKey(idempotencyKey))
            return Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.InvalidIdempotencyKey);
        request ??= new CorrectDeskObservationRequest(null, null);
        var valid = await ValidationRules.CorrectDeskObservation().ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<CapturedDeskObservationViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidObservationReason);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null || observationId == Guid.Empty)
            return Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.NotFound);

        var id = campaign.Id.GetValueOrDefault();
        var current = await Query<DeskObservation>().FirstOrDefaultAsync(o => o.Id == observationId && o.CampaignId == id && o.SiteCode == siteCode, ct);
        // Another observer's state answers like a missing one, also to a resent request.
        if (current is null || current.ObserverId != observer)
            return Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.NotFound);
        if (campaign.ObserverProblem(observer) is { } excluded)
            return Result.Error<CapturedDeskObservationViewModel>(excluded);
        var state = DeskObservation.StateOf(request.State).GetValueOrDefault();
        if (idempotencyKey is not null &&
            await QueryAsNoTracking<DeskObservation>().FirstOrDefaultAsync(o => o.ObserverId == observer && o.IdempotencyKey == idempotencyKey, ct) is { } replay)
        {
            return replay.IsSameCorrection(id, observationId, state, request.Reason)
                ? new Result<CapturedDeskObservationViewModel>(new CapturedDeskObservationViewModel(View(campaign, replay, await IsCurrentAsync(replay, ct)), Replayed: true))
                : Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.KeyReused);
        }

        if (campaign.DeskCorrectionProblem(current, observer) is { } problem)
            return Result.Error<CapturedDeskObservationViewModel>(problem);
        if (!await IsCurrentAsync(current, ct))
            return Result.Error<CapturedDeskObservationViewModel>(ValidationErrors.NotLatest);

        var corrected = campaign.CorrectDeskObservation(current, observer, state, request.Reason, UtcNow, idempotencyKey);
        await SaveAsync(corrected, ct);
        if (await FlushRefusedAsync(ct) is { } refused)
            return Result.Error<CapturedDeskObservationViewModel>(Refusal(refused, ValidationErrors.NotLatest, "ux_desk_observation_idempotency"));
        await audit.RecordAsync(CorrectedAction, nameof(DeskObservation), corrected.Id, siteCode, AuditSummary(campaign, current), AuditSummary(campaign, corrected), ct);
        await FlushAsync(ct);
        return new Result<CapturedDeskObservationViewModel>(new CapturedDeskObservationViewModel(View(campaign, corrected, current: true), Replayed: false));
    }

    #endregion

    #region Reads

    public async Task<Result<PageViewModel<DeskObservationViewModel>>> OwnObservationsAsync(string siteCode, Guid campaignId, DeskObservationCriteria criteria,
        CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } caller)
            return Result.Error<PageViewModel<DeskObservationViewModel>>(ValidationErrors.NotFound);
        if (!(await DeskAccessAsync(callerRoles, ct)).Observes)
            return Result.Error<PageViewModel<DeskObservationViewModel>>(ValidationErrors.DeskObservationsNeedBorderRole);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<PageViewModel<DeskObservationViewModel>>(ValidationErrors.NotFound);
        criteria ??= new DeskObservationCriteria();
        var valid = await ValidationRules.DeskObservationSearch().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<PageViewModel<DeskObservationViewModel>>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidSort);
        // The caller's own states only (what it recorded itself), whatever observer the query names.
        return new Result<PageViewModel<DeskObservationViewModel>>(await ObservationsAsync(campaign, criteria, caller, ct));
    }

    public async Task<Result<DeskObservationPageViewModel>> SearchAsync(string siteCode, Guid campaignId, DeskObservationCriteria criteria, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<DeskObservationPageViewModel>(ValidationErrors.NotFound);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<DeskObservationPageViewModel>(ValidationErrors.NotFound);
        criteria ??= new DeskObservationCriteria();
        var valid = await ValidationRules.DeskObservationSearch().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<DeskObservationPageViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidSort);
        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        // Border data (data boundary): decided here, before any row is read, so a duty manager's answer never holds a desk state.
        if (!(await DeskAccessAsync(callerRoles, ct)).Sees)
            return new Result<DeskObservationPageViewModel>(new DeskObservationPageViewModel(false, [], 0, pageIndex, pageSize));
        var page = await ObservationsAsync(campaign, criteria, criteria.ObserverId, ct);
        return new Result<DeskObservationPageViewModel>(new DeskObservationPageViewModel(true, page.Data, page.TotalCount, page.PageIndex, page.PageSize));
    }

    /// <summary>
    /// A page of a campaign's desk states: the current revisions only (no later revision corrects them) or every revision, of a
    /// desk, of an observer, of minutes in [FromDate, ToDate), sorted by an allowlisted field (CWE-89).
    /// </summary>
    private async Task<PageViewModel<DeskObservationViewModel>> ObservationsAsync(ValidationCampaign campaign, DeskObservationCriteria criteria, Guid? observerId,
        CancellationToken ct)
    {
        var campaignId = campaign.Id.GetValueOrDefault();
        var observations = QueryAsNoTracking<DeskObservation>().Where(o => o.CampaignId == campaignId && o.SiteCode == campaign.SiteCode);
        if (observerId is { } observer)
            observations = observations.Where(o => o.ObserverId == observer);
        if (criteria.DeskId is { } deskId)
            observations = observations.Where(o => o.DeskId == deskId);
        if (criteria.FromDate is { } from)
            observations = observations.Where(o => o.MinuteUtc >= from);
        if (criteria.ToDate is { } to)
            observations = observations.Where(o => o.MinuteUtc < to);
        if (criteria.CurrentOnly)
        {
            var corrections = QueryAsNoTracking<DeskObservation>().Where(n => n.CampaignId == campaignId && n.CorrectsId != null);
            observations = observations.Where(o => !corrections.Any(n => n.CorrectsId == o.Id));
        }

        var ordered = (criteria.SortBy?.ToLowerInvariant(), criteria.SortDescending) switch
        {
            ("recordedutc", false) => observations.OrderBy(o => o.RecordedUtc).ThenBy(o => o.Id),
            ("recordedutc", true) => observations.OrderByDescending(o => o.RecordedUtc).ThenBy(o => o.Id),
            (_, true) => observations.OrderByDescending(o => o.MinuteUtc).ThenBy(o => o.DeskId).ThenBy(o => o.ObserverId).ThenBy(o => o.Revision),
            _ => observations.OrderBy(o => o.MinuteUtc).ThenBy(o => o.DeskId).ThenBy(o => o.ObserverId).ThenBy(o => o.Revision)
        };

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await observations.CountAsync(ct);
        var rows = await ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var corrected = new HashSet<Guid>();
        if (!criteria.CurrentOnly && rows.Count > 0)
        {
            var ids = rows.Select(r => (Guid?)r.Id).ToList();
            corrected = (await QueryAsNoTracking<DeskObservation>().Where(n => n.CampaignId == campaignId && ids.Contains(n.CorrectsId))
                .Select(n => n.CorrectsId.Value).ToListAsync(ct)).ToHashSet();
        }

        return new PageViewModel<DeskObservationViewModel>(
            [.. rows.Select(r => View(campaign, r, current: !corrected.Contains(r.Id.GetValueOrDefault())))], total, pageIndex, pageSize);
    }

    #endregion

    #region Helpers

    /// <summary>Stable audit action code; never rename it once shipped.</summary>
    internal const string CorrectedAction = "DeskObservation.Corrected";

    private async Task<DeskObservationBatchViewModel> BatchViewAsync(ValidationCampaign campaign, DeskObservationBatch batch, CancellationToken ct)
    {
        Guid? batchId = batch.Id;
        var observations = await QueryAsNoTracking<DeskObservation>().Where(o => o.BatchId == batchId).ToListAsync(ct);
        return View(campaign, batch, observations);
    }

    private static DeskObservationBatchViewModel View(ValidationCampaign campaign, DeskObservationBatch batch, IReadOnlyList<DeskObservation> observations) =>
        new(batch.Id.GetValueOrDefault(), batch.CampaignId, batch.ObserverId, Utc(batch.BinStartUtc), Utc(batch.ReceivedUtc),
        [
            .. observations.OrderBy(o => campaign.DeskOf(o.DeskId)?.CheckpointCode, StringComparer.Ordinal).ThenBy(o => campaign.DeskOf(o.DeskId)?.DeskCode, StringComparer.Ordinal)
                .ThenBy(o => o.MinuteUtc).Select(o => View(campaign, o, current: true))
        ]);

    private static DeskObservationViewModel View(ValidationCampaign campaign, DeskObservation observation, bool current)
    {
        var desk = campaign.DeskOf(observation.DeskId);
        return new DeskObservationViewModel(observation.Id.GetValueOrDefault(), observation.CampaignId, observation.BatchId, observation.DeskId, desk?.CheckpointCode,
            desk?.DeskCode, Utc(observation.MinuteUtc), observation.ObserverId, observation.Revision, current, observation.State.ToString(), observation.Reason,
            observation.CorrectsId, Utc(observation.RecordedUtc));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task<bool> IsCurrentAsync(DeskObservation observation, CancellationToken ct)
    {
        Guid? id = observation.Id;
        return !await QueryAsNoTracking<DeskObservation>().AnyAsync(n => n.CorrectsId == id, ct);
    }

    /// <summary>What the audit log keeps of a desk state: ids, the desk's codes, the minute, the state and the reason as a JSON string; no names.</summary>
    private static string AuditSummary(ValidationCampaign campaign, DeskObservation observation)
    {
        var desk = campaign.DeskOf(observation.DeskId);
        return string.Create(CultureInfo.InvariantCulture,
            $"campaign={observation.CampaignId}; desk={desk?.CheckpointCode}/{desk?.DeskCode}; minute={observation.MinuteUtc:yyyy-MM-ddTHH:mm}Z; observer={observation.ObserverId}; " +
            $"revision={observation.Revision}; state={observation.State}; reason={System.Text.Json.JsonSerializer.Serialize(observation.Reason)}");
    }

    /// <summary>
    /// The answer to a refused insert: the campaign closed meanwhile (a script 0048 trigger, 23001), the same key sent twice at
    /// once (send it again for the stored answer), or the same desk, minute and revision written concurrently.
    /// </summary>
    private static string Refusal((string State, string Constraint) refused, string duplicate, string keyIndex) =>
        refused.State == RestrictViolation ? ValidationErrors.Closed
        : refused.Constraint == keyIndex ? ValidationErrors.Concurrent
        : duplicate;

    #endregion
}
