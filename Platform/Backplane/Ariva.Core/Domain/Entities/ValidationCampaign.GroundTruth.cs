using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// Tracer runs and desk observations of a validation campaign (ARV-104b; formulas F10, F18, F19). Same rules as manual counts
/// (ARV-104a): the campaign runs, the observer neither created nor started it (separation of duties), the time is on a planned
/// local day, has ended (within <see cref="ClockTolerance"/>) and ended before the profile version was retired; the zone or
/// desk is in scope. A tracer batch is corrected to the server's clock by the offset measured at receipt
/// (<see cref="TracerBatch.MeasureOffset"/>); a desk batch is one observer's 15-minute bin.
/// </summary>
public partial class ValidationCampaign
{
    #region Scope

    /// <summary>The queue zone in scope with this id, or null.</summary>
    public virtual ValidationCampaignZone ZoneOf(Guid zoneId) => Zones.FirstOrDefault(z => z.ZoneId == zoneId);

    /// <summary>The border desk in scope with this id, or null.</summary>
    public virtual ValidationCampaignDesk DeskOf(Guid deskId) => Desks.FirstOrDefault(d => d.DeskId == deskId);

    /// <summary>
    /// Why the desks are not a valid scope for a campaign of <paramref name="siteCode"/>, or null: at most
    /// <see cref="MaxDesks"/> distinct ids, each one of <paramref name="desks"/> and a border desk of the site
    /// (<see cref="IsBorderDesk"/>). None is valid (a campaign without desk observations).
    /// </summary>
    public static string DeskScopeProblem(string siteCode, IReadOnlyCollection<Guid> deskIds, IReadOnlyCollection<Desk> desks)
    {
        if (deskIds is null || deskIds.Count == 0)
            return null;
        if (deskIds.Count > MaxDesks || deskIds.Contains(Guid.Empty) || deskIds.Distinct().Count() != deskIds.Count || desks is null)
            return ValidationErrors.InvalidDesks;
        foreach (var id in deskIds)
        {
            var desk = desks.FirstOrDefault(d => d?.Id == id);
            if (desk is null || desk.SiteCode != siteCode || !IsBorderDesk(desk))
                return ValidationErrors.InvalidDesks;
        }

        return null;
    }

    /// <summary>
    /// A desk whose state an observer logs: a staffed immigration or emigration desk (not an e-gate, not an airport counter or
    /// lane), in service, and neither it nor its checkpoint deleted. Its state is border data (data boundary).
    /// </summary>
    public static bool IsBorderDesk(Desk desk) =>
        desk is { Kind: DeskKind.Desk, IsDeleted: false, InService: true, Checkpoint: { IsBorder: true, IsDeleted: false } };

    #endregion

    #region Tracer runs

    /// <summary>
    /// Why the run of <paramref name="observerId"/> in <paramref name="zoneId"/>, joined and exited at these server times, cannot
    /// be recorded now, or null: the observer may capture for this campaign (<see cref="ObserverProblem"/>), the campaign runs,
    /// the zone is in scope, the run lasts more than nothing and at most <see cref="TracerRun.MaxDuration"/>, the tracer joined on
    /// a planned local day, the exit is not in the future beyond <see cref="ClockTolerance"/>, and it was before the profile
    /// version was retired (when it was).
    /// </summary>
    public virtual string TracerRunProblem(Guid zoneId, DateTime joinedUtc, DateTime exitedUtc, Guid observerId, DateTime nowUtc, TimeZoneInfo siteZone,
        DateTime? profileRetiredUtc)
    {
        ArgumentNullException.ThrowIfNull(siteZone);
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (RunningProblem() is { } running)
            return running;
        if (ZoneOf(zoneId) is null)
            return ValidationErrors.ZoneNotInScope;
        if (joinedUtc.Kind != DateTimeKind.Utc || exitedUtc.Kind != DateTimeKind.Utc || !TracerRun.IsDuration(joinedUtc, exitedUtc))
            return ValidationErrors.InvalidRunTimes;
        if (!Days.Contains(LocalDay(joinedUtc, siteZone)))
            return ValidationErrors.RunOutsideCampaign;
        if (exitedUtc > nowUtc + ClockTolerance)
            return ValidationErrors.TimeInFuture;
        if (profileRetiredUtc is { } retired && exitedUtc > retired)
            return ValidationErrors.TimeAfterRetirement;
        return null;
    }

