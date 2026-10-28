using Ariva.Infra.Resilience;
using Ariva.Infra.Retention;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The check of TimescaleDB's own policy jobs (ARV-259c), every ten minutes: one read of <see cref="TimescalePolicyJobs"/> with the
/// runtime login, reported as <see cref="RetentionMetrics.PolicyJobsFailedName"/> for <see cref="ReportFreshFor"/>, so a check
/// that stops (the database out of reach, which its own rule reports) lets the copy lapse. A check from the dashboard or a second
/// replica only reads. A check ends at <see cref="RunTimeout"/> (ARV-096e).
/// </summary>
public sealed class TimescaleJobs(TimescalePolicyJobs policyJobs, RetentionMetrics metrics, ILogger<TimescaleJobs> logger, TimeProvider timeProvider = null)
    : IBoundedRuns
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string PolicyJobs = "TimescalePolicyJobs";

    /// <summary>Twenty-five minutes: two checks may be missed before the copy lapses.</summary>
    public static readonly TimeSpan ReportFreshFor = TimeSpan.FromMinutes(25);

    /// <summary>One minute: a check is one read of at most <see cref="TimescalePolicyJobs.MaxProcedures"/> rows.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(1);

    [TickerFunction(PolicyJobs, "0 */10 * * * *")]
    public async Task CheckAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kinds = await RunBound.RunAsync(PolicyJobs, RunTimeout, timeProvider ?? TimeProvider.System, policyJobs.FailedAsync, ct);
        metrics.PolicyJobs(kinds, ReportFreshFor);
        var failed = kinds.Where(k => k.Failed > 0).ToList();
        if (failed.Count > 0)
        {
            logger.LogWarning("TimescaleDB policy jobs ({Type}): the last run failed for {Jobs}; timescaledb_information.job_stats names them",
                context.Type, string.Join(", ", failed.Select(k => $"{k.Failed} {k.Kind}")));
        }
    }
}
