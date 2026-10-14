using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// Reads the instruments of one meter instance (never another instance of the same name, which a parallel test may
/// create): counters add up their measurements, gauges are read when asked (ARV-125a).
/// </summary>
public sealed class InstrumentReader : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentQueue<(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags)> _measured = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags)> _observed = new();
    private readonly object _observing = new();

    public InstrumentReader(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>The sum of a counter's measurements so far whose tags include <paramref name="tags"/>.</summary>
    public double Sum(string instrument, params (string Key, string Value)[] tags) =>
        _measured.Where(m => m.Instrument == instrument && Matches(m.Tags, tags)).Sum(m => m.Value);

    /// <summary>A gauge's measurements now (empty when it reports nothing), each with its tags.</summary>
    public IReadOnlyList<(double Value, IReadOnlyDictionary<string, string> Tags)> Observe(string instrument)
    {
        lock (_observing)
        {
            _observed.Clear();
            _listener.RecordObservableInstruments();
            return [.. _observed.Where(m => m.Instrument == instrument).Select(m => (m.Value, m.Tags))];
        }
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = tag.Value?.ToString();
        (instrument.IsObservable ? _observed : _measured).Enqueue((instrument.Name, value, copy));
    }

    private static bool Matches(IReadOnlyDictionary<string, string> tags, (string Key, string Value)[] wanted) =>
        wanted.All(w => tags.TryGetValue(w.Key, out var value) && value == w.Value);

    public void Dispose() => _listener.Dispose();
}