    /// <summary>
    /// Why a tracer batch from <paramref name="observerId"/> cannot be recorded now, or null: the device's offset is within
    /// <see cref="TracerBatch.MaxClockOffset"/>, every run's device times are near the device's clock reading
    /// (<see cref="TracerBatch.IsNearDeviceClock"/>, checked before they are corrected) and every run, corrected by the offset,
    /// passes <see cref="TracerRunProblem"/>. The first problem found is the answer (the batch is all or nothing).
    /// </summary>
    public virtual string TracerBatchProblem(IReadOnlyList<TracerRunInput> runs, Guid observerId, DateTime deviceClockUtc, DateTime receivedUtc, TimeZoneInfo siteZone,
        DateTime? profileRetiredUtc)
    {
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (!IsTracerBatch(runs))
            return ValidationErrors.InvalidRuns;
        if (TracerBatch.MeasureOffset(deviceClockUtc, receivedUtc) is not { } offset)
            return ValidationErrors.ClockOffsetTooLarge;
        foreach (var run in runs)
        {
            if (!TracerRun.IsTracerCode(run.TracerCode))
                return ValidationErrors.InvalidTracerCode;
            // Before the correction: a well-formed but absurd time (0001-01-01, 9999-12-31) would leave DateTime's range.
            if ((DeviceClockProblem(run.JoinedRawUtc, deviceClockUtc) ?? DeviceClockProblem(run.ExitedRawUtc, deviceClockUtc)) is { } far)
                return far;
            if (TracerRunProblem(run.ZoneId, TracerBatch.ToServerTime(run.JoinedRawUtc, offset), TracerBatch.ToServerTime(run.ExitedRawUtc, offset), observerId,
                    receivedUtc, siteZone, profileRetiredUtc) is { } problem)
                return problem;
        }

        return null;
    }

    /// <summary>
    /// Records a tracer batch: the batch with the measured offset, and one run per input corrected to the server's clock;
    /// <see cref="TracerBatchProblem"/> must be null. Times are cut to the millisecond before this is called.
    /// </summary>
    public virtual (TracerBatch Batch, IReadOnlyList<TracerRun> Runs) RecordTracerRuns(IReadOnlyList<TracerRunInput> runs, Guid observerId, DateTime deviceClockUtc,
        DateTime receivedUtc, TimeZoneInfo siteZone, DateTime? profileRetiredUtc, string idempotencyKey)
    {
        RequireUtc(receivedUtc, nameof(receivedUtc));
        if (IsTransient)
            throw new InvalidOperationException("Save the campaign before its tracer runs.");
        if (TracerBatchProblem(runs, observerId, deviceClockUtc, receivedUtc, siteZone, profileRetiredUtc) is { } problem)
            throw new InvalidOperationException(problem);
        var offset = TracerBatch.MeasureOffset(deviceClockUtc, receivedUtc).GetValueOrDefault();
        var batch = new TracerBatch(this, observerId, deviceClockUtc, receivedUtc, offset, runs.Count, idempotencyKey,
            RequestFingerprint.OfTracerRuns(Id.GetValueOrDefault(), runs));
        return (batch, [.. runs.Select(run => new TracerRun(batch, run, receivedUtc))]);
    }

