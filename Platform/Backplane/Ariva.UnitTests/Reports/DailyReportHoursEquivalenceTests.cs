using System.Globalization;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Common;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Queueing;
using Ariva.Core.Reports;
using Ariva.Infra.Services.Reports;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Reports;

/// <summary>
/// ARV-234: the daily report builds its lanes from zone hours (<see cref="ReportHour"/>, read aggregated in the database or computed
/// by <see cref="DailyReports.HoursOf"/>), and the lanes are the ones the previous per-minute code built, kept word for word below,
/// on random minutes of every kind in UTC, Asia/Kolkata (half an hour off the hour), Europe/Berlin's 25 and 23-hour days and Dubai;
/// hours given or computed build the same report; a malformed histogram is left out whole while its minute counts; the default
/// bounds are the profile's 500 queue zones on a 25-hour day; and the report slots' lock class is no other lock's.
/// </summary>
public sealed class DailyReportHoursEquivalenceTests
{
    private static readonly DateTime Generated = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly ReportZone[] Zones =
        [new("DMO/A-CIT", "A-CIT", "CIT"), new("DMO/A-VIS", "A-VIS", "VIS"), new("DMO/B-EG", "B-EG", null)];

    [Theory]
    [InlineData("UTC", "2026-10-03", 9311)]
    [InlineData("Asia/Kolkata", "2026-10-03", 9312)]
    [InlineData("Europe/Berlin", "2026-10-25", 9313)]
    [InlineData("Europe/Berlin", "2026-03-29", 9314)]
    [InlineData("Asia/Dubai", "2026-10-03", 9315)]
    public void Build_Should_GiveThePreviousLanes_When_TheMinutesAreOfEveryKind(string timeZone, string day, int seed)
    {
        var zone = SiteTimeZones.TryFind(timeZone, out var found) ? found : throw new InvalidOperationException(timeZone);
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (from, to) = DailyReports.DayBounds(date, zone);
        var minutes = RandomMinutes(from, to, new Bogus.Randomizer(seed));

        var report = DailyReports.Build(new DailyReportInput("DMO", date, zone, Zones, minutes, [], [], Generated));

        var hours = new List<DateTime>();
        for (var t = from; t < to; t = t.AddHours(1))
            hours.Add(t);
        var byZone = minutes.Where(m => m.MinuteUtc >= from && m.MinuteUtc < to).ToLookup(m => m.ZoneKey, StringComparer.Ordinal);
        var previous = Zones.OrderBy(z => z.LaneCategory ?? "~", StringComparer.Ordinal).ThenBy(z => z.Name, StringComparer.Ordinal)
            .Select(z => PreviousLane(z, [.. byZone[z.ZoneKey]], hours, to, zone)).ToList();
        report.Lanes.Should().BeEquivalentTo(previous, o => o.WithStrictOrdering(), "the lanes from zone hours are the per-minute code's");
        report.Lanes.SelectMany(l => l.Hours).Should().Contain(h => h.Provisional).And.Contain(h => h.HistogramMissing).And.Contain(h => h.P90Minutes != null);
        report.Lanes[0].Hours.Should().HaveCount((int)Math.Ceiling((to - from).TotalHours));
    }

    [Fact]
    public void Build_Should_BuildTheSameReport_When_TheHoursAreGivenOrComputed()
    {
        var zone = SiteTimeZones.TryFind("Europe/Berlin", out var berlin) ? berlin : throw new InvalidOperationException();
        var date = new DateOnly(2026, 10, 25);
        var (from, to) = DailyReports.DayBounds(date, zone);
        var minutes = RandomMinutes(from, to, new Bogus.Randomizer(9316));

        var computed = DailyReports.Build(new DailyReportInput("DMO", date, zone, Zones, minutes, [], [], Generated));
        var given = DailyReports.Build(new DailyReportInput("DMO", date, zone, Zones, [], [], [], Generated, DailyReports.HoursOf(minutes, from, to)));

        given.Should().BeEquivalentTo(computed, o => o.WithStrictOrdering());
    }

