using System.Globalization;

namespace Ariva.Infra.Retention;

/// <summary>
/// <c>Cronz:Retention</c> (ARV-111a): how the Cronz retention job runs. The periods are not here: they are migration-job
/// parameters (<c>Database:Retention</c>, written to <c>retention_period</c> by the migration login), so the runtime login the
/// job uses can never shorten one. <see cref="BatchSize"/> rows go in one statement and transaction (script 0054 refuses more
/// than 10,000), and one run takes at most <see cref="MaxBatchesPerRun"/> batches per data set (CWE-400): a backlog beyond that
/// is deleted by the following runs.
/// </summary>
public sealed class RetentionJobSettings
{
    #region Constants

    public const string SectionName = "Cronz:Retention";

    /// <summary>The largest batch script 0054's <c>ariva_retention_purge</c> takes.</summary>
    public const int MaxBatchSize = 10_000;

    #endregion

    #region Properties

    /// <summary>Run the retention job in this host (Ariva.Api.Cronz).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Rows one statement deletes at most (100 to 10,000).</summary>
    public int BatchSize { get; init; } = 5_000;

    /// <summary>Batches one run takes per data set at most (1 to 1,000).</summary>
    public int MaxBatchesPerRun { get; init; } = 100;

    #endregion

    #region Methods

    public IEnumerable<string> Problems()
    {
        if (BatchSize is < 100 or > MaxBatchSize)
            yield return string.Create(CultureInfo.InvariantCulture, $"{SectionName}:BatchSize is from 100 to {MaxBatchSize:N0} rows.");
        if (MaxBatchesPerRun is < 1 or > 1_000)
            yield return $"{SectionName}:MaxBatchesPerRun is from 1 to 1,000.";
    }

    #endregion
}
