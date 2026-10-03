using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Ariva.Core.Flights;

namespace Ariva.Infra.Flights.Aidx;

/// <summary>
/// Reads an AIDX 22.1 <c>IATA_AIDX_FlightLegNotifRQ</c> (ARV-044) safely (CWE-611, CWE-776, CWE-120, CWE-501): no DTD
/// (a DOCTYPE is refused, so no external entity and no entity expansion), no resolver, no processing instructions, at
/// most 5 MB of characters, 32 levels and 200,000 elements, validated against Ariva's AIDX profile
/// (<c>aidx-22.1-ariva-profile.xsd</c>) and at most 500 flight legs. A refusal gives only where the message went wrong
/// (line and position), never its text.
/// </summary>
public static class AidxReader
{
    public const string Namespace = "http://www.iata.org/IATA/2007/00";
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxDepth = 32;
    public const int MaxElements = 200_000;
    public const int MaxLegs = FlightRules.MaxBatch;

    private static readonly XNamespace Ns = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas);

    /// <summary>The message, or why it is not one.</summary>
    public static (AidxMessage Message, string Error) Read(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length == 0)
            return (null, "The message is empty.");
        if (body.Length > MaxBytes)
            return (null, "An AIDX message is at most 5 MB.");

        // First pass, not validating: depth and element count, before anything is built in memory.
        var shapeError = CheckShape(body);
        if (shapeError is not null)
            return (null, shapeError);

        XDocument document;
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, Settings(validate: true));
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlSchemaValidationException e)
        {
            return (null, $"The message is not AIDX 22.1 as Ariva reads it, at line {e.LineNumber}, position {e.LinePosition}.");
        }
        catch (XmlException e)
        {
            return (null, $"The message is not well-formed XML (or has a DOCTYPE), at line {e.LineNumber}, position {e.LinePosition}.");
        }

        var root = document.Root;
        if (root is null || root.Name != Ns + "IATA_AIDX_FlightLegNotifRQ")
            return (null, "The message is not an IATA_AIDX_FlightLegNotifRQ in the AIDX namespace.");
        var legs = root.Elements(Ns + "FlightLeg").ToList();
        if (legs.Count is 0 or > MaxLegs)
            return (null, $"An AIDX message has 1 to {MaxLegs} FlightLeg elements.");

        // The message time orders every field Ariva keeps, so it is required: without it a late re-delivery of an old
        // message would be stamped with its receipt time and overwrite newer values (CWE-501).
        try
        {
            if (root.Attribute("TimeStamp") is not { } at)
                return (null, "The message has no TimeStamp; Ariva orders AIDX messages by it.");
            var stamp = XmlConvert.ToDateTime(at.Value, XmlDateTimeSerializationMode.RoundtripKind);
            if (stamp.Kind != DateTimeKind.Utc)
                return (null, "TimeStamp is in UTC (ending in Z).");
            return (new AidxMessage(stamp, root.Attribute("TransactionIdentifier")?.Value, [.. legs.Select(Leg)]), null);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            // The profile types every date and time; anything it let through that still cannot be read is a 400, never a 500.
            return (null, "The message has a date or time Ariva cannot read.");
        }
    }

    private static string CheckShape(byte[] body)
    {
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, Settings(validate: false));
            var elements = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.Depth >= MaxDepth)
                    return $"The message is nested deeper than {MaxDepth} levels.";
                if (++elements > MaxElements)
                    return $"The message has more than {MaxElements} elements.";
            }

            return null;
        }
        catch (XmlException e)
        {
            return $"The message is not well-formed XML (or has a DOCTYPE), at line {e.LineNumber}, position {e.LinePosition}.";
        }
    }

    private static XmlReaderSettings Settings(bool validate)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxBytes,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            CloseInput = false
        };
        if (validate)
        {
            settings.ValidationType = ValidationType.Schema;
            settings.Schemas = Schemas.Value;
            settings.ValidationFlags = XmlSchemaValidationFlags.None;
            // Every validation error throws. Elements the profile does not declare are let through unread (lax), and the
            // root is checked by name after loading.
            settings.ValidationEventHandler += (_, e) =>
            {
                if (e.Severity == XmlSeverityType.Error)
                    throw e.Exception ?? new XmlSchemaValidationException(e.Message);
            };
        }

        return settings;
    }

    private static XmlSchemaSet LoadSchemas()
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        using var stream = typeof(AidxReader).Assembly.GetManifestResourceStream("Ariva.Infra.Flights.Aidx.aidx-22.1-ariva-profile.xsd")
                           ?? throw new InvalidOperationException("The AIDX profile is not embedded.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(Namespace, reader);
        set.Compile();
        return set;
    }

    private static AidxLeg Leg(XElement leg)
    {
        var id = leg.Element(Ns + "LegIdentifier");
        var data = leg.Element(Ns + "LegData");
        if (id is null)
            return null;
        var resources = data?.Elements(Ns + "AirportResources").ToList() ?? [];
        return new AidxLeg(
            Text(id.Element(Ns + "Airline")),
            Text(id.Element(Ns + "FlightNumber")),
            Text(id.Element(Ns + "OperationalSuffix")),
            Text(id.Element(Ns + "DepartureAirport")),
            Text(id.Element(Ns + "ArrivalAirport")),
            id.Element(Ns + "OriginDate") is { } date ? DateOnly.FromDateTime(XmlConvert.ToDateTime(date.Value, XmlDateTimeSerializationMode.Unspecified)) : null,
            Text(data?.Element(Ns + "OperationalStatus")),
            [.. (data?.Elements(Ns + "OperationTime") ?? []).Select(Time)],
            Side(resources, "Departure"),
            Side(resources, "Arrival"),
            Text(data?.Element(Ns + "AircraftInfo")?.Element(Ns + "AircraftType")),
            [.. (data?.Elements(Ns + "CodeShareInfo") ?? []).Select(c => Text(c.Element(Ns + "Airline")) + Text(c.Element(Ns + "FlightNumber"))).Where(c => c.Length > 0)]);
    }

    private static AidxTime Time(XElement time)
    {
        var value = XmlConvert.ToDateTime(time.Value, XmlDateTimeSerializationMode.RoundtripKind);
        // Only a time with its zone is a time Ariva can order by; one without (Unspecified) or local is passed on as
        // such and refused by FlightRules for its field.
        return new AidxTime(time.Attribute("OperationQualifier")?.Value, time.Attribute("TimeType")?.Value, value);
    }

    // The side's terminal, stand and gate: an Actual AirportResources wins over a Planned one (or one without usage).
    private static AidxResources Side(List<XElement> resources, string side)
    {
        string terminal = null, stand = null, gate = null;
        foreach (var usage in resources.OrderBy(r => string.Equals((string)r.Attribute("Usage"), "Actual", StringComparison.Ordinal) ? 1 : 0))
        {
            foreach (var resource in usage.Elements(Ns + "Resource").Where(r => string.Equals((string)r.Attribute("DepartureOrArrival"), side, StringComparison.Ordinal)))
            {
                terminal = Text(resource.Element(Ns + "AircraftTerminal")) ?? terminal;
                stand = Text(resource.Element(Ns + "AircraftParkingPosition")) ?? stand;
                gate = Text(resource.Element(Ns + "PassengerGate")) ?? gate;
            }
        }

        return new AidxResources(terminal, stand, gate);
    }

    private static string Text(XElement element) => element is null ? null : element.Value.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>The IATA and ICAO codes of a site's airports, as the leg's airports are compared with them.</summary>
    public static IReadOnlySet<string> Airports(IEnumerable<string> codes) =>
        new HashSet<string>((codes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpper(CultureInfo.InvariantCulture)), StringComparer.Ordinal);
}
