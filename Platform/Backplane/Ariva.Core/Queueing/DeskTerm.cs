namespace Ariva.Core.Queueing;

/// <summary>
/// The desk term of the nowcast (F8) for one queue at a minute: n_open, the desks Idle or Serving, and c, the mean cycle
/// time per desk in minutes (null when no transaction was seen in the window). <see cref="Degraded"/> when a desk's state
/// was Unknown or degraded, or a desk's minutes stopped before the others'.
/// </summary>
public sealed record DeskTerm(DateTime AsOfMinuteUtc, int OpenServers, double? CycleMinutes, bool Degraded, int Desks)
{
    /// <summary>
    /// The same desks' term without any AMAN input (ARV-117, <see cref="DeskTerms.SensorOnly"/>), for the shadow nowcast
    /// only; null when the sensors say nothing about how many desks are open.
    /// </summary>
    public DeskTerm SensorOnly { get; init; }

    /// <summary>
    /// On the sensor-only part only (ARV-117b): the lane's sensor-only busy time over the sensor cycle time's window, as
    /// aggregates (no desk key), from which the stream gives the shadow its cycle time with the queue's exits
    /// (<see cref="SensorCycle"/>); null when the term was built without the sensor cycle time's settings.
    /// </summary>
    public SensorBusyWindow SensorBusy { get; init; }
}

/// <summary>
/// One desk's minute as the sensor-only desk engine wrote it (ARV-117a, <c>desk_sensor_minute</c>): its open (Idle or
/// Serving) and Unknown seconds and its F11 flag from the staff and service zones alone, whatever AMAN said, and
/// (ARV-117b) its Serving seconds (staff present and the service zone occupied), the busy time of the sensor cycle time.
/// For the shadow nowcast's desk term only (<see cref="DeskTerms.SensorOnly"/>).
/// </summary>
public sealed record DeskSensorSample(double OpenSeconds, double UnknownSeconds, bool Degraded, double ServingSeconds = 0)
{
    /// <summary>The open seconds as a number of seconds of the minute (0 when not a finite number).</summary>
    public double Open => double.IsFinite(OpenSeconds) ? Math.Clamp(OpenSeconds, 0, 60) : 0;

    /// <summary>Whether the minute flags the term, as a published minute does: flagged, Unknown for half of it, or not a number.</summary>
    public bool Unknown => Degraded || !double.IsFinite(OpenSeconds) || !double.IsFinite(UnknownSeconds) || UnknownSeconds >= 30;
}

/// <summary>
/// One desk's closed minute as the desk state engine wrote it (<c>desk_minute</c>, F10). <see cref="SensorDerivedSeconds"/>
/// (ARV-116) is the part of its Idle and Serving time whose state came from its staff and service zones alone.
/// <see cref="Sensor"/> (ARV-117a) is the same desk and minute from the sensor-only desk engine, null for a desk without
/// staff or service zones or a minute written before that engine ran.
/// </summary>
public sealed record DeskMinuteSample(string DeskKey, DateTime MinuteUtc, double IdleSeconds, double ServingSeconds, double UnknownSeconds, int Transactions, bool Degraded,
    double SensorDerivedSeconds = 0, DeskSensorSample Sensor = null)
{
    /// <summary>tau (F9): the seconds the desk was open for throughput.</summary>
    public double OpenSeconds => Math.Max(0, IdleSeconds) + Math.Max(0, ServingSeconds);

    /// <summary>The open seconds the desk's zones alone proved (never more than <see cref="OpenSeconds"/>).</summary>
    public double SensorOpenSeconds => double.IsFinite(SensorDerivedSeconds) ? Math.Clamp(SensorDerivedSeconds, 0, OpenSeconds) : 0;
}

/// <summary>
/// Builds a queue's <see cref="DeskTerm"/> from the closed minutes of the desks serving it (ARV-064). The latest minute
/// any of them has closed is the term's minute: a desk is open there when it was Idle or Serving for at least half of it
/// (n_open), and Unknown for half of it, or degraded, flags the term; with no desk open and any Unknown there is no term
/// (n_open is not known, which is not "nothing open"). c is the lane cycle time from the border system's interval
/// statistics when given (F10, <see cref="Ariva.Core.Desks.LaneCycle"/>); otherwise the open time per transaction over the
/// last <c>windowMinutes</c> minutes up to that minute, across the desks (when queues are long, desks are serving nearly
/// all of their open time, so this is the cycle time; with short queues it is longer, which only lowers the desk term).
/// Pure.
/// </summary>
public static class DeskTerms
{
    private const double HalfMinuteSeconds = 30;

    /// <summary>
    /// The published term with its sensor-only part; with <paramref name="sensorCycle"/> the sensor-only part also carries
    /// the busy time of the sensor cycle time's window (ARV-117b, <see cref="DeskTerm.SensorBusy"/>), which the published
    /// term never reads.
    /// </summary>
    public static DeskTerm Compute(IEnumerable<DeskMinuteSample> minutes, int windowMinutes, DateTime notAfterUtc, double? laneCycleMinutes = null,
        SensorCycleSettings sensorCycle = null)
    {
        var published = Published(minutes, windowMinutes, notAfterUtc, laneCycleMinutes);
        return published is null ? null : published with { SensorOnly = SensorOnly(minutes, windowMinutes, notAfterUtc, sensorCycle) };
    }

