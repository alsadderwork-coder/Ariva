using System.Globalization;
using System.Text;
using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Border;
using Ariva.Infra.Flights.Acris;
using Ariva.Infra.Flights.Aidx;
using Ariva.Infra.Flights.Ssim;
using Ariva.Infra.Sensing;
using Ariva.Infra.Sensing.Declarative;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Setup;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// ARV-070, CWE-120 and CWE-501: every parser at Ariva's edge (the sensing dialects, AIDX, ACRIS, SSIM, AMAN's pull pages
/// and Kafka records, declarative mappings, zone rings) given random bytes, valid samples cut short, mutated or with a value
/// retyped: each answers with a result or Ariva's reason, never an exception past the adapter boundary, and a sensing push
/// never yields more events than its limit. A failure prints CsCheck's shrunk input and seed; the seed goes into a
/// regression test below.
/// </summary>
public sealed class ParserPropertyTests
{
    private const int MaxEvents = 40;
    private static readonly DateTime Received = new(2026, 10, 3, 18, 31, 0, DateTimeKind.Utc);
    private static readonly DevicePose Pose = new(12.5, 4, 90);

    private static byte[] File(string relative) => System.IO.File.ReadAllBytes(RepositoryPaths.Resolve(relative));

    private static readonly Lazy<byte[][]> XovisSamples = new(() =>
        [.. Directory.GetFiles(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples"), "*.json").Order().Select(System.IO.File.ReadAllBytes)]);

    private static readonly Lazy<byte[][]> OusterSamples = new(() =>
        [.. Directory.GetFiles(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative/ouster-detect-v1"), "*.json").Order().Select(System.IO.File.ReadAllBytes)]);

    private static readonly Lazy<byte[][]> CanonicalSamples = new(() =>
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var midnight = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        return [.. new[] { "S-15", "S-17" }.SelectMany(id => new[] { 1090, 1110 }.Select(minute =>
                SensorTraffic.Build(day, SensorTraffic.Sensor(id), EmulatedDialect.Canonical, minute, minute, m => midnight.AddMinutes(m), midnight.AddMinutes(minute + 1))))
            .Where(p => p is not null).Select(p => Encoding.UTF8.GetBytes(p.Json))];
    });

    private static void Holds(Gen<byte[]> inputs, Action<byte[]> property, string seed = null) =>
        inputs.Sample(property, iter: Fuzz.Iterations, seed: seed, print: b => Convert.ToBase64String(b));

    private static void SensingHolds(DeviceDialect dialect, DeclarativeMapping mapping, byte[] input)
    {
        var (push, error) = SensingIngest.Read(input, dialect, Pose, mapping, MaxEvents, Received);
        (push is null).Should().NotBe(error is null, "a push or a reason, never both or neither");
        if (push is not null)
            push.EventCount.Should().BeLessThanOrEqualTo(MaxEvents, "a push never yields more events than its limit");
        else
            error.Length.Should().BeLessThan(400, "the reason is Ariva's own sentence, not the payload");
    }

    [Fact]
    public void Xovis_Should_GiveAPushOrAReason_ForAnyBody()
    {
        foreach (var sample in XovisSamples.Value)
            Holds(Fuzz.From(sample, json: true), b => SensingHolds(DeviceDialect.Xovis, null, b));
    }

    [Fact]
    public void Canonical_Should_GiveAPushOrAReason_ForAnyBody()
    {
        CanonicalSamples.Value.Should().NotBeEmpty();
        foreach (var sample in CanonicalSamples.Value)
            Holds(Fuzz.From(sample, json: true), b => SensingHolds(DeviceDialect.Canonical, null, b));
    }

    [Fact]
    public void Declarative_Should_GiveAPushOrAReason_ForAnyBody()
    {
        var mapping = DeclarativeMappingCatalog.Embedded.Find("ouster-detect-v1");
        mapping.Should().NotBeNull();
        foreach (var sample in OusterSamples.Value)
            Holds(Fuzz.From(sample, json: true), b => SensingHolds(DeviceDialect.Declarative, mapping, b));
        Holds(Fuzz.Bytes, b => SensingHolds(DeviceDialect.Declarative, null, b));
    }

    [Fact]
    public void DeclarativeMapping_Should_RefuseABrokenDocument_OnlyWithAMappingException()
    {
        var seed = File("Platform/Backplane/Ariva.Infra/Sensing/Mappings/ouster-detect-v1.json");
        Holds(Fuzz.From(seed, json: true), b =>
        {
            try
            {
                DeclarativeMapping.Parse(b, "ouster-detect-v1").Should().NotBeNull();
            }
            catch (MappingException)
            {
                // Ariva's reason for an administrator's document.
            }
        });
    }

    private const string AidxNs = "http://www.iata.org/IATA/2007/00";

    private static readonly byte[] AidxSample = Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?><IATA_AIDX_FlightLegNotifRQ xmlns="{AidxNs}" Version="22.1" TimeStamp="2026-10-03T09:00:00Z" TransactionIdentifier="T-42"><Originator CompanyShortName="AODB"/><FlightLeg><LegIdentifier><Airline CodeContext="3">RJ</Airline><FlightNumber>111</FlightNumber><DepartureAirport CodeContext="3">AMM</DepartureAirport><ArrivalAirport CodeContext="3">DMO</ArrivalAirport><OriginDate>2026-10-03</OriginDate><RepeatNumber CurrentInd="true">1</RepeatNumber></LegIdentifier><LegData InternationalStatus="International"><ServiceType>J</ServiceType><AirportResources Usage="Actual"><Resource DepartureOrArrival="Arrival"><AircraftParkingPosition Qualifier="Gate">B21</AircraftParkingPosition></Resource></AirportResources><OperationTime OperationQualifier="ONB" CodeContext="9750" TimeType="SCT">2026-10-03T10:00:00Z</OperationTime><OperationTime OperationQualifier="ONB" CodeContext="9750" TimeType="EST">2026-10-03T10:05:00Z</OperationTime><AircraftInfo><AircraftType>320</AircraftType><Registration>JY-AYA</Registration></AircraftInfo><CodeShareInfo><Airline>XR</Airline><FlightNumber>1214</FlightNumber></CodeShareInfo></LegData></FlightLeg></IATA_AIDX_FlightLegNotifRQ>
        """);

    [Fact]
    public void Aidx_Should_GiveAMessageOrAReason_ForAnyBody()
    {
        AidxReader.Read(AidxSample).Error.Should().BeNull("the sample is a valid AIDX message");
        Holds(Fuzz.From(AidxSample, json: false), b =>
        {
            var (message, error) = AidxReader.Read(b);
            (message is null).Should().NotBe(error is null);
        });
    }

    private static readonly byte[] AcrisSample = Encoding.UTF8.GetBytes("""
        {"flights":[{"flightNumber":{"airlineCode":"RJ","trackNumber":"0111"},"originDate":"2026-10-03","departureAirport":"AMM","arrivalAirport":"DMO",
        "arrival":{"scheduled":"2026-10-03T10:00:00Z","estimated":"2026-10-03T10:10:00Z","block":"2026-10-03T10:14:00Z","gate":"G4","stand":"B7"},
        "aircraftType":"320","flightStatus":"Landed","codeShares":[{"airlineCode":"XR","trackNumber":"01214"}]},
        {"flightNumber":{"airlineCode":"RJ","trackNumber":"112"},"originDate":"2026-10-03","departureAirport":"DMO","arrivalAirport":"CAI","departure":{"scheduled":"2026-10-03T12:00:00Z"},"flightStatus":"cancelled"}]}
        """);

    [Fact]
    public void Acris_Should_GiveFlightsOrAReason_ForAnyBody()
    {
        AcrisReader.Read(AcrisSample).Error.Should().BeNull("the sample is a valid ACRIS answer");
        Holds(Fuzz.From(AcrisSample, json: true), b =>
        {
            var (flights, error) = AcrisReader.Read(b);
            (flights is null).Should().NotBe(error is null);
        });
    }

    private static string SsimLeg()
    {
        var line = new StringBuilder(new string(' ', 200));
        void Put(int start, string value) => line.Remove(start - 1, value.Length).Insert(start - 1, value);
        Put(1, "3");
        Put(3, "RJ ");
        Put(6, "0111");
        Put(10, "01");
        Put(12, "01");
        Put(14, "J");
        Put(15, "23SEP26");
        Put(22, "02DEC26");
        Put(29, "1234567");
        Put(37, "AMM");
        Put(40, "0900");
        Put(44, "0900");
        Put(48, "+0300");
        Put(55, "DMO");
        Put(58, "1100");
        Put(62, "1100");
        Put(66, "+0400");
        Put(71, "T1");
        Put(73, "320");
        return line.ToString();
    }

    private static readonly byte[] SsimSample = Encoding.ASCII.GetBytes(string.Join("\r\n",
        "1AIRLINE STANDARD SCHEDULE DATA SET".PadRight(200), new string('0', 200), "2LRJ ".PadRight(200), SsimLeg(), "5 RJ".PadRight(200)) + "\r\n");

    [Fact]
    public async Task Ssim_Should_GiveAScheduleOrAReason_ForAnyFile()
    {
        var airports = AidxReader.Airports(["DMO"]);
        var now = new DateTime(2026, 10, 3, 6, 0, 0, DateTimeKind.Utc);
        var ct = TestContext.Current.CancellationToken;
        (await SsimReader.ReadAsync(new MemoryStream(SsimSample), airports, now, 14, ct)).Error.Should().BeNull("the sample is a valid SSIM file");
        await Fuzz.From(SsimSample, json: false).SampleAsync(async b =>
        {
            var (schedule, error) = await SsimReader.ReadAsync(new MemoryStream(b), airports, now, 14, ct);
            (schedule is null).Should().NotBe(error is null);
        }, iter: Fuzz.Iterations, print: b => Convert.ToBase64String(b));
    }

    private static readonly object[] AmanRecords =
    [
        new DeskSessionChanged("DMO", "IN01", DeskSessionState.Opened, "CIT", new DateTimeOffset(2026, 10, 3, 18, 29, 12, TimeSpan.Zero), "aman-1"),
        new DeskIntervalStats("DMO", "IN01", new DateTimeOffset(2026, 10, 3, 18, 29, 0, TimeSpan.Zero), 60, 2, 3, 41, 66, 30, "CIT", "aman-2"),
        new EGateIntervalStats("DMO", "EG01", new DateTimeOffset(2026, 10, 3, 18, 29, 0, TimeSpan.Zero), 60, 9, 5, 4,
            new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.DocumentRead] = 3, [EGateRejectCategory.Other] = 1 }, 14, "aman-3"),
        new InboundFlightLaneDemand("DMO", "DM214-20261003-A", new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero), 180,
            new Dictionary<string, int> { ["CIT"] = 40, ["VIS"] = 70 }, 60, new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero), "aman-4")
    ];

    private static void AmanHolds<T>(T record) where T : class
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(record, AmanContracts.Json);
        var page = Encoding.UTF8.GetBytes($$"""{"data":{"items":[{{Encoding.UTF8.GetString(json)}}],"next":14},"hasErrors":false,"errorMessages":[]}""");
        new AmanFeedJson<T>().Deserialize(json, isNull: false, default).Should().NotBeNull("the record is valid");
        AmanFeedPages.Read<T>(page, 10).Error.Should().BeNull("the page is valid");

        Holds(Fuzz.From(json, json: true), b => new AmanFeedJson<T>().Deserialize(b, isNull: false, default));
        Holds(Fuzz.From(page, json: true), b =>
        {
            var (result, error) = AmanFeedPages.Read<T>(b, 10);
            (result is null).Should().NotBe(error is null);
            if (result is not null)
                result.Items.Should().NotContainNulls();
        });
    }

    [Fact]
    public void AmanRecordsAndPages_Should_ReadOrRefuse_AnyBody()
    {
        AmanHolds((DeskSessionChanged)AmanRecords[0]);
        AmanHolds((DeskIntervalStats)AmanRecords[1]);
        AmanHolds((EGateIntervalStats)AmanRecords[2]);
        AmanHolds((InboundFlightLaneDemand)AmanRecords[3]);
    }

    [Fact]
    public void OutboundTokenAnswers_Should_GiveATokenOrAReason_ForAnyBody()
    {
        var now = new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
        foreach (var seed in new[] { """{"accessToken":"eyJ.abc.def","expiresAt":"2026-10-03T19:00:00Z"}""", """{"access_token":"tok-123","expires_in":3600,"token_type":"Bearer"}""" })
        {
            var bytes = Encoding.UTF8.GetBytes(seed);
            Ariva.Infra.Integration.Outbound.TokenHandler.ReadToken(bytes, now).Error.Should().BeNull("the seed is a valid answer");
            Holds(Fuzz.From(bytes, json: true), b =>
            {
                var (token, _, error) = Ariva.Infra.Integration.Outbound.TokenHandler.ReadToken(b, now);
                (token is null).Should().NotBe(error is null);
                if (token is not null)
                    token.Should().MatchRegex("^[!-~]{1,8192}$", "a token is printable ASCII");
            });
        }
    }

    [Fact]
    public void ZoneRings_Should_ParseOrBeNull_ForAnyText()
    {
        var seed = Encoding.UTF8.GetBytes("0 0,10 0,10 5.25,0 5.25");
        Holds(Fuzz.From(seed, json: false), b =>
        {
            var ring = Geometry.ParseRing(Encoding.UTF8.GetString(b), 200);
            if (ring is not null)
            {
                ring.Count.Should().BeInRange(1, 200);
                ring.Should().OnlyContain(p => double.IsFinite(p.X) && double.IsFinite(p.Y));
                Geometry.ParseRing(Geometry.FormatRing(ring), 200).Should().HaveSameCount(ring, "a ring reads back from its own text");
            }
        });
        Geometry.ParseRing(string.Join(",", Enumerable.Repeat("1 1", 201)), 200).Should().BeNull("more points than the limit");
        Geometry.ParseRing(string.Create(CultureInfo.InvariantCulture, $"0 {double.MaxValue}e10,1 1"), 200).Should().BeNull("not finite");
    }

    // Regression tests: the counterexamples the properties found (ARV-070), shrunk, each now refused with Ariva's reason.

    [Fact]
    public void Regression_InvalidUtf8InsideAJsonString_IsRefusedBeforeMapping()
    {
        var body = Encoding.UTF8.GetBytes("""{"logics_data":{"package_info":{"version":"5.0","id":1},"logics":[{"id":1,"name":"Forward","records":[]}]}}""");
        body[Array.IndexOf(body, (byte)'w')] = 0xF0; // "For\xF0ard": JsonDocument parsed it, reading the string threw
        var (push, error) = SensingIngest.Read(body, DeviceDialect.Xovis, Pose, null, MaxEvents, Received);
        push.Should().BeNull();
        error.Should().Be("The body is not valid UTF-8.");
    }

    [Fact]
    public void Regression_ANullItemInACanonicalList_IsRefused()
    {
        foreach (var list in new[] { "tracks", "crossings", "occupancy", "intervals" })
        {
            var (push, error) = SensingIngest.Read(Encoding.UTF8.GetBytes($$"""{"{{list}}":[null]}"""), DeviceDialect.Canonical, Pose, null, MaxEvents, Received);
            push.Should().BeNull();
            error.Should().Be($"{list}[] has an empty item.", "a null item threw a NullReferenceException");
        }
    }

    [Fact]
    public void Regression_AnAmanPagePositionThatIsNotANumber_IsRefused()
    {
        foreach (var next in new[] { "[]", "\"14\"", "{}", "true", "null", "1.5" })
            AmanFeedPages.Read<DeskSessionChanged>(Encoding.UTF8.GetBytes($$"""{"data":{"items":[],"next":{{next}}},"hasErrors":false}"""), 10).Error
                .Should().Be("AMAN's answer is not its feed envelope.", "next = {0} threw an InvalidOperationException", next);
        AmanFeedPages.Read<DeskSessionChanged>([0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D], 10).Error.Should().Be("AMAN's answer is not UTF-8 JSON.");
    }

    [Fact]
    public void Regression_ATokenAnswerOfAnotherShape_IsRefused()
    {
        var now = new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
        static (string Token, DateTimeOffset Expires, string Error) Read(string json, DateTimeOffset now) =>
            Ariva.Infra.Integration.Outbound.TokenHandler.ReadToken(Encoding.UTF8.GetBytes(json), now);
        Read("[]", now).Error.Should().Be("has no usable token.", "an array root threw an InvalidOperationException");
        Read("\"tok\"", now).Error.Should().Be("has no usable token.");
        Read("""{"access_token":"tok","expires_in":"3600"}""", now).Expires.Should().Be(now.AddMinutes(5), "expires_in as text threw; it now counts as no expiry");
        Read("""{"access_token":"tok","expires_in":3600}""", now).Expires.Should().Be(now.AddHours(1));
        Read("{", now).Error.Should().Be("is not JSON.");
    }

    [Fact]
    public void Regression_AnEscapedLoneSurrogate_IsRefused_ByEveryJsonReader()
    {
        // Valid UTF-8 and well-formed JSON, but no string: reading the member name or value threw an InvalidOperationException
        // (found by the review of ARV-070; the generators now insert such escapes).
        string[] bodies = [@"{""\uDC00"":0}", @"{""logics_data"":{""\uD800x"":1}}", @"[{""\uDC00"":1}]", @"{""flights"":[{""\uDC00"":1}]}"];
        foreach (var body in bodies.Select(Encoding.UTF8.GetBytes))
        {
            foreach (var dialect in new[] { DeviceDialect.Xovis, DeviceDialect.Canonical, DeviceDialect.Declarative })
            {
                var mapping = dialect == DeviceDialect.Declarative ? DeclarativeMappingCatalog.Embedded.Find("ouster-detect-v1") : null;
                // A push when the mapper never reads that member, a reason when it does: never an exception.
                var (push, error) = SensingIngest.Read(body, dialect, Pose, mapping, MaxEvents, Received);
                (push is null).Should().NotBe(error is null);
            }

            AcrisReader.Read(body).Error.Should().NotBeNull();
            AmanFeedPages.Read<DeskSessionChanged>(body, 10).Error.Should().NotBeNull();
            Ariva.Infra.Integration.Outbound.TokenHandler.ReadToken(body, Received).Error.Should().NotBeNull();
        }

        var xovis = Encoding.UTF8.GetString(XovisSamples.Value.First(b => Encoding.UTF8.GetString(b).Contains(@"""name"":""Entry A""", StringComparison.Ordinal)))
            .Replace(@"""name"":""Entry A""", @"""name"":""\uDC00""", StringComparison.Ordinal);
        SensingIngest.Read(Encoding.UTF8.GetBytes(xovis), DeviceDialect.Xovis, Pose, null, MaxEvents, Received).Error.Should().Be("The body is not well-formed JSON.");
        AmanFeedPages.Read<DeskSessionChanged>(Encoding.UTF8.GetBytes(@"{""data"":{""\uDC00"":[],""next"":1}}"), 1).Error.Should().Be("AMAN's answer is not JSON.");
        Ariva.Infra.Integration.Outbound.TokenHandler.ReadToken(Encoding.UTF8.GetBytes(@"{""access_token"":""\uDC00""}"), Received).Error.Should().Be("is not JSON.");
    }

    [Fact]
    public void Declarative_Should_RefuseAPush_When_TheDevicesMappingIsMissing()
    {
        var sample = OusterSamples.Value[0];
        SensingIngest.Read(sample, DeviceDialect.Declarative, Pose, null, MaxEvents, Received).Error
            .Should().Be("The device's declarative mapping is not in this version of Ariva.");
    }
}
