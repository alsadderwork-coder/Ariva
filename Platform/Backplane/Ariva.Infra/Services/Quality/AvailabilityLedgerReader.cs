using Ariva.Core.Availability;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Quality;

/// <summary>
/// The availability ledger's minutes per local date (ARV-118, script 0042), one constant, parameterised query (CWE-89), shared
/// by the availability read (<see cref="SvcAvailability"/>) and the weekly summary (ARV-119), so both count the same way. At
/// most one row per local date of the range. The caller decides who may read the site; this class only reads.
/// </summary>
internal sealed class AvailabilityLedgerReader(IUnitOfWork unitOfWork) : SvcDb(unitOfWork)
{
    /// <summary>The counts per local date of [from, to] as the ledger recorded them; a date without rows is absent.</summary>
    public async Task<IReadOnlyDictionary<DateOnly, AvailabilityCounts>> CountsByDayAsync(string siteCode, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // The UTC bounds only let the hypertable skip chunks; the local dates decide. Two days either side cover any offset.
        var rows = await ExecuteSqlAsync<DayRow>("""
            SELECT to_char(local_date, 'YYYY-MM-DD') AS "Date",
                   CAST(count(*) AS integer) AS "Recorded",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating') AS integer) AS "Operating",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND state = 'Available') AS integer) AS "Available",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND state = 'Unavailable') AS integer) AS "Unavailable",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND state = 'Unobserved') AS integer) AS "Unobserved",
                   CAST(count(*) FILTER (WHERE calendar = 'Maintenance') AS integer) AS "Maintenance",
                   CAST(count(*) FILTER (WHERE calendar = 'Closed') AS integer) AS "Closed",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'StaleZone' = ANY (reasons)) AS integer) AS "StaleZone",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'MissingMinute' = ANY (reasons)) AS integer) AS "MissingMinute",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'StreamLag' = ANY (reasons)) AS integer) AS "StreamLag",
                   CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'NoPublishedZones' = ANY (reasons)) AS integer) AS "NoPublishedZones"
            FROM availability_minute
            WHERE site_code = :site AND local_date >= CAST(:from AS date) AND local_date <= CAST(:to AS date)
              AND minute_utc >= :fromUtc AND minute_utc < :toUtc
            GROUP BY local_date
            ORDER BY local_date
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode,
            ["from"] = CalendarDates.Format(from),
            ["to"] = CalendarDates.Format(to),
            ["fromUtc"] = DateTime.SpecifyKind(from.AddDays(-2).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            ["toUtc"] = DateTime.SpecifyKind(to.AddDays(3).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
        }, ct);

        var counts = new Dictionary<DateOnly, AvailabilityCounts>();
        foreach (var row in rows)
        {
            if (CalendarDates.TryParse(row.Date, out var date))
                counts[date] = new AvailabilityCounts(row.Recorded, row.Operating, row.Available, row.Unavailable, row.Unobserved, row.Maintenance, row.Closed,
                    row.StaleZone, row.MissingMinute, row.StreamLag, row.NoPublishedZones);
        }

        return counts;
    }

    private sealed class DayRow
    {
        public string Date { get; set; }
        public int Recorded { get; set; }
        public int Operating { get; set; }
        public int Available { get; set; }
        public int Unavailable { get; set; }
        public int Unobserved { get; set; }
        public int Maintenance { get; set; }
        public int Closed { get; set; }
        public int StaleZone { get; set; }
        public int MissingMinute { get; set; }
        public int StreamLag { get; set; }
        public int NoPublishedZones { get; set; }
    }
}
