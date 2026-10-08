using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// One batch of tracer runs a capturing device sent for a validation campaign (ARV-104b; formulas F18, F19): who sent it (an
/// Ariva user id), its Idempotency-Key and request fingerprint, and the device's clock against the server's at receipt. The
/// device sends its own clock reading with the batch (<see cref="DeviceClockUtc"/>); the server measures
/// <see cref="ClockOffsetMs"/> = device clock minus receipt (F19's sign, to the millisecond, the network delay included) and
/// every run of the batch is corrected by it. A batch whose offset exceeds <see cref="MaxClockOffset"/> is refused. Created
/// only by its campaign (<see cref="ValidationCampaign.RecordTracerRuns"/>); evidence, never changed.
/// </summary>
public class TracerBatch : EntityBase<TracerBatch>, ISiteBound
{
    #region Constants

    /// <summary>Runs in one batch, at most (a tablet sends a run as it ends; a few queued after a dropped connection).</summary>
    public const int MaxRuns = 20;

    /// <summary>
    /// The largest clock offset a batch may have (Proposed, docs/product/decisions.md): a device set by network time is within
    /// seconds; beyond this the device's time is wrong (a manual setting, a time zone mistake) or the reading is stale, and
    /// correcting it would hide the error.
    /// </summary>
    public static readonly TimeSpan MaxClockOffset = TimeSpan.FromMinutes(5);

    public const int MaxClockOffsetMs = 300_000;

