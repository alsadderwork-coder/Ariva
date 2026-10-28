using Ariva.Core.Queueing;
using Ariva.Infra.Resilience;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.Infra.Streaming;

/// <summary>
/// The bin maturity sweep (ARV-259b, wiki 10 section 3's "bin maturity backlog"): every
/// <see cref="StreamSettings.BinMaturitySweepSeconds"/> one Stream replica, the one that takes the sweep's lock, counts per site
/// the queue bins whose latest revision is still Provisional though the bin is mature: it ended more than the censoring timeout
/// (formulas F5 and F6, 120 minutes) plus the longest lateness any site runs under before now, by when every open entrant has
/// left or been censored and every event is in. A bin that stays Provisional past that means a watermark that does not move, a
/// Stream that stopped or lags, or a fault in finalisation. It reads the stored bins, so it also sees a queue worker that stopped
/// while its host runs (or a zone no replica owns), which a count inside the worker could not; a Stream with no replica running
/// reports nothing, which the host's availability shows. A sweep ends at <see cref="RunTimeout"/> (ARV-096e).
/// </summary>
internal sealed class BinMaturityMonitor(StreamStore store, StreamSettings settings, StreamMetrics metrics, TimeProvider timeProvider,
    ILogger<BinMaturityMonitor> logger, ZoneProcessorSettings zoneSettings = null) : BackgroundService, IBoundedRuns
{
    #region Properties

    /// <summary>One minute: a sweep is one read of the newest bins.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The last sweep's sites and counts this replica ran (tests and diagnostics); null while another replica sweeps.</summary>
    internal IReadOnlyList<(string Site, int Count)> Last { get; private set; }

    /// <summary>How long after its end a bin is mature: the censoring timeout plus the longest lateness of any site.</summary>
    internal TimeSpan MaturityDelay =>
        ((zoneSettings ?? new ZoneProcessorSettings()).Engine ?? new QueueEngineSettings()).CensorAfter + TimeSpan.FromSeconds(settings.MaxLatenessSeconds);

    #endregion

    #region Methods

    /// <summary>One sweep under the run timeout (tests).</summary>
    internal Task RunOnceAsync(CancellationToken stoppingToken) => RunBound.RunAsync("Bin maturity sweep", RunTimeout, timeProvider, async ct =>
    {
        var matureBefore = timeProvider.GetUtcNow().UtcDateTime - MaturityDelay;
        var sites = await store.CountOverdueBinsAsync(matureBefore - TimeSpan.FromHours(settings.BinMaturityLookbackHours), matureBefore, ct);
        Last = sites;
        // Another replica swept this round: its copy is the fresh one, and this one's lapses with its window.
        if (sites is not null)
            metrics.OverdueBins(sites, TimeSpan.FromSeconds(2 * settings.BinMaturitySweepSeconds));
    }, stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.BinMaturitySweepSeconds), timeProvider);
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
                    logger.LogError(e, "Bin maturity sweep failed; retrying at the next interval");
                }
            }
            while (await WaitAsync(timer, stoppingToken));
        }
        finally
        {
            // A stopped sweep reports nothing, as a stopped worker does.
            metrics.ClearOverdueBins();
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
