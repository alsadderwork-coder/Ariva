using System.Globalization;
using Ariva.Core;
using Ariva.Core.Domain.Common;
using Ariva.Core.Queueing;
using Ariva.Core.Reports;
using Ariva.Core.Services;
using Ariva.Infra.Services.Reports;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Reports;

/// <summary>
/// ARV-234 against PostgreSQL 17 with TimescaleDB: the daily report's hours read in the database (<see cref="ReportReader.HoursSql"/>)
/// equals <see cref="DailyReports.HoursOf"/> over the same minutes read one by one as the report read them before (their lists as
/// text, so NULL elements drop out), on random minutes of every kind: final, provisional and of no status; no histogram, an empty
/// one, valid ones with repeated, first and last buckets, a bucket out of range, a negative count, NULL elements in either list and
/// lists of unequal length; the day's first and last minute in, the minute before and the minute it ends out; beside another site
/// whose code starts with this one's. In UTC, Asia/Kolkata (half an hour off the hour) and Europe/Berlin's 25-hour day. The pairs
/// the reader counts equal theirs, and the report built from the hours equals the one built from the minutes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DailyReportHoursTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, new Dictionary<string, string>(), TestDatabase.ReportBounds);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("HRU", "HRX", "UTC", "2026-09-30", 9301)]
    [InlineData("HRK", "HRY", "Asia/Kolkata", "2026-09-30", 9302)]
    [InlineData("HRB", "HRZ", "Europe/Berlin", "2026-10-25", 9303)]
    public async Task ReadHours_Should_EqualTheHoursOfTheMinutes_When_TheMinutesAreOfEveryKind(string site, string iata, string timeZone, string day, int seed)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (from, to) = DailyReports.DayBounds(date, SiteTimeZones.TryFind(timeZone, out var zone) ? zone : throw new InvalidOperationException(timeZone));
        await Host.CreateUserAsync("it.hours." + site.ToLowerInvariant(), roles: [RoleCodes.SystemAdministrator], allSites: true);
        await SeedAsync(site, iata, timeZone, from, to, new Bogus.Randomizer(seed));

        var read = await Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()).ReadHoursAsync(site, from, to, Ct));
        var minutes = await MinutesAsync(site, from, to);
        var expected = DailyReports.HoursOf(minutes, from, to);

        read.Slotted.Should().BeTrue();
        minutes.Should().HaveCountGreaterThan(500);
        Order(read.Hours).Should().BeEquivalentTo(Order(expected), o => o.WithStrictOrdering(), "the database's hours are the minutes' hours");
        read.Pairs.Should().Be(expected.Sum(h => h.Histogram.Count), "the pairs counted are the merged pairs");
        expected.Should().Contain(h => h.Hour == 0).And.Contain(h => h.Hour == (int)(to - from).TotalHours - 1, "the day's first and last hour are read");

        var fromHours = await Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()).ReadAsync(site, date, [RoleCodes.SystemAdministrator], Now, Ct));
        fromHours.HasErrors.Should().BeFalse(string.Join(" ", fromHours.ErrorMessages ?? []));
        var fromMinutes = DailyReports.Build(new DailyReportInput(site, date, zone,
            [.. minutes.Select(m => m.ZoneKey).Distinct(StringComparer.Ordinal).Select(k => new ReportZone(k, k[(site.Length + 1)..], null))], minutes, [], [], Now));
        fromHours.Data.Should().BeEquivalentTo(fromMinutes, o => o.WithStrictOrdering(), "the report built from the hours is the one built from the minutes");
    }

    private static List<ReportHour> Order(IEnumerable<ReportHour> hours) =>
        [.. hours.OrderBy(h => h.ZoneKey, StringComparer.Ordinal).ThenBy(h => h.Hour)];

    /// <summary>The site's minutes as the report read them before ARV-234: one by one, each list through its text.</summary>
    private async Task<List<ReportMinute>> MinutesAsync(string site, DateTime from, DateTime to)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT zone_key, minute_utc, status, entries, waits, queue_length, array_to_string(wait_buckets, ','), array_to_string(wait_counts, ',')
            FROM queue_minute WHERE zone_key LIKE @prefix AND minute_utc >= @from AND minute_utc < @to ORDER BY zone_key, minute_utc
            """, connection);
        command.Parameters.AddWithValue("prefix", site + "/%");
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        var minutes = new List<ReportMinute>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            minutes.Add(new ReportMinute(reader.GetString(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), !reader.IsDBNull(2) && reader.GetString(2) == "Final",
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3), reader.IsDBNull(4) ? 0 : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Numbers(reader.IsDBNull(6) ? null : reader.GetString(6)), Numbers(reader.IsDBNull(7) ? null : reader.GetString(7))));
        }

        return minutes;
    }

    private static int[] Numbers(string csv) =>
        string.IsNullOrEmpty(csv) ? [] : [.. csv.Split(',').Select(n => int.Parse(n, NumberStyles.Integer, CultureInfo.InvariantCulture))];

    /// <summary>
    /// The site (its airport in <paramref name="timeZone"/>) with four queue zones of random minutes over the day and the minute on
    /// either side of it, and another site whose code starts with this one's, with minutes of its own the read must leave out.
    /// </summary>
    private async Task SeedAsync(string site, string iata, string timeZone, DateTime from, DateTime to, Bogus.Randomizer random)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using (var topology = new NpgsqlCommand("""
            WITH site AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, 'Hours'), (gen_random_uuid(), @site || 'X', 'Hours beside') RETURNING code),
            airport AS (INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, 'Hours airport', @tz) RETURNING id)
            INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', @site FROM airport a
            """, connection))
        {
            topology.Parameters.AddWithValue("site", site);
            topology.Parameters.AddWithValue("iata", iata);
            topology.Parameters.AddWithValue("tz", timeZone);
            await topology.ExecuteNonQueryAsync(Ct);
        }

        var last = WaitStatistics.Buckets - 1;
        await using var batch = new NpgsqlBatch(connection);
        foreach (var zoneKey in new[] { site + "/Q1", site + "/Q2", site + "/Q3", site + "/Q4", site + "X/Q1" })
        {
            for (var minute = from.AddMinutes(-1); minute <= to; minute = minute.AddMinutes(1))
            {
                // The day's edges always; inside it about one minute in four.
                var edge = minute == from.AddMinutes(-1) || minute == from || minute == to.AddMinutes(-1) || minute == to;
                if (!edge && !random.Bool(0.25f))
                    continue;
                var (buckets, counts) = random.Number(0, 11) switch
                {
                    0 => (null, null),
                    1 => (Array.Empty<int?>(), Array.Empty<int?>()),
                    2 => (new int?[] { 0, 0, last }, new int?[] { 1, 2, 3 }),
                    3 => (new int?[] { 4, WaitStatistics.Buckets, 6 }, new int?[] { 1, 1, 1 }),
                    4 => (new int?[] { -1, 4 }, new int?[] { 1, 1 }),
                    5 => (new int?[] { 3, 5 }, new int?[] { 2, -1 }),
                    6 => (new int?[] { 3, null, 7 }, new int?[] { 1, 2, 3 }),
                    7 => (new int?[] { 3, 7, 9 }, new int?[] { null, 2, 3 }),
                    8 => (new int?[] { 2, 4, 6, 8 }, new int?[] { 1, 1, null, null }),
                    9 => (new int?[] { null }, new int?[] { 5 }),
                    _ => Valid(random)
                };
                var command = new NpgsqlBatchCommand("""
                    INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
                    VALUES (@zone, @minute, 1, @status, @entries, 0, @waits, @queue, @buckets, @counts, now())
                    """);
                command.Parameters.AddWithValue("zone", zoneKey);
                command.Parameters.AddWithValue("minute", minute);
                command.Parameters.AddWithValue("status", random.Number(0, 2) switch { 0 => "Final", 1 => "Provisional", _ => DBNull.Value });
                command.Parameters.AddWithValue("entries", (long)random.Number(0, 30));
                command.Parameters.AddWithValue("waits", random.Number(0, 3) == 0 ? 0L : random.Number(1, 30));
                command.Parameters.AddWithValue("queue", random.Number(0, 4) == 0 ? DBNull.Value : random.Number(0, 80));
                command.Parameters.Add(new NpgsqlParameter("buckets", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)buckets ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("counts", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)counts ?? DBNull.Value });
                batch.BatchCommands.Add(command);
            }
        }

        await batch.ExecuteNonQueryAsync(Ct);
    }

    // A valid histogram of one to seven pairs (the table holds both lists or neither, of one length), buckets repeated at times.
    private static (int?[] Buckets, int?[] Counts) Valid(Bogus.Randomizer random)
    {
        var length = random.Number(1, 7);
        return ([.. Enumerable.Range(0, length).Select(_ => (int?)random.Number(0, 300))], [.. Enumerable.Range(0, length).Select(_ => (int?)random.Number(0, 5))]);
    }
}