    [Fact]
    public void HoursOf_Should_LeaveAMalformedHistogramOutWhole_When_ItsMinuteCounts()
    {
        var from = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        ReportMinute[] minutes =
        [
            new("DMO/A-CIT", from.AddMinutes(10), true, 4, 3, 7, [2, 4], [1, 2]),
            // A bucket past the last: the minute's histogram goes whole, its counts and waits stay.
            new("DMO/A-CIT", from.AddMinutes(11), true, 5, 2, 9, [6, WaitStatistics.Buckets], [1, 1]),
            // A negative count: likewise.
            new("DMO/A-CIT", from.AddMinutes(12), true, 1, 1, null, [8], [-1]),
            // Lists of unequal length: the pairs up to the shorter one.
            new("DMO/A-CIT", from.AddMinutes(13), false, 2, 2, 3, [2, 10, 12], [5])
        ];

        var hour = DailyReports.HoursOf(minutes, from, from.AddDays(1)).Should().ContainSingle().Subject;

        hour.Should().BeEquivalentTo(new ReportHour("DMO/A-CIT", 0, 12, 8, 9, true, false, [(2, 6L), (4, 2L)]), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Limits_Should_HoldTheProfilesQueueZonesOnALongDay_When_Default()
    {
        var limits = DailyReportLimits.Default;

        DailyReportLimits.MaxQueueZones.Should().Be(ZoneProfile.MaxZones).And.Be(500);
        limits.MaxZoneHours.Should().Be(12_500, "500 queue zones on a 25-hour day");
        DailyReportLimits.ZoneHourBuckets.Should().BeGreaterThan(241, "above the buckets a wait censored at the default 120 minutes fills");
        limits.MaxHistogramPairs.Should().Be(12_500L * DailyReportLimits.ZoneHourBuckets);
        limits.DatabaseSlots.Should().Be(2, "two reports at once, as the reports policy lets through");
    }

    [Fact]
    public void SlotLockClass_Should_BeNoOtherLocksClass_When_AnyAdvisoryLockIsTaken()
    {
        var infra = Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Infra");
        var classes = Directory.EnumerateFiles(infra, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"pg_(?:try_)?advisory_(?:xact_)?lock\((\d+)\s*,", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
            .ToList();

        classes.Should().NotBeEmpty("the two-key locks Ariva takes name their class first");
        classes.Should().NotContain(ReportReader.SlotLockClass, "the report slots' class is theirs alone");
        ReportReader.HoursSql.Should().Contain("pg_try_advisory_xact_lock(:lockClass, s)");
    }

    /// <summary>
    /// Minutes of three queue zones around the day (the minute either side of it included), of every kind: final or not, no
    /// histogram, an empty one, valid ones with repeated, first and last buckets, a bucket out of range, a negative count, and lists of
    /// unequal length or without counts.
    /// </summary>
    private static List<ReportMinute> RandomMinutes(DateTime from, DateTime to, Bogus.Randomizer random)
    {
        var last = WaitStatistics.Buckets - 1;
        var minutes = new List<ReportMinute>();
        foreach (var zone in Zones)
        {
            for (var minute = from.AddMinutes(-2); minute <= to.AddMinutes(1); minute = minute.AddMinutes(1))
            {
                var edge = minute == from || minute == to.AddMinutes(-1) || minute == from.AddMinutes(-1) || minute == to;
                if (!edge && !random.Bool(0.3f))
                    continue;
                var waits = random.Number(0, 3) == 0 ? 0 : random.Number(1, 25);
                var (buckets, counts) = random.Number(0, 9) switch
                {
                    0 => ((int[])null, (int[])null),
                    1 => ([], []),
                    2 => ([0, 0, last], [1, 2, 3]),
                    3 => ([4, WaitStatistics.Buckets], [1, 1]),
                    4 => ([3, 5], [2, -1]),
                    5 => ([2, 4, 6, 8], [1, 1]),
                    6 => ([9, 11], null),
                    _ => ([.. Enumerable.Range(0, random.Number(1, 8)).Select(_ => random.Number(0, 300))], [.. Enumerable.Range(0, 8).Select(_ => random.Number(0, 6))])
                };
                minutes.Add(new ReportMinute(zone.ZoneKey, minute, random.Number(0, 4) != 0, random.Number(0, 30), waits,
                    random.Number(0, 3) == 0 ? null : random.Number(0, 80), buckets, counts));
            }
        }

        return minutes;
    }

    #region The per-minute code before ARV-234, word for word

    private static LaneReport PreviousLane(ReportZone zone, List<ReportMinute> minutes, List<DateTime> hours, DateTime dayEnd, TimeZoneInfo tz)
    {
        var rows = new List<LaneHour>();
        var dayHistograms = new List<IReadOnlyList<(int Bucket, long Count)>>();
        foreach (var start in hours)
        {
            var end = start.AddHours(1) < dayEnd ? start.AddHours(1) : dayEnd;
            var inHour = minutes.Where(m => m.MinuteUtc >= start && m.MinuteUtc < end).ToList();
            var histograms = inHour.Where(m => m.Buckets is { Count: > 0 }).Select(Histogram).ToList();
            dayHistograms.AddRange(histograms);
            var merged = WaitStatistics.Merge([.. histograms]);
            var waits = inHour.Sum(m => m.Waits);
            rows.Add(new LaneHour(
                DailyReports.LocalText(start, tz)[^5..],
                DailyReports.LocalText(end, tz)[^5..],
                inHour.Sum(m => m.Entries),
                waits,
                Round(WaitStatistics.Percentile(merged, 0.5)),
                Round(WaitStatistics.Percentile(merged, 0.9)),
                inHour.Max(m => m.QueueLength),
                inHour.Any(m => !m.Final),
                inHour.Any(m => m.Waits > 0 && m.Buckets is not { Count: > 0 })));
        }

        var day = WaitStatistics.Merge([.. dayHistograms]);
        var peak = rows.Where(h => h.Waits >= DailyReports.MinWaitsForPeak && h.P90Minutes is not null)
            .OrderByDescending(h => h.P90Minutes).ThenBy(h => h.Start, StringComparer.Ordinal).FirstOrDefault();
        return new LaneReport(
            zone.Name,
            zone.LaneCategory,
            rows.Sum(h => h.Passengers),
            rows.Sum(h => h.Waits),
            Round(WaitStatistics.Percentile(day, 0.5)),
            Round(WaitStatistics.Percentile(day, 0.9)),
            rows.Max(h => h.MaxQueueLength),
            peak,
            rows.Any(h => h.Provisional),
            rows);
    }

    private static IReadOnlyList<(int Bucket, long Count)> Histogram(ReportMinute m)
    {
        var count = Math.Min(m.Buckets.Count, m.Counts?.Count ?? 0);
        var pairs = new List<(int Bucket, long Count)>(count);
        for (var i = 0; i < count; i++)
        {
            // A stored histogram that breaks the format is left out of the merge rather than failing the whole report.
            if (m.Buckets[i] is < 0 or >= WaitStatistics.Buckets || m.Counts[i] < 0)
                return [];
            pairs.Add((m.Buckets[i], m.Counts[i]));
        }

        pairs.Sort((a, b) => a.Bucket.CompareTo(b.Bucket));
        return pairs;
    }

    private static double? Round(double? minutes) => minutes is { } m ? Math.Round(m, 2) : null;

    #endregion
}
