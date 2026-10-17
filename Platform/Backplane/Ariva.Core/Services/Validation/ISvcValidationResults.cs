using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Validation;

/// <summary>The fixed texts of the validation results service (ARV-104g2); none repeats a request value.</summary>
public static class ValidationResultsErrors
{
    /// <summary>The results took longer than the computation may (single-flight per campaign, a timeout; ARV-104g2).</summary>
    public const string TimedOut = "The campaign's results took too long to compute; try again later.";

    /// <summary>The database connections the results read through are all in use (the reader's pool, or the runtime login's).</summary>
    public const string Busy = "The campaign's results cannot be read now; try again later.";

    /// <summary>A read of the campaign's stored rows is beyond its bound (CWE-120): the campaign is larger than the results can compare.</summary>
    public const string TooLarge = "The campaign holds more stored rows than its results can compare.";

    /// <summary>Two desks of the campaign share a desk key, so their stored minutes cannot be told apart (L6 of the ARV-104g2 review).</summary>
    public const string DeskKeysCollide = "The campaign's desks cannot be told apart by their keys.";

    /// <summary>
    /// The campaign covers more queue zones times planned days than one computation may hold in memory (ARV-104g, M3 of the
    /// ARV-104g2 review; <see cref="ValidationCampaign.MaxZoneDays"/>).
    /// </summary>
    public const string TooManyZoneDays = "The campaign covers more zone-days than its results can be computed for (400 queue zones times planned days).";

    /// <summary>The stored document of a frozen revision does not match its content hash (ARV-104g): it is not served.</summary>
    public const string Corrupt = "The campaign's frozen results do not match their content hash and are not served.";

    /// <summary>A frozen revision's results read back are larger than a stored document may be (16 MB).</summary>
    public const string DocumentTooLarge = "The campaign's results are larger than a frozen document may be.";

    /// <summary>A revision number outside 1 to 1,000 (400).</summary>
    public const string InvalidRevision = "The revision is a whole number from 1 to 1,000.";

    /// <summary>A recomputation's reason (400).</summary>
    public const string InvalidReason = "A recomputation has a reason of 1 to 500 characters, without control, invisible or broken characters.";

    /// <summary>Recomputing results that are not frozen yet (409): they are frozen when the campaign closes.</summary>
    public const string NotFrozen = "The campaign's results are frozen when it closes; recompute them after that.";

    /// <summary>A recomputation of the campaign's results is already under way on this host (409).</summary>
    public const string RecomputeInProgress = "The campaign's results are being recomputed; read them when that has finished.";

    /// <summary>Another revision was added at the same moment, or the campaign has every revision it may have (409).</summary>
    public const string RevisionConflict = "Another revision of the campaign's results was added at the same moment, or the most revisions (1,000) are reached; read them and try again.";

    /// <summary>The answers that mean "try again later" (503 with Retry-After).</summary>
    public static readonly IReadOnlySet<string> Unavailable = new HashSet<string>(StringComparer.Ordinal) { TimedOut, Busy };

    /// <summary>The answers that mean the campaign's state or size forbids the results (409).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal)
    {
        TooLarge, DeskKeysCollide, TooManyZoneDays, DocumentTooLarge, NotFrozen, RecomputeInProgress, RevisionConflict
    };
}

/// <summary>
/// A validation campaign's results (ARV-104g2; served as JSON by ARV-104g): the campaign's stored ground truth and Ariva's stored
/// outputs, read within the campaign's site and by the campaign's id (the shadow nowcast through the validation reader login
/// only, ARV-104g1), compared by the F18 engine in slices that stay within its bounds, with each pilot criterion's campaign
/// verdict against the campaign's targets, availability, calibration records, the profile version, the geometry hash and the
/// nowcast's coverage. A site the caller does not reach and a campaign of another site answer NotFound (CWE-863, CWE-204). One
/// computation per campaign at a time, shared by every caller that asks meanwhile, within a timeout. The results carry
/// desk-level, observer-level and shadow figures in sections of their own (<see cref="ValidationResultsViewModel"/>).
/// <para>
/// ARV-104g: the results are served only as the caller may read them (<see cref="ValidationResultsViewModel.For"/>, from the
/// caller's stored roles: desks to border roles, observer-level results to View or Manage holders, the shadow's figures to View
/// holders), as JSON the results type writes itself (<see cref="ValidationResultsJson"/>). A closed campaign's results are frozen
/// as revision 1 of a stored document with a SHA-256 content hash (script 0050), by the first computation after the close (a
/// background freeze on the host, or the first read); a recomputation adds the next revision with a reason, never an edit.
/// </para>
/// </summary>
public interface ISvcValidationResults : ISvcScoped
{
    /// <summary>
    /// The campaign's results for the caller: the frozen revision <paramref name="revision"/>, or the latest one when null; a
    /// closed campaign not frozen yet is frozen now (revision 1); a campaign not closed is computed now and not stored.
    /// </summary>
    Task<Result<ValidationResultsJson>> GetAsync(string siteCode, Guid campaignId, int? revision = null, CancellationToken ct = default);

    /// <summary>
    /// A closed campaign's results computed again and stored as the next revision with the caller's reason (audited); the earlier
    /// revisions stay. The answer is the new revision as the caller may read it.
    /// </summary>
    Task<Result<ValidationResultsJson>> RecomputeAsync(string siteCode, Guid campaignId, RecomputeValidationResultsRequest request, CancellationToken ct = default);
}
