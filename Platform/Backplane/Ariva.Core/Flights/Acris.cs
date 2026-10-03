namespace Ariva.Core.Flights;

/// <summary>
/// A flight as Ariva reads an ACRIS flight resource (ARV-045, wiki 08 section 9): the identifier (airline code,
/// track number, suffix, origin date, departure and arrival airports), each side's times and resources, the aircraft
/// type, the status and codeshares. Field names follow the ACRIS Semantic Model flight resource; the exact profile each
/// AODB serves is To confirm during onboarding. Values are the partner's, unchecked: <see cref="AcrisMapping"/> and then
/// <see cref="FlightRules"/> decide (CWE-501).
/// </summary>
public sealed record AcrisFlight(
    AcrisFlightNumber FlightNumber,
    DateOnly? OriginDate,
    string DepartureAirport,
    string ArrivalAirport,
    AcrisSide Departure,
    AcrisSide Arrival,
    string AircraftType,
    string FlightStatus,
    IReadOnlyList<AcrisFlightNumber> CodeShares);

public sealed record AcrisFlightNumber(string AirlineCode, string TrackNumber, string Suffix);

/// <summary>One side of the flight: scheduled, estimated and actual times (touchdown or take-off), the block time, terminal, gate and stand.</summary>
public sealed record AcrisSide(DateTime? Scheduled, DateTime? Estimated, DateTime? Actual, DateTime? Block, string Terminal, string Gate, string Stand);

/// <summary>
/// ACRIS flights to Ariva flight legs for one site: the site's side as for AIDX (<see cref="FlightKeys.SiteSide"/>), the
/// same key, that side's times and resources (its <c>block</c> is on-block for an arrival, off-block for a departure),
/// <c>flightStatus</c> Cancelled or Diverted (case-insensitive) as the status.
/// </summary>
public static class AcrisMapping
{
    public static (FlightLegData Leg, string Error) ToLeg(AcrisFlight flight, IReadOnlySet<string> siteAirports)
    {
        ArgumentNullException.ThrowIfNull(siteAirports);
        if (flight?.FlightNumber is null)
            return (null, "The flight has no flightNumber.");
        if (flight.OriginDate is not { } date)
            return (null, "originDate is required.");
        if (FlightKeys.SiteSide(flight.DepartureAirport, flight.ArrivalAirport, siteAirports) is not { } arrival)
            return (null, FlightKeys.NotThisSite);
        var side = (arrival ? flight.Arrival : flight.Departure) ?? new AcrisSide(null, null, null, null, null, null, null);
        var status = flight.FlightStatus?.Trim().ToUpperInvariant() switch
        {
            "CANCELLED" or "CANCELED" => "Cancelled",
            "DIVERTED" => "Diverted",
            _ => null
        };
        var number = flight.FlightNumber;
        return (new FlightLegData(
            FlightKeys.Of(number.AirlineCode, number.TrackNumber, number.Suffix, date, arrival),
            number.AirlineCode,
            FlightKeys.Number(number.TrackNumber),
            number.Suffix,
            arrival ? "Arrival" : "Departure",
            side.Scheduled,
            side.Estimated,
            side.Actual,
            arrival ? side.Block : null,
            arrival ? null : side.Block,
            flight.DepartureAirport,
            flight.ArrivalAirport,
            side.Terminal,
            side.Stand,
            side.Gate,
            flight.AircraftType,
            null,
            null,
            status,
            flight.CodeShares?.Where(c => c is not null).Select(c => c.AirlineCode + FlightKeys.Number(c.TrackNumber)).ToList()), null);
    }
}