    private static DeskTerm Published(IEnumerable<DeskMinuteSample> minutes, int windowMinutes, DateTime notAfterUtc, double? laneCycleMinutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        if (windowMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(windowMinutes), "The window is from 1 to 60 minutes.");
        var usable = minutes.Where(m => m is not null && m.DeskKey is not null && m.MinuteUtc <= notAfterUtc &&
                                        double.IsFinite(m.IdleSeconds) && double.IsFinite(m.ServingSeconds) && double.IsFinite(m.UnknownSeconds)).ToList();
        if (usable.Count == 0)
            return null;

        var asOf = usable.Max(m => m.MinuteUtc);
        var latest = usable.GroupBy(m => m.DeskKey, StringComparer.Ordinal).Select(g => System.Linq.Enumerable.MaxBy(g, m => m.MinuteUtc)).ToList();
        var current = latest.Where(m => m.MinuteUtc == asOf).ToList();
        var open = current.Count(m => m.OpenSeconds >= HalfMinuteSeconds);
        // A desk whose minutes stopped before the others' (its feed is behind) is not counted, and the term says so.
        var unknown = current.Any(m => m.Degraded || m.UnknownSeconds >= HalfMinuteSeconds);
        var degraded = current.Count < latest.Count || unknown;
        // "Nothing open" needs every desk known to be closed: with none open and any Unknown, n_open is not known.
        if (open == 0 && unknown)
            return null;

        var from = asOf.AddMinutes(-windowMinutes);
        var window = usable.Where(m => m.MinuteUtc > from).ToList();
        var transactions = window.Sum(m => (long)Math.Max(0, m.Transactions));
        var openMinutes = window.Sum(m => m.OpenSeconds) / 60;
        // The lane cycle time from AMAN's own interval statistics (F10, LaneCycle) when there is one; otherwise open time per
        // transaction from the desk minutes.
        double? cycle = laneCycleMinutes is { } lane && double.IsFinite(lane) && lane > 0
            ? lane
            : transactions > 0 && openMinutes > 0 ? openMinutes / transactions : null;
        return new DeskTerm(asOf, open, cycle, degraded, latest.Count);
    }

    /// <summary>
    /// The sensor-only desk term of the shadow nowcast (ARV-117, F8): the term <see cref="Compute"/> gives with no AMAN
    /// input. n_open counts the desks open for at least half of the term's minute by their staff and service zones alone.
    /// Since ARV-117a a desk with zones has its minute from the sensor-only desk engine (<see cref="DeskMinuteSample.Sensor"/>),
    /// which never sees AMAN: open when the zones made it Idle or Serving for half the minute, flagging the term when its
    /// minute is flagged or half Unknown, whatever AMAN said (so at a site where every desk has an AMAN code, n_open comes from the
    /// zones while AMAN is live). A desk without that minute (no zones, or a minute written before ARV-117a) falls back to
    /// the published minute's sensor-derived seconds (F10 rows 6 and 7, ARV-117): a desk open only through AMAN (a
    /// session, a recent transaction) is not seen by the sensors, so it is treated as an Unknown desk is. An Unknown desk
    /// flags the term, and with no desk open from the sensors and any Unknown there is no term (n_open is not known,
    /// which is not "nothing open"). c is null here: AMAN's interval statistics and the transactions the desk minutes count
    /// both come from AMAN. Since ARV-117b the term carries, with <paramref name="sensorCycle"/>, the lane's sensor-only
    /// busy time over the window ending at its minute (<see cref="DeskTerm.SensorBusy"/>), and the stream turns it into
    /// the sensor cycle time with the queue's exits (<see cref="SensorCycle"/>);
    /// without one the nowcast falls back as F8 does (the exit term alone, or no service when nothing is open). Where every
    /// open desk minute is sensor-derived and no AMAN statistics exist (a site without AMAN), the sensor-only n_open equals
    /// the published one. Pure.
    /// </summary>
    public static DeskTerm SensorOnly(IEnumerable<DeskMinuteSample> minutes, int windowMinutes, DateTime notAfterUtc, SensorCycleSettings sensorCycle = null)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        if (windowMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(windowMinutes), "The window is from 1 to 60 minutes.");
        var usable = minutes.Where(m => m is not null && m.DeskKey is not null && m.MinuteUtc <= notAfterUtc &&
                                        double.IsFinite(m.IdleSeconds) && double.IsFinite(m.ServingSeconds) && double.IsFinite(m.UnknownSeconds)).ToList();
        if (usable.Count == 0)
            return null;

        var asOf = usable.Max(m => m.MinuteUtc);
        var latest = usable.GroupBy(m => m.DeskKey, StringComparer.Ordinal).Select(g => System.Linq.Enumerable.MaxBy(g, m => m.MinuteUtc)).ToList();
        var current = latest.Where(m => m.MinuteUtc == asOf).ToList();
        // ARV-117a: the sensor-only engine's minute when there is one (AMAN never reaches it); otherwise ARV-117's reading
        // of the published minute, where a desk open through AMAN only is a desk the sensors cannot see: Unknown to the shadow.
        var open = current.Count(m => m.Sensor is { } s ? s.Open >= HalfMinuteSeconds : m.SensorOpenSeconds >= HalfMinuteSeconds);
        var unknown = current.Any(m => m.Sensor is { } s
            ? s.Unknown
            : (m.OpenSeconds >= HalfMinuteSeconds && m.SensorOpenSeconds < HalfMinuteSeconds) || m.Degraded || m.UnknownSeconds >= HalfMinuteSeconds);
        var degraded = current.Count < latest.Count || unknown;
        if (open == 0 && unknown)
            return null;
        return new DeskTerm(asOf, open, null, degraded, latest.Count)
        {
            SensorBusy = sensorCycle is null ? null : SensorCycle.Window(usable, asOf, sensorCycle)
        };
    }
}
