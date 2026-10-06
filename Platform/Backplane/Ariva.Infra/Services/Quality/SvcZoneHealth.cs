using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Security;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Quality;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Quality;

/// <summary>
/// The stored health checks of a queue zone (ARV-114a, <see cref="ISvcZoneHealth"/>, script 0038): the site is checked
/// first (outside the caller's sites, or unknown, answers NotFound, CWE-204, CWE-863), then the criteria
/// (<see cref="ZoneHealthCriteria.Rules"/>), then the zone (a queue zone of a published or retired version of the site's
/// profile). The latest revision of each bin whose start lies in the range is read with constant, parameterised SQL
/// (CWE-89), at most <see cref="ISvcZoneHealth.MaxBins"/> rows (CWE-120).
/// </summary>
internal sealed class SvcZoneHealth(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcZoneHealth
{
    public async Task<Result<ZoneHealthViewModel>> SearchAsync(string siteCode, ZoneHealthCriteria criteria, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            (await ExecuteSqlAsync<TextRow>("""SELECT code AS "Value" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count == 0)
            return Result.Error<ZoneHealthViewModel>(ZoneHealthErrors.NotFound);
        criteria ??= new ZoneHealthCriteria();
        var valid = await ZoneHealthCriteria.Rules().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<ZoneHealthViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ZoneHealthCriteria.InvalidRange);

        var zone = criteria.Zone;
        var known = await ExecuteSqlAsync<TextRow>("""
            SELECT z.name AS "Value" FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND z.name = :zone AND z.kind = 'Queue' AND p.status IN ('Published', 'Retired') LIMIT 1
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zone"] = zone }, ct);
        if (known.Count == 0)
            return Result.Error<ZoneHealthViewModel>(ZoneHealthErrors.NotFound);

        var (from, to) = (criteria.FromDate!.Value, criteria.ToDate!.Value);
        var rows = await ExecuteSqlAsync<HealthRow>("""
            SELECT DISTINCT ON (start_utc) start_utc AS "StartUtc", length_minutes AS "LengthMinutes", revision AS "Revision", status AS "Status",
                   profile_version AS "ZoneProfileVersion", entries AS "Entries", exits AS "Exits", occupancy_start AS "OccupancyStart",
                   occupancy_end AS "OccupancyEnd", conservation_residual AS "ConservationResidual", tracks_entered AS "TracksEntered",
                   tracks_exited AS "TracksExited", tracks_abandoned AS "TracksAbandoned", tracks_fragmented AS "TracksFragmented",
                   tracks_censored AS "TracksCensored", tracks_rejected AS "TracksRejected", tracks_open AS "TracksOpen",
                   track_completion_rate AS "TrackCompletionRate", occupancy_minutes AS "OccupancyMinutes", capacity_minutes AS "CapacityMinutes",
                   minutes_outside_capacity AS "MinutesOutsideCapacity"
            FROM zone_health_bin
            WHERE zone_key = :key AND start_utc >= :from AND start_utc < :to
            ORDER BY start_utc, revision DESC
            LIMIT :limit
            """, new Dictionary<string, object>
        {
            ["key"] = ZoneKeys.For(siteCode, zone),
            ["from"] = from,
            ["to"] = to,
            ["limit"] = ISvcZoneHealth.MaxBins + 1
        }, ct);

        var truncated = rows.Count > ISvcZoneHealth.MaxBins;
        var bins = rows.Take(ISvcZoneHealth.MaxBins).Select(r => new ZoneHealthBinViewModel(Utc(r.StartUtc), r.LengthMinutes, r.Revision, r.Status,
            r.ZoneProfileVersion, r.Entries, r.Exits, r.OccupancyStart, r.OccupancyEnd, r.ConservationResidual, r.TracksEntered, r.TracksExited,
            r.TracksAbandoned, r.TracksFragmented, r.TracksCensored, r.TracksRejected, r.TracksOpen, r.TrackCompletionRate, r.OccupancyMinutes,
            r.CapacityMinutes, r.MinutesOutsideCapacity)).ToList();
        return new Result<ZoneHealthViewModel>(new ZoneHealthViewModel(siteCode, zone, from, to, bins, truncated));
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private sealed class TextRow
    {
        public string Value { get; set; }
    }

    private sealed class HealthRow
    {
        public DateTime StartUtc { get; set; }
        public int LengthMinutes { get; set; }
        public int Revision { get; set; }
        public string Status { get; set; }
        public int ZoneProfileVersion { get; set; }
        public long Entries { get; set; }
        public long Exits { get; set; }
        public int? OccupancyStart { get; set; }
        public int? OccupancyEnd { get; set; }
        public long? ConservationResidual { get; set; }
        public long TracksEntered { get; set; }
        public long TracksExited { get; set; }
        public long TracksAbandoned { get; set; }
        public long TracksFragmented { get; set; }
        public long TracksCensored { get; set; }
        public long TracksRejected { get; set; }
        public long TracksOpen { get; set; }
        public double? TrackCompletionRate { get; set; }
        public int OccupancyMinutes { get; set; }
        public int CapacityMinutes { get; set; }
        public int MinutesOutsideCapacity { get; set; }
    }
}
