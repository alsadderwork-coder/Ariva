using System.Globalization;
using Ariva.Core.Domain.Components;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The illustrative layout of the AUH Terminal A arrivals immigration hall (ARV-139a), in metres on the level's floor
/// coordinates: the seed's zones, desks and sensors and the drawn floor plan all come from here, so they never drift.
/// <para>
/// Cited public facts (docs/demo/auh-terminal-a.md lists the sources): Zayed International Airport (AUH), Abu Dhabi,
/// Terminal A; arrivals on the lower level with immigration, baggage claim and customs in a straight line, then landside
/// transport; 38 immigration counters and 34 smart gates (reported by secondary travel guides, unverified, so labelled
/// "reported"); smart gates for residents and eligible nationals, most first-time visitors at a staffed counter.
/// </para>
/// <para>
/// ASSUMPTIONS (everything else): the level's extent (200 by 150 m) and floor number (0); the hall's arrangement (flow from
/// the start of x to its end, smart gates at the low y end, counters in one line); every position, pitch and size below;
/// the split of the 38 counters by lane (<see cref="CounterLanes"/>); every smart gate serving the EG lane alone; a
/// transfer lane at all (transfer flows normally bypass arrivals immigration); the overflow bands; the queue capacities
/// (one person per <see cref="SquareMetresPerPerson"/> m2); the sensors (overhead stereo at <see cref="MountingHeightMetres"/> m
/// with the BOQ's assumed 10 by 10 m footprint, a grid over every queue and band, one desk sensor per four counters).
/// </para>
/// </summary>
public static class AuhTerminalALayout
{
    #region Site and topology (codes are Ariva's own; names are descriptive, never a real system's)

    public const string SiteCode = "AUH-TA";
    public const string SiteName = "Zayed International Airport, Terminal A arrivals (illustrative)";
    public const string AirportIata = "AUH";
    public const string AirportName = "Zayed International Airport (Abu Dhabi)";
    public const string TimeZoneId = "Asia/Dubai";
    public const string TerminalCode = "A";
    public const string TerminalName = "Terminal A";
    public const string LevelCode = "ARR";
    public const string LevelName = "Arrivals, lower level";

    /// <summary>ASSUMPTION: the lower arrivals level as floor 0.</summary>
    public const int FloorNumber = 0;

    /// <summary>ASSUMPTION: the modelled part of the lower level (airside corridor to landside), not the whole terminal.</summary>
    public const double WidthMetres = 200;

    /// <summary>ASSUMPTION.</summary>
    public const double DepthMetres = 150;

    public const string CheckpointCode = "IMM";
    public const string CheckpointName = "Arrivals immigration";

    /// <summary>Reported count (secondary travel guides; unverified).</summary>
    public const int Counters = 38;

    /// <summary>Reported count (secondary travel guides; unverified).</summary>
    public const int SmartGates = 34;

    public const string CounterPrefix = "IC-";
    public const string SmartGatePrefix = "SG-";

    #endregion

    #region Geometry (ASSUMPTIONS)

    /// <summary>The plan is drawn at 10 pixels per metre with the floor origin at the image's top left.</summary>
    public const double MetresPerPixel = 0.1;

    public const double SquareMetresPerPerson = 1.2;
    public const double MountingHeightMetres = 5;

    /// <summary>Where every queue ends: the exit line in front of the counters and gates.</summary>
    public const double QueueEndX = 112;

    /// <summary>Where the wide queues start (the narrow crew, diplomatic and transfer lanes start at <see cref="NarrowQueueStartX"/>).</summary>
    public const double QueueStartX = 84;

    public const double NarrowQueueStartX = 100;
    public const double OverflowStartX = 64;
    public const double OverflowEndX = 82;
    public const double ServiceStartX = 112.5;
    public const double ServiceEndX = 116;
    public const double BoothEndX = 118;
    public const double StaffEndX = 121.5;
    public const double GateStartY = 5;
    public const double GatePitch = 1.2;
    public const double CounterStartY = 54;
    public const double CounterPitch = 2.4;

