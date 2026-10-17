using System.ComponentModel.DataAnnotations;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// One observer's 15-minute batch of desk states for a validation campaign (ARV-104b; formulas F10, F18): the bin, the
/// Idempotency-Key and the request fingerprint, so a tablet that resends the batch after a dropped connection gets the stored
/// batch back. Its observations are <see cref="DeskObservation"/> rows of revision 1. Created only by its campaign
/// (<see cref="ValidationCampaign.ObserveDesks"/>); evidence, never changed. Desk-level border data (data boundary).
/// </summary>
public class DeskObservationBatch : EntityBase<DeskObservationBatch>, ISiteBound
{
    #region Constants

    /// <summary>Desks in one batch, at most: more than one person can watch minute by minute.</summary>
    public const int MaxDesks = 20;

    /// <summary>Minutes in a bin, so states per desk in a batch.</summary>
    public const int MinutesPerBin = 15;

    public const int MaxObservations = MaxDesks * MinutesPerBin;

    #endregion

    #region Constructors

    protected DeskObservationBatch()
    {
    }

    internal DeskObservationBatch(ValidationCampaign campaign, Guid observerId, DateTime binStartUtc, DateTime receivedUtc, int observations, string idempotencyKey,
        string requestHash)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (observerId == Guid.Empty)
            throw new ArgumentException("A batch is sent by an Ariva user.", nameof(observerId));
        if (!ValidationCampaign.IsBinStart(binStartUtc))
            throw new ArgumentException("A batch is one 15-minute UTC bin.", nameof(binStartUtc));
        if (observations is < 1 or > MaxObservations)
            throw new ArgumentOutOfRangeException(nameof(observations), $"A batch has 1 to {MaxObservations} observations.");
        if (!ManualCount.IsIdempotencyKey(idempotencyKey))
            throw new ArgumentException("A batch has a valid idempotency key.", nameof(idempotencyKey));
        if (requestHash is not { Length: 64 } || !requestHash.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("The request hash is lower-case hex SHA-256.", nameof(requestHash));

        Id = NewId();
        CampaignId = campaign.Id.GetValueOrDefault();
        SiteCode = campaign.SiteCode;
        ObserverId = observerId;
        BinStartUtc = binStartUtc;
        ReceivedUtc = receivedUtc;
        Observations = observations;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
    }

    #endregion

    #region Properties

    public virtual Guid CampaignId { get; protected set; }
    public virtual string SiteCode { get; protected set; }

    /// <summary>The Ariva user id of the observer: the only person identifier a batch holds.</summary>
    public virtual Guid ObserverId { get; protected set; }

    /// <summary>The 15-minute UTC bin the batch covers (its minutes are the bin's).</summary>
    public virtual DateTime BinStartUtc { get; protected set; }

    [MaxLength(ManualCount.MaxIdempotencyKeyLength)]
    public virtual string IdempotencyKey { get; protected set; }

    /// <summary>The batch's fingerprint (<see cref="RequestFingerprint.OfDeskMinutes"/>), lower-case hex SHA-256.</summary>
    [MaxLength(64)]
    public virtual string RequestHash { get; protected set; }

    public virtual DateTime ReceivedUtc { get; protected set; }

    /// <summary>The observed minutes the batch holds (desks times minutes, those not observed left out).</summary>
    public virtual int Observations { get; protected set; }

    #endregion
}

/// <summary>
/// One observer's state of one border desk for one minute of a validation campaign (ARV-104b; formulas F10, F18; F18's
/// desk-state agreement compares it with the desk's <c>desk_minute</c>, ARV-104f). Revision 1 comes in a
/// <see cref="DeskObservationBatch"/>; a correction is a new row with the next revision, a reason and the revision it
/// corrects, never an edit, so the observer's state for a desk and minute is its highest revision and every earlier one
/// stays. The observer is an Ariva user id, nothing more (data boundary); desk-level border data, read by border roles only.
/// </summary>
public class DeskObservation : EntityBase<DeskObservation>, ISiteBound
{
    #region Constants

    public const int MaxRevisions = 100;
    public const int MaxReasonLength = 200;

    #endregion

    #region Constructors

    protected DeskObservation()
    {
    }

