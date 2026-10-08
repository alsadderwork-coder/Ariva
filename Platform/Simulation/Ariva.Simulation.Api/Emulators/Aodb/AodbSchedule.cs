using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Aodb;

/// <summary>
/// One flight leg as the emulated AODB knows it at a demo minute (ARV-029): identity, airports, the scheduled block
/// time, the estimate once published (an hour before), the actual block time once it happened, and the stand, gate,
/// terminal and aircraft type. <see cref="ChangedAt"/> is the demo minute of the last change.
/// </summary>
public sealed record AodbLeg(
    string Key,
    string Carrier,
    string Number,
    bool Arrival,
    string Origin,
    string Destination,
    DateTime OriginDateUtc,
    DateTime ScheduledUtc,
    DateTime? EstimatedUtc,
    DateTime? ActualBlockUtc,
    string Terminal,
    string Stand,
    string Gate,
    string AircraftType,
    int Seats,
    int ChangedAt);

/// <summary>
/// The demo day's schedule as an AODB holds it (ARV-029): the scenario's arrivals and departures at the site's airport
/// (ARV-139b: AUH-TA has arrivals only),
/// with the other airport, stand, gate and aircraft fixed per flight code, and the times known at each demo minute. An
/// arrival's estimated on-block (the scenario's estimate, with its error) is published an hour before the schedule and
/// its actual on-block when it happens; a departure's estimated off-block is published an hour before and its actual
/// off-block (the schedule plus up to 9 minutes) when it happens. Flights more than 6 hours before the minute played
/// and those beyond the next 18 hours are left out.
/// </summary>
public static class AodbSchedule
{
    private static string AircraftOf(int seats) => seats switch
    {
        <= 180 => "320",
        <= 220 => "321",
        <= 300 => "359",
        _ => "77W"
    };

    internal static IReadOnlyList<AodbLeg> At(ScenarioDay day, int minute, Func<int, DateTime> timeOf, string airport)
    {
        ArgumentNullException.ThrowIfNull(day);
        // The other airports and the terminal are the scenario site's (DMO: T1; AUH-TA: Terminal A).
        var airports = day.Site.AodbAirports;
        var terminal = day.Site.AodbTerminal;
        var legs = new List<AodbLeg>();
        foreach (var f in day.Schedule.Arrivals)
        {
            if (f.Sched < minute - 360 || f.Sched > minute + 1080)
                continue;
            var (carrier, number) = AmanFeed.Split(f.Code);
            var hash = Hash(f.Code);
            var origin = timeOf(f.Sched - AmanFeed.BlockMinutes(f.Code));
            var estimateKnown = minute >= f.Sched - 60;
            var landed = minute >= f.OnBlock;
            legs.Add(new AodbLeg(AmanFeed.FlightKey(f.Code, origin, arrival: true), carrier, number, true, airports[(int)(hash % (uint)airports.Count)], airport, origin.Date,
                timeOf(f.Sched), estimateKnown ? timeOf(f.Eibt) : null, landed ? timeOf(f.OnBlock) : null, terminal,
                "B" + (1 + hash % 40).ToString(CultureInfo.InvariantCulture), "A" + (1 + hash / 7 % 20).ToString(CultureInfo.InvariantCulture), AircraftOf(f.Seats), f.Seats,
                landed ? f.OnBlock : estimateKnown ? f.Sched - 60 : int.MinValue));
        }

        foreach (var f in day.Schedule.Departures)
        {
            if (f.Std < minute - 360 || f.Std > minute + 1080)
                continue;
            var (carrier, number) = AmanFeed.Split(f.Code);
            var hash = Hash(f.Code);
            var offBlock = f.Std + (int)(hash % 10);
            var estimateKnown = minute >= f.Std - 60;
            var gone = minute >= offBlock;
            var origin = timeOf(f.Std);
            legs.Add(new AodbLeg(AmanFeed.FlightKey(f.Code, origin, arrival: false), carrier, number, false, airport, airports[(int)(hash % (uint)airports.Count)], origin.Date,
                origin, estimateKnown ? timeOf(offBlock) : null, gone ? timeOf(offBlock) : null, terminal,
                "C" + (1 + hash % 30).ToString(CultureInfo.InvariantCulture), "C" + (1 + hash / 7 % 24).ToString(CultureInfo.InvariantCulture), AircraftOf(f.Seats), f.Seats,
                gone ? offBlock : estimateKnown ? f.Std - 60 : int.MinValue));
        }

        return legs;
    }