    /// <summary>The smart gates' queue (and its overflow band) spans the gates with a margin of one metre.</summary>
    public static (double Top, double Bottom) GateBand => (GateStartY - 1, GateStartY + (SmartGates * GatePitch) + 1);

    #endregion

    #region Lanes

    /// <summary>A lane of the hall: its code, its queue zone, which counters serve it and whether its queue is narrow.</summary>
    public sealed record Lane(string Code, string Description, int FirstCounter, int LastCounter, bool Narrow, bool HasOverflow);

    /// <summary>
    /// ASSUMPTION: the split of the 38 counters by lane, in counter order (crew, diplomatic, citizens, residents, GCC
    /// nationals, visitors and visa on arrival, transfer). Transfer is itself an assumption.
    /// </summary>
    public static IReadOnlyList<Lane> CounterLanes { get; } =
    [
        new("CRW", "Crew", 1, 2, true, false),
        new("DIP", "Diplomatic", 3, 3, true, false),
        new("CIT", "Citizens", 4, 7, false, false),
        new("RES", "Residents", 8, 13, false, false),
        new("GCC", "GCC nationals", 14, 19, false, false),
        new("VIS", "Visitors and visa on arrival", 20, 36, false, true),
        new("TRF", "Transfer (assumption)", 37, 38, true, false)
    ];

    /// <summary>The smart gates' lane (EG): every gate, and an overflow band (ASSUMPTION).</summary>
    public static Lane GateLane { get; } = new("EG", "Smart gates", 1, SmartGates, false, true);

    /// <summary>Every lane with a queue zone: the counters' lanes, then the smart gates'.</summary>
    public static IReadOnlyList<Lane> Lanes { get; } = [.. CounterLanes, GateLane];

    /// <summary>The queue zone of a lane, for example A-VIS (well within the outbox key rule: AUH-TA/A-VIS is 12 characters).</summary>
    public static string QueueName(string lane) => $"A-{lane}";

    /// <summary>The overflow band of a lane, for example A-VIS-OV.</summary>
    public static string OverflowName(string lane) => $"A-{lane}-OV";

    public static string CounterCode(int n) => string.Create(CultureInfo.InvariantCulture, $"{CounterPrefix}{n:00}");

    public static string SmartGateCode(int n) => string.Create(CultureInfo.InvariantCulture, $"{SmartGatePrefix}{n:00}");

    /// <summary>The lane a counter serves.</summary>
    public static Lane LaneOfCounter(int n) => CounterLanes.Single(l => n >= l.FirstCounter && n <= l.LastCounter);

    /// <summary>The y range of a counter.</summary>
    public static (double Top, double Bottom) CounterBand(int n) => (CounterStartY + ((n - 1) * CounterPitch), CounterStartY + (n * CounterPitch));

    /// <summary>The y range of a lane's queue: its counters' (or the gates'), inset 0.1 m from its neighbours.</summary>
    public static (double Top, double Bottom) LaneBand(Lane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        var (top, bottom) = lane == GateLane ? GateBand : (CounterBand(lane.FirstCounter).Top, CounterBand(lane.LastCounter).Bottom);
        return (top + 0.1, bottom - 0.1);
    }

    /// <summary>The queue zone's rectangle of a lane.</summary>
    public static IReadOnlyList<FloorPoint> QueueRect(Lane lane)
    {
        var (top, bottom) = LaneBand(lane);
        return Rect(lane.Narrow ? NarrowQueueStartX : QueueStartX, top, QueueEndX, bottom);
    }

    /// <summary>The overflow band's rectangle of a lane.</summary>
    public static IReadOnlyList<FloorPoint> OverflowRect(Lane lane)
    {
        var (top, bottom) = LaneBand(lane);
        return Rect(OverflowStartX, top, OverflowEndX, bottom);
    }

