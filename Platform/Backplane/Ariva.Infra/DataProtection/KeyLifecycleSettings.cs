using System.Globalization;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// <c>Cronz:KeyLifecycle</c> (ARV-207): how the Cronz key lifecycle job runs. <see cref="ExpiredKeyRetentionDays"/> is how long an
/// expired Data Protection key is kept before it may be deleted (30 to 3,650 days; script 0072 refuses less, so the runtime login
/// the job uses cannot shorten it); a key a stored value still needs is kept whatever its age. One run deletes at most
/// <see cref="BatchSize"/> keys per statement and <see cref="MaxBatchesPerRun"/> statements (CWE-400).
/// </summary>
public sealed class KeyLifecycleSettings
{
    #region Constants

    public const string SectionName = "Cronz:KeyLifecycle";

    /// <summary>The least retention script 0072's <c>ariva_data_protection_key_purge</c> takes.</summary>
    public const int MinRetentionDays = 30;

    /// <summary>The most retention script 0072 takes.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>The largest batch script 0072 takes.</summary>
    public const int MaxBatchSize = 1_000;

    #endregion

    #region Properties

    /// <summary>Run the key lifecycle job in this host (Ariva.Api.Cronz).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Days an expired Data Protection key is kept (Proposed: 30). A key is the default for 90 days, the re-protection job moves
    /// every stored value off it within a day of its replacement, and the short-lived values (a refresh token's successor, 30 s; a
    /// preview token, 2 hours; a session, 12 hours) are long gone by then.
    /// </summary>
    public int ExpiredKeyRetentionDays { get; init; } = MinRetentionDays;

    /// <summary>Keys one statement deletes at most (1 to 1,000).</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Statements one run takes at most (1 to 100).</summary>
    public int MaxBatchesPerRun { get; init; } = 10;

    #endregion

    #region Methods

    public IEnumerable<string> Problems()
    {
        if (ExpiredKeyRetentionDays is < MinRetentionDays or > MaxRetentionDays)
            yield return string.Create(CultureInfo.InvariantCulture, $"{SectionName}:ExpiredKeyRetentionDays is from {MinRetentionDays} to {MaxRetentionDays:N0} days.");
        if (BatchSize is < 1 or > MaxBatchSize)
            yield return string.Create(CultureInfo.InvariantCulture, $"{SectionName}:BatchSize is from 1 to {MaxBatchSize:N0} keys.");
        if (MaxBatchesPerRun is < 1 or > 100)
            yield return $"{SectionName}:MaxBatchesPerRun is from 1 to 100.";
    }

    #endregion
}
