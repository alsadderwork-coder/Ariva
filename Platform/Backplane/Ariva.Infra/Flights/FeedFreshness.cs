using System.Diagnostics.Metrics;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Flights;

/// <summary>The flight feed settings (<c>Flights:Feeds</c>, ARV-041, runbook 4.3).</summary>
public sealed record FlightFeedSettings
{
    public const string SectionName = "Flights:Feeds";

    /// <summary>A feed silent for longer than this while flights are due is stale, unless <see cref="StaleAfterMinutes"/> names it.</summary>
    public int DefaultStaleAfterMinutes { get; init; } = 20;

    /// <summary>Per feed name: its cadence in minutes, or 0 for a feed that is not live (an SSIM schedule import).</summary>
    public Dictionary<string, int> StaleAfterMinutes { get; init; } = new(StringComparer.Ordinal) { ["ssim"] = 0 };

    /// <summary>A flight is due from this long before its expected time (estimated, else scheduled) ...</summary>
    public int DueBeforeMinutes { get; init; } = 60;

    /// <summary>... until it is on block (arrival) or off block (departure), or this long after its expected time.</summary>
    public int DueAfterMinutes { get; init; } = 120;

    public int SweepSeconds { get; init; } = 60;

    public IEnumerable<string> Problems()
    {
        if (DefaultStaleAfterMinutes is < 1 or > 1440)
            yield return "Flights:Feeds:DefaultStaleAfterMinutes is 1 to 1440.";
        foreach (var (feed, minutes) in StaleAfterMinutes ?? [])
        {
            if (FlightRules.NormalizeFeed(feed) != feed || minutes is < 0 or > 1440)
                yield return $"Flights:Feeds:StaleAfterMinutes:{feed} names a feed (lower case) and is 0 (not watched) to 1440 minutes.";
        }

        if (DueBeforeMinutes is < 0 or > 720 || DueAfterMinutes is < 0 or > 720)
            yield return "Flights:Feeds:DueBeforeMinutes and DueAfterMinutes are 0 to 720.";
        if (SweepSeconds is < 10 or > 600)
            yield return "Flights:Feeds:SweepSeconds is 10 to 600.";
    }

    /// <summary>The cadence of <paramref name="feed"/>, or null when it is not watched.</summary>
    public TimeSpan? StaleAfter(string feed)
    {
        var minutes = StaleAfterMinutes is not null && StaleAfterMinutes.TryGetValue(feed, out var set) ? set : DefaultStaleAfterMinutes;
        return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }
}

/// <summary>
/// Flight feed metrics (meter <c>Ariva.Flights</c>): items applied, unchanged and refused per kind, site and feed; feeds
/// that went stale and recovered; and, from the last sweep, the feeds stale now (the stale-feed alarm, runbook 3).
/// </summary>
public sealed class FlightMetrics : IDisposable
{
    public const string MeterName = "Ariva.Flights";

    private readonly Meter _meter;
    private readonly Counter<long> _items;
    private readonly Counter<long> _wentStale;
    private readonly Counter<long> _recovered;
    private volatile int _stale;

    public FlightMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _items = _meter.CreateCounter<long>("ariva.flights.items", description: "Feed items by kind (legs, events, allocations) and outcome (applied, unchanged, refused)");
        _wentStale = _meter.CreateCounter<long>("ariva.feeds.went_stale", description: "Flight feeds that went stale (silent while flights are due)");
        _recovered = _meter.CreateCounter<long>("ariva.feeds.recovered", description: "Stale flight feeds heard from again, or with no flight due any more");
        _meter.CreateObservableGauge("ariva.feeds.stale", () => _stale, description: "Flight feeds stale at the last sweep");
    }

    public void Items(string kind, string siteCode, string feed, IReadOnlyList<FlightItemResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var outcome in results.GroupBy(r => r.HasErrors ? "refused" : r.Applied ? "applied" : "unchanged"))
            _items.Add(outcome.Count(), new("kind", kind), new("site", siteCode), new("feed", feed), new("outcome", outcome.Key));
    }

    public void WentStale(string siteCode, string feed) => _wentStale.Add(1, new("site", siteCode), new("feed", feed));

    public void Recovered(string siteCode, string feed) => _recovered.Add(1, new("site", siteCode), new("feed", feed));

    public void Snapshot(int stale) => _stale = stale;

    public void Dispose() => _meter.Dispose();
}

/// <summary>Sweeps the flight feeds every <see cref="FlightFeedSettings.SweepSeconds"/> in Ariva.Api.Integration (ARV-041).</summary>
internal sealed class FeedFreshnessMonitor(IServiceScopeFactory scopes, FlightFeedSettings settings, TimeProvider timeProvider, ILogger<FeedFreshnessMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.SweepSeconds), timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await scope.ServiceProvider.GetRequiredService<ISvcFeedFreshness>().SweepAsync(stoppingToken);
                unitOfWork.PromiseToCommit();
                await unitOfWork.EndAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed sweep (the database restarting) must not stop the monitor; the next one retries
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Feed freshness sweep failed; retrying at the next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
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
}
