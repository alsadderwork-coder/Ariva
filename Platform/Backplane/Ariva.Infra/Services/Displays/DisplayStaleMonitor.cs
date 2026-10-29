using Ariva.Core.Services.Displays;
using Ariva.Infra.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// Runs the display stale sweep (<see cref="ISvcDisplayStaleness"/>, ARV-259d) every <see cref="Interval"/> in Ariva.Api.Main; a
/// sweep ends at <see cref="RunTimeout"/> (ARV-096e) and is rolled back (it writes nothing; the rollback ends its transaction and
/// its lock). Only the replica that took the sweep's lock reports <see cref="DisplayMetrics.StaleName"/>, after its transaction
/// ends, for two intervals. A player that stopped asking for its board is not seen: the board endpoint only reads, and recording
/// each request would add a write to it on every replica.
/// </summary>
internal sealed class DisplayStaleMonitor(IServiceScopeFactory scopes, DisplayMetrics metrics, TimeProvider timeProvider, ILogger<DisplayStaleMonitor> logger)
    : BackgroundService, IBoundedRuns
{
    #region Fields

    private bool _truncated;

    #endregion

    #region Properties

    /// <summary>One minute: a sweep is one read of the enabled displays and one Redis read per zone they show.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A minute between sweeps: the shortest stale threshold a display takes (60 seconds).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The last sweep's outcome (tests and diagnostics).</summary>
    internal DisplayStaleSweep Last { get; private set; }

    #endregion

    #region Methods

    /// <summary>One sweep under the run timeout (tests).</summary>
    internal Task RunOnceAsync(CancellationToken stoppingToken) => RunBound.RunAsync("Display stale sweep", RunTimeout, timeProvider, async ct =>
    {
        DisplayStaleSweep sweep;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                sweep = await scope.ServiceProvider.GetRequiredService<ISvcDisplayStaleness>().SweepAsync(ct);
                unitOfWork.PromiseToCommit();
                await unitOfWork.EndAsync(ct);
            }
            catch
            {
                // A sweep that failed or was cut part way is rolled back (a promised unit would otherwise commit as the scope ends).
                await unitOfWork.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        Last = sweep;
        // Another replica swept this round: its copy is the fresh one, and this one's lapses with its window.
        if (!sweep.Ran)
            return;
        metrics.Stale(sweep.Sites, 2 * Interval);
        if (sweep.Truncated != _truncated)
        {
            _truncated = sweep.Truncated;
            if (sweep.Truncated)
                logger.LogWarning("More than {Max} enabled displays: the display stale sweep judges only the first of them by site and code",
                    SvcDisplayStaleness.MaxDisplays);
        }
    }, stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // one failed sweep (the database restarting) must not stop the monitor; the next one retries
                catch (Exception e)
#pragma warning restore CA1031
                {
                    logger.LogError(e, "Display stale sweep failed; retrying at the next interval");
                }
            }
            while (await WaitAsync(timer, stoppingToken));
        }
        finally
        {
            // A stopped sweep reports nothing.
            metrics.Clear();
        }
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
