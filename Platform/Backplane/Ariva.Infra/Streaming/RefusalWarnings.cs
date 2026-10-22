using Ariva.Core.Queueing;

namespace Ariva.Infra.Streaming;

/// <summary>
/// ARV-231: the queue stream worker warns of each zone's refusals at most once a minute per zone and kind. A checkpoint adds the
/// refusals its outputs carried; <see cref="Due"/> gives, for each zone and kind, the refusals since its last warning once a
/// minute has passed since it (the first one at once), so a burst of refusals is one line a minute, and a quiet minute after
/// one still says how many followed it. The names only: a zone key and a kind, never an event's content. Bounded: entries
/// with nothing left to say are forgotten once their minute has passed.
/// </summary>
internal sealed class RefusalWarnings(TimeProvider timeProvider)
{
    #region Fields

    /// <summary>The longest a zone and kind stay quiet between warnings.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<(string Zone, string Kind), (DateTimeOffset? Warned, long Since)> _state = [];

    #endregion

    #region Methods

    /// <summary>Adds the refusals a checkpoint wrote for <paramref name="zone"/>.</summary>
    public void Add(string zone, IEnumerable<QueueRefusalCount> refusals)
    {
        ArgumentException.ThrowIfNullOrEmpty(zone);
        ArgumentNullException.ThrowIfNull(refusals);
        foreach (var (kind, count) in refusals)
        {
            if (count <= 0 || string.IsNullOrEmpty(kind))
                continue;
            var key = (zone, kind);
            var (warned, since) = _state.GetValueOrDefault(key);
            _state[key] = (warned, since + count);
        }
    }

    /// <summary>The warnings due now: each zone and kind with refusals since its last warning, a minute or more after it.</summary>
    public IReadOnlyList<(string Zone, string Kind, long Count)> Due()
    {
        var now = timeProvider.GetUtcNow();
        var due = new List<(string Zone, string Kind, long Count)>();
        foreach (var (key, (warned, since)) in _state.ToList())
        {
            var quiet = warned is not { } last || now - last >= Interval;
            if (since > 0 && quiet)
            {
                due.Add((key.Zone, key.Kind, since));
                _state[key] = (now, 0);
            }
            else if (since == 0 && quiet)
            {
                _state.Remove(key);
            }
        }

        return [.. due.OrderBy(d => d.Zone, StringComparer.Ordinal).ThenBy(d => d.Kind, StringComparer.Ordinal)];
    }

    #endregion
}