    private static uint Hash(string code)
    {
        var hash = 2166136261u;
        foreach (var c in code ?? string.Empty)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    #region AIDX

    private const string AidxNamespace = "http://www.iata.org/IATA/2007/00";

    /// <summary>
    /// An IATA AIDX 22.1 <c>IATA_AIDX_FlightLegNotifRQ</c> with the legs (at most 500 per message, Ariva's limit):
    /// identifier, block times (ONB for arrivals, OFB for departures; SCT, EST and ACT), the planned resources of the
    /// site's side and the aircraft type. <paramref name="stampUtc"/> is the message time.
    /// </summary>
    public static string Aidx(IReadOnlyList<AodbLeg> legs, DateTime stampUtc, string transaction)
    {
        ArgumentNullException.ThrowIfNull(legs);
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, new XmlWriterSettings { OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false), Indent = false }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("IATA_AIDX_FlightLegNotifRQ", AidxNamespace);
            xml.WriteAttributeString("Version", "22.1");
            xml.WriteAttributeString("TimeStamp", Iso(stampUtc));
            xml.WriteAttributeString("TransactionIdentifier", transaction);
            xml.WriteStartElement("Originator", AidxNamespace);
            xml.WriteAttributeString("CompanyShortName", "SIM-AODB");
            xml.WriteEndElement();
            foreach (var leg in legs)
                AidxLeg(xml, leg);
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AidxLeg(XmlWriter xml, AodbLeg leg)
    {
        void Element(string name, string value, string context = null)
        {
            xml.WriteStartElement(name, AidxNamespace);
            if (context is not null)
                xml.WriteAttributeString("CodeContext", context);
            xml.WriteString(value);
            xml.WriteEndElement();
        }

        void Time(string qualifier, string type, DateTime value)
        {
            xml.WriteStartElement("OperationTime", AidxNamespace);
            xml.WriteAttributeString("OperationQualifier", qualifier);
            xml.WriteAttributeString("CodeContext", "9750");
            xml.WriteAttributeString("TimeType", type);
            xml.WriteString(Iso(value));
            xml.WriteEndElement();
        }

        xml.WriteStartElement("FlightLeg", AidxNamespace);
        xml.WriteStartElement("LegIdentifier", AidxNamespace);
        Element("Airline", leg.Carrier, "2");
        Element("FlightNumber", leg.Number);
        Element("DepartureAirport", leg.Origin, "3");
        Element("ArrivalAirport", leg.Destination, "3");
        Element("OriginDate", leg.OriginDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        xml.WriteEndElement();
        xml.WriteStartElement("LegData", AidxNamespace);
        xml.WriteStartElement("AirportResources", AidxNamespace);
        xml.WriteAttributeString("Usage", "Planned");
        xml.WriteStartElement("Resource", AidxNamespace);
        xml.WriteAttributeString("DepartureOrArrival", leg.Arrival ? "Arrival" : "Departure");
        Element("AircraftTerminal", leg.Terminal);
        Element("AircraftParkingPosition", leg.Stand);
        Element("PassengerGate", leg.Gate);
        xml.WriteEndElement();
        xml.WriteEndElement();
        var block = leg.Arrival ? "ONB" : "OFB";
        Time(block, "SCT", leg.ScheduledUtc);
        if (leg.EstimatedUtc is { } estimated)
            Time(block, "EST", estimated);
        if (leg.ActualBlockUtc is { } actual)
            Time(block, "ACT", actual);
        xml.WriteStartElement("AircraftInfo", AidxNamespace);
        Element("AircraftType", leg.AircraftType);
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    #endregion

    #region ACRIS

    /// <summary>
    /// The legs as ACRIS flight resources (the profile Ariva reads, ARV-045): flight number, origin date, airports, each
    /// side's scheduled, estimated and block times with terminal, gate and stand, and the aircraft type.
    /// </summary>
    public static byte[] Acris(IReadOnlyList<AodbLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        object Side(AodbLeg leg, bool arrival) => leg.Arrival == arrival
            ? new { scheduled = Iso(leg.ScheduledUtc), estimated = leg.EstimatedUtc is { } e ? Iso(e) : null, block = leg.ActualBlockUtc is { } a ? Iso(a) : null,
                terminal = leg.Terminal, gate = leg.Gate, stand = leg.Stand }
            : null;
        var flights = legs.Select(l => new
        {
            flightNumber = new { airlineCode = l.Carrier, trackNumber = l.Number },
            originDate = l.OriginDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            departureAirport = l.Origin,
            arrivalAirport = l.Destination,
            departure = Side(l, arrival: false),
            arrival = Side(l, arrival: true),
            aircraftType = l.AircraftType
        });
        return JsonSerializer.SerializeToUtf8Bytes(flights, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    #endregion
}