    internal DeskObservation(ValidationCampaign campaign, DeskObservationBatch batch, Guid deskId, DateTime minuteUtc, Guid observerId, int revision,
        ObservedDeskState state, string reason, Guid? correctsId, DateTime recordedUtc, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (observerId == Guid.Empty)
            throw new ArgumentException("A desk state is recorded by an Ariva user.", nameof(observerId));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), "The state is Closed, Idle, Serving or Paused.");
        if (!IsMinute(minuteUtc))
            throw new ArgumentException("A desk state is for a whole UTC minute.", nameof(minuteUtc));
        if (revision is < 1 or > MaxRevisions || (revision == 1) != (reason is null) || (revision == 1) != (correctsId is null) || (revision == 1) != (batch is not null))
            throw new ArgumentException("Revision 1 comes in a batch without reason; a later revision has a reason and corrects one.", nameof(revision));
        if (batch is not null && (idempotencyKey is not null || minuteUtc < batch.BinStartUtc || minuteUtc >= batch.BinStartUtc + ValidationCampaign.BinLength))
            throw new ArgumentException("A batch's state is for a minute of its bin; the batch holds the key.", nameof(minuteUtc));
        if (idempotencyKey is not null && !ManualCount.IsIdempotencyKey(idempotencyKey))
            throw new ArgumentException("The idempotency key is not valid.", nameof(idempotencyKey));

        CampaignId = campaign.Id.GetValueOrDefault();
        SiteCode = campaign.SiteCode;
        BatchId = batch?.Id;
        DeskId = deskId;
        MinuteUtc = minuteUtc;
        ObserverId = observerId;
        Revision = revision;
        State = state;
        Reason = reason;
        CorrectsId = correctsId;
        RecordedUtc = recordedUtc;
        IdempotencyKey = idempotencyKey;
    }

    #endregion

    #region Properties

    /// <summary>The batch of revision 1; null for a correction.</summary>
    public virtual Guid? BatchId { get; protected set; }

    public virtual Guid CampaignId { get; protected set; }
    public virtual string SiteCode { get; protected set; }

    /// <summary>The border desk of the campaign's scope.</summary>
    public virtual Guid DeskId { get; protected set; }

    /// <summary>The observed minute (UTC, on the whole minute).</summary>
    public virtual DateTime MinuteUtc { get; protected set; }

    /// <summary>The Ariva user id of the observer: the only person identifier an observation holds.</summary>
    public virtual Guid ObserverId { get; protected set; }

    public virtual int Revision { get; protected set; }
    public virtual ObservedDeskState State { get; protected set; }

    /// <summary>Why the state was corrected; null for revision 1.</summary>
    [MaxLength(MaxReasonLength)]
    public virtual string Reason { get; protected set; }

    /// <summary>The revision this one corrects; null for revision 1.</summary>
    public virtual Guid? CorrectsId { get; protected set; }

    public virtual DateTime RecordedUtc { get; protected set; }

    /// <summary>A correction's own Idempotency-Key (revision 1 carries its batch's).</summary>
    [MaxLength(ManualCount.MaxIdempotencyKeyLength)]
    public virtual string IdempotencyKey { get; protected set; }

    #endregion

    #region Rules

    /// <summary>A UTC instant on a whole minute.</summary>
    public static bool IsMinute(DateTime value) => value.Kind == DateTimeKind.Utc && value.Ticks % TimeSpan.TicksPerMinute == 0;

    /// <summary>A state name as the API takes it: exactly Closed, Idle, Serving or Paused (case-sensitive), or null.</summary>
    public static ObservedDeskState? StateOf(string text) =>
        text switch
        {
            nameof(ObservedDeskState.Closed) => ObservedDeskState.Closed,
            nameof(ObservedDeskState.Idle) => ObservedDeskState.Idle,
            nameof(ObservedDeskState.Serving) => ObservedDeskState.Serving,
            nameof(ObservedDeskState.Paused) => ObservedDeskState.Paused,
            _ => null
        };

    /// <summary>Whether a resent correction with the same key asked for exactly this revision (so it is the answer).</summary>
    public virtual bool IsSameCorrection(Guid campaignId, Guid correctsId, ObservedDeskState state, string reason) =>
        CampaignId == campaignId && CorrectsId == correctsId && State == state && string.Equals(Reason, reason?.Trim(), StringComparison.Ordinal);

    #endregion
}
