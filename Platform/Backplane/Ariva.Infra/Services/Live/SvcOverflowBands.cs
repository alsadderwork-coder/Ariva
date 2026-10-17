using Ariva.Core.Domain.Entities;
using Ariva.Core.Security;
using Ariva.Core.Services.Live;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Live;

/// <summary>
/// The overflow bands of a site's queue zones (ARV-120, <see cref="ISvcOverflowBands"/>): the site is checked first (outside
/// the caller's sites, or unknown, answers NotFound, CWE-204, CWE-863), then the overflow bands of the site's published
/// profile are read with each band's latest minute in <c>overflow_minute</c> (ARV-115, script 0039) within the last
/// <see cref="ISvcOverflowBands.FreshMinutes"/> minutes, in one constant, parameterised statement (CWE-89): the published
/// bands bound the rows, each band reads at most one row by the table's key (zone key, band, minute), and the statement asks
/// for one band more than <see cref="ISvcOverflowBands.MaxBands"/> so that a profile beyond the bound is refused
/// (<see cref="OverflowBandErrors.TooMany"/>), never cut short (CWE-120). A band minute under a band name the published
/// profile no longer has is not read.
/// </summary>
internal sealed class SvcOverflowBands(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcOverflowBands
{
    public async Task<Result<OverflowBandsViewModel>> GetAsync(string siteCode, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            (await ExecuteSqlAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count == 0)
            return Result.Error<OverflowBandsViewModel>(TopologyErrors.NotFound);

        var now = UtcNow;
        var fresh = TimeSpan.FromMinutes(ISvcOverflowBands.FreshMinutes);
        // The zone key is <site>/<queue zone> (ZoneKeys.For), built here from the checked site code and the stored zone name.
        var rows = await ExecuteSqlAsync<BandRow>("""
            SELECT q.name AS "Zone", b.name AS "Band", r.minute_utc AS "MinuteUtc", r.max_occupancy AS "MaxOccupancy"
              FROM zone b
              JOIN zone_profile p ON p.id = b.profile_id
              JOIN zone q ON q.id = b.queue_zone_id AND q.profile_id = b.profile_id
              LEFT JOIN LATERAL (
                   SELECT m.minute_utc, m.max_occupancy
                     FROM overflow_minute m
                    WHERE m.zone_key = CAST(:site AS varchar) || '/' || q.name AND m.band_name = b.name
                      AND m.minute_utc >= :from AND m.minute_utc <= :to
                    ORDER BY m.minute_utc DESC
                    LIMIT 1) r ON true
             WHERE p.site_code = :site AND p.status = 'Published' AND b.kind = 'Overflow' AND q.kind = 'Queue'
             ORDER BY q.name, b.name
             LIMIT :limit
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode,
            ["from"] = now - fresh,
            ["to"] = now,
            ["limit"] = ISvcOverflowBands.MaxBands + 1
        }, ct);

        var bands = OverflowBandStates.Within(rows
            .Select(r => new OverflowBandReading(r.Zone, r.Band, r.MinuteUtc is { } m ? Utc(m) : null, r.MaxOccupancy))
            .ToList());
        if (bands is null)
            return Result.Error<OverflowBandsViewModel>(OverflowBandErrors.TooMany);
        return new Result<OverflowBandsViewModel>(new OverflowBandsViewModel(siteCode, now, ISvcOverflowBands.FreshMinutes,
            OverflowBandStates.Build(bands, now, fresh)));
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }

    private sealed class BandRow
    {
        public string Zone { get; set; }
        public string Band { get; set; }
        public DateTime? MinuteUtc { get; set; }
        public int? MaxOccupancy { get; set; }
    }
}