    /// <summary>The service zone in front of a counter (where the passenger stands).</summary>
    public static IReadOnlyList<FloorPoint> ServiceRect(int counter)
    {
        var (top, bottom) = CounterBand(counter);
        return Rect(ServiceStartX, top + 0.2, ServiceEndX, bottom - 0.2);
    }

    /// <summary>The staff zone behind a counter (where the officer sits).</summary>
    public static IReadOnlyList<FloorPoint> StaffRect(int counter)
    {
        var (top, bottom) = CounterBand(counter);
        return Rect(BoothEndX, top + 0.2, StaffEndX, bottom - 0.2);
    }

    /// <summary>A queue's or band's physical capacity in people (ASSUMPTION: one person per 1.2 m2).</summary>
    public static int CapacityOf(IReadOnlyList<FloorPoint> rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        return (int)Math.Floor(Math.Abs(Geometry.SignedArea(rect)) / SquareMetresPerPerson);
    }

    #endregion

    #region Sensors (ASSUMPTIONS)

    /// <summary>One sensor of the hall: its code, the queue zone that owns its events and where it hangs.</summary>
    public sealed record Sensor(string Code, string QueueZoneName, double X, double Y);

    /// <summary>The BOQ's assumed footprint side at 4 to 6 m (CoverageFootprint.Assumed).</summary>
    private const double FootprintMetres = 10;

    /// <summary>The upper bound of the sensors the layout places (CWE-120: the seed's counts are bounded).</summary>
    public const int MaxSensors = 200;

    /// <summary>
    /// Every sensor: a grid of 10 by 10 m footprints over each queue zone (Q-) and overflow band (O-), and one desk sensor
    /// (D-) over each run of up to four counters of a lane, hung above the counters so its footprint reaches their
    /// service and staff zones. All of a lane's sensors belong to the lane's queue zone.
    /// </summary>
    public static IReadOnlyList<Sensor> Sensors()
    {
        var sensors = new List<Sensor>();
        foreach (var lane in Lanes)
        {
            Grid(sensors, "Q", lane.Code, QueueRect(lane));
            if (lane.HasOverflow)
                Grid(sensors, "O", lane.Code, OverflowRect(lane));
        }

        foreach (var lane in CounterLanes)
        {
            var n = 0;
            for (var first = lane.FirstCounter; first <= lane.LastCounter; first += 4)
            {
                var last = Math.Min(first + 3, lane.LastCounter);
                var y = (CounterBand(first).Top + CounterBand(last).Bottom) / 2;
                sensors.Add(new Sensor(Code("D", lane.Code, ++n), QueueName(lane.Code), (ServiceStartX + StaffEndX) / 2, Math.Round(y, 2)));
            }
        }

        return sensors;
    }

    private static void Grid(List<Sensor> sensors, string kind, string lane, IReadOnlyList<FloorPoint> rect)
    {
        var (x0, y0, x1, y1) = (rect[0].X, rect[0].Y, rect[2].X, rect[2].Y);
        var columns = (int)Math.Ceiling((x1 - x0) / FootprintMetres);
        var rows = (int)Math.Ceiling((y1 - y0) / FootprintMetres);
        var n = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var x = x0 + ((column + 0.5) * (x1 - x0) / columns);
                var y = y0 + ((row + 0.5) * (y1 - y0) / rows);
                sensors.Add(new Sensor(Code(kind, lane, ++n), QueueName(lane), Math.Round(x, 2), Math.Round(y, 2)));
            }
        }
    }

    private static string Code(string kind, string lane, int n) => string.Create(CultureInfo.InvariantCulture, $"{kind}-{lane}-{n:00}");

    #endregion

    private static FloorPoint[] Rect(double x0, double y0, double x1, double y1) =>
        [new(Math.Round(x0, 2), Math.Round(y0, 2)), new(Math.Round(x1, 2), Math.Round(y0, 2)), new(Math.Round(x1, 2), Math.Round(y1, 2)), new(Math.Round(x0, 2), Math.Round(y1, 2))];
}
