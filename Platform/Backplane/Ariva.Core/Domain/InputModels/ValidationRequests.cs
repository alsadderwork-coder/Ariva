using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// A validation campaign to plan (ARV-104a). The site comes from the route, never the body. <c>ProfileVersion</c> names the
/// site's published zone profile version the creator reviewed (a newer one answers 409); zones and lines are ids of that
/// version (from GET api/v1/admin/zone-profiles/{id}); days are local dates "yyyy-MM-dd" in the site's time zone. A target
/// left empty takes its placeholder default until TC-04 is answered. <c>DeskIds</c> (ARV-104b, optional) are the staffed
/// immigration or emigration desks of the site observers log minute by minute; only a caller who sees border desks names them.
/// </summary>
public sealed record CreateValidationCampaignRequest(
    [MaxLength(ValidationCampaign.MaxNameLength)] string Name,
    int? ProfileVersion,
    [MaxLength(ValidationCampaign.MaxZones)] IReadOnlyList<Guid> ZoneIds,
    [MaxLength(ValidationCampaign.MaxLines)] IReadOnlyList<Guid> LineIds,
    [MaxLength(ValidationCampaign.MaxDays)] IReadOnlyList<string> Days,
    int? TargetBinsPerLine,
    int? TargetTracerRuns,
    [MaxLength(ValidationCampaign.MaxDesks)] IReadOnlyList<Guid> DeskIds = null);

/// <summary>
/// One observer's count of a line for one 15-minute bin (ARV-104a): the bin's start in UTC on the quarter hour ("2026-10-08T06:15:00Z"),
/// and the people tallied crossing the line in and out. The observer is the caller.
/// </summary>
public sealed record CaptureManualCountRequest(
    Guid? LineId,
    [MaxLength(40)] string BinStartUtc,
    int? CrossingsIn,
    int? CrossingsOut);

/// <summary>A correction of one's own count (ARV-104a): the corrected crossings and why. It becomes the next revision; the corrected one stays.</summary>
public sealed record CorrectManualCountRequest(
    int? CrossingsIn,
    int? CrossingsOut,
    [MaxLength(ManualCount.MaxReasonLength)] string Reason);

/// <summary>
/// A batch of tracer runs from the capturing device (ARV-104b). <c>DeviceClockUtc</c> is the device's own clock when it sends
/// the batch, read again at every retry: the server measures the device's offset from it and corrects every run. The
/// Idempotency-Key header is required (new per batch, kept for its retries). The observer is the caller.
/// </summary>
public sealed record CaptureTracerRunsRequest(
    [MaxLength(40)] string DeviceClockUtc,
    [MaxLength(TracerBatch.MaxRuns)] IReadOnlyList<TracerRunRequest> Runs);

/// <summary>
/// One tracer run (ARV-104b): the queue zone in scope, the campaign's tracer label (T-07, never a name), when the tracer joined
/// and left the queue on the device's clock (UTC, ISO 8601 ending in Z, to the millisecond) and whether they left without
/// being served.
/// </summary>
public sealed record TracerRunRequest(
    Guid? ZoneId,
    [MaxLength(TracerRun.MaxTracerCodeLength)] string TracerCode,
    [MaxLength(40)] string JoinedUtc,
    [MaxLength(40)] string ExitedUtc,
    bool? Abandoned);

/// <summary>
/// One observer's 15-minute batch of desk states (ARV-104b): the bin's start in UTC on the quarter hour and, per desk in
/// scope, the state seen in each of the bin's 15 minutes in order (Closed, Idle, Serving or Paused; null for a minute not
/// observed). The Idempotency-Key header is required. The observer is the caller.
/// </summary>
public sealed record CaptureDeskObservationsRequest(
    [MaxLength(40)] string BinStartUtc,
    [MaxLength(DeskObservationBatch.MaxDesks)] IReadOnlyList<DeskMinutesRequest> Desks);

/// <summary>A desk's 15 minutes in a batch: exactly 15 states, null where the minute was not observed.</summary>
public sealed record DeskMinutesRequest(
    Guid? DeskId,
    [MaxLength(DeskObservationBatch.MinutesPerBin)] IReadOnlyList<string> States);

/// <summary>A correction of one's own desk state (ARV-104b): the corrected state and why. It becomes the next revision; the corrected one stays.</summary>
public sealed record CorrectDeskObservationRequest(
    [MaxLength(16)] string State,
    [MaxLength(DeskObservation.MaxReasonLength)] string Reason);

/// <summary>
/// A recomputation of a closed campaign's frozen results (ARV-104g): why they are computed again (1 to
/// <see cref="ViewModels.ValidationResultsViewModel.MaxReasonLength"/> characters, stored with the new revision and audited). The
/// earlier revisions stay as they are; the new one is the next revision.
/// </summary>
public sealed record RecomputeValidationResultsRequest(
    [MaxLength(ViewModels.ValidationResultsViewModel.MaxReasonLength)] string Reason);
