namespace Ariva.Core.Domain.Constants;

/// <summary>
/// The answers of the validation campaign and manual count API (ARV-104a). The service returns one of these texts; the
/// controller maps <see cref="NotFound"/> to 404 (without detail), <see cref="Forbidden"/> to 403, <see cref="Conflicts"/> to
/// 409 and everything else to 400. No text repeats a value of the request (CWE-501).
/// </summary>
public static class ValidationErrors
{
    #region Not found (404)

    /// <summary>A site the caller does not reach or that does not exist, a campaign of another site, or another observer's count: one answer (CWE-204).</summary>
    public const string NotFound = "The site, validation campaign or count does not exist.";

    #endregion

    #region Not allowed (403)

    /// <summary>
    /// Separation of duties (owner decision 2026-10-08): the account that created or started a campaign never counts for it,
    /// also when it holds the Validation observer role as well as a manager role.
    /// </summary>
    public const string OwnCampaign = "You created or started this campaign; its counts come from other observers.";

    /// <summary>Not allowed (403) rather than invalid (400) or conflicting (409).</summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal) { OwnCampaign };

    #endregion

    #region Not valid (400)

    public const string InvalidName = "The name is 1 to 200 characters, without control, invisible or broken characters.";
    public const string InvalidProfileVersion = "The profile version is the number of the site's published zone profile version.";
    public const string InvalidScope = "The scope is 1 to 50 distinct queue zones and at most 200 distinct lines of the profile version; each line belongs to a queue zone in scope or to one of its overflow bands.";
    public const string InvalidDays = "The planned days are 1 to 31 distinct local dates yyyy-MM-dd, from 31 days ago to 366 days ahead in the site's time zone.";
    public const string InvalidTargets = "The targets are 1 to 2,976 bins per line and 0 to 1,000 tracer runs, or empty for the placeholder defaults.";
    public const string InvalidCrossings = "Crossings in and out are both given, whole numbers from 0 to 10,000.";
    public const string InvalidBin = "The bin start is a UTC time on a 15-minute boundary, ISO 8601 ending in Z.";
    public const string BinOutsideCampaign = "The bin does not start on a planned day of the campaign (site local time).";
    public const string BinNotEnded = "The bin has not ended yet; submit it once it has.";
    public const string LineNotInScope = "The line is not in the campaign's scope.";
    public const string InvalidReason = "A correction has a reason of 1 to 200 characters, without control, invisible or broken characters.";
    public const string InvalidIdempotencyKey = "The Idempotency-Key is 8 to 64 letters, digits, dots, underscores, colons or hyphens, starting with a letter or a digit.";
    public const string InvalidStatus = "The status is Planned, Running or Closed.";
    public const string InvalidSort = "Sort campaigns by name, createdUtc, status or firstDay, and counts by binStartUtc or recordedUtc.";
    public const string InvalidRange = "The range is from and to in UTC (ISO 8601 ending in Z), from before to.";
    public const string InvalidText = "The text is at most 64 characters, without control, invisible or broken characters.";

    #endregion

    #region Conflicts (409)

    public const string NotPublished = "The profile version is not the site's published version; read the site's zone profile and try again.";
    public const string ProfileRetired = "The campaign's profile version has been retired; the campaign can only be closed.";
    public const string BinAfterRetirement = "The campaign's profile version was retired before this bin ended.";
    public const string NotStarted = "The campaign has not started.";
    public const string Closed = "The campaign is closed.";
    public const string NotPlanned = "Only a planned campaign is started.";
    public const string AlreadyCaptured = "You have already counted this line and bin; correct that count instead.";
    public const string NotLatest = "A newer revision of this count exists; correct the latest one.";
    public const string TooManyRevisions = "This count has reached the most revisions it may have (100).";
    public const string KeyReused = "The Idempotency-Key was already used for another request.";
    public const string Concurrent = "The campaign or count changed at the same moment; read it and try again.";

    /// <summary>Conflicts (409) rather than invalid requests (400).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal)
    {
        NotPublished, ProfileRetired, BinAfterRetirement, NotStarted, Closed, NotPlanned, AlreadyCaptured, NotLatest, TooManyRevisions, KeyReused, Concurrent
    };

    #endregion
}
