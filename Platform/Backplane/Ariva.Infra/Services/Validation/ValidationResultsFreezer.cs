using System.Collections.Concurrent;
using Ariva.Infra.Resilience;
using Ariva.Infra.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// Freezes the results of closed validation campaigns (ARV-104g; Ariva.Api.Main only, where the validation service and its reader
/// login live): every <see cref="ValidationResultsSettings.FreezeInterval"/> it looks for closed campaigns of any site without a
/// frozen revision and freezes them one after another through the validation service (revision 1 with its content hash, audited
/// as the system), in the same flight a read of that campaign would join and under the host-wide limit of computations. The
/// close itself stays a short transaction (no computation inside the critical action); a read that comes first freezes the
/// campaign itself. A campaign whose freeze fails (too large for the bounds, a read refused, the pool busy) is left for
/// <see cref="ValidationResultsSettings.FreezeRetryAfter"/>, so it never holds up the others. Every replica may freeze: the first
/// revision is inserted once (its unique key), and a replica that loses keeps the stored one. Holds no results: the service
/// returns only which campaigns it tried and why one failed.
/// </summary>
internal sealed class ValidationResultsFreezer(IServiceScopeFactory scopes, ValidationResultsSettings settings, TimeProvider timeProvider,
    ILogger<ValidationResultsFreezer> logger) : BackgroundService, IBoundedRuns
{
    #region Constants

    /// <summary>Campaigns tried in one pass at most.</summary>
    public const int PassSize = 20;

    #endregion

    #region Fields

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _failedUntil = new();

    #endregion

    #region Properties

    /// <summary>
    /// ARV-096e: long enough that a pass within its bounds is never cut: at most <see cref="PassSize"/> campaigns, each frozen within
    /// its computation's timeout (<c>Validation:Results:Timeout</c>, 3 minutes), and 5 minutes for the database; 65 minutes by
    /// default, at most <see cref="RunBound.MaxRunTimeout"/>. Each campaign commits on its own, so a pass it cuts keeps the campaigns
    /// it froze and leaves the rest to the next pass.
    /// </summary>
    public TimeSpan RunTimeout { get; init; } = PassTimeout(settings);

    /// <summary>The run timeout a pass needs with these settings (see <see cref="RunTimeout"/>).</summary>
    internal static TimeSpan PassTimeout(ValidationResultsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var timeout = settings.Timeout * PassSize + TimeSpan.FromMinutes(5);
        return timeout < RunBound.MaxRunTimeout ? timeout : RunBound.MaxRunTimeout;
    }

    #endregion

    #region Methods

    /// <summary>One pass under the run timeout (tests).</summary>
    internal Task<int> RunOnceAsync(CancellationToken stoppingToken) => RunBound.RunAsync("Validation results freeze", RunTimeout, timeProvider, PassAsync, stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.FreezeInterval, timeProvider);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed pass (the database restarting) must not stop the freeze; the next pass retries
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Freezing validation results failed; retrying at the next interval");
            }
        }
    }

    /// <summary>
    /// At most this many failed campaigns are skipped in one pass (each is one bound parameter); beyond it the earliest to retry
    /// may be tried again sooner, which costs a retry and nothing else.
    /// </summary>
    internal const int MaxSkipped = 500;

    /// <summary>One pass: the closed campaigns due, frozen in turn; a failure is remembered until its retry time.</summary>
    internal async Task<int> PassAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in _failedUntil.Where(f => f.Value <= now).Select(f => f.Key).ToList())
            _failedUntil.TryRemove(expired, out _);

        await using var scope = scopes.CreateAsyncScope();
        // The service is never kept in a local across an await (its state machine would then hold the service and, through it, the
        // reader settings and the results; the exposure tests of the Architecture and Persistence suites reflect the field graph).
        var outcomes = await scope.ServiceProvider.GetRequiredService<SvcValidationResults>().FreezeDueAsync([.. _failedUntil.OrderByDescending(f => f.Value).Take(MaxSkipped).Select(f => f.Key)], PassSize, ct);
        var frozen = 0;
        foreach (var (campaign, error) in outcomes)
        {
            if (error is null)
            {
                frozen++;
                continue;
            }

            _failedUntil[campaign] = timeProvider.GetUtcNow() + settings.FreezeRetryAfter;
            logger.LogWarning("The validation results of campaign {Campaign} could not be frozen: {Failure}", campaign, error);
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(ct);
        if (frozen > 0)
            logger.LogInformation("Froze the validation results of {Count} closed campaigns", frozen);
        return frozen;
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    #endregion
}
