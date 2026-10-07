using System.Runtime.CompilerServices;
using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Aman;

/// <summary>
/// What one border desk completed in one closed one-minute interval (ARV-117c): transactions (approaches), documents
/// (people), the mean and 90th percentile service time of those transactions and their mean cycle time, in seconds.
/// Aggregates only: no passenger, officer or document identity exists in the process that produces them.
/// </summary>
/// <remarks>
/// <see cref="MeanBusyCycleSeconds"/> is not published: the same mean cycle counted in the desk's working time only (idle
/// minutes left out), for the scenario tests' comparison of what idle time does to AMAN's cycle time.
/// </remarks>
internal readonly record struct DeskIntervalFigures(int Transactions, int Documents, double MeanServiceSeconds, double P90ServiceSeconds, double MeanCycleSeconds,
    double MeanBusyCycleSeconds)
{
    public static readonly DeskIntervalFigures Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The served passengers of the scenario as discrete transactions at each border desk (ARV-117c), from which the AMAN
/// emulator reports its interval statistics. Per desk and minute the scenario's lane serves a fluid number of people
/// (<see cref="ScenarioDay.D"/>), shared between the lane's active desks by their speed, as the emulator always shared it.
/// Each desk works through a deterministic sequence of transactions: a family of 1 (80 percent), 2 (15 percent) or 3
/// (5 percent) people, mean 1.25, each needing its people times the desk's service time per person times a variation
/// (0.5 plus an exponential of mean 0.5, capped at 4; mean about 1, P90 about 1.65). A minute's served share is the work
/// the desk does in it, from the minute's start, at the desk's own speed (faster when the scenario's capacity noise
/// serves more than the desk's nominal rate). A transaction is counted in the interval in which it completes, whole:
/// <list type="bullet">
/// <item>Service time: the desk's working seconds on the transaction, start to end (idle time between minutes when the
/// lane runs dry is not service).</item>
/// <item>Cycle time: from the previous transaction's start at the desk to this one's start, in open time (AMAN's "start to
/// next start while open": paused minutes do not count, idle minutes do). The first transaction of a session has no
/// previous start; its cycle is its own service time (the scenario has no walk-up gap, so a desk serving back to back
/// has a cycle equal to its service time). A gap beyond the contract's 3,600 seconds is treated the same way.</item>
/// <item>A pause keeps the transaction in progress; a close (or out of service) drops it and ends the session.</item>
/// </list>
/// The interval's mean cycle is the mean over its completed transactions, so Ariva's transaction-weighted lane cycle time
/// (F10) is the mean cycle per transaction. Deterministic for a day; computed once per day, for every immigration
/// desk of both sides, and cached while the day lives.
/// </summary>
internal static class AmanDeskProcess
{
    #region Fields

    private const double MaxSeconds = 3_600;
    private const double MaxVariation = 4;

    private static readonly ConditionalWeakTable<ScenarioDay, Lazy<Dictionary<(int Q, int K), Dictionary<int, DeskIntervalFigures>>>> Days = new();

    /// <summary>The immigration queues (arrival and departure, every lane) whose desks AMAN reports.</summary>
    internal static readonly string[] Queues = ["A-CRW", "A-CIT", "A-RES", "A-VIS", "D-CRW", "D-CIT", "D-RES", "D-VIS"];

    #endregion

    #region Methods

    /// <summary>What desk <paramref name="k"/> of queue <paramref name="q"/> completed in demo minute <paramref name="minute"/>.</summary>
    public static DeskIntervalFigures Of(ScenarioDay day, int q, int k, int minute)
    {
        ArgumentNullException.ThrowIfNull(day);
        var table = Days.GetValue(day, d => new Lazy<Dictionary<(int, int), Dictionary<int, DeskIntervalFigures>>>(() => Build(d), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return table.TryGetValue((q, k), out var desk) && desk.TryGetValue(minute, out var figures) ? figures : DeskIntervalFigures.Empty;
    }

    /// <summary>The family size of transaction <paramref name="n"/> at a desk: 1, 2 or 3 people, mean 1.25.</summary>
    internal static int FamilySize(double unit) => unit < 0.80 ? 1 : unit < 0.95 ? 2 : 3;

    /// <summary>A service time variation of mean about 1: 0.5 plus an exponential of mean 0.5, at most <see cref="MaxVariation"/>.</summary>
    internal static double Variation(double unit) => Math.Min(MaxVariation, 0.5 - 0.5 * Math.Log(1 - Math.Min(unit, 0.999_999)));

    private static Dictionary<(int, int), Dictionary<int, DeskIntervalFigures>> Build(ScenarioDay day)
    {
        var result = new Dictionary<(int, int), Dictionary<int, DeskIntervalFigures>>();
        foreach (var queue in Queues)
        {
            var q = ScenarioModel.Q(queue);
            var servers = ScenarioModel.Queues[q].Servers.Count;
            var desks = Enumerable.Range(0, servers).Select(k => new Desk(day.Seed, ScenarioModel.Queues[q].Servers[k])).ToArray();
            for (var minute = -ScenarioModel.Pre; minute < ScenarioModel.N - ScenarioModel.Pre; minute++)
            {
                var now = day.ServerStates(q, minute);
                var served = day.D[q][minute + ScenarioModel.Pre];
                var active = now.Where(s => s.State is "serving" or "idle" or "unknown").ToList();
                var speed = active.Sum(s => 1 / Math.Max(s.Svc, 1));
                for (var k = 0; k < now.Count; k++)
                {
                    var desk = desks[k];
                    switch (now[k].State)
                    {
                        case "closed" or "oos":
                            desk.Close();
                            continue;
                        case "paused":
                            continue;
                    }

                    var svc = Math.Max(now[k].Svc, 1);
                    var share = speed > 0 ? served * (1 / svc) / speed : 0;
                    if (desk.Work(share, svc) is { Transactions: > 0 } figures)
                    {
                        if (!result.TryGetValue((q, k), out var minutes))
                            result[(q, k)] = minutes = [];
                        minutes[minute] = figures;
                    }
                }
            }
        }

        return result;
    }

    #endregion

    #region Desk

    /// <summary>One desk's transactions in progress, in open seconds since the start of the run.</summary>
    private sealed class Desk(uint seed, string code)
    {
        private readonly uint _stream = seed ^ ScenarioMath.StrHash(code ?? string.Empty) ^ 0xa3a7u;
        private readonly List<(int People, double Service, double Cycle, double BusyCycle)> _done = [];
        private double _open;
        private double? _previousStart;
        private double _busy;
        private double _previousBusyStart;
        private double _busyStart;
        private int _index;
        private bool _active;
        private int _people;
        private double _need;
        private double _worked;
        private double _start;
        private double _serviceSeconds;

        /// <summary>A close ends the session: the transaction in progress is dropped and the next has no previous start.</summary>
        public void Close()
        {
            _active = false;
            _previousStart = null;
        }

        /// <summary>One open minute in which the desk does <paramref name="share"/> people's worth of work at <paramref name="svc"/> seconds a person.</summary>
        public DeskIntervalFigures Work(double share, double svc)
        {
            _done.Clear();
            // Seconds per unit of work: the desk's own, or faster when the scenario's capacity noise serves more in the minute.
            var perUnit = share * svc > 60 ? 60 / share : svc;
            var remaining = share;
            var offset = 0.0;
            while (remaining > 1e-9)
            {
                if (!_active)
                    Begin(_open + offset);
                var left = _need - _worked;
                if (remaining + 1e-12 < left)
                {
                    _worked += remaining;
                    _serviceSeconds += remaining * perUnit;
                    _busy += remaining * perUnit;
                    remaining = 0;
                    break;
                }

                offset += left * perUnit;
                remaining -= left;
                _serviceSeconds += left * perUnit;
                _busy += left * perUnit;
                var service = Math.Min(_serviceSeconds, MaxSeconds);
                var chained = _previousStart is { } previous && _start - previous <= MaxSeconds;
                var cycle = chained ? _start - (_previousStart ?? _start) : service;
                _done.Add((_people, service, cycle, chained ? _busyStart - _previousBusyStart : service));
                _previousStart = _start;
                _previousBusyStart = _busyStart;
                _active = false;
            }

            _open += 60;
            if (_done.Count == 0)
                return DeskIntervalFigures.Empty;
            var services = _done.Select(d => d.Service).Order().ToList();
            var p90 = services[(int)Math.Ceiling(0.9 * services.Count) - 1];
            return new DeskIntervalFigures(_done.Count, _done.Sum(d => d.People), Math.Round(services.Average(), 1), Math.Round(p90, 1),
                Math.Round(Math.Min(_done.Average(d => d.Cycle), MaxSeconds), 1), Math.Round(Math.Min(_done.Average(d => d.BusyCycle), MaxSeconds), 1));
        }

        private void Begin(double at)
        {
            _index++;
            _people = FamilySize(Unit(1));
            _need = _people * Variation(Unit(2));
            _worked = 0;
            _serviceSeconds = 0;
            _start = at;
            _busyStart = _busy;
            _active = true;
        }

        private double Unit(int salt) => ScenarioMath.H3(_stream, _index, salt);
    }

    #endregion
}
