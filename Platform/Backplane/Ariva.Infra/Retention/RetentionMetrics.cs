using System.Diagnostics.Metrics;

namespace Ariva.Infra.Retention;

/// <summary>
/// Whether data is still being let go (meter <c>Ariva.Retention</c>, ARV-259c), Ariva.Api.Cronz only.
/// <see cref="ProblemsName"/> is the last run of the relational retention job that this replica ran, by <c>outcome</c>:
/// <see cref="Failed"/>, the data sets whose purge failed; <see cref="Unset"/>, the data sets without a period (nothing is
/// deleted for them); <see cref="Run"/>, 1 when the run failed as a whole (the database out of reach, or its run timeout), when
/// the other two are not known. It is reported for <see cref="RunFreshFor"/> (the job runs daily), so only the replica
/// that ran the job reports it and a replica that restarted reports nothing until the next run: take the largest copy, never
/// the sum. <see cref="PolicyJobsFailedName"/> is TimescaleDB's own policy jobs whose last run failed, by <c>kind</c>
/// (<see cref="TimescalePolicyJobs.Retention"/>, <see cref="TimescalePolicyJobs.Compression"/>,
/// <see cref="TimescalePolicyJobs.Refresh"/>), a kind with jobs reported with 0, from the check this replica ran last, for its
/// <c>freshFor</c>: take the largest copy.
/// </summary>
public sealed class RetentionMetrics : IDisposable
{
    public const string MeterName = "Ariva.Retention";
    public const string ProblemsName = "ariva.retention.problems";
    public const string PolicyJobsFailedName = "ariva.retention.policy_jobs_failed";

    /// <summary>The outcomes of <see cref="ProblemsName"/>.</summary>
    public const string Failed = "failed";

    /// <inheritdoc cref="Failed"/>
    public const string Unset = "unset";

    /// <inheritdoc cref="Failed"/>
    public const string Run = "run";

    /// <summary>A day and two hours: the retention job runs daily, so a run's outcome stands until the next one is due and a little after.</summary>
    public static readonly TimeSpan RunFreshFor = TimeSpan.FromHours(26);

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private volatile Report _problems;
    private volatile Report _policyJobs;

    #endregion

    private sealed record Report(IReadOnlyList<(string Tag, int Count)> Values, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public RetentionMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(ProblemsName, () => Observe(_problems, "outcome"),
            description: "The last relational retention run on this replica: data sets that failed, data sets without a period, and 1 for a run that failed as a whole");
        _meter.CreateObservableGauge(PolicyJobsFailedName, () => Observe(_policyJobs, "kind"), unit: "{job}",
            description: "TimescaleDB's retention, compression and refresh policy jobs whose last run failed, by kind, at this replica's last check");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>A run that finished: its data sets that failed and those without a period, and a run that did not fail as a whole.</summary>
    public void RunFinished(RetentionRun run, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(run);
        _problems = new Report([(Failed, run.DataSets.Count(d => d.Failed)), (Unset, run.DataSets.Count(d => d.Days is null)), (Run, 0)],
            _time.GetUtcNow(), freshFor);
    }

    /// <summary>A run that failed as a whole: what its data sets did is not known.</summary>
    public void RunFailed(TimeSpan freshFor) => _problems = new Report([(Run, 1)], _time.GetUtcNow(), freshFor);

    /// <summary>A check of TimescaleDB's policy jobs: per kind with jobs, those whose last run failed.</summary>
    public void PolicyJobs(IReadOnlyList<(string Kind, int Failed)> kinds, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        _policyJobs = new Report([.. kinds], _time.GetUtcNow(), freshFor);
    }

    private IEnumerable<Measurement<int>> Observe(Report report, string tag)
    {
        if (report is null || _time.GetUtcNow() - report.At > report.FreshFor)
            return [];
        return report.Values.Select(v => new Measurement<int>(v.Count, new KeyValuePair<string, object>(tag, v.Tag)));
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
