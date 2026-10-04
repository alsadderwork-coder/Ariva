using System.Globalization;

namespace Ariva.LoadTests;

/// <summary>
/// CPU and memory of the Ariva hosts on this machine during a run (Linux /proc; elsewhere nothing is sampled). A lab
/// stand-in for "per pod": each host is one process here, as it is one container in a pod.
/// </summary>
public sealed class HostSampler : IAsyncDisposable
{
    private static readonly string[] Hosts = ["Ariva.Api.Main", "Ariva.Api.Ingest", "Ariva.Api.Integration", "Ariva.Api.Cronz", "Ariva.Api.Stream"];
    private const double TicksPerSecond = 100; // USER_HZ on Linux
    private readonly Dictionary<string, List<(double Cpu, double RssMb)>> _samples = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public HostSampler() => _loop = OperatingSystem.IsLinux() ? LoopAsync(_stop.Token) : Task.CompletedTask;

    private static IEnumerable<(string Host, int Pid)> Find()
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            string cmdline;
            try
            {
                cmdline = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            // The host process itself (its apphost or "dotnet Ariva.Api.X.dll"), not the "dotnet run" that started it.
            var host = Hosts.FirstOrDefault(h => cmdline.Contains("/" + h + " ", StringComparison.Ordinal) || cmdline.Contains("/" + h + ".dll", StringComparison.Ordinal) ||
                                                 cmdline.EndsWith("/" + h + " ", StringComparison.Ordinal));
            if (host is not null && !cmdline.Contains(" run ", StringComparison.Ordinal))
                yield return (host, pid);
        }
    }

    private static (double Ticks, double RssMb)? Read(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var ticks = double.Parse(fields[11], CultureInfo.InvariantCulture) + double.Parse(fields[12], CultureInfo.InvariantCulture);
            var rss = File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith("VmRSS:", StringComparison.Ordinal));
            var kb = double.Parse(rss.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
            return (ticks, kb / 1024);
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidOperationException or IndexOutOfRangeException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var previous = new Dictionary<int, double>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var (host, pid) in Find())
                {
                    if (Read(pid) is not { } now)
                        continue;
                    if (previous.TryGetValue(pid, out var before))
                    {
                        // Percent of one core over the last second.
                        var cpu = (now.Ticks - before) / TicksPerSecond * 100;
                        if (!_samples.TryGetValue(host, out var list))
                            _samples[host] = list = [];
                        list.Add((cpu, now.RssMb));
                    }

                    previous[pid] = now.Ticks;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public object Summary() => _samples.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => new
    {
        CpuAveragePercentOfOneCore = Math.Round(s.Value.Average(v => v.Cpu), 1),
        CpuMaxPercentOfOneCore = Math.Round(s.Value.Max(v => v.Cpu), 1),
        MemoryMaxMb = Math.Round(s.Value.Max(v => v.RssMb), 0),
        Samples = s.Value.Count
    });

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