    /// <summary>
    /// How far after the device's clock reading a run's time on that clock may be (an exit up to one minute ahead, the clock
    /// tolerance of every capture time). Checked before any correction, so a well-formed but absurd time (9999-12-31) is a 400,
    /// never an overflow (first security review of ARV-104b, CWE-501).
    /// </summary>
    public static readonly TimeSpan MaxAheadOfDeviceClock = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far before the device's clock reading a run's time on that clock may be: 32 days (a join on the earliest planned
    /// day, 31 days back, and a day more). Before any correction, as <see cref="MaxAheadOfDeviceClock"/> (0001-01-01 is a 400).
    /// </summary>
    public static readonly TimeSpan MaxBehindDeviceClock = TimeSpan.FromDays(32);

    #endregion

    #region Constructors

    protected TracerBatch()
    {
    }

    internal TracerBatch(ValidationCampaign campaign, Guid observerId, DateTime deviceClockUtc, DateTime receivedUtc, int clockOffsetMs, int runs,
        string idempotencyKey, string requestHash)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (observerId == Guid.Empty)
            throw new ArgumentException("A batch is sent by an Ariva user.", nameof(observerId));
        if (Math.Abs(clockOffsetMs) > MaxClockOffsetMs || deviceClockUtc - receivedUtc != TimeSpan.FromMilliseconds(clockOffsetMs))
            throw new ArgumentOutOfRangeException(nameof(clockOffsetMs), "The offset is the device clock minus the receipt, within the bound.");
        if (runs is < 1 or > MaxRuns)
            throw new ArgumentOutOfRangeException(nameof(runs), $"A batch has 1 to {MaxRuns} runs.");
        if (!ManualCount.IsIdempotencyKey(idempotencyKey))
            throw new ArgumentException("A batch has a valid idempotency key.", nameof(idempotencyKey));
        if (requestHash is not { Length: 64 } || !requestHash.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("The request hash is lower-case hex SHA-256.", nameof(requestHash));

        Id = NewId();
        CampaignId = campaign.Id.GetValueOrDefault();
        SiteCode = campaign.SiteCode;
        ObserverId = observerId;
        DeviceClockUtc = deviceClockUtc;
        ReceivedUtc = receivedUtc;
        ClockOffsetMs = clockOffsetMs;
        Runs = runs;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
    }

    #endregion

    #region Properties

    public virtual Guid CampaignId { get; protected set; }
    public virtual string SiteCode { get; protected set; }

    /// <summary>The Ariva user id that sent the batch: the only person identifier it holds.</summary>
    public virtual Guid ObserverId { get; protected set; }

    [MaxLength(ManualCount.MaxIdempotencyKeyLength)]
    public virtual string IdempotencyKey { get; protected set; }

    /// <summary>The batch's fingerprint (<see cref="RequestFingerprint.OfTracerRuns"/>), lower-case hex SHA-256.</summary>
    [MaxLength(64)]
    public virtual string RequestHash { get; protected set; }

    /// <summary>The device's clock when it sent the batch, as it said (UTC, to the millisecond).</summary>
    public virtual DateTime DeviceClockUtc { get; protected set; }

    /// <summary>The server's clock when the batch arrived (UTC, to the millisecond).</summary>
    public virtual DateTime ReceivedUtc { get; protected set; }

    /// <summary>Device clock minus server clock, in milliseconds: positive when the device runs ahead.</summary>
    public virtual int ClockOffsetMs { get; protected set; }

    public virtual int Runs { get; protected set; }

    #endregion

    #region Rules

    /// <summary>
    /// The device's offset against the server in whole milliseconds (device clock minus receipt), or null when it exceeds
    /// <see cref="MaxClockOffset"/>. Both instants are UTC and already to the millisecond.
    /// </summary>
    public static int? MeasureOffset(DateTime deviceClockUtc, DateTime receivedUtc)
    {
        if (deviceClockUtc.Kind != DateTimeKind.Utc || receivedUtc.Kind != DateTimeKind.Utc)
            return null;
        var offset = deviceClockUtc - receivedUtc;
        return offset.Duration() <= MaxClockOffset ? (int)offset.TotalMilliseconds : null;
    }

    /// <summary>A UTC instant cut to the whole millisecond (the precision of every stored capture time).</summary>
    public static DateTime ToMillisecond(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    /// <summary>
    /// A time on the device's clock as the server's clock read it: the device time minus the offset. Only for a time within
    /// <see cref="IsNearDeviceClock"/> of a reading the offset was measured from (anything else may leave DateTime's range).
    /// </summary>
    public static DateTime ToServerTime(DateTime deviceUtc, int clockOffsetMs) => DateTime.SpecifyKind(deviceUtc.AddMilliseconds(-clockOffsetMs), DateTimeKind.Utc);

    /// <summary>
    /// Whether a run's time on the device's clock is within <see cref="MaxBehindDeviceClock"/> before and
    /// <see cref="MaxAheadOfDeviceClock"/> after the device's clock reading of its batch. Compared in ticks, so no value of
    /// either argument overflows.
    /// </summary>
    public static bool IsNearDeviceClock(DateTime deviceUtc, DateTime deviceClockUtc)
    {
        var ahead = deviceUtc.Ticks - deviceClockUtc.Ticks;
        return ahead <= MaxAheadOfDeviceClock.Ticks && ahead >= -MaxBehindDeviceClock.Ticks;
    }

    #endregion
}

/// <summary>
/// One timed tracer of a validation campaign (ARV-104b; formulas F18): a member of staff who joined a queue zone in scope and
/// recorded when they joined and left it, with <see cref="Abandoned"/> true when they left without being served. The tracer
/// is a campaign-local label (<see cref="TracerCode"/>, T-01 to T-999), never a name; the observer is an Ariva user id
/// (data boundary). The times are kept as the device's clock read them (<see cref="JoinedRawUtc"/>, <see cref="ExitedRawUtc"/>),
/// with the batch's offset, and corrected to the server's clock (<see cref="JoinedUtc"/>, <see cref="ExitedUtc"/>): the
/// comparison (ARV-104e) uses the corrected ones. Created only by its campaign; evidence, never changed.
/// </summary>
public partial class TracerRun : EntityBase<TracerRun>, ISiteBound
{
    #region Constants

    public const int MaxTracerCodeLength = 5;

    /// <summary>The longest run (Proposed): T_censor (F6, 120 minutes) and an hour more; longer is a forgotten Exit tap.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(3);

    /// <summary>
    /// Runs one observer records for one campaign, at most (Proposed, docs/product/decisions.md; first security review of
    /// ARV-104b): many times the 30-run placeholder target, so a campaign never meets it, while a runaway device or account
    /// cannot grow the table without bound (CWE-770).
    /// </summary>
    public const int MaxRunsPerObserver = 500;

    #endregion

    #region Constructors

    protected TracerRun()
    {
    }

    internal TracerRun(TracerBatch batch, TracerRunInput run, DateTime recordedUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(run);
        if (!IsTracerCode(run.TracerCode))
            throw new ArgumentException("A tracer code is a campaign label, never a name.", nameof(run));
        var joined = TracerBatch.ToServerTime(run.JoinedRawUtc, batch.ClockOffsetMs);
        var exited = TracerBatch.ToServerTime(run.ExitedRawUtc, batch.ClockOffsetMs);
        if (!IsDuration(joined, exited))
            throw new ArgumentOutOfRangeException(nameof(run), "A run ends after it starts and lasts at most three hours.");

        BatchId = batch.Id.GetValueOrDefault();
        CampaignId = batch.CampaignId;
        SiteCode = batch.SiteCode;
        ObserverId = batch.ObserverId;
        ZoneId = run.ZoneId;
        TracerCode = run.TracerCode;
        JoinedRawUtc = run.JoinedRawUtc;
        ExitedRawUtc = run.ExitedRawUtc;
        ClockOffsetMs = batch.ClockOffsetMs;
        JoinedUtc = joined;
        ExitedUtc = exited;
        Abandoned = run.Abandoned;
        RecordedUtc = recordedUtc;
    }

    #endregion

    #region Properties

    public virtual Guid BatchId { get; protected set; }
    public virtual Guid CampaignId { get; protected set; }
    public virtual string SiteCode { get; protected set; }

    /// <summary>The queue zone of the campaign's scope the tracer stood in.</summary>
    public virtual Guid ZoneId { get; protected set; }

    /// <summary>The campaign's label for the tracer (T-07): never a name, staff number or badge.</summary>
    [MaxLength(MaxTracerCodeLength)]
    public virtual string TracerCode { get; protected set; }

    /// <summary>The Ariva user id that recorded the run: the only person identifier it holds.</summary>
    public virtual Guid ObserverId { get; protected set; }

    /// <summary>When the tracer joined, on the capturing device's clock.</summary>
    public virtual DateTime JoinedRawUtc { get; protected set; }

    /// <summary>When the tracer left the queue, on the capturing device's clock.</summary>
    public virtual DateTime ExitedRawUtc { get; protected set; }

    /// <summary>The batch's measured offset (device minus server, milliseconds) these times were corrected by.</summary>
    public virtual int ClockOffsetMs { get; protected set; }

    /// <summary>When the tracer joined, on the server's clock: the raw time minus the offset.</summary>
    public virtual DateTime JoinedUtc { get; protected set; }

    /// <summary>When the tracer left the queue, on the server's clock: the raw time minus the offset.</summary>
    public virtual DateTime ExitedUtc { get; protected set; }

    /// <summary>True when the tracer left the queue without being served (no realised wait to compare, F5).</summary>
    public virtual bool Abandoned { get; protected set; }

    /// <summary>When the server recorded it (UTC).</summary>
    public virtual DateTime RecordedUtc { get; protected set; }

    /// <summary>The tracer's wait: exited minus joined.</summary>
    public virtual TimeSpan Wait => ExitedUtc - JoinedUtc;

    #endregion

    #region Rules

    /// <summary>
    /// A tracer code: T, a hyphen and 2 or 3 ASCII digits (T-07, T-123). A label the campaign hands out, so a name, a staff
    /// number or anything else that could point at a person never fits it. Script 0048 checks the same pattern.
    /// </summary>
    public static bool IsTracerCode(string code) => code is { Length: >= 4 and <= MaxTracerCodeLength } && TracerCodePattern().IsMatch(code);

    /// <summary>Whether <paramref name="adding"/> more runs keep an observer who has <paramref name="recorded"/> within <see cref="MaxRunsPerObserver"/>.</summary>
    public static bool IsWithinObserverCap(int recorded, int adding) => recorded >= 0 && adding >= 0 && recorded <= MaxRunsPerObserver - adding;

    /// <summary>Exited after joined, and at most <see cref="MaxDuration"/> later.</summary>
    public static bool IsDuration(DateTime joinedUtc, DateTime exitedUtc) => exitedUtc > joinedUtc && exitedUtc - joinedUtc <= MaxDuration;

    // \z, not $: in .NET $ also matches before a final line feed. [0-9], not \d: \d matches every Unicode digit.
    [GeneratedRegex(@"^T-[0-9]{2,3}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TracerCodePattern();

    #endregion
}