    /// <summary>
    /// Why a run's time on the device's clock is refused before it is corrected, or null when it is near the device's clock
    /// reading (<see cref="TracerBatch.IsNearDeviceClock"/>). The answers are the rules the corrected time would break: more than
    /// <see cref="TracerBatch.MaxAheadOfDeviceClock"/> ahead is in the future (the corrected time minus the receipt equals the
    /// device time minus its clock reading), more than <see cref="TracerBatch.MaxBehindDeviceClock"/> behind is before any day
    /// a campaign may plan.
    /// </summary>
    private static string DeviceClockProblem(DateTime deviceUtc, DateTime deviceClockUtc) =>
        TracerBatch.IsNearDeviceClock(deviceUtc, deviceClockUtc) ? null
        : deviceUtc.Ticks > deviceClockUtc.Ticks ? ValidationErrors.TimeInFuture
        : ValidationErrors.RunOutsideCampaign;

    /// <summary>1 to <see cref="TracerBatch.MaxRuns"/> runs, none twice (the same tracer code and join time).</summary>
    public static bool IsTracerBatch(IReadOnlyList<TracerRunInput> runs) =>
        runs is { Count: >= 1 and <= TracerBatch.MaxRuns } && runs.All(r => r is not null) &&
        runs.Select(r => (r.TracerCode, r.JoinedRawUtc)).Distinct().Count() == runs.Count;

    #endregion

    #region Desk observations

    /// <summary>
    /// Why <paramref name="observerId"/>'s state of <paramref name="deskId"/> for the minute starting at
    /// <paramref name="minuteUtc"/> cannot be recorded now, or null: the observer may capture for this campaign, the campaign
    /// runs, the desk is in scope, the minute is a whole UTC minute in a bin that starts on a planned local day, it has ended
    /// (within <see cref="ClockTolerance"/>) and ended before the profile version was retired (when it was).
    /// </summary>
    public virtual string DeskObservationProblem(Guid deskId, DateTime minuteUtc, Guid observerId, DateTime nowUtc, TimeZoneInfo siteZone, DateTime? profileRetiredUtc)
    {
        ArgumentNullException.ThrowIfNull(siteZone);
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (RunningProblem() is { } running)
            return running;
        if (DeskOf(deskId) is null)
            return ValidationErrors.DeskNotInScope;
        if (!DeskObservation.IsMinute(minuteUtc))
            return ValidationErrors.InvalidObservations;
        var binStart = new DateTime(minuteUtc.Ticks - (minuteUtc.Ticks % BinLength.Ticks), DateTimeKind.Utc);
        if (!Days.Contains(LocalDay(binStart, siteZone)))
            return ValidationErrors.BinOutsideCampaign;
        var minuteEnd = minuteUtc.AddMinutes(1);
        if (minuteEnd > nowUtc + ClockTolerance)
            return ValidationErrors.TimeInFuture;
        if (profileRetiredUtc is { } retired && minuteEnd > retired)
            return ValidationErrors.TimeAfterRetirement;
        return null;
    }

    /// <summary>
    /// Why a desk batch for the bin starting at <paramref name="binStartUtc"/> cannot be recorded now, or null: a 15-minute UTC
    /// bin, 1 to <see cref="DeskObservationBatch.MaxDesks"/> distinct desks each with exactly
    /// <see cref="DeskObservationBatch.MinutesPerBin"/> states and at least one observed minute in the batch, and every observed
    /// minute passes <see cref="DeskObservationProblem"/> (the first problem found is the answer: all or nothing).
    /// </summary>
    public virtual string DeskBatchProblem(DateTime binStartUtc, IReadOnlyList<DeskMinutesInput> desks, Guid observerId, DateTime nowUtc, TimeZoneInfo siteZone,
        DateTime? profileRetiredUtc)
    {
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (!IsBinStart(binStartUtc))
            return ValidationErrors.InvalidBin;
        if (!IsDeskBatch(desks))
            return ValidationErrors.InvalidObservations;
        foreach (var (deskId, minuteUtc, _) in Observed(binStartUtc, desks))
        {
            if (DeskObservationProblem(deskId, minuteUtc, observerId, nowUtc, siteZone, profileRetiredUtc) is { } problem)
                return problem;
        }

        return null;
    }

