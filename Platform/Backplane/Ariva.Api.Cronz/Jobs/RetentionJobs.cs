using Ariva.Infra.Resilience;
using Ariva.Infra.Retention;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The relational retention job (ARV-111a), daily at 03:15 UTC: one run of <see cref="RetentionRound"/>, which deletes the alert
/// emails, Integration API call records and report deliveries older than the periods the migration job set
/// (<c>Database:Retention</c>), in bounded batches, one replica at a time. A run from the dashboard or a second replica is
/// harmless: a replica that cannot take the lock skips the run, and a batch deletes only what its period already lets go (the
/// runtime login cannot move the time column the purge reads: column grants on email_message and report_delivery, no UPDATE
/// on integration_call).
/// TimescaleDB drops old hypertable chunks itself (its retention policies, also set by the migration job).
/// A run ends at <see cref="RunTimeout"/> (ARV-096e): the batches it deleted stay deleted, and the next day's run takes the rest.
/// The run's outcome is reported as <see cref="RetentionMetrics.ProblemsName"/> (ARV-259c): the round reports the data sets that
/// failed or have no period, and this job a run that failed as a whole.
/// </summary>
public sealed class RetentionJobs(RetentionRound round, RetentionJobSettings settings, ILogger<RetentionJobs> logger, TimeProvider timeProvider = null,
    RetentionMetrics metrics = null) : IBoundedRuns
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Purge = "RelationalRetention";

    /// <summary>
    /// An hour: 100 batches per data set by default, each a bounded delete on an index; a larger backlog cut here waits for the
    /// next day's run, as one beyond <c>MaxBatchesPerRun</c> does.
    /// </summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromHours(1);

    [TickerFunction(Purge, "0 15 3 * * *")]
    public async Task PurgeAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!settings.Enabled)
            return;
        RetentionRun run;
        try
        {
            run = await RunBound.RunAsync(Purge, RunTimeout, timeProvider ?? TimeProvider.System, round.RunAsync, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // ARV-259c: the database out of reach or the run timeout; TickerQ records the failure as before.
            metrics?.RunFailed(RetentionMetrics.RunFreshFor);
            throw;
        }

        if (run.HeldElsewhere)
        {
            logger.LogInformation("Retention ({Type}): another replica is running it; skipped", context.Type);
            return;
        }

        var failed = run.DataSets.Count(d => d.Failed);
        var unset = run.DataSets.Count(d => d.Days is null);
        if (failed > 0 || unset > 0)
            logger.LogWarning("Retention ({Type}): {Rows} rows deleted; {Failed} data sets failed, {Unset} without a period", context.Type, run.Deleted, failed, unset);
        else
            logger.LogInformation("Retention ({Type}): {Rows} rows deleted over {DataSets} data sets", context.Type, run.Deleted, run.DataSets.Count);
    }
}

/// <summary>Registers the retention job's settings (<c>Cronz:Retention</c>, checked at start), its round, its metrics and the policy job check.</summary>
public static class RetentionJobsRegistration
{
    public static IServiceCollection AddArivaRetentionJob(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(RetentionJobSettings.SectionName).Get<RetentionJobSettings>() ?? new RetentionJobSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        services.TryAddSingleton<RetentionRound>();
        // ARV-259c: the outcome of the retention run and TimescaleDB's policy jobs (TimescaleJobs).
        services.TryAddSingleton<RetentionMetrics>();
        services.TryAddSingleton<TimescalePolicyJobs>();
        return services;
    }
}
