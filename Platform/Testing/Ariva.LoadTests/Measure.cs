using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace Ariva.LoadTests;

/// <summary>Latencies (ms) and outcomes of one scenario; percentiles by nearest rank over every request.</summary>
public sealed class Measure(string name)
{
    private readonly ConcurrentQueue<double> _latencies = new();
    private readonly ConcurrentDictionary<string, int> _outcomes = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private long _bytes;

    public string Name { get; } = name;

    public void Start() => _clock.Start();

    public void Stop() => _clock.Stop();

    public void Record(double milliseconds, string outcome, long bytes = 0)
    {
        _latencies.Enqueue(milliseconds);
        _outcomes.AddOrUpdate(outcome, 1, (_, n) => n + 1);
        Interlocked.Add(ref _bytes, bytes);
    }

    public void Record(double milliseconds, HttpStatusCode status, long bytes = 0) => Record(milliseconds, ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), bytes);

    public int Count(string outcome) => _outcomes.TryGetValue(outcome, out var n) ? n : 0;

    public int Total => _outcomes.Values.Sum();

    /// <summary>Server errors and transport failures: what must never happen under load.</summary>
    public int Failures => _outcomes.Where(o => o.Key.StartsWith('5') || o.Key.StartsWith("error", StringComparison.Ordinal)).Sum(o => o.Value);

    public object Summary()
    {
        var sorted = _latencies.ToArray();
        Array.Sort(sorted);
        double? P(double p) => sorted.Length == 0 ? null : Math.Round(sorted[Math.Max(0, (int)Math.Ceiling(Math.Round(p * sorted.Length, 9)) - 1)], 1);
        var seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        return new
        {
            Name,
            Requests = sorted.Length,
            Seconds = Math.Round(seconds, 1),
            PerSecond = Math.Round(sorted.Length / seconds, 1),
            MegabytesPerSecond = Math.Round(_bytes / seconds / 1_000_000, 3),
            P50Ms = P(0.50),
            P95Ms = P(0.95),
            P99Ms = P(0.99),
            MaxMs = sorted.Length == 0 ? (double?)null : Math.Round(sorted[^1], 1),
            Outcomes = _outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => o.Value),
            Failures
        };
    }
}

/// <summary>
/// Open-model injection: <paramref name="perSecond"/> calls a second for <paramref name="seconds"/>, started on time
/// whether or not earlier ones have answered (a closed loop would slow down with the server and hide its queueing).
/// </summary>
public static class Inject
{
    public static async Task RunAsync(double perSecond, int seconds, Func<long, Task> call, CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(100);
        var due = 0.0;
        long sequence = 0;
        var running = new List<Task>();
        using var timer = new PeriodicTimer(tick);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds) && await timer.WaitForNextTickAsync(ct))
        {
            due += perSecond * tick.TotalSeconds;
            for (; due >= 1; due--)
                running.Add(call(sequence++));
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running);
    }
}
