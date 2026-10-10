using Ariva.Core.Validation;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>
/// Settings of the validation results service (ARV-104g2, section <c>Validation:Results</c>; Proposed values,
/// docs/product/decisions.md): how long one campaign's computation may take (waiting for a turn included), how many campaigns
/// compute at once on a host (peak memory), the most rows one read returns (a zone's minutes over a contiguous run of planned
/// days, its shadow nowcasts, a day of line counts or desk minutes), and the exclusion rule of the campaign verdicts. ARV-104g
/// adds how long a request for the results may wait (<see cref="RequestTimeout"/>) and how often the host freezes the results of
/// closed campaigns (<see cref="FreezeInterval"/>, <see cref="FreezeRetryAfter"/>); one computation at a time is the Proposed
/// default, sized to api-main's 1000Mi (M3 of the ARV-104g2 review, docs/product/decisions.md).
/// </summary>
public sealed class ValidationResultsSettings
{
    public const string SectionName = "Validation:Results";

    /// <summary>The request timeout policy of the results endpoints (ARV-104g; <see cref="RequestTimeout"/>).</summary>
    public const string RequestTimeoutPolicy = "validation-results";

    /// <summary>How long one campaign's computation may take, its wait for a turn included (Proposed 3 minutes).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Campaigns computing at once on a host, freezes and recomputations included (ARV-104g Proposed 1: a campaign at the bound of
    /// 400 zone-days peaks near 200 MB, so one fits api-main's 1000Mi beside authentication and the live hub; at most 4).
    /// </summary>
    public int MaxConcurrentComputations { get; init; } = 1;

    /// <summary>
    /// How long one request for the results may take before it is answered 503 with Retry-After (ARV-104g, Proposed 90 s, below
    /// the usual proxy timeouts). A computation the request waited for goes on, so a closed campaign's freeze still completes.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the host looks for closed campaigns whose results are not frozen yet (ARV-104g, Proposed 1 minute).</summary>
    public TimeSpan FreezeInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the freeze leaves a campaign whose results could not be frozen before it tries again (Proposed 1 hour).</summary>
    public TimeSpan FreezeRetryAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The most rows one read returns before it is refused (Proposed 100,000: a zone's 47,520 minutes of 33 days fit twice). The
    /// count is taken through a LIMITed query before the rows are read (ARV-104g1 review, L7), so no read holds more.
    /// </summary>
    public int MaxRowsPerRead { get; init; } = 100_000;

    /// <summary>How the campaign verdicts count the excluded desk minutes and no-service nowcast minutes (Proposed: strict).</summary>
    public ExclusionRule Exclusions { get; init; } = ExclusionRule.Strict;

    /// <summary>With <see cref="ExclusionRule.Capped"/>, the largest excluded share a criterion may hold and still pass.</summary>
    public double MaxExcludedShare { get; init; } = 0.05;

    public CampaignVerdictSettings Verdicts => new() { Exclusions = Exclusions, MaxExcludedShare = MaxExcludedShare };

    public static ValidationResultsSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(SectionName).Get<ValidationResultsSettings>() ?? new ValidationResultsSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException($"{SectionName}: {string.Join(" ", problems)}");
        return settings;
    }

    public IEnumerable<string> Problems()
    {
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(30))
            yield return "Timeout is above 0 and at most 30 minutes.";
        if (MaxConcurrentComputations is < 1 or > 4)
            yield return "MaxConcurrentComputations is 1 to 4.";
        if (RequestTimeout < TimeSpan.FromSeconds(5) || RequestTimeout > TimeSpan.FromMinutes(10))
            yield return "RequestTimeout is 5 seconds to 10 minutes.";
        if (FreezeInterval < TimeSpan.FromSeconds(5) || FreezeInterval > TimeSpan.FromHours(1))
            yield return "FreezeInterval is 5 seconds to 1 hour.";
        if (FreezeRetryAfter < TimeSpan.FromMinutes(1) || FreezeRetryAfter > TimeSpan.FromDays(1))
            yield return "FreezeRetryAfter is 1 minute to 1 day.";
        if (MaxRowsPerRead is < 1_000 or > 1_000_000)
            yield return "MaxRowsPerRead is 1,000 to 1,000,000.";
        foreach (var problem in Verdicts.Problems())
            yield return problem;
    }
}
