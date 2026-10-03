using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Flights;

namespace Ariva.Infra.Flights.Ssim;

/// <summary>A problem on one line of an SSIM file: its number and Ariva's reason, never the line's text.</summary>
public sealed record SsimLineError(int Line, string Reason);

/// <summary>
/// What an SSIM file holds for one site (ARV-046): its SHA-256, how many lines and flight leg records it had, the legs
/// that arrive at or leave the site's airports expanded to one <see cref="FlightLegData"/> per operating date within the
/// horizon, and the lines Ariva could not read (the first 100, with the total).
/// </summary>
public sealed record SsimSchedule(
    string Sha256,
    int Lines,
    int LegRecords,
    int LegRecordsOfSite,
    IReadOnlyList<FlightLegData> Legs,
    IReadOnlyList<SsimLineError> Errors,
    int ErrorCount,
    DateOnly From,
    DateOnly To);

/// <summary>
/// Reads an IATA SSIM chapter 7 file (ARV-046, CWE-120, CWE-501) as a stream: at most 20 MB, 200,000 lines of at most
/// 200 printable ASCII characters each, and 100,000 expanded legs; anything else is a line error or refuses the file.
/// Record 2 gives the time mode (U for UTC, L for local with the record's UTC variations) of the carrier's legs; record 3
/// is a flight leg: suffix (2), airline (3-5), flight number (6-9), service type (14), period of operation (15-21,
/// 22-28, ddMMMyy, 00XXX00 for open), days of operation (29-35), frequency rate (36, 2 for every other week),
/// departure station (37-39), passenger departure time (40-43), departure variation (48-52), departure terminal
/// (53-54), arrival station (55-57), passenger arrival time (62-65), arrival variation (66-70), arrival terminal
/// (71-72) and aircraft type (73-75). Records 1, 4, 5 and zero-filled lines are skipped. The arrival date is the first
/// one after the departure (the date variation field is not read). Only legs touching the site are expanded, from
/// yesterday to the horizon, and every expanded leg must pass <see cref="FlightRules.Check(FlightLegData, DateTime)"/>
/// (the intake's own rules), so a record the import would refuse is a line error here and never reaches the preview.
/// </summary>
public static class SsimReader
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxLines = 200_000;
    public const int MaxLineLength = 200;
    public const int MaxLegs = 100_000;
    public const int MaxHorizonDays = 200;
    private const int MaxErrorsKept = 100;

    /// <summary>The schedule, or why the file as a whole cannot be read.</summary>
    public static async Task<(SsimSchedule Schedule, string Error)> ReadAsync(Stream input, IReadOnlySet<string> siteAirports, DateTime nowUtc, int horizonDays,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(siteAirports);
        if (horizonDays is < 1 or > MaxHorizonDays)
            return (null, $"horizonDays is 1 to {MaxHorizonDays}.");
        var today = DateOnly.FromDateTime(nowUtc);
        var from = today.AddDays(-1);
        var to = today.AddDays(horizonDays);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var legs = new List<FlightLegData>();
        var errors = new List<SsimLineError>();
        var errorCount = 0;
        var (lines, legRecords, ofSite) = (0, 0, 0);
        var timeMode = 'U';
        long bytes = 0;
        var buffer = new byte[64 * 1024];
        var line = new StringBuilder(MaxLineLength + 2);
        var lineTooLong = false;

        void Fail(string reason)
        {
            errorCount++;
            if (errors.Count < MaxErrorsKept)
                errors.Add(new SsimLineError(lines, reason));
        }

        string EndLine()
        {
            lines++;
            if (lineTooLong)
            {
                line.Clear();
                Fail($"The line is longer than {MaxLineLength} characters.");
                return null;
            }

            var text = line.ToString().TrimEnd('\r');
            line.Clear();
            if (text.Length > MaxLineLength)
            {
                Fail($"The line is longer than {MaxLineLength} characters.");
                return null;
            }

            return text;
        }

        string Process(string text)
        {
            if (text is null || text.Length == 0 || text.All(c => c is '0' or ' '))
                return null;
            if (text.Any(c => c is < ' ' or > '~'))
            {
                Fail("The line has characters other than printable ASCII.");
                return null;
            }

            switch (text[0])
            {
                case '2':
                    timeMode = text.Length > 1 ? text[1] : ' ';
                    if (timeMode is not ('U' or 'L'))
                        Fail("Record 2 has a time mode other than U or L.");
                    return null;
                case '3':
                    legRecords++;
                    var (expanded, reason, touches) = Expand(text.PadRight(MaxLineLength), timeMode, siteAirports, from, to, nowUtc);
                    if (touches)
                        ofSite++;
                    if (reason is not null)
                        Fail(reason);
                    else
                        legs.AddRange(expanded);
                    return legs.Count > MaxLegs ? $"The file expands to more than {MaxLegs} legs for this site; shorten the horizon." : null;
                default:
                    return null;
            }
        }

        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            bytes += read;
            if (bytes > MaxBytes)
                return (null, "An SSIM file is at most 20 MB.");
            hash.AppendData(buffer, 0, read);
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\n')
                {
                    if (lines >= MaxLines)
                        return (null, $"An SSIM file has at most {MaxLines} lines.");
                    if (Process(EndLine()) is { } stop)
                        return (null, stop);
                    lineTooLong = false;
                }
                else if (line.Length > MaxLineLength)
                {
                    // 200 characters and a CR fit; anything more makes the line too long.
                    lineTooLong = true;
                }
                else
                {
                    line.Append(b < 0x80 ? (char)b : (char)1);
                }
            }
        }

        if (line.Length > 0 || lineTooLong)
        {
            if (lines >= MaxLines)
                return (null, $"An SSIM file has at most {MaxLines} lines.");
            if (Process(EndLine()) is { } stop)
                return (null, stop);
        }

        if (legRecords == 0)
            return (null, "The file has no flight leg records (type 3); it is not an SSIM chapter 7 file.");
        return (new SsimSchedule(Convert.ToHexStringLower(hash.GetHashAndReset()), lines, legRecords, ofSite, legs, errors, errorCount, from, to), null);
    }

    private static (List<FlightLegData> Legs, string Error, bool TouchesSite) Expand(string r, char timeMode, IReadOnlySet<string> airports, DateOnly from, DateOnly to,
        DateTime nowUtc)
    {
        string Field(int start, int end) => r[(start - 1)..end].Trim();

        var suffix = Field(2, 2);
        var airline = Field(3, 5);
        var number = Field(6, 9);
        var departure = Field(37, 39);
        var arrival = Field(55, 57);
        if (FlightKeys.SiteSide(departure, arrival, airports) is not { } arriving)
            return ([], null, false); // a leg of another airport is not an error
        if (!TryDate(Field(15, 21), out var first) || !(Field(22, 28) == "00XXX00" ? true : TryDate(Field(22, 28), out _)))
            return ([], "The period of operation is not two dates in ddMMMyy form.", true);
        var last = Field(22, 28) == "00XXX00" ? to : ParseDate(Field(22, 28));
        if (last < first)
            return ([], "The period of operation ends before it starts.", true);
        var days = r[28..35];
        if (days.Where((c, i) => c != ' ' && c != (char)('1' + i)).Any())
            return ([], "Days of operation are 1 to 7 in their positions, or spaces.", true);
        var fortnightly = r[35] == '2';
        if (r[35] is not (' ' or '2'))
            return ([], "The frequency rate is a space or 2.", true);
        if (!TryTime(Field(40, 43), out var std) || !TryTime(Field(62, 65), out var sta))
            return ([], "Passenger departure and arrival times are HHMM.", true);
        if (!TryVariation(Field(48, 52), out var departureVariation) || !TryVariation(Field(66, 70), out var arrivalVariation))
            return ([], "UTC variations are +HHMM or -HHMM.", true);
        if (timeMode == 'U')
            (departureVariation, arrivalVariation) = (TimeSpan.Zero, TimeSpan.Zero);

        var legs = new List<FlightLegData>();
        var start = first > from ? first : from;
        var end = last < to ? last : to;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var dayIndex = ((int)date.DayOfWeek + 6) % 7; // Monday 0
            if (days[dayIndex] == ' ')
                continue;
            if (fortnightly && (date.DayNumber - first.DayNumber) / 7 % 2 == 1)
                continue;
            var leaves = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) + std - departureVariation, DateTimeKind.Utc);
            var lands = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) + sta - arrivalVariation, DateTimeKind.Utc);
            for (var i = 0; i < 3 && lands <= leaves; i++)
                lands = lands.AddDays(1);
            var leg = new FlightLegData(
                FlightKeys.Of(airline, number, suffix, date, arriving),
                airline,
                FlightKeys.Number(number),
                suffix.Length == 0 ? null : suffix,
                arriving ? "Arrival" : "Departure",
                arriving ? lands : leaves,
                Origin: departure,
                Destination: arrival,
                Terminal: arriving ? NullIfEmpty(Field(71, 72)) : NullIfEmpty(Field(53, 54)),
                AircraftType: NullIfEmpty(Field(73, 75)));
            // Ariva's reason only (FlightRules never quotes the value), so nothing from the file is echoed.
            if (FlightRules.Check(leg, nowUtc).Errors is [var refused, ..])
                return ([], $"The leg breaks Ariva's flight rules: {refused}", true);
            legs.Add(leg);
        }

        return (legs, null, true);
    }

    private static string NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "ddMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static DateOnly ParseDate(string text) => DateOnly.ParseExact(text, "ddMMMyy", CultureInfo.InvariantCulture);

    private static bool TryTime(string text, out TimeSpan time)
    {
        time = default;
        if (text.Length != 4 || !text.All(char.IsAsciiDigit))
            return false;
        var (hours, minutes) = (int.Parse(text[..2], CultureInfo.InvariantCulture), int.Parse(text[2..], CultureInfo.InvariantCulture));
        if (hours > 24 || minutes > 59 || (hours == 24 && minutes > 0))
            return false;
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private static bool TryVariation(string text, out TimeSpan variation)
    {
        variation = default;
        if (text.Length != 5 || text[0] is not ('+' or '-') || !TryTime(text[1..], out var magnitude) || magnitude > TimeSpan.FromHours(14))
            return false;
        variation = text[0] == '-' ? -magnitude : magnitude;
        return true;
    }
}
