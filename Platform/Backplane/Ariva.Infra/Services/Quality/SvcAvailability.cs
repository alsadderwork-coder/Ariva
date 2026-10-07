using Ariva.Core.Availability;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Security;
using Ariva.Core.Services.Quality;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;

namespace Ariva.Infra.Services.Quality;

/// <summary>
/// Reads a site's availability ledger (ARV-118, <see cref="ISvcAvailability"/>, script 0042): the site first (outside the
/// caller's sites, or unknown, answers NotFound, CWE-204, CWE-863), then the range (<see cref="AvailabilityCriteria"/>,
/// at most 92 local days). One constant, parameterised query (CWE-89) counts the ledger's minutes per local date as the
/// ledger recorded it; weeks and the range are sums of the days, and the ratio is available operating minutes over
/// operating minutes (<see cref="AvailabilitySummary"/>).
/// </summary>
internal sealed class SvcAvailability(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, ReportReader reader)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAvailability
{
    public async Task<Result<AvailabilityViewModel>> GetAsync(string siteCode, AvailabilityCriteria criteria, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) || !await reader.SiteExistsAsync(siteCode, ct))
            return Result.Error<AvailabilityViewModel>(AvailabilityErrors.NotFound);
        criteria ??= new AvailabilityCriteria(null, null);
        var valid = await AvailabilityCriteria.Rules().ValidateAllAsync(criteria);
        if (valid.HasErrors || AvailabilityCriteria.Bounds(criteria) is not { } range)
            return Result.Error<AvailabilityViewModel>(valid.ErrorMessages.FirstOrDefault() ?? AvailabilityCriteria.InvalidRange);

        var zone = await reader.TimeZoneAsync(siteCode, ct);
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
            ["from"] = CalendarDates.Format(range.From),
            ["to"] = CalendarDates.Format(range.To),
            ["fromUtc"] = DateTime.SpecifyKind(range.From.AddDays(-2).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            ["toUtc"] = DateTime.SpecifyKind(range.To.AddDays(3).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
        }, ct);

        var counts = new Dictionary<DateOnly, AvailabilityCounts>();
        foreach (var row in rows)
        {
            if (CalendarDates.TryParse(row.Date, out var date))
                counts[date] = new AvailabilityCounts(row.Recorded, row.Operating, row.Available, row.Unavailable, row.Unobserved, row.Maintenance, row.Closed,
                    row.StaleZone, row.MissingMinute, row.StreamLag, row.NoPublishedZones);
        }

        var days = AvailabilitySummary.Days(range.From, range.To, counts);
        return new Result<AvailabilityViewModel>(new AvailabilityViewModel(siteCode, zone.Id, range.From, range.To, AvailabilitySummary.PilotTarget,
            AvailabilitySummary.Total(days), AvailabilitySummary.Weeks(days), days));
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
