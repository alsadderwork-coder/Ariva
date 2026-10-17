namespace Ariva.Core.Domain.Entities;

/// <summary>
/// One observer's manual count of one line for one 15-minute bin of a validation campaign (ARV-104a, F18 N_manual): the
/// crossings in and out the observer tallied. Created only by its campaign (<see cref="ValidationCampaign.Capture"/> and
/// <see cref="ValidationCampaign.Correct"/>). A count is evidence and never changes: a correction is a new row with the next
/// revision, a reason and the revision it corrects; the current count of a line, bin and observer is its highest revision.
/// The observer is an Ariva user id, nothing more (data boundary). The optional idempotency key lets a tablet resend a bin
/// after a dropped connection and get the stored count back (ARV-104c).
/// </summary>
public class ManualCount : EntityBase<ManualCount>, ISiteBound
{
    #region Constants

    /// <summary>People crossing one line in 15 minutes, at most (more than 11 a second).</summary>
    public const int MaxCrossings = 10_000;

    public const int MaxRevisions = 100;
    public const int MaxReasonLength = 200;
    public const int MinIdempotencyKeyLength = 8;
    public const int MaxIdempotencyKeyLength = 64;

    #endregion

    #region Constructors

    protected ManualCount()
    {
    }

    internal ManualCount(ValidationCampaign campaign, Guid lineId, DateTime binStartUtc, Guid observerId, int revision, int crossingsIn, int crossingsOut,
        string reason, Guid? correctsId, DateTime recordedUtc, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (observerId == Guid.Empty)
            throw new ArgumentException("A count is recorded by an Ariva user.", nameof(observerId));
        if (!AreCrossings(crossingsIn, crossingsOut))
            throw new ArgumentOutOfRangeException(nameof(crossingsIn), $"Crossings are from 0 to {MaxCrossings}.");
        if (revision is < 1 or > MaxRevisions || (revision == 1) != (reason is null) || (revision == 1) != (correctsId is null))
            throw new ArgumentException("Revision 1 has no reason and corrects nothing; a later revision has both.", nameof(revision));
        if (idempotencyKey is not null && !IsIdempotencyKey(idempotencyKey))
            throw new ArgumentException("The idempotency key is not valid.", nameof(idempotencyKey));

        CampaignId = campaign.Id.GetValueOrDefault();
        SiteCode = campaign.SiteCode;
        LineId = lineId;
        BinStartUtc = binStartUtc;
        ObserverId = observerId;
        Revision = revision;
        CrossingsIn = crossingsIn;
        CrossingsOut = crossingsOut;
        Reason = reason;
        CorrectsId = correctsId;
        RecordedUtc = recordedUtc;
        IdempotencyKey = idempotencyKey;
    }

    #endregion

    #region Properties

    public virtual Guid CampaignId { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual Guid LineId { get; protected set; }
    public virtual DateTime BinStartUtc { get; protected set; }

    /// <summary>The Ariva user id of the observer: the only person identifier a count holds.</summary>
    public virtual Guid ObserverId { get; protected set; }

    public virtual int Revision { get; protected set; }
    public virtual int CrossingsIn { get; protected set; }
    public virtual int CrossingsOut { get; protected set; }

    /// <summary>Why the count was corrected; null for revision 1.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(MaxReasonLength)]
    public virtual string Reason { get; protected set; }

    /// <summary>The revision this one corrects; null for revision 1.</summary>
    public virtual Guid? CorrectsId { get; protected set; }

    /// <summary>When the server recorded it (UTC).</summary>
    public virtual DateTime RecordedUtc { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(MaxIdempotencyKeyLength)]
    public virtual string IdempotencyKey { get; protected set; }

    #endregion

    #region Rules

    /// <summary>Whether a resent request with the same key asked for exactly this count (so the stored count is the answer).</summary>
    public virtual bool IsSameRequest(Guid campaignId, Guid lineId, DateTime binStartUtc, int crossingsIn, int crossingsOut, Guid? correctsId, string reason) =>
        CampaignId == campaignId && LineId == lineId && BinStartUtc == binStartUtc && CrossingsIn == crossingsIn && CrossingsOut == crossingsOut &&
        CorrectsId == correctsId && string.Equals(Reason, reason?.Trim(), StringComparison.Ordinal);

    /// <summary>Both crossings given and within 0 to <see cref="MaxCrossings"/>.</summary>
    public static bool AreCrossings(int? crossingsIn, int? crossingsOut) =>
        crossingsIn is >= 0 and <= MaxCrossings && crossingsOut is >= 0 and <= MaxCrossings;

    /// <summary>
    /// An idempotency key as the integration API takes it (script 0026): 8 to 64 ASCII letters, digits, dots, underscores,
    /// colons or hyphens, starting with a letter or a digit.
    /// </summary>
    public static bool IsIdempotencyKey(string key)
    {
        if (key is null || key.Length is < MinIdempotencyKeyLength or > MaxIdempotencyKeyLength || !char.IsAsciiLetterOrDigit(key[0]))
            return false;
        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-'))
                return false;
        }

        return true;
    }

    #endregion
}
