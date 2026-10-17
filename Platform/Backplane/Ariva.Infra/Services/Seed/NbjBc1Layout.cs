using System.Globalization;
using Ariva.Core.Domain.Components;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The layout of the border control halls of terminal BC1 of Dr. António Agostinho Neto International Airport (NBJ),
/// Luanda (ARV-139c), in metres on each level's floor coordinates: the seed's zones, desks and sensors and the drawn
/// schematics all come from here, so they never drift.
/// <para>
/// Source: the terminal's 2018 design drawings (execution stage), sheets ETP-ARQ-008 (ground floor, arrivals) and
/// ETP-ARQ-010 (departures floor), held by the owner outside the repository (<c>.private/</c>, git-ignored; they are a
/// third party's confidential drawings and are never committed, nor drawn into the product). Read from the drawings:
/// one arrivals immigration row of 13 double booths (26 desks, owner-confirmed 2026-10-09) with 5 e-gate channels at its
/// left end and a staff channel at its right end, behind a row of 8 health counters; one departures emigration row
/// of 13 double booths (26 desks) with 5 e-gate channels at its right end, after the security lanes; booth pitch about
/// 3.5 m (arrivals) and 3.7 m (departures), measured against the drawings' 15,000 mm grid. No lane segregation: every
/// desk of a row serves one shared queue (owner, 2026-10-09; a known gap of Ariva's lane model, recorded in
/// docs/demo/nbj-bc1.md). The drawings predate construction: the as-built halls may differ, so the site carries the
/// "Illustrative, not surveyed" flag.
/// </para>
/// <para>
/// ASSUMPTIONS: each level's modelled extent (the hall, not the whole terminal) and the departures floor number; the
/// queue and overflow depths (the drawings show an open hall, about 22 m from the health counters to the booths in
/// arrivals and about 16 m from the security exits in departures); the service and staff zones' sizes; capacities (one
/// person per <see cref="SquareMetresPerPerson"/> m2); the sensors (overhead stereo at <see cref="MountingHeightMetres"/> m,
/// the BOQ's assumed 10 by 10 m footprint); every code (Ariva's own, never the airport's or a border system's).
/// </para>
/// </summary>
public static class NbjBc1Layout
{
    #region Site and topology

    public const string SiteCode = "NBJ-BC1";
    public const string SiteName = "Dr. António Agostinho Neto International Airport, terminal BC1 (from 2018 design drawings)";
    public const string AirportIata = "NBJ";
    public const string AirportName = "Dr. António Agostinho Neto International Airport (Luanda)";
    public const string TimeZoneId = "Africa/Luanda";
    public const string TerminalCode = "BC1";
    public const string TerminalName = "Terminal BC1";

    /// <summary>The lane of every desk of a row: no segregation (owner, 2026-10-09).</summary>
    public const string SharedLane = "ALL";

    public const int BoothsPerRow = 13;
    public const int DesksPerBooth = 2;
    public const int DesksPerRow = BoothsPerRow * DesksPerBooth;
    public const int EGatesPerRow = 5;

    #endregion

    #region Geometry

    /// <summary>The schematics are drawn at 20 pixels per metre with the floor origin at the image's top left.</summary>
    public const double MetresPerPixel = 0.05;

    public const double SquareMetresPerPerson = 1.2;
    public const double MountingHeightMetres = 5;

    /// <summary>A booth's depth in the direction of travel (drawings: about 2.4 m).</summary>
    public const double BoothDepth = 2.4;

    /// <summary>The service zone's depth, where the passenger stands at the booth window (assumption).</summary>
    public const double ServiceDepth = 1.4;

    /// <summary>An e-gate channel's width (drawings: 5 channels in about 5 m).</summary>
    public const double EGatePitch = 1.0;

    #endregion

    #region Halls

