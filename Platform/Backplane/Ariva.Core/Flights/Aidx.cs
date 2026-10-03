using System.Globalization;

namespace Ariva.Core.Flights;

/// <summary>
/// An IATA AIDX 22.1 <c>IATA_AIDX_FlightLegNotifRQ</c> message as Ariva reads it (ARV-044): the message time, the
/// sender's transaction id and its flight legs, already parsed from schema-validated XML. Values are the message's,
/// unchecked; <see cref="AidxMapping"/> turns each leg into a <see cref="FlightLegData"/> for the site, and
/// <see cref="FlightRules"/> then decides whether it may touch the model (CWE-501).
/// </summary>
public sealed record AidxMessage(DateTime? TimeStamp, string TransactionIdentifier, IReadOnlyList<AidxLeg> Legs);

/// <summary>One <c>FlightLeg</c>: its identifier, status, operation times, resources per side, aircraft and codeshares.</summary>
public sealed record AidxLeg(
    string Airline,
    string FlightNumber,
    string Suffix,
    string DepartureAirport,
    string ArrivalAirport,
    DateOnly? OriginDate,
    string OperationalStatus,
    IReadOnlyList<AidxTime> Times,
    AidxResources DepartureResources,
    AidxResources ArrivalResources,
    string AircraftType,
    IReadOnlyList<string> Codeshares);

/// <summary>An <c>OperationTime</c>: qualifier (code set 9750: OFB, TKO, TDN, ONB), type (SCT, EST, ACT) and time.</summary>
public sealed record AidxTime(string Qualifier, string Type, DateTime? Time);

/// <summary>The terminal, stand and gate of one side of the leg (<c>AirportResources/Resource</c>), actual before planned.</summary>
public sealed record AidxResources(string Terminal, string Stand, string Gate)
{
    public static readonly AidxResources None = new(null, null, null);
}

/// <summary>
/// AIDX legs to Ariva flight legs for one site (ARV-044, wiki 08 section 9). The direction is the site's side of the
/// leg: arriving at one of the site's airports (IATA or ICAO) is an Arrival, leaving one a Departure; a leg that touches
/// none of them, or both, is refused (codes compared in upper case). The flight key is airline, number, suffix, origin date and direction
/// (<c>RJ111-20261003-A</c>), so the same flight from the Integration API and from AIDX is one leg. Times by side:
/// arrivals take the scheduled and estimated on-block (touchdown when there is none) and the actual touchdown and
/// on-block; departures the scheduled and estimated off-block and the actual off-block and take-off. OperationalStatus
/// <c>DX</c> is Cancelled and <c>DV</c> Diverted (PADIS code set 2005); other codes leave the status to the times.
/// </summary>
public static class AidxMapping
{
    public const string Cancelled = "DX";
    public const string Diverted = "DV";

    public static (FlightLegData Leg, string Error) ToLeg(AidxLeg leg, IReadOnlySet<string> siteAirports)
    {
        ArgumentNullException.ThrowIfNull(siteAirports);
        if (leg is null)
            return (null, "The FlightLeg has no LegIdentifier.");
        if (leg.OriginDate is not { } date)
            return (null, "LegIdentifier/OriginDate is required.");
        var arrives = leg.ArrivalAirport is not null && siteAirports.Contains(leg.ArrivalAirport.Trim().ToUpperInvariant());
        var departs = leg.DepartureAirport is not null && siteAirports.Contains(leg.DepartureAirport.Trim().ToUpperInvariant());
        if (arrives == departs)
            return (null, "The leg must arrive at or depart from exactly one airport of this site.");

        var arrival = arrives;
        var key = string.Concat(leg.Airline?.Trim().ToUpperInvariant(), leg.FlightNumber?.Trim(), leg.Suffix?.Trim().ToUpperInvariant(), "-",
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), arrival ? "-A" : "-D");
        var side = arrival ? leg.ArrivalResources : leg.DepartureResources;
        side ??= AidxResources.None;
        var block = arrival ? "ONB" : "OFB";
        var wheels = arrival ? "TDN" : "TKO";
        var status = leg.OperationalStatus?.Trim() switch
        {
            Cancelled => "Cancelled",
            Diverted => "Diverted",
            _ => null
        };

        return (new FlightLegData(
            key,
            leg.Airline,
            leg.FlightNumber,
            leg.Suffix,
            arrival ? "Arrival" : "Departure",
            Time(leg, block, "SCT") ?? Time(leg, wheels, "SCT"),
            Time(leg, block, "EST") ?? Time(leg, wheels, "EST"),
            Time(leg, wheels, "ACT"),
            arrival ? Time(leg, "ONB", "ACT") : null,
            arrival ? null : Time(leg, "OFB", "ACT"),
            leg.DepartureAirport,
            leg.ArrivalAirport,
            side.Terminal,
            side.Stand,
            side.Gate,
            leg.AircraftType,
            null,
            null,
            status,
            leg.Codeshares), null);
    }

    // The last time of that qualifier and type (a later OperationTime of the same kind supersedes an earlier one).
    private static DateTime? Time(AidxLeg leg, string qualifier, string type) =>
        leg.Times?.LastOrDefault(t => string.Equals(t.Qualifier, qualifier, StringComparison.Ordinal) && string.Equals(t.Type, type, StringComparison.Ordinal))?.Time;
}
