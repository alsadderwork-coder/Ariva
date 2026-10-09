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
}

/// <summary>
/// A validation campaign's results (ARV-104g2; served as JSON by ARV-104g): the campaign's stored ground truth and Ariva's stored
/// outputs, read within the campaign's site and by the campaign's id (the shadow nowcast through the validation reader login
/// only, ARV-104g1), compared by the F18 engine in slices that stay within its bounds, with each pilot criterion's campaign
/// verdict against the campaign's targets, availability, calibration records, the profile version, the geometry hash and the
/// nowcast's coverage. A site the caller does not reach and a campaign of another site answer NotFound (CWE-863, CWE-204). One
/// computation per campaign at a time, shared by every caller that asks meanwhile, within a timeout. The results carry
/// desk-level, observer-level and shadow figures in sections of their own (<see cref="ValidationResultsViewModel"/>), which
/// ARV-104g serves by its rules; this service applies none of them, so no endpoint may return its result as it is.
/// </summary>
public interface ISvcValidationResults : ISvcScoped
{
    Task<Result<ValidationResultsViewModel>> GetAsync(string siteCode, Guid campaignId, CancellationToken ct = default);
}