    /// <summary>
    /// A border control row and the hall in front of it. Passengers walk towards larger y: from the hall's entry side
    /// (<see cref="OverflowStartY"/>) through the overflow band and the queue to the booths at <see cref="QueueEndY"/>.
    /// </summary>
    /// <param name="Prefix">The hall's letter in zone names: A for arrivals, D for departures.</param>
    /// <param name="DeskPrefix">The desks' code prefix, for example IM- (IM-01 to IM-26).</param>
    /// <param name="EGatePrefix">The e-gates' code prefix, for example EGA- (EGA-01 to EGA-05).</param>
    /// <param name="EGatesLeft">Whether the e-gates stand at the row's low x end (arrivals) or its high x end (departures).</param>
    public sealed record Hall(
        string Prefix, string LevelCode, string LevelName, int FloorNumber, double WidthMetres, double DepthMetres,
        string CheckpointCode, string CheckpointName, bool Arrivals, string DeskPrefix, string EGatePrefix,
        double FirstBoothX, double BoothPitch, double BoothWidth, bool EGatesLeft,
        double OverflowStartY, double QueueStartY, double QueueEndY, string UpstreamLabel, string DownstreamLabel)
    {
        /// <summary>Where the row of booths ends (the last booth's far edge).</summary>
        public double LastBoothEndX => FirstBoothX + ((BoothsPerRow - 1) * BoothPitch) + BoothWidth;

        /// <summary>The e-gates' x range, beside the booths with a 1 m gap.</summary>
        public (double Left, double Right) EGateBand => EGatesLeft
            ? (FirstBoothX - 1 - (EGatesPerRow * EGatePitch), FirstBoothX - 1)
            : (LastBoothEndX + 1, LastBoothEndX + 1 + (EGatesPerRow * EGatePitch));

        public double ServiceStartY => QueueEndY + 0.2;
        public double BoothStartY => ServiceStartY + ServiceDepth;
        public double BoothEndY => BoothStartY + BoothDepth;

        public string QueueName => $"{Prefix}-{SharedLane}";
        public string OverflowName => $"{Prefix}-{SharedLane}-OV";
        public string EGateQueueName => $"{Prefix}-{Ariva.Core.Domain.Entities.LaneCategory.EGateEligible}";

        public string DeskCode(int n) => string.Create(CultureInfo.InvariantCulture, $"{DeskPrefix}{n:00}");

        public string EGateCode(int n) => string.Create(CultureInfo.InvariantCulture, $"{EGatePrefix}{n:00}");

        /// <summary>
        /// A desk's x range: desk 2b-1 is the left half of booth b, desk 2b its right half (a double booth with an
        /// officer at each window, serving the lane on its side).
        /// </summary>
        public (double Left, double Right) DeskBand(int n)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(n, DesksPerRow);
            var booth = (n - 1) / DesksPerBooth;
            var half = (n - 1) % DesksPerBooth;
            var left = FirstBoothX + (booth * BoothPitch) + (half * BoothWidth / DesksPerBooth);
            return (left, left + (BoothWidth / DesksPerBooth));
        }

        /// <summary>The shared queue: in front of every booth, from the first booth's edge to the last's.</summary>
        public IReadOnlyList<FloorPoint> QueueRect() => Rect(FirstBoothX, QueueStartY, LastBoothEndX, QueueEndY);

        /// <summary>The overflow band behind the shared queue (assumption).</summary>
        public IReadOnlyList<FloorPoint> OverflowRect() => Rect(FirstBoothX, OverflowStartY, LastBoothEndX, QueueStartY - 0.1);

        /// <summary>The e-gates' queue, in front of the channels.</summary>
        public IReadOnlyList<FloorPoint> EGateQueueRect()
        {
            var (left, right) = EGateBand;
            return Rect(left, QueueStartY, right, QueueEndY);
        }

        /// <summary>The service zone in front of a desk (where the passenger stands).</summary>
        public IReadOnlyList<FloorPoint> ServiceRect(int n)
        {
            var (left, right) = DeskBand(n);
            return Rect(left + 0.1, ServiceStartY, right - 0.1, BoothStartY - 0.1);
        }

