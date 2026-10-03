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
        if (FlightKeys.SiteSide(leg.DepartureAirport, leg.ArrivalAirport, siteAirports) is not { } arrival)
            return (null, FlightKeys.NotThisSite);
        var key = FlightKeys.Of(leg.Airline, leg.FlightNumber, leg.Suffix, date, arrival);
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
            FlightKeys.Number(leg.FlightNumber),
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

/// <summary>
/// The flight key every adapter builds the same way (ARV-044, ARV-045): airline, flight number without leading zeros,
/// suffix, origin date and direction (<c>RJ111-20261003-A</c>), so one flight sent by AIDX, ACRIS or the JSON API is one
/// leg; and which side of a leg a site is on.
/// </summary>
public static class FlightKeys
{
    public const string NotThisSite = "The leg must arrive at or depart from exactly one airport of this site.";

    public static string Number(string number)
    {
        var trimmed = number?.Trim();
        return string.IsNullOrEmpty(trimmed) ? trimmed : trimmed.TrimStart('0') is { Length: > 0 } digits ? digits : "0";
    }

    public static string Of(string airline, string number, string suffix, DateOnly originDate, bool arrival) =>
        string.Concat(airline?.Trim().ToUpperInvariant(), Number(number), suffix?.Trim().ToUpperInvariant(), "-",
            originDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), arrival ? "-A" : "-D");

    /// <summary>True when the leg arrives at the site, false when it departs from it, null when it touches none or both of its airports (codes compared in upper case).</summary>
    public static bool? SiteSide(string departureAirport, string arrivalAirport, IReadOnlySet<string> siteAirports)
    {
        ArgumentNullException.ThrowIfNull(siteAirports);
        var arrives = arrivalAirport is not null && siteAirports.Contains(arrivalAirport.Trim().ToUpperInvariant());
        var departs = departureAirport is not null && siteAirports.Contains(departureAirport.Trim().ToUpperInvariant());
        return arrives == departs ? null : arrives;
    }
}
