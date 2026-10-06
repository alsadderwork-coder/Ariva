using System.Globalization;
using System.Text;
using System.Text.Json;
using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Sensors;

/// <summary>The dialects the emulator speaks: Ariva's canonical events and the Xovis firmware 5 data push.</summary>
public enum EmulatedDialect
{
    Canonical,
    Xovis
}

/// <summary>
/// What a sensor sends. The first sensor of a queue zone counts the zone (per-passenger crossings of its entry and exit
/// lines and its occupancy); the first sensor of an overflow band reports the band's occupancy; every other sensor
/// only reports that it is alive.
/// </summary>
public enum SensorRole
{
    QueueLead,
    OverflowLead,
    Heartbeat
}

/// <summary>One push for one sensor and one demo minute: the Ingest path, the JSON body and how many events Ingest should accept.</summary>
public sealed record SensorPush(string Sensor, string Path, string Json, int ExpectedAccepted, int Crossings, int Occupancy, int Intervals);

/// <summary>
/// Turns the simulated day into device traffic, deterministically (ARV-028). Passengers are the whole people of the
/// fluid model: passenger n of a queue enters when the cumulative arrivals reach n and leaves when the cumulative
/// departures reach n (FIFO), so entry and exit crossings carry the same track id and the occupancy is always entries
/// minus exits. Demo times become wall times through <c>wallOf</c>, so that Ingest's clock checks see live traffic.
/// </summary>
internal static class SensorTraffic
{
    private const double Epsilon = 1e-9;

    /// <summary>The queue zone a sensor's device belongs to: its own zone, or the queue an overflow band feeds.</summary>
    public static string QueueZoneOf(string sensorZone) => sensorZone switch
    {
        "A-OV" => "A-VIS",
        "D-OV" => "D-VIS",
        "SEC-OV" => "SEC-N",
        _ => sensorZone
    };

    /// <summary>Whether a queue zone has an overflow band with a lead sensor (A-VIS, D-VIS and SEC-N).</summary>
    public static bool HasOverflowBand(string queueZone) =>
        ScenarioModel.Sensors.Any(s => RoleOf(s) == SensorRole.OverflowLead && string.Equals(QueueZoneOf(s.Zone), queueZone, StringComparison.Ordinal));

    /// <summary>The scenario sensor with this id, or null.</summary>
    public static SensorDef Sensor(string id) => ScenarioModel.Sensors.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public static SensorRole RoleOf(SensorDef sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        if (sensor.Slot != 0)
            return SensorRole.Heartbeat;
        return ScenarioModel.QueueIndex.ContainsKey(sensor.Zone) ? SensorRole.QueueLead : SensorRole.OverflowLead;
    }

    /// <summary>
    /// The push of <paramref name="sensor"/> for demo minute <paramref name="minute"/> (0 to 1439), or null while the
    /// sensor is offline in the scenario (it sends nothing).
    /// </summary>
    internal static SensorPush Build(ScenarioDay day, SensorDef sensor, EmulatedDialect dialect, int minute, long packageId,
        Func<double, DateTime> wallOf, DateTime sentUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(wallOf);
        if (minute is < 0 or >= ScenarioModel.Day)
            throw new ArgumentOutOfRangeException(nameof(minute));
        if (ScenarioDay.SensorOffline(sensor.Id, minute))
            return null;

        var queueZone = QueueZoneOf(sensor.Zone);
        var role = RoleOf(sensor);
        var path = $"api/v1/ingest/zones/{Uri.EscapeDataString(queueZone)}/{(dialect == EmulatedDialect.Xovis ? "xovis" : "events")}";
        var from = wallOf(minute);
        var to = wallOf(minute + 1);

        var entries = new List<(string Track, DateTime Time)>();
        var exits = new List<(string Track, DateTime Time)>();
        var occupancy = new List<(string Zone, int Count)>();
        if (role == SensorRole.QueueLead)
        {
            var q = ScenarioModel.Q(queueZone);
            var i = minute + ScenarioModel.Pre;
            Passengers(day.CumA[q], day.A[q], i, queueZone, minute, wallOf, entries);
            Passengers(day.CumD[q], day.D[q], i, queueZone, minute, wallOf, exits);
            // A queue with an overflow band counts up to its snake capacity (the run's, ARV-115); the band's lead counts the
            // rest, so that Ariva's sum of the queue zone and its bands is the queue (ARV-064).
            var inQueue = OccupancyAt(day, q, i);
            if (HasOverflowBand(queueZone) && day.SnakeCapacity(queueZone) is { } capacity)
                inQueue = Math.Min(inQueue, (int)capacity);
            occupancy.Add((queueZone, inQueue));
        }
        else if (role == SensorRole.OverflowLead)
        {
            var q = ScenarioModel.Q(queueZone);
            var occupied = OccupancyAt(day, q, minute + ScenarioModel.Pre);
            var cap = day.SnakeCapacity(queueZone) is { } c ? (int)c : int.MaxValue;
            occupancy.Add((sensor.Zone, Math.Max(0, occupied - cap)));
        }

        return dialect == EmulatedDialect.Xovis
            ? Xovis(sensor, role, queueZone, path, packageId, from, to, entries.Count, exits.Count, occupancy, sentUtc)
            : Canonical(sensor, role, queueZone, path, packageId, entries, exits, occupancy, to, sentUtc);
    }

