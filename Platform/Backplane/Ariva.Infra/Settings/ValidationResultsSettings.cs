using Ariva.Core.Validation;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>
/// Settings of the validation results service (ARV-104g2, section <c>Validation:Results</c>; Proposed values,
/// docs/product/decisions.md): how long one campaign's computation may take (waiting for a turn included), how many campaigns
/// compute at once on a host (peak memory), the most rows one read returns (a zone's minutes over a contiguous run of planned
/// days, its shadow nowcasts, a day of line counts or desk minutes), and the exclusion rule of the campaign verdicts.
/// </summary>
public sealed class ValidationResultsSettings
{
    public const string SectionName = "Validation:Results";

    /// <summary>How long one campaign's computation may take, its wait for a turn included (Proposed 3 minutes).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Campaigns computing at once on a host (Proposed 2: about 12 s and up to a few GB each at the engine's bounds).</summary>
    public int MaxConcurrentComputations { get; init; } = 2;

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
        if (MaxConcurrentComputations is < 1 or > 16)
            yield return "MaxConcurrentComputations is 1 to 16.";
        if (MaxRowsPerRead is < 1_000 or > 1_000_000)
            yield return "MaxRowsPerRead is 1,000 to 1,000,000.";
        foreach (var problem in Verdicts.Problems())
            yield return problem;
    }
}
