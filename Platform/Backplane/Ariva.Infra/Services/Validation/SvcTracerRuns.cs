using System.Linq.Expressions;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Validation;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// Tracer runs (ARV-104b, <see cref="ISvcTracerRuns"/>, script 0048), in ARV-104a's order: the site first (NotFound outside the
/// caller's sites), then the Idempotency-Key (required for a batch) and the request (<see cref="ValidationRules.TracerRuns"/>),
/// then the campaign and whether the caller may capture for it at all (its creator or starter may not, 403, checked before the
/// replay). A key belongs to its observer: the replay looks among the caller's own batches only, so another observer's
/// identical key is a new batch (CWE-863). A resend of the same batch returns the stored one with the offset measured the
/// first time; the same key with another batch is 409. Otherwise the server's receipt time (to the millisecond) and the
/// device's clock reading give the offset, every run is checked on the corrected times
/// (<see cref="ValidationCampaign.TracerBatchProblem"/>, which refuses device times far from the device's clock reading before
/// correcting them), a run already recorded (the exact tracer code and device join time pair) answers 409, as does a batch that
/// would take the observer past <see cref="TracerRun.MaxRunsPerObserver"/> runs in the campaign, and the batch is stored whole
/// or not at all. Script 0048's insert triggers re-check the running campaign under a share lock, so a batch racing a close
/// commits before it or answers 409.
/// </summary>
internal sealed class SvcTracerRuns(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, ReportReader reader)
    : ValidationServiceBase(unitOfWork, currentUser, timeProvider, siteScope, reader), ISvcTracerRuns
{
    #region Capture

    public async Task<Result<CapturedTracerBatchViewModel>> CaptureAsync(string siteCode, Guid campaignId, CaptureTracerRunsRequest request, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } observer)
            return Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.NotFound);
        if (idempotencyKey is null)
            return Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.IdempotencyKeyRequired);
        if (!ManualCount.IsIdempotencyKey(idempotencyKey))
            return Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.InvalidIdempotencyKey);
        request ??= new CaptureTracerRunsRequest(null, null);
        var valid = await ValidationRules.TracerRuns().ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<CapturedTracerBatchViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidRuns);
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.NotFound);
        // Before the replay too: the campaign's creator or starter gets one answer whatever key it sends.
        if (campaign.ObserverProblem(observer) is { } excluded)
            return Result.Error<CapturedTracerBatchViewModel>(excluded);

        var runs = ValidationRules.Runs(request);
        var id = campaign.Id.GetValueOrDefault();
        var fingerprint = RequestFingerprint.OfTracerRuns(id, runs);
        var stored = await QueryAsNoTracking<TracerBatch>().FirstOrDefaultAsync(b => b.ObserverId == observer && b.IdempotencyKey == idempotencyKey, ct);
        if (stored is not null)
        {
            return stored.CampaignId == id && string.Equals(stored.RequestHash, fingerprint, StringComparison.Ordinal)
                ? new Result<CapturedTracerBatchViewModel>(new CapturedTracerBatchViewModel(await BatchViewAsync(campaign, stored, ct), Replayed: true))
                : Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.KeyReused);
        }

        var received = TracerBatch.ToMillisecond(UtcNow);
        var deviceClock = ValidationRules.Instant(request.DeviceClockUtc).GetValueOrDefault();
        var (_, retiredOn) = await ProfileStateAsync(campaign.ProfileId, ct);
        var zone = await TimeZoneAsync(siteCode, ct);
        if (campaign.TracerBatchProblem(runs, observer, deviceClock, received, zone, retiredOn) is { } problem)
            return Result.Error<CapturedTracerBatchViewModel>(problem);
        // One run per observer, tracer code and join time (the device's): a run sent again in another batch is 409. The
        // database compares the exact pairs (at most 20, parameters), so nothing but a duplicate is read.
        if (await QueryAsNoTracking<TracerRun>().Where(r => r.CampaignId == id && r.ObserverId == observer).Where(SameRunAs(runs)).AnyAsync(ct))
            return Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.RunAlreadyRecorded);
        // At most MaxRunsPerObserver runs per observer and campaign (Proposed); two batches sent at once may each pass this
        // count, so the bound can be passed by at most one batch per concurrent request (20 runs each).
        var recordedRuns = await QueryAsNoTracking<TracerRun>().CountAsync(r => r.CampaignId == id && r.ObserverId == observer, ct);
        if (!TracerRun.IsWithinObserverCap(recordedRuns, runs.Count))
            return Result.Error<CapturedTracerBatchViewModel>(ValidationErrors.TooManyRuns);

        var (batch, recorded) = campaign.RecordTracerRuns(runs, observer, deviceClock, received, zone, retiredOn, idempotencyKey);
        await SaveAsync(batch, ct);
        foreach (var run in recorded)
            await SaveAsync(run, ct);
        if (await FlushRefusedAsync(ct) is { } refused)
            return Result.Error<CapturedTracerBatchViewModel>(Refusal(refused));
        return new Result<CapturedTracerBatchViewModel>(new CapturedTracerBatchViewModel(View(campaign, batch, recorded), Replayed: false));
    }

    #endregion

    #region Reads

    public async Task<Result<PageViewModel<TracerRunViewModel>>> OwnRunsAsync(string siteCode, Guid campaignId, TracerRunCriteria criteria, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } caller)
            return Result.Error<PageViewModel<TracerRunViewModel>>(ValidationErrors.NotFound);
        // The caller's own runs only, whatever observer the query names.
        return await RunsAsync(siteCode, campaignId, criteria, caller, ct);
    }

    public async Task<Result<PageViewModel<TracerRunViewModel>>> SearchAsync(string siteCode, Guid campaignId, TracerRunCriteria criteria, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<PageViewModel<TracerRunViewModel>>(ValidationErrors.NotFound);
        return await RunsAsync(siteCode, campaignId, criteria, criteria?.ObserverId, ct);
    }

    /// <summary>
    /// A page of a campaign's runs: in a zone, of an observer, of a tracer code, joined in [FromDate, ToDate) on the server's
    /// clock, sorted by an allowlisted field (CWE-89).
    /// </summary>
    private async Task<Result<PageViewModel<TracerRunViewModel>>> RunsAsync(string siteCode, Guid campaignId, TracerRunCriteria criteria, Guid? observerId,
        CancellationToken ct)
    {
        var campaign = await CampaignAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<PageViewModel<TracerRunViewModel>>(ValidationErrors.NotFound);
        criteria ??= new TracerRunCriteria();
        var valid = await ValidationRules.TracerRunSearch().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<PageViewModel<TracerRunViewModel>>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidSort);

        var id = campaign.Id.GetValueOrDefault();
        var runs = QueryAsNoTracking<TracerRun>().Where(r => r.CampaignId == id && r.SiteCode == campaign.SiteCode);
        if (observerId is { } observer)
            runs = runs.Where(r => r.ObserverId == observer);
        if (criteria.ZoneId is { } zoneId)
            runs = runs.Where(r => r.ZoneId == zoneId);
        if (criteria.TracerCode is { } code)
            runs = runs.Where(r => r.TracerCode == code);
        if (criteria.FromDate is { } from)
            runs = runs.Where(r => r.JoinedUtc >= from);
        if (criteria.ToDate is { } to)
            runs = runs.Where(r => r.JoinedUtc < to);

        var ordered = (criteria.SortBy?.ToLowerInvariant(), criteria.SortDescending) switch
        {
            ("recordedutc", false) => runs.OrderBy(r => r.RecordedUtc).ThenBy(r => r.Id),
            ("recordedutc", true) => runs.OrderByDescending(r => r.RecordedUtc).ThenBy(r => r.Id),
            (_, true) => runs.OrderByDescending(r => r.JoinedUtc).ThenBy(r => r.TracerCode).ThenBy(r => r.Id),
            _ => runs.OrderBy(r => r.JoinedUtc).ThenBy(r => r.TracerCode).ThenBy(r => r.Id)
        };

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await runs.CountAsync(ct);
        var rows = await ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<TracerRunViewModel>>(new PageViewModel<TracerRunViewModel>([.. rows.Select(r => View(campaign, r))], total, pageIndex, pageSize));
    }

    #endregion

    #region Views

    /// <summary>
    /// A run with one of these exact tracer code and device join time pairs: an OR of one equality pair per run, built from the
    /// typed values (NHibernate sends each value as a parameter; no text of the request becomes query text, CWE-89, CWE-94).
    /// </summary>
    internal static Expression<Func<TracerRun, bool>> SameRunAs(IReadOnlyList<TracerRunInput> runs)
    {
        var run = Expression.Parameter(typeof(TracerRun), "run");
        Expression any = Expression.Constant(false);
        foreach (var input in runs ?? [])
        {
            var same = Expression.AndAlso(
                Expression.Equal(Expression.Property(run, nameof(TracerRun.TracerCode)), Expression.Constant(input.TracerCode, typeof(string))),
                Expression.Equal(Expression.Property(run, nameof(TracerRun.JoinedRawUtc)), Expression.Constant(input.JoinedRawUtc, typeof(DateTime))));
            any = any is ConstantExpression ? same : Expression.OrElse(any, same);
        }

        return Expression.Lambda<Func<TracerRun, bool>>(any, run);
    }

    private async Task<TracerBatchViewModel> BatchViewAsync(ValidationCampaign campaign, TracerBatch batch, CancellationToken ct)
    {
        Guid? batchId = batch.Id;
        var runs = await QueryAsNoTracking<TracerRun>().Where(r => r.BatchId == batchId).OrderBy(r => r.Id).ToListAsync(ct);
        return View(campaign, batch, runs);
    }

    private static TracerBatchViewModel View(ValidationCampaign campaign, TracerBatch batch, IReadOnlyList<TracerRun> runs) =>
        new(batch.Id.GetValueOrDefault(), batch.CampaignId, batch.ObserverId, Utc(batch.DeviceClockUtc), Utc(batch.ReceivedUtc), batch.ClockOffsetMs,
            [.. runs.Select(r => View(campaign, r))]);

    private static TracerRunViewModel View(ValidationCampaign campaign, TracerRun run) =>
        new(run.Id.GetValueOrDefault(), run.CampaignId, run.BatchId, run.ZoneId, campaign.ZoneOf(run.ZoneId)?.ZoneName, run.TracerCode, run.ObserverId,
            Utc(run.JoinedUtc), Utc(run.ExitedUtc), Math.Round(run.Wait.TotalSeconds, 3), run.Abandoned, Utc(run.JoinedRawUtc), Utc(run.ExitedRawUtc), run.ClockOffsetMs,
            Utc(run.RecordedUtc));

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// The answer to a refused insert: the campaign closed meanwhile (a script 0048 trigger, 23001), the same key sent twice at
    /// once (send it again for the stored batch), or the same run written concurrently.
    /// </summary>
    private static string Refusal((string State, string Constraint) refused) =>
        refused.State == RestrictViolation ? ValidationErrors.Closed
        : refused.Constraint == "ux_tracer_batch_idempotency" ? ValidationErrors.Concurrent
        : ValidationErrors.RunAlreadyRecorded;

    #endregion
}