    /// <summary>People in the queue at the end of minute index <paramref name="i"/>: whole entries minus whole exits.</summary>
    private static int OccupancyAt(ScenarioDay day, int q, int i) =>
        Whole(day.CumA[q][i + 1]) - Whole(day.CumD[q][i + 1]);

    private static int Whole(double cumulative) => (int)Math.Floor(cumulative + Epsilon);

    private static void Passengers(double[] cumulative, double[] perMinute, int i, string zone, int minute, Func<double, DateTime> wallOf,
        List<(string Track, DateTime Time)> into)
    {
        var before = Whole(cumulative[i]);
        var after = Whole(cumulative[i + 1]);
        for (var n = before + 1; n <= after; n++)
        {
            var fraction = perMinute[i] > Epsilon ? Math.Clamp((n - cumulative[i]) / perMinute[i], 0, 0.999) : 0;
            into.Add((zone + "." + n.ToString(CultureInfo.InvariantCulture), wallOf(minute + fraction)));
        }
    }

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static SensorPush Canonical(SensorDef sensor, SensorRole role, string queueZone, string path, long packageId,
        List<(string Track, DateTime Time)> entries, List<(string Track, DateTime Time)> exits, List<(string Zone, int Count)> occupancy,
        DateTime minuteEnd, DateTime sentUtc)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("sentUtc", Time(sentUtc));
            w.WriteNumber("packageId", packageId);
            if (role == SensorRole.QueueLead)
            {
                w.WriteStartArray("crossings");
                foreach (var (track, time) in entries)
                    Crossing(w, queueZone + " entry", "In", track, time);
                foreach (var (track, time) in exits)
                    Crossing(w, queueZone + " exit", "Out", track, time);
                w.WriteEndArray();
            }

            if (occupancy.Count > 0)
            {
                w.WriteStartArray("occupancy");
                foreach (var (zone, count) in occupancy)
                {
                    w.WriteStartObject();
                    w.WriteString("zoneName", zone);
                    w.WriteNumber("count", count);
                    w.WriteString("timeUtc", Time(minuteEnd));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteStartObject("status");
            w.WriteBoolean("online", true);
            w.WriteString("timeUtc", Time(minuteEnd));
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var crossings = entries.Count + exits.Count;
        return new SensorPush(sensor.Id, path, Encoding.UTF8.GetString(buffer.ToArray()), crossings + occupancy.Count + 1, crossings, occupancy.Count, 0);
    }

    private static void Crossing(Utf8JsonWriter w, string line, string direction, string track, DateTime time)
    {
        w.WriteStartObject();
        w.WriteString("lineName", line);
        w.WriteString("direction", direction);
        w.WriteString("trackId", track);
        w.WriteString("timeUtc", Time(time));
        w.WriteEndObject();
    }

    /// <summary>
    /// A Xovis firmware 5 logics push for the minute: line logics named like the zone profile's lines (forward counts
    /// in, backward counts out) and a balance logic named like the zone. A heartbeat sensor sends the envelope with no
    /// logics, which Ingest takes as a sign of life.
    /// </summary>
    private static SensorPush Xovis(SensorDef sensor, SensorRole role, string queueZone, string path, long packageId, DateTime from, DateTime to,
        int entries, int exits, List<(string Zone, int Count)> occupancy, DateTime sentUtc)
    {
        using var buffer = new MemoryStream();
        var logicId = 1000;
        var intervals = 0;
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("logics_data");
            w.WriteStartObject("package_info");
            w.WriteString("version", "5.0");
            w.WriteNumber("id", packageId);
            w.WriteNumber("agent_id", 1000);
            w.WriteEndObject();
            w.WriteStartObject("sensor_info");
            w.WriteString("serial_number", SerialOf(sensor.Id));
            w.WriteString("type", "SINGLE_SENSOR");
            w.WriteString("name", sensor.Id);
            w.WriteString("time", Time(sentUtc));
            w.WriteEndObject();
            w.WriteStartArray("logics");
            if (role == SensorRole.QueueLead)
            {
                Logic(w, logicId++, queueZone + " entry", from, to, ("fw", entries), ("bw", 0));
                Logic(w, logicId++, queueZone + " exit", from, to, ("fw", 0), ("bw", exits));
                intervals = 2;
            }

            foreach (var (zone, count) in occupancy)
                Logic(w, logicId++, zone, from, to, ("balance", count));
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return new SensorPush(sensor.Id, path, Encoding.UTF8.GetString(buffer.ToArray()), intervals + occupancy.Count, 0, occupancy.Count, intervals);
    }

    private static void Logic(Utf8JsonWriter w, int id, string name, DateTime from, DateTime to, params (string Name, int Value)[] counts)
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("name", name);
        w.WriteStartArray("records");
        w.WriteStartObject();
        w.WriteString("from", Time(from));
        w.WriteString("to", Time(to));
        w.WriteNumber("samples", 1);
        w.WriteNumber("samples_expected", 1);
        w.WriteStartArray("counts");
        var k = 1;
        foreach (var (name2, value) in counts)
        {
            w.WriteStartObject();
            w.WriteNumber("id", id * 1000 + k++);
            w.WriteString("name", name2);
            w.WriteNumber("value", value);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>A stable fake MAC address per sensor, in Xovis's serial number format.</summary>
    private static string SerialOf(string sensorId)
    {
        var h = ScenarioMath.StrHash(sensorId);
        return string.Create(CultureInfo.InvariantCulture, $"00:6E:02:{(h >> 16) & 0xFF:X2}:{(h >> 8) & 0xFF:X2}:{h & 0xFF:X2}");
    }
}