        /// <summary>The staff zone of a desk: its half of the booth (where the officer sits).</summary>
        public IReadOnlyList<FloorPoint> StaffRect(int n)
        {
            var (left, right) = DeskBand(n);
            return Rect(left + 0.1, BoothStartY + 0.1, right - 0.1, BoothEndY - 0.1);
        }
    }

    /// <summary>
    /// Arrivals immigration, ground floor (ETP-ARQ-008): the health counters at y 4, the hall from y 6, the overflow band
    /// to y 14, the shared queue to y 28, the booths beyond; e-gates at the left end. 64 by 40 m modelled.
    /// </summary>
    public static Hall ArrivalsHall { get; } = new(
        "A", "ARR", "Ground floor, international arrivals", 0, 64, 40,
        "IMM", "Arrivals immigration", Arrivals: true, "IM-", "EGA-",
        FirstBoothX: 9, BoothPitch: 3.5, BoothWidth: 2.6, EGatesLeft: true,
        OverflowStartY: 6, QueueStartY: 14, QueueEndY: 28,
        "From the health counters", "To baggage reclaim");

    /// <summary>
    /// Departures emigration control (ETP-ARQ-010): an unlabelled band upstream (no lanes and no count are drawn: security
    /// checkpoints are out of scope), the hall from y 6, the overflow band to y 10, the shared queue to y 22, the booths
    /// beyond; e-gates at the right end. 64 by 34 m modelled; the departures floor number 2 is an assumption.
    /// </summary>
    public static Hall DeparturesHall { get; } = new(
        "D", "DEP", "Departures floor, international departures", 2, 64, 34,
        "EMI", "Departures emigration control", Arrivals: false, "EM-", "EGD-",
        FirstBoothX: 4, BoothPitch: 3.7, BoothWidth: 2.8, EGatesLeft: false,
        OverflowStartY: 6, QueueStartY: 10, QueueEndY: 22,
        "Hall entry", "To the airside departure lounge");

    public static IReadOnlyList<Hall> Halls { get; } = [ArrivalsHall, DeparturesHall];

    /// <summary>A queue's or band's physical capacity in people (ASSUMPTION: one person per 1.2 m2).</summary>
    public static int CapacityOf(IReadOnlyList<FloorPoint> rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        return (int)Math.Floor(Math.Abs(Geometry.SignedArea(rect)) / SquareMetresPerPerson);
    }

    #endregion

    #region Sensors (ASSUMPTIONS)

    /// <summary>One sensor of a hall: its code, the queue zone that owns its events, its level and where it hangs.</summary>
    public sealed record Sensor(string Code, string QueueZoneName, string LevelCode, double X, double Y);

    private const double FootprintMetres = 10;

    /// <summary>The upper bound of the sensors the layout places (CWE-120: the seed's counts are bounded).</summary>
    public const int MaxSensors = 200;

    /// <summary>
    /// Every sensor: per hall, a grid of 10 by 10 m footprints over the shared queue (Q-), its overflow band (O-) and the
    /// e-gates' queue (G-), and one desk sensor (K-) over every run of four desks (two booths), hung above the booths so
    /// its footprint reaches their service and staff zones. Codes carry the hall's letter, for example Q-A-07.
    /// </summary>
    public static IReadOnlyList<Sensor> Sensors()
    {
        var sensors = new List<Sensor>();
        foreach (var hall in Halls)
        {
            Grid(sensors, hall, "Q", hall.QueueName, hall.QueueRect());
            Grid(sensors, hall, "O", hall.QueueName, hall.OverflowRect());
            Grid(sensors, hall, "G", hall.EGateQueueName, hall.EGateQueueRect());
            var n = 0;
            for (var first = 1; first <= DesksPerRow; first += 4)
            {
                var last = Math.Min(first + 3, DesksPerRow);
                var x = (hall.DeskBand(first).Left + hall.DeskBand(last).Right) / 2;
                var y = (hall.ServiceStartY + hall.BoothEndY) / 2;
                sensors.Add(new Sensor(Code("K", hall, ++n), hall.QueueName, hall.LevelCode, Math.Round(x, 2), Math.Round(y, 2)));
            }
        }

        return sensors;
    }

    private static void Grid(List<Sensor> sensors, Hall hall, string kind, string queue, IReadOnlyList<FloorPoint> rect)
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
                sensors.Add(new Sensor(Code(kind, hall, ++n), queue, hall.LevelCode, Math.Round(x, 2), Math.Round(y, 2)));
            }
        }
    }

    private static string Code(string kind, Hall hall, int n) => string.Create(CultureInfo.InvariantCulture, $"{kind}-{hall.Prefix}-{n:00}");

    #endregion

    private static FloorPoint[] Rect(double x0, double y0, double x1, double y1) =>
        [new(Math.Round(x0, 2), Math.Round(y0, 2)), new(Math.Round(x1, 2), Math.Round(y0, 2)), new(Math.Round(x1, 2), Math.Round(y1, 2)), new(Math.Round(x0, 2), Math.Round(y1, 2))];
}
