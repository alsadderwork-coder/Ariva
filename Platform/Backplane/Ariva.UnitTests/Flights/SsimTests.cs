using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Flights;
using Ariva.Infra.Flights.Ssim;
using FluentAssertions;

namespace Ariva.UnitTests.Flights;

/// <summary>
/// ARV-046: SSIM chapter 7 files are read as a stream with size, line and leg limits (CWE-120); each flight leg record
/// expands to the site's legs per operating date within the horizon, in UTC from the record's time mode and
/// variations; malformed lines are reported by number without their text and never stop the others (CWE-501).
/// </summary>
public sealed class SsimTests
{
    private static readonly IReadOnlySet<string> Dmo = new HashSet<string>(["DMO"]);
    private static readonly DateOnly Today = new(2026, 10, 5); // a Monday
    private static readonly DateTime Now = new(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Date(DateOnly date) => date.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>A type 3 record with its fields at their SSIM positions (1-based, inclusive).</summary>
    private static string Leg(string airline = "RJ", string number = "0111", DateOnly? from = null, string to = null, string days = "1234567", string frequency = " ",
        string departure = "AMM", string std = "0900", string departureVariation = "+0300", string arrival = "DMO", string sta = "1100", string arrivalVariation = "+0400",
        string aircraft = "320", string suffix = " ", string terminal = "T1")
    {
        var line = new StringBuilder(new string(' ', 200));
        void Put(int start, string value) => line.Remove(start - 1, value.Length).Insert(start - 1, value);
        Put(1, "3");
        Put(2, suffix);
        Put(3, airline.PadRight(3));
        Put(6, number.PadLeft(4, '0'));
        Put(10, "01");
        Put(12, "01");
        Put(14, "J");
        Put(15, Date(from ?? Today.AddDays(-10)));
        Put(22, to ?? Date(Today.AddDays(60)));
        Put(29, days.PadRight(7));
        Put(36, frequency);
        Put(37, departure);
        Put(40, std);
        Put(44, std);
        Put(48, departureVariation);
        Put(53, "  ");
        Put(55, arrival);
        Put(58, sta);
        Put(62, sta);
        Put(66, arrivalVariation);
        Put(71, terminal.PadRight(2));
        Put(73, aircraft);
        return line.ToString();
    }

    private static string File(char timeMode, params string[] legs) =>
        string.Join("\r\n", new[] { "1AIRLINE STANDARD SCHEDULE DATA SET".PadRight(200), new string('0', 200), ("2" + timeMode + "RJ ").PadRight(200) }
            .Concat(legs).Append("5 RJ".PadRight(200))) + "\r\n";

    private static Task<(SsimSchedule Schedule, string Error)> Read(string text, int horizon = 14) => Read(Encoding.ASCII.GetBytes(text), horizon);

    private static Task<(SsimSchedule Schedule, string Error)> Read(byte[] bytes, int horizon = 14) =>
        SsimReader.ReadAsync(new MemoryStream(bytes), Dmo, Now, horizon, Ct);

    [Fact]
    public async Task Read_Should_ExpandTheSitesLegsPerOperatingDate_When_TheFileIsValid()
    {
        var text = File('L', Leg(), Leg(number: "112", departure: "DMO", std: "1300", departureVariation: "+0400", arrival: "CAI", sta: "1430", arrivalVariation: "+0300",
            days: "1 3 5"), Leg(number: "113", departure: "AMM", arrival: "CAI"));

        var (schedule, error) = await Read(text);

        error.Should().BeNull();
        schedule.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(text))));
        schedule.LegRecords.Should().Be(3);
        schedule.LegRecordsOfSite.Should().Be(2, "the AMM to CAI leg does not touch the site");
        schedule.From.Should().Be(Today.AddDays(-1));
        schedule.To.Should().Be(Today.AddDays(14));
        var arrivals = schedule.Legs.Where(l => l.Direction == "Arrival").ToList();
        arrivals.Should().HaveCount(16, "daily from yesterday to the horizon");
        var first = arrivals.Single(l => l.FlightKey == "RJ111-20261005-A");
        first.ScheduledUtc.Should().Be(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc), "11:00 local at +04:00");
        first.Should().Match<FlightLegData>(l => l.Number == "111" && l.Origin == "AMM" && l.Destination == "DMO" && l.Terminal == "T1" && l.AircraftType == "320");
        var departures = schedule.Legs.Where(l => l.Direction == "Departure").ToList();
        departures.Select(l => DateOnly.FromDateTime(l.ScheduledUtc!.Value).DayOfWeek).Distinct().Should()
            .BeEquivalentTo([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        departures.Single(l => l.FlightKey == "RJ112-20261005-D").ScheduledUtc.Should().Be(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));
        schedule.Legs.Should().OnlyContain(l => FlightRules.Check(l, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)).Errors.Count == 0);
    }

    [Fact]
    public async Task Read_Should_KeepUtcTimesAndRollTheArrivalPastMidnight_When_Needed()
    {
        var (utc, _) = await Read(File('U', Leg(std: "0900", sta: "1100")));
        utc.Legs.First(l => l.FlightKey == "RJ111-20261005-A").ScheduledUtc.Should().Be(new DateTime(2026, 10, 5, 11, 0, 0, DateTimeKind.Utc), "time mode U ignores variations");

        var (overnight, _) = await Read(File('L', Leg(std: "2300", departureVariation: "+0300", sta: "0130", arrivalVariation: "+0300")));
        overnight.Legs.First(l => l.FlightKey == "RJ111-20261005-A").ScheduledUtc.Should().Be(new DateTime(2026, 10, 5, 22, 30, 0, DateTimeKind.Utc),
            "01:30 local the next day is after the 20:00Z departure");
    }

    [Fact]
    public async Task Read_Should_HonourFortnightlyAndOpenPeriods_When_Given()
    {
        var (fortnightly, _) = await Read(File('U', Leg(from: Today, days: "1", frequency: "2")), horizon: 40);
        fortnightly.Legs.Select(l => DateOnly.FromDateTime(l.ScheduledUtc!.Value)).Should().Equal(Today, Today.AddDays(14), Today.AddDays(28));

        var (open, _) = await Read(File('U', Leg(from: Today, to: "00XXX00", days: "1")), horizon: 20);
        open.Legs.Should().HaveCount(3, "open to the horizon");
    }

    [Theory]
    [InlineData("from", "period of operation")]
    [InlineData("days", "Days of operation")]
    [InlineData("time", "HHMM")]
    [InlineData("variation", "variations")]
    [InlineData("frequency", "frequency rate")]
    [InlineData("backwards", "ends before")]
    public async Task Read_Should_ReportTheLineAndKeepTheOthers_When_ALegRecordIsMalformed(string what, string reason)
    {
        var bad = what switch
        {
            "from" => Leg(from: null).Remove(14, 7).Insert(14, "32FOO26"),
            "days" => Leg(days: "7654321"),
            "time" => Leg(std: "9900"),
            "variation" => Leg(departureVariation: "03:00"),
            "frequency" => Leg(frequency: "9"),
            _ => Leg(from: Today, to: Date(Today.AddDays(-3)))
        };

        var (schedule, error) = await Read(File('U', Leg(number: "200"), bad));

        error.Should().BeNull();
        schedule.Errors.Should().ContainSingle().Which.Should().Match<SsimLineError>(e => e.Line == 5 && e.Reason.Contains(reason, StringComparison.Ordinal));
        schedule.Legs.Should().OnlyContain(l => l.FlightKey.StartsWith("RJ200-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_Should_ReportLinesWithoutEchoingThem_When_TheyAreNotPrintableAscii()
    {
        var bytes = Encoding.ASCII.GetBytes(File('U', Leg())).ToList();
        var injected = Encoding.UTF8.GetBytes("3 RJ ' OR 1=1 -- é" + (char)7 + new string(' ', 10) + "\r\n");
        bytes.InsertRange(0, injected);
        bytes.InsertRange(0, Encoding.ASCII.GetBytes(new string('x', 400) + "\r\n"));

        var (schedule, _) = await Read(bytes.ToArray());

        schedule.ErrorCount.Should().Be(2);
        schedule.Errors.Select(e => e.Reason).Should().Contain(r => r.Contains("longer than 200", StringComparison.Ordinal))
            .And.Contain(r => r.Contains("printable ASCII", StringComparison.Ordinal));
        string.Join(' ', schedule.Errors.Select(e => e.Reason)).Should().NotContain("OR 1=1");
    }

    [Fact]
    public async Task Read_Should_RefuseTheFile_When_ItBreaksAWholeFileLimit()
    {
        (await Read(new byte[SsimReader.MaxBytes + 1])).Error.Should().Contain("20 MB");
        (await Read(string.Concat(Enumerable.Repeat("0\n", SsimReader.MaxLines + 1)))).Error.Should().Contain("200000 lines");
        (await Read("1HEADER\n2URJ\n5RJ\n")).Error.Should().Contain("no flight leg records");
        (await Read(File('U', Leg()), horizon: 0)).Error.Should().Contain("horizonDays");
        (await Read(File('U', Leg()), horizon: 201)).Error.Should().Contain("horizonDays");
        var many = File('U', [.. Enumerable.Range(1, 720).Select(n => Leg(number: n.ToString(CultureInfo.InvariantCulture), to: "00XXX00"))]);
        (await Read(many, horizon: 200)).Error.Should().Contain("more than 100000 legs");
    }

    [Theory]
    [InlineData("airline", "flightKey")]
    [InlineData("number", "number")]
    [InlineData("terminal", "terminal")]
    [InlineData("aircraft", "aircraftType")]
    public async Task Read_Should_ReportTheLineAndKeepItOutOfTheLegs_When_ALegBreaksTheFlightRules(string what, string reason)
    {
        var bad = what switch
        {
            "airline" => Leg(airline: "<s>"),
            "number" => Leg(number: "12AB"),
            "terminal" => Leg(terminal: "<b"),
            _ => Leg(aircraft: "3'0")
        };

        var (schedule, error) = await Read(File('U', Leg(number: "200"), bad));

        error.Should().BeNull();
        var lineError = schedule.Errors.Should().ContainSingle().Which;
        lineError.Line.Should().Be(5);
        lineError.Reason.Should().StartWith("The leg breaks Ariva's flight rules: ").And.Contain(reason);
        schedule.Legs.Should().OnlyContain(l => l.FlightKey.StartsWith("RJ200-", StringComparison.Ordinal), "the refused record never reaches the preview");
        string.Join(' ', schedule.Errors.Select(e => e.Reason)).Should().NotContain("<").And.NotContain("12AB").And.NotContain("3'0");
    }

    [Theory]
    [InlineData(200, "\r\n", false)]
    [InlineData(200, "\n", false)]
    [InlineData(200, "", false)]
    [InlineData(201, "\r\n", true)]
    [InlineData(201, "\n", true)]
    [InlineData(201, "", true)]
    [InlineData(202, "", true)]
    public async Task Read_Should_AllowLinesOfAtMost200Characters_When_EndedAnyWay(int length, string ending, bool refused)
    {
        var last = Leg(number: "300").PadRight(length, ' ')[..length];
        var text = File('U', Leg(number: "200")).Replace("5 RJ".PadRight(200) + "\r\n", string.Empty, StringComparison.Ordinal) + last + ending;

        var (schedule, error) = await Read(text);

        error.Should().BeNull();
        if (refused)
        {
            schedule.Errors.Should().ContainSingle().Which.Reason.Should().Contain("longer than 200");
            schedule.LegRecords.Should().Be(1);
        }
        else
        {
            schedule.ErrorCount.Should().Be(0);
            schedule.LegRecords.Should().Be(2);
        }
    }

    [Fact]
    public async Task Read_Should_CountAFinalLineWithoutANewline_When_TheLineLimitIsReached()
    {
        var atLimit = string.Concat(Enumerable.Repeat("0\n", SsimReader.MaxLines));
        (await Read(atLimit + "0")).Error.Should().Contain("200000 lines", "the final line without a newline is line 200001");
    }
}
