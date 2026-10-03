namespace Ariva.Simulation.Api.Emulators;

/// <summary>
/// An emulator that plays the demo day minute by minute on the shared demo clock (ARV-029). The clock is the sensor
/// emulator's (<c>api/v1/simulation/sensors</c>: start, pause, speed, jump, stop minute): each time a demo minute
/// completes, the sensors push it and every sink plays it, so sensors, AODB and AMAN describe the same moment.
/// <paramref name="wallOf"/> gives the wall time of a demo minute at the clock's speed.
/// </summary>
public interface IDemoMinuteSink
{
    Task PlayAsync(int minute, Func<double, DateTime> wallOf, CancellationToken ct);
}

/// <summary>
/// The UTC time the feeds give a demo minute (ARV-029). Feed records need whole minutes (AMAN's one-minute intervals,
/// flight times), so the demo day is laid on real time once: when a run starts or jumps to minute m, minute m is the
/// current wall minute, and every later minute follows one minute apart whatever the speed. At speed 1 the feeds stay
/// on real time; faster, they run ahead of it (Ariva may treat far-future records as implausible).
/// </summary>
public sealed class FeedTime
{
    private readonly Lock _gate = new();
    private DateTime _dayStart;
    private int _last = int.MinValue;

    /// <summary>
    /// Re-anchors when <paramref name="minute"/> neither repeats nor follows the last one played (another feed may have
    /// played it already); returns the minute's UTC time.
    /// </summary>
    public DateTime Observe(int minute, Func<double, DateTime> wallOf)
    {
        ArgumentNullException.ThrowIfNull(wallOf);
        lock (_gate)
        {
            if ((minute != _last + 1 && minute != _last) || _dayStart == default)
            {
                var wall = wallOf(minute);
                _dayStart = new DateTime(wall.Ticks - wall.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-minute);
            }

            _last = minute;
            return _dayStart.AddMinutes(minute);
        }
    }

    /// <summary>The UTC time of a demo minute on the current anchor (the anchor is set by the first minute played).</summary>
    public DateTime At(int minute)
    {
        lock (_gate)
            return _dayStart == default ? default : _dayStart.AddMinutes(minute);
    }
}
