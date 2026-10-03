using System.Text;
using System.Xml.Linq;
using Ariva.Api.Integration.Controllers;
using Ariva.Core.Flights;
using Ariva.Infra.Flights.Aidx;
using FluentAssertions;

namespace Ariva.UnitTests.Flights;

/// <summary>
/// ARV-044: AIDX 22.1 messages are read safely (CWE-611 XXE, CWE-776 entity expansion, CWE-120 size and depth) and
/// validated against Ariva's profile; legs map to the site's side with the documented times, resources, status and key;
/// the acknowledgement reports refused and unchanged legs without echoing the message.
/// </summary>
public sealed class AidxTests
{
    private const string Ns = "http://www.iata.org/IATA/2007/00";
    private static readonly IReadOnlySet<string> Dmo = AidxReader.Airports(["DMO", "ODMO"]);

    private static string Leg(string from = "AMM", string to = "DMO", string number = "111", string extra = "", string legData = "") => $"""
        <FlightLeg>
          <LegIdentifier>
            <Airline CodeContext="3">RJ</Airline>
            <FlightNumber>{number}</FlightNumber>
            <DepartureAirport CodeContext="3">{from}</DepartureAirport>
            <ArrivalAirport CodeContext="3">{to}</ArrivalAirport>
            <OriginDate>2026-10-03</OriginDate>
            <RepeatNumber CurrentInd="true">1</RepeatNumber>
          </LegIdentifier>
          <LegData InternationalStatus="International">
            <ServiceType>J</ServiceType>
            {legData}
            <AirportResources Usage="Planned">
              <Resource DepartureOrArrival="Arrival"><AircraftParkingPosition Qualifier="Gate">P1</AircraftParkingPosition><PassengerGate>G1</PassengerGate></Resource>
            </AirportResources>
            <AirportResources Usage="Actual">
              <Resource DepartureOrArrival="Arrival"><AircraftParkingPosition Qualifier="Gate">B21</AircraftParkingPosition></Resource>
              <Resource DepartureOrArrival="Departure"><PassengerGate>D4</PassengerGate></Resource>
            </AirportResources>
            <OperationTime OperationQualifier="ONB" CodeContext="9750" TimeType="SCT">2026-10-03T10:00:00Z</OperationTime>
            <OperationTime OperationQualifier="OFB" CodeContext="9750" TimeType="SCT">2026-10-03T07:00:00Z</OperationTime>
            <OperationTime OperationQualifier="ONB" CodeContext="9750" TimeType="EST">2026-10-03T10:05:00Z</OperationTime>
            <OperationTime OperationQualifier="ONB" CodeContext="9750" TimeType="EST">2026-10-03T10:12:00Z</OperationTime>
            <OperationTime OperationQualifier="TDN" CodeContext="9750" TimeType="ACT">2026-10-03T10:03:00Z</OperationTime>
            <OperationTime OperationQualifier="OFB" CodeContext="9750" TimeType="ACT">2026-10-03T07:08:00Z</OperationTime>
            <OperationTime OperationQualifier="TKO" CodeContext="9750" TimeType="ACT">2026-10-03T07:20:00Z</OperationTime>
            <AircraftInfo><AircraftType>320</AircraftType><Registration>JY-AYA</Registration></AircraftInfo>
            <CodeShareInfo><Airline>XR</Airline><FlightNumber>1214</FlightNumber></CodeShareInfo>
          </LegData>
          {extra}
          <TPA_Extension><x:anything xmlns:x="urn:aodb">free text &amp; more</x:anything></TPA_Extension>
        </FlightLeg>
        """;

    private static string Message(string legs, string rootAttributes = "Version=\"22.1\" TimeStamp=\"2026-10-03T09:00:00Z\" TransactionIdentifier=\"T-42\"", string prolog = "") =>
        $"""<?xml version="1.0" encoding="UTF-8"?>{prolog}<IATA_AIDX_FlightLegNotifRQ xmlns="{Ns}" {rootAttributes}><Originator CompanyShortName="AODB"/>{legs}</IATA_AIDX_FlightLegNotifRQ>""";

