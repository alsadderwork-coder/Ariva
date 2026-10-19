namespace Ariva.Api.Common.Logging;

/// <summary>
/// Bounds how many events of one kind a host process writes per second (ARV-091, CWE-400, CWE-779), with a share per source
/// so a flood from one source cannot hide another's (CWE-223, CWE-778). In each second:
/// <list type="bullet">
/// <item>a source (the caller, or the client address before authentication) writes at most its share;</item>
/// <item>sources are counted in a map of at most <c>maxSources</c> keys, reset each second; sources beyond it share one
/// overflow bucket with the same share, so the map never grows with the flood;</item>
/// <item>all sources together write at most the kind's ceiling.</item>
/// </list>
/// An event left out is counted; <see cref="TakeSuppressed"/> hands the count to the periodic summary
/// (<see cref="SecurityEventLog.WriteSummaries"/>). A flood costs a short lock and a counter per request instead of a line.
/// </summary>
public sealed class SecurityEventThrottle
{
    #region Fields

    private readonly int _perSecond;
    private readonly int _perSource;
    private readonly int _maxSources;
    private readonly Func<long> _milliseconds;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _sources = new(StringComparer.Ordinal);
    private long _second = long.MinValue;
    private int _written;
    private int _overflow;
    private long _suppressed;

    #endregion

    #region Constructors

    /// <summary>A throttle of <paramref name="perSecond"/> events a second, <paramref name="perSource"/> of them per source.</summary>
    /// <param name="perSecond">Events of the kind written per second at most.</param>
    /// <param name="perSource">Events one source writes per second at most.</param>
    /// <param name="maxSources">Sources counted apart per second; the rest share the overflow bucket.</param>
    /// <param name="milliseconds">A monotonic clock in milliseconds; <see cref="Environment.TickCount64"/> by default.</param>
    public SecurityEventThrottle(int perSecond, int perSource, int maxSources, Func<long> milliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perSource, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSources, 1);
        _perSecond = perSecond;
        _perSource = perSource;
        _maxSources = maxSources;
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }

    #endregion

    #region Methods

    /// <summary>Takes a place for an event of <paramref name="source"/> in the current second, or counts it as left out.</summary>
    /// <param name="source">The source's key; null shares the overflow bucket.</param>
    /// <returns>Whether the event may be written.</returns>
    public bool TryTake(string source)
    {
        var second = _milliseconds() / 1000;
        lock (_gate)
        {
            if (second != _second)
            {
                _second = second;
                _written = 0;
                _overflow = 0;
                _sources.Clear();
            }

            if (_written >= _perSecond)
                return Suppress();

            if (source is not null && _sources.TryGetValue(source, out var used))
            {
                if (used >= _perSource)
                    return Suppress();
                _sources[source] = used + 1;
            }
            else if (source is not null && _sources.Count < _maxSources)
                _sources[source] = 1;
            else if (_overflow >= _perSource)
                return Suppress();
            else
                _overflow++;

            _written++;
            return true;
        }
    }

    /// <summary>The events left out since the last call, and resets the count.</summary>
    /// <returns>How many.</returns>
    public long TakeSuppressed() => Interlocked.Exchange(ref _suppressed, 0);

    private bool Suppress()
    {
        Interlocked.Increment(ref _suppressed);
        return false;
    }

    #endregion
}
