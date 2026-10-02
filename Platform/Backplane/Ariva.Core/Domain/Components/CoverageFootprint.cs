using System.Globalization;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Components;

/// <summary>
/// The area a sensor tracks at the tracking plane (glossary Coverage footprint): a rectangle of
/// <see cref="LengthMetres"/> by <see cref="WidthMetres"/> for overhead stereo and counters, or a circle of
/// <see cref="RadiusMetres"/> for LiDAR. The vendor's table for the model and height is authoritative; without one the
/// BOQ's planning assumption is used and labelled as such (<see cref="Note"/>).
/// </summary>
public sealed record CoverageFootprint(double? LengthMetres, double? WidthMetres, double? RadiusMetres, FootprintSource Source, string Note)
{
    /// <summary>The BOQ's assumed LiDAR radius until a perception platform is certified (wiki/06).</summary>
    public const double AssumedLidarRadiusMetres = 10;

    /// <summary>The largest footprint side or radius accepted; no listed sensor covers more than this at any height.</summary>
    public const double MaxMetres = 60;

    /// <summary>The smallest footprint side or radius accepted (footprints are kept to 0.1 m).</summary>
    public const double MinMetres = 0.1;

    public bool IsCircle => RadiusMetres is not null;

    public string Text => IsCircle
        ? string.Create(CultureInfo.InvariantCulture, $"{RadiusMetres:0.#} m radius")
        : string.Create(CultureInfo.InvariantCulture, $"{LengthMetres:0.#} x {WidthMetres:0.#} m");

    /// <summary>
    /// The BOQ's assumed footprint for a mounting height (the prototype's table): 10 x 10 m from 4 to 6 m, 12 x 9 m from
    /// 10 to 14 m, interpolated between 6 and 10 m, scaled below 4 m and above 14 m; LiDAR a 10 m radius. An estimate
    /// for planning only; the note says so.
    /// </summary>
    public static CoverageFootprint Assumed(DeviceFamily family, double mountingHeightMetres)
    {
        if (!double.IsFinite(mountingHeightMetres) || mountingHeightMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(mountingHeightMetres), mountingHeightMetres, "The mounting height must be a positive number of metres.");
        if (family == DeviceFamily.Lidar)
            return new(null, null, AssumedLidarRadiusMetres, FootprintSource.AssumedFromBoq, "Assumed radius; the BOQ has no LiDAR footprint");

        var h = mountingHeightMetres;
        if (h is >= 4 and <= 6)
            return new(10, 10, null, FootprintSource.AssumedFromBoq, "Assumed, from the BOQ (4 to 6 m)");
        if (h is >= 10 and <= 14)
            return new(12, 9, null, FootprintSource.AssumedFromBoq, "Assumed, from the BOQ (10 to 14 m)");

        double length, width;
        string note;
        if (h is > 6 and < 10)
        {
            var t = (h - 6) / 4;
            length = 10 + (2 * t);
            width = 10 - t;
            note = "Estimate, interpolated between the BOQ's assumed footprints";
        }
        else if (h < 4)
        {
            length = width = 10 * h / 4;
            note = "Estimate, scaled from the BOQ's assumed footprints";
        }
        else
        {
            length = 12 * h / 14;
            width = 9 * h / 14;
            note = "Estimate, scaled from the BOQ's assumed footprints";
        }

        return new(Round(length), Round(width), null, FootprintSource.AssumedFromBoq, note);
    }

    /// <summary>A footprint from the vendor's table: a rectangle (length and width) or, for LiDAR, a radius.</summary>
    public static CoverageFootprint FromVendor(double? lengthMetres, double? widthMetres, double? radiusMetres)
    {
        var rectangle = lengthMetres is not null || widthMetres is not null;
        if (rectangle == radiusMetres is not null)
            throw new ArgumentException("Give either the footprint's length and width or its radius.");
        if (rectangle)
        {
            Check(lengthMetres, nameof(lengthMetres));
            Check(widthMetres, nameof(widthMetres));
            return new(Round(lengthMetres!.Value), Round(widthMetres!.Value), null, FootprintSource.Vendor, "From the vendor's footprint table");
        }

        Check(radiusMetres, nameof(radiusMetres));
        return new(null, null, Round(radiusMetres!.Value), FootprintSource.Vendor, "From the vendor's footprint table");
    }

    /// <summary>
    /// The footprint on the floor around a sensor at <paramref name="centre"/>, its length along the sensor's
    /// orientation (degrees from the x axis, counter-clockwise); a circle as a 32-sided polygon.
    /// </summary>
    public IReadOnlyList<FloorPoint> Ring(FloorPoint centre, double orientationDegrees)
    {
        if (IsCircle)
        {
            var r = RadiusMetres!.Value;
            return [.. Enumerable.Range(0, 32).Select(i => new FloorPoint(centre.X + (r * Math.Cos(i * Math.PI / 16)), centre.Y + (r * Math.Sin(i * Math.PI / 16))))];
        }

        var angle = orientationDegrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var (hl, hw) = (LengthMetres!.Value / 2, WidthMetres!.Value / 2);
        FloorPoint Corner(double u, double v) => new(centre.X + (u * cos) - (v * sin), centre.Y + (u * sin) + (v * cos));
        return [Corner(-hl, -hw), Corner(hl, -hw), Corner(hl, hw), Corner(-hl, hw)];
    }

    private static void Check(double? value, string name)
    {
        // Checked as stored (to 0.1 m), so a side that rounds to nothing is refused here, not by the database.
        if (value is not { } v || !double.IsFinite(v) || Round(v) < MinMetres || v > MaxMetres)
            throw new ArgumentOutOfRangeException(name, value, $"A footprint side or radius is from {MinMetres} to {MaxMetres} m.");
    }

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