    private static (AidxMessage Message, string Error) Read(string xml) => AidxReader.Read(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void Read_Should_ReadTheLegsOfAValidMessage_When_TheProfileAcceptsIt()
    {
        var (message, error) = Read(Message(Leg() + Leg("DMO", "CAI", "112")));

        error.Should().BeNull();
        message.TimeStamp.Should().Be(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc));
        message.TransactionIdentifier.Should().Be("T-42");
        message.Legs.Should().HaveCount(2);
        var leg = message.Legs[0];
        leg.Should().Match<AidxLeg>(l => l.Airline == "RJ" && l.FlightNumber == "111" && l.DepartureAirport == "AMM" && l.ArrivalAirport == "DMO" &&
                                          l.OriginDate == new DateOnly(2026, 10, 3) && l.AircraftType == "320");
        leg.ArrivalResources.Should().Be(new AidxResources(null, "B21", "G1"), "an actual resource wins, a planned one fills what it does not give");
        leg.DepartureResources.Gate.Should().Be("D4");
        leg.Codeshares.Should().Equal("XR1214");
        leg.Times.Should().OnlyContain(t => t.Time!.Value.Kind == DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("""<!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]>""", "an external entity (XXE)")]
    [InlineData("""<!DOCTYPE r [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>""", "entity expansion")]
    [InlineData("""<!DOCTYPE r SYSTEM "http://169.254.169.254/latest/meta-data/">""", "an external DTD (SSRF)")]
    public void Read_Should_RefuseTheMessage_When_ItHasADoctype(string prolog, string why)
    {
        var (message, error) = Read(Message(Leg(), prolog: prolog));

        message.Should().BeNull(why);
        error.Should().Contain("DOCTYPE").And.NotContain("passwd").And.NotContain("169.254");
    }

    [Theory]
    [InlineData("1234X", "a flight number that is not 1 to 4 digits")]
    [InlineData("<b>1</b>", "markup where a code belongs")]
    public void Read_Should_RefuseTheMessage_When_AFieldBreaksTheProfile(string number, string why)
    {
        var (message, error) = Read(Message(Leg(number: number)));

        message.Should().BeNull(why);
        error.Should().Contain("line").And.NotContain(number);
    }

    [Fact]
    public void Read_Should_RefuseTheMessage_When_ItIsNotANotification()
    {
        Read($"""<IATA_AIDX_FlightLegNotifRQ xmlns="urn:other">{Leg()}</IATA_AIDX_FlightLegNotifRQ>""").Error.Should().Contain("AIDX namespace");
        Read($"""<IATA_AIDX_FlightLegRS xmlns="{Ns}"/>""").Error.Should().NotBeNull("another AIDX message");
        Read(Message("")).Error.Should().Contain("1 to 500 FlightLeg");
        Read(Message(string.Concat(Enumerable.Repeat(Leg(), 501)))).Error.Should().Contain("1 to 500 FlightLeg");
        Read(Message(Leg(), rootAttributes: "TimeStamp=\"2026-10-03T12:00:00+03:00\"")).Error.Should().Contain("UTC");
        Read(Message(Leg(), rootAttributes: "TimeStamp=\"yesterday\"")).Error.Should().Contain("line");
        Read("not xml at all").Error.Should().Contain("well-formed");
        Read("").Error.Should().Contain("empty");
    }

    [Fact]
    public void Read_Should_RefuseTheMessage_When_ItIsTooDeepOrTooLarge()
    {
        var deep = string.Concat(Enumerable.Repeat("<TPA_Extension>", 40)) + string.Concat(Enumerable.Repeat("</TPA_Extension>", 40));
        Read(Message(Leg(extra: deep))).Error.Should().Contain("deeper than 32");

        AidxReader.Read(new byte[AidxReader.MaxBytes + 1]).Error.Should().Contain("5 MB");
    }

    [Fact]
    public void Read_Should_IgnoreProcessingInstructionsAndComments_When_Present()
    {
        var (message, error) = Read(Message("<?xml-stylesheet href=\"http://x/y.xsl\"?><!-- note -->" + Leg()));

        error.Should().BeNull();
        message.Legs.Should().HaveCount(1);
    }

    [Fact]
    public void ToLeg_Should_MapTheSitesSide_When_TheLegArrives()
    {
        var leg = Read(Message(Leg())).Message.Legs[0];

        var (data, error) = AidxMapping.ToLeg(leg, Dmo);

        error.Should().BeNull();
        data.Should().Match<FlightLegData>(d => d.FlightKey == "RJ111-20261003-A" && d.Direction == "Arrival" && d.Origin == "AMM" && d.Destination == "DMO" &&
                                                d.Stand == "B21" && d.Gate == "G1" && d.AircraftType == "320" && d.Status == null);
        data.ScheduledUtc.Should().Be(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc), "the scheduled on-block");
        data.EstimatedUtc.Should().Be(new DateTime(2026, 10, 3, 10, 12, 0, DateTimeKind.Utc), "the last estimate of its kind");
        data.ActualUtc.Should().Be(new DateTime(2026, 10, 3, 10, 3, 0, DateTimeKind.Utc), "touchdown");
        data.OffBlockUtc.Should().BeNull("an arrival has no off-block");
        FlightRules.Check(data, new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc)).Errors.Should().BeEmpty();
    }

    [Fact]
    public void ToLeg_Should_MapTheSitesSide_When_TheLegDeparts()
    {
        var leg = Read(Message(Leg("ODMO", "CAI", "112"))).Message.Legs[0];

        var (data, error) = AidxMapping.ToLeg(leg, Dmo);

        error.Should().BeNull("an ICAO code of the site's airport counts too");
        data.Should().Match<FlightLegData>(d => d.FlightKey == "RJ112-20261003-D" && d.Direction == "Departure" && d.Gate == "D4" && d.OnBlockUtc == null);
        data.ScheduledUtc.Should().Be(new DateTime(2026, 10, 3, 7, 0, 0, DateTimeKind.Utc));
        data.OffBlockUtc.Should().Be(new DateTime(2026, 10, 3, 7, 8, 0, DateTimeKind.Utc));
        data.ActualUtc.Should().Be(new DateTime(2026, 10, 3, 7, 20, 0, DateTimeKind.Utc), "take-off");
    }

    [Theory]
    [InlineData("AMM", "CAI", "a leg that does not touch the site")]
    [InlineData("DMO", "DMO", "a leg that both leaves and reaches the site")]
    public void ToLeg_Should_RefuseTheLeg_When_ItsSideIsNotClear(string from, string to, string why)
    {
        var leg = Read(Message(Leg(from, to))).Message.Legs[0];

        AidxMapping.ToLeg(leg, Dmo).Error.Should().Contain("exactly one airport", why);
        AidxMapping.ToLeg(null, Dmo).Error.Should().Contain("LegIdentifier");
    }

    [Theory]
    [InlineData("DX", "Cancelled")]
    [InlineData("DV", "Diverted")]
    [InlineData("ZZ", null)]
    public void ToLeg_Should_MapCancelAndDivert_When_TheOperationalStatusSaysSo(string code, string status)
    {
        var leg = Read(Message(Leg(legData: $"<OperationalStatus CodeContext=\"2005\">{code}</OperationalStatus>"))).Message.Legs[0];

        AidxMapping.ToLeg(leg, Dmo).Leg.Status.Should().Be(status);
    }

    [Fact]
    public void ToLeg_Should_LeaveATimeWithAnOffsetToTheRules_When_TheMessageHasOne()
    {
        var xml = Message(Leg()).Replace("2026-10-03T10:00:00Z", "2026-10-03T13:00:00+03:00", StringComparison.Ordinal);
        var data = AidxMapping.ToLeg(Read(xml).Message.Legs[0], Dmo).Leg;

        FlightRules.Check(data, new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc)).Errors.Should().Contain(e => e.Contains("scheduledUtc", StringComparison.Ordinal));
    }

    [Fact]
    public void Acknowledgement_Should_ReportRefusedAndUnchangedLegs_When_Answering()
    {
        var at = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        var xml = AidxController.Acknowledgement("T-42", [
            new FlightItemResult(0, "RJ111-20261003-A", true, [], []),
            new FlightItemResult(1, null, false, ["The leg must arrive at or depart from exactly one airport of this site."], []),
            new FlightItemResult(2, "RJ113-20261003-A", false, [], ["Nothing newer than what is known."])
        ], at);

        var root = XDocument.Parse(xml).Root!;
        root.Name.Should().Be(XName.Get("IATA_AIDX_FlightLegRS", Ns));
        root.Attribute("TransactionIdentifier")!.Value.Should().Be("T-42");
        root.Element(XName.Get("Success", Ns)).Should().NotBeNull();
        root.Descendants(XName.Get("Warning", Ns)).Select(w => (w.Attribute("RecordID")!.Value, w.Attribute("Status")!.Value)).Should().Equal(("1", "Refused"), ("2", "Unchanged"));
        AidxController.Acknowledgement("<script>", [], at).Should().NotContain("script", "only a plain transaction id is echoed");
    }

    [Fact]
    public void Read_Should_RefuseTheMessage_When_ItHasNoTimeStamp()
    {
        // Without it a re-delivered old message would be stamped with its receipt time and overwrite newer values.
        var (message, error) = Read(Message(Leg(), rootAttributes: "Version=\"22.1\" TransactionIdentifier=\"T-1\""));

        message.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Read_Should_RefuseTheMessage_When_ItHasTooManyElements()
    {
        var many = string.Concat(Enumerable.Repeat("<y/>", AidxReader.MaxElements + 1));
        var xml = Message(Leg(extra: "<TPA_Extension>" + many + "</TPA_Extension>"));
        Encoding.UTF8.GetByteCount(xml).Should().BeLessThan(AidxReader.MaxBytes, "well under the size limit, so the element cap is what refuses it");

        Read(xml).Error.Should().Contain($"more than {AidxReader.MaxElements} elements");
    }

    [Fact]
    public void Read_Should_ValidateWithTheEmbeddedProfileOnly_When_TheMessageNamesOrCarriesASchema()
    {
        // A schema location (SSRF) and an inline schema that would loosen FlightNumber: neither is fetched or used.
        var inline = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="http://www.iata.org/IATA/2007/00"><xs:element name="FlightNumber" type="xs:string"/></xs:schema>
            """;
        var xml = Message(inline + Leg(number: "NOT-A-NUMBER"),
            rootAttributes: "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"http://www.iata.org/IATA/2007/00 http://169.254.169.254/x.xsd\" TimeStamp=\"2026-10-03T09:00:00Z\"");

        var (message, error) = Read(xml);

        message.Should().BeNull();
        error.Should().Contain("line").And.NotContain("169.254").And.NotContain("NOT-A-NUMBER");
    }

    [Fact]
    public void ToLeg_Should_CompareAirportsInUpperCase_When_TheMessageUsesLowerCase()
    {
        var leg = Read(Message(Leg("amm", "dmo"))).Message.Legs[0];

        AidxMapping.ToLeg(leg, Dmo).Leg.Direction.Should().Be("Arrival");
    }
}
