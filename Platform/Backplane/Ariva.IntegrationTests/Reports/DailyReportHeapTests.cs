using System.Diagnostics;
using Ariva.Core;
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
/// ARV-234 (CWE-400, CWE-120, ASVS 5.0 V15.2.2) against PostgreSQL 17 with TimescaleDB: the daily report at its own bounds, the 500
/// queue zones a profile can hold on a 25-hour day (12,500 zone hours), at six and at twenty pairs a minute and at 98 % of the
/// histogram pairs bound, is built within the reports' request timeout and holds under 100 MB of live managed heap, its statement writing no
/// temporary file (and so running under the runtime login's default cap on them, ARV-290); its time and peak written to the test output for <c>docs/security/costly-operations.md</c> section 4. The
/// collection runs alone, after every other, on a container of its own (<see cref="PostgresAloneCollection"/>): the heap sampled
/// is the test process's, which tests running beside it would add to, and the seeds (750,000 minutes a row) stay off the suite's
/// shared database. Each row first drops the minutes the rows before it left, so the disk holds one row's at a time.
/// </summary>
[Collection(PostgresAloneCollection.Name)]
public sealed class DailyReportHeapTests(PostgresFixture fixture, ITestOutputHelper output) : IAsyncDisposable
{
    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, new Dictionary<string, string>(), TestDatabase.ReportHeap);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeZoneInfo Berlin => Ariva.Core.Domain.Common.SiteTimeZones.TryFind("Europe/Berlin", out var berlin) ? berlin : TimeZoneInfo.Utc;

    private Task<Fluentx.Result<DailyReport>> ReadAsync(string site, DateOnly day, DateTime now, DailyReportLimits limits) =>
        Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()) { Limits = limits }
            .ReadAsync(site, day, [RoleCodes.SystemAdministrator], now, Ct));

    [Theory]
    [InlineData("BIG", "BGX", 6, 3)]
    [InlineData("BIGD", "BGY", 20, 3)]
    [InlineData("BIGP", "BGZ", 20, 12)]
    public async Task Read_Should_BuildTheReportWithinItsTimeoutAndHeapBudget_When_TheSiteHasTheMostZoneHoursAReportReads(string site, string iata, int pairs, int spread)
    {
        // The report's bound: its most queue zones on Europe/Berlin's 25-hour day, every zone hour with a minute's worth of final
        // minutes and histograms of six pairs a minute (a busy hall's) or twenty (denser than a wait censored at 120 minutes gives),
        // their buckets b * spread + (minute % (spread + 4)) for b from 1: a spread of 3 merges 22 or 64 buckets a zone hour (275,000
        // or 800,000 pairs in the day), a spread of 12 with twenty pairs merges 244 (3,050,000 pairs, 98 % of MaxHistogramPairs).
        const int zones = DailyReportLimits.MaxQueueZones;
        var date = new DateOnly(2026, 10, 25);
        var now = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        await Host.CreateUserAsync("it.heap." + site.ToLowerInvariant(), roles: [RoleCodes.SystemAdministrator], allSites: true);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            // The rows before this one leave their minutes; their chunks go first, so the disk holds one row's minutes at a time.
            await using (var clear = new NpgsqlCommand("SELECT count(*) FROM drop_chunks('queue_minute', older_than => timestamptz '2100-01-01T00:00:00Z')", connection))
                await clear.ExecuteScalarAsync(Ct);
            await using var seed = new NpgsqlCommand("""
                WITH site AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, 'Largest') RETURNING code),
                airport AS (INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, 'Largest airport', 'Europe/Berlin') RETURNING id),
                terminal AS (INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', s.code FROM airport a, site s RETURNING id),
                minutes AS (
                    INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
                    SELECT @site || '/Q-' || lpad(z::text, 3, '0'), timestamptz '2026-10-24T22:00:00Z' + m * interval '1 minute', 1, 'Final', 12, 11, 11, 40,
                           (SELECT array_agg(b * @spread + (m % (@spread + 4))) FROM generate_series(1, @pairs) b), (SELECT array_agg(1 + b % 3) FROM generate_series(1, @pairs) b), now()
                    FROM generate_series(1, 500) z CROSS JOIN generate_series(0, 1499) m
                    RETURNING 1)
                SELECT count(*) FROM minutes
                """, connection) { CommandTimeout = 600 };
            seed.Parameters.AddWithValue("site", site);
            seed.Parameters.AddWithValue("iata", iata);
            seed.Parameters.AddWithValue("pairs", pairs);
            seed.Parameters.AddWithValue("spread", spread);
            ((long)await seed.ExecuteScalarAsync(Ct)).Should().Be(zones * 1500L);
            await using var analyze = new NpgsqlCommand("ANALYZE queue_minute", connection) { CommandTimeout = 600 };
            await analyze.ExecuteNonQueryAsync(Ct);
        }

        // Twice: a timed run left to the collector (its time and the bytes it allocates), then a run that forces a full collection
        // at each sample, so its peak is the live heap the report holds (alone, a run the collector is left to may never collect, and
        // its sampled heap is then every byte it allocated).
        var timed = await MeasureAsync(site, date, now, forceCollections: false);
        var live = await MeasureAsync(site, date, now, forceCollections: true);
        foreach (var (what, run) in new[] { ("timed", timed), ("live heap", live) })
        {
            run.Report.HasErrors.Should().BeFalse(string.Join(" ", run.Report.ErrorMessages ?? []));
            output.WriteLine($"Daily report at its bound ({zones} queue zones, a 25-hour day, {zones * 1500:N0} minutes, {pairs} pairs a minute, spread {spread}), {what} run: " +
                             $"{run.Elapsed.TotalSeconds:F1} s, heap peak {run.Peak / 1048576.0:F0} MB above the baseline ({run.Samples} samples), {run.Allocated / 1048576.0:F0} MB allocated in all.");
        }

        // A fixed point beside the samples: the hours read holds every zone hour's merged histogram at once, the report's largest
        // structure, and a full collection with it held measures it whatever a sampler catches (after the rest of the suite a full
        // collection takes seconds, and the read's own work in the process is short beside the statement's time in the database).
        var held = await HoursHeldAsync(site, date);
        output.WriteLine($"Hours read at the bound ({pairs} pairs a minute, spread {spread}): {held / 1048576.0:F0} MB of live heap held once read, after a full collection.");

        var report = live.Report;

        // The statement's plan at the bound: it writes no temporary file (each zone hour merged in memory, nothing buffers every row),
        // and runs under the runtime login's default cap on temporary files (ARV-290, Database:TempFileLimitMegabytes), set here.
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using (var cap = new NpgsqlCommand("SELECT set_config('temp_file_limit', @limit, false)", connection))
            {
                cap.Parameters.AddWithValue("limit", new Ariva.Infra.Settings.DatabaseSettings().TempFileLimitMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "MB");
                (await cap.ExecuteScalarAsync(Ct)).Should().Be("1GB");
            }

            await using var explain = new NpgsqlCommand(ExplainSql, connection) { CommandTimeout = 120 };
            var (from, to) = DailyReports.DayBounds(date, Berlin);
            explain.Parameters.AddWithValue("first", 1);
            explain.Parameters.AddWithValue("slots", DailyReportLimits.Default.DatabaseSlots);
            explain.Parameters.AddWithValue("lockClass", ReportReader.SlotLockClass);
            explain.Parameters.AddWithValue("prefix", site + "/%");
            explain.Parameters.AddWithValue("from", from);
            explain.Parameters.AddWithValue("to", to);
            explain.Parameters.AddWithValue("buckets", Ariva.Core.Queueing.WaitStatistics.Buckets);
            explain.Parameters.AddWithValue("cap", DailyReportLimits.Default.MaxZoneHours + 1);
            var plan = (string)await explain.ExecuteScalarAsync(Ct);
            var written = System.Text.RegularExpressions.Regex.Matches(plan, "\"Temp Written Blocks\": (\\d+)").Sum(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            var time = System.Text.RegularExpressions.Regex.Match(plan, "\"Execution Time\": ([0-9.]+)").Groups[1].Value;
            output.WriteLine($"Plan at the bound ({pairs} pairs a minute, spread {spread}): {time} ms in the database, {written} temporary blocks written.");
            (to - from).Should().Be(TimeSpan.FromHours(25));
            written.Should().Be(0, "the hours read writes no temporary file at its bound");
        }

        report.Data.Lanes.Should().HaveCount(zones).And.OnlyContain(l => l.Hours.Count == 25 && l.Passengers == 12 * 1500, "every zone hour of the day is read");
        timed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "within the reports' request timeout (60 s)");
        held.Should().BePositive().And.BeLessThan(100L * 1048576, "the live heap the hours read holds at the bound, every merged histogram at once, its budget (costly-operations section 4)");
        live.Peak.Should().BePositive().And.BeLessThan(100L * 1048576, "the live heap sampled through the report's read at its bound, its budget (costly-operations section 4)");
        timed.Allocated.Should().BeLessThan(250L * 1048576, "a loose ceiling on what a report allocates (104 to 115 MB measured at the pairs bound)");

        // The last row's minutes go too, so a shared server keeps none of them after the run.
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await Host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using var clear = new NpgsqlCommand("SELECT count(*) FROM drop_chunks('queue_minute', older_than => timestamptz '2100-01-01T00:00:00Z')", connection);
            await clear.ExecuteScalarAsync(Ct);
        }
    }

    private sealed record Run(Fluentx.Result<DailyReport> Report, TimeSpan Elapsed, long Peak, long Allocated, int Samples);

    /// <summary>
    /// One report read as a request reads it, the managed heap sampled meanwhile: every 5 ms as it stands, or, with
    /// <paramref name="forceCollections"/>, after one blocking full collection at most every 50 ms (the live heap; the collections slow the run).
    /// </summary>
    private async Task<Run> MeasureAsync(string site, DateOnly date, DateTime now, bool forceCollections)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        var samples = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                // One blocking full collection a sample (GetTotalMemory(true) repeats them until the heap settles, seconds a sample in
                // a process that has run the rest of the suite).
                if (forceCollections)
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                var current = GC.GetTotalMemory(false);
                Interlocked.Increment(ref samples);
                if (current > Interlocked.Read(ref peak))
                    Interlocked.Exchange(ref peak, current);
                await Task.Delay(forceCollections ? 50 : 5, CancellationToken.None);
            }
        }, CancellationToken.None);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();

        var report = await ReadAsync(site, date, now, DailyReportLimits.Default);

        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        await sampling.CancelAsync();
        await sampler;
        if (forceCollections)
        {
            // One last sample with the finished report held, so the report's own heap is measured whatever the sampler's timing.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            peak = Math.Max(peak, GC.GetTotalMemory(false));
            samples++;
        }

        return new Run(report, clock.Elapsed, peak - baseline, allocated, samples);
    }

    /// <summary>
    /// The live heap the hours read holds once it returns (every zone hour's merged histogram at once), measured by a full
    /// collection before the read and one with its result held.
    /// </summary>
    private async Task<long> HoursHeldAsync(string site, DateOnly date)
    {
        var (from, to) = DailyReports.DayBounds(date, Berlin);
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var read = await Host.AsCallerAsync(null, s => new ReportReader(s.GetRequiredService<IUnitOfWork>()) { Limits = DailyReportLimits.Default }
            .ReadHoursAsync(site, from, to, Ct));
        var held = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        read.Slotted.Should().BeTrue();
        read.Hours.Should().HaveCount(DailyReportLimits.MaxQueueZones * 25).And.OnlyContain(h => h.Histogram.Count > 0, "every histogram is read below the pairs bound");
        return held;
    }

    private const string ExplainSql = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + ReportReader.HoursSql;
}