    /// <summary>
    /// Records an observer's desk batch: the batch and one revision-1 observation per observed minute;
    /// <see cref="DeskBatchProblem"/> must be null.
    /// </summary>
    public virtual (DeskObservationBatch Batch, IReadOnlyList<DeskObservation> Observations) ObserveDesks(DateTime binStartUtc, IReadOnlyList<DeskMinutesInput> desks,
        Guid observerId, DateTime nowUtc, TimeZoneInfo siteZone, DateTime? profileRetiredUtc, string idempotencyKey)
    {
        RequireUtc(nowUtc, nameof(nowUtc));
        if (IsTransient)
            throw new InvalidOperationException("Save the campaign before its desk observations.");
        if (DeskBatchProblem(binStartUtc, desks, observerId, nowUtc, siteZone, profileRetiredUtc) is { } problem)
            throw new InvalidOperationException(problem);
        var observed = Observed(binStartUtc, desks).ToList();
        var batch = new DeskObservationBatch(this, observerId, binStartUtc, nowUtc, observed.Count, idempotencyKey,
            RequestFingerprint.OfDeskMinutes(Id.GetValueOrDefault(), binStartUtc, desks));
        return (batch, [.. observed.Select(o => new DeskObservation(this, batch, o.DeskId, o.MinuteUtc, observerId, 1, o.State, null, null, nowUtc, null))]);
    }

    /// <summary>
    /// Why <paramref name="observerId"/> cannot correct <paramref name="current"/> now, or null: it is one of this campaign's
    /// observations and the observer's own (another observer's answers like a missing one), the observer may capture for this
    /// campaign, the campaign runs, and it has fewer than <see cref="DeskObservation.MaxRevisions"/> revisions. Whether it is
    /// the latest revision is the service's check.
    /// </summary>
    public virtual string DeskCorrectionProblem(DeskObservation current, Guid observerId)
    {
        if (current is null || current.CampaignId != Id || current.ObserverId != observerId || observerId == Guid.Empty)
            return ValidationErrors.NotFound;
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (RunningProblem() is { } running)
            return running;
        return current.Revision >= DeskObservation.MaxRevisions ? ValidationErrors.TooManyRevisions : null;
    }

    /// <summary>A correction: the next revision of the same desk, minute and observer with the corrected state and a reason; the corrected one stays.</summary>
    public virtual DeskObservation CorrectDeskObservation(DeskObservation current, Guid observerId, ObservedDeskState state, string reason, DateTime nowUtc,
        string idempotencyKey = null)
    {
        RequireUtc(nowUtc, nameof(nowUtc));
        if (DeskCorrectionProblem(current, observerId) is { } problem)
            throw new InvalidOperationException(problem);
        return new DeskObservation(this, null, current.DeskId, current.MinuteUtc, observerId, current.Revision + 1, state,
            DisplayText.Require(reason, DeskObservation.MaxReasonLength, nameof(reason)), current.Id, nowUtc, idempotencyKey);
    }

    /// <summary>1 to <see cref="DeskObservationBatch.MaxDesks"/> distinct desks, each with exactly 15 states, and at least one minute observed.</summary>
    public static bool IsDeskBatch(IReadOnlyList<DeskMinutesInput> desks) =>
        desks is { Count: >= 1 and <= DeskObservationBatch.MaxDesks } &&
        desks.All(d => d is { States.Count: DeskObservationBatch.MinutesPerBin } && d.DeskId != Guid.Empty) &&
        desks.Select(d => d.DeskId).Distinct().Count() == desks.Count &&
        desks.Any(d => d.States.Any(s => s is not null));

    /// <summary>The observed minutes of a batch: each desk's non-null states at the bin's minutes, in the order sent.</summary>
    public static IEnumerable<(Guid DeskId, DateTime MinuteUtc, ObservedDeskState State)> Observed(DateTime binStartUtc, IReadOnlyList<DeskMinutesInput> desks)
    {
        foreach (var desk in desks ?? [])
        {
            for (var minute = 0; minute < (desk?.States?.Count ?? 0); minute++)
            {
                if (desk.States[minute] is { } state)
                    yield return (desk.DeskId, binStartUtc.AddMinutes(minute), state);
            }
        }
    }

    #endregion
}
