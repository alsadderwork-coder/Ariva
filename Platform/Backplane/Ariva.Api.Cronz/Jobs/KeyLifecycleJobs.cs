using Ariva.Infra.DataProtection;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Resilience;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The key lifecycle job (ARV-207), daily at 03:10 UTC, half an hour after the re-protection job (<see cref="SecretJobs"/>, 02:40)
/// has moved the stored secrets to the current key: one run of <see cref="KeyLifecycleRound"/>, which deletes the expired Data
/// Protection keys past their retention that no stored value needs (script 0072 decides what goes) and reads when the oldest
/// dated credential of each kind was made. The run is reported as <see cref="KeyAgeMetrics.KeyRingName"/> and
/// <see cref="KeyAgeMetrics.AgeName"/> for <see cref="KeyAgeMetrics.RunFreshFor"/>, and its outcome as
/// <see cref="KeyAgeMetrics.ProblemsName"/> (1 for a run that failed as a whole). A run from the dashboard or a second replica is
/// harmless: the function deletes only what its rule lets go, and the rest is read only. A run ends at <see cref="RunTimeout"/>
/// (ARV-096e).
/// </summary>
public sealed class KeyLifecycleJobs(KeyLifecycleRound round, KeyLifecycleSettings settings, ILogger<KeyLifecycleJobs> logger, TimeProvider timeProvider = null,
    KeyAgeMetrics metrics = null) : IBoundedRuns
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Lifecycle = "KeyLifecycle";

    /// <summary>Ten minutes: at most <see cref="KeyLifecycleSettings.MaxBatchesPerRun"/> bounded deletes on a table of a few rows, and one read of aggregates.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

    [TickerFunction(Lifecycle, "0 10 3 * * *")]
    public async Task RunAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!settings.Enabled)
            return;
        KeyLifecycleRun run;
        try
        {
            run = await JobRun.RunAsync(Lifecycle, RunTimeout, timeProvider ?? TimeProvider.System, round.RunAsync, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The review's finding L7: the database out of reach, the purge refused or the run timeout; TickerQ records the failure.
            metrics?.RunFailed(KeyAgeMetrics.RunFreshFor);
            throw;
        }

        metrics?.Reported(run, KeyAgeMetrics.RunFreshFor);
        if (run.KeyRing.Deleted > 0 || run.KeyRing.Held > 0)
            logger.LogInformation("Key lifecycle ({Type}): {Deleted} expired keys deleted, {Held} held past their retention", context.Type, run.KeyRing.Deleted, run.KeyRing.Held);
    }
}

/// <summary>Registers the key lifecycle job's settings (<c>Cronz:KeyLifecycle</c>, checked at start) and its round.</summary>
public static class KeyLifecycleJobsRegistration
{
    public static IServiceCollection AddArivaKeyLifecycleJob(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(KeyLifecycleSettings.SectionName).Get<KeyLifecycleSettings>() ?? new KeyLifecycleSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        services.TryAddSingleton<KeyLifecycleRound>();
        return services;
    }
}
