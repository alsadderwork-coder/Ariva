using Ariva.Core.Domain.Entities;
using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights.Aidx;
using Ariva.Infra.Integration.Outbound;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Flights;

/// <summary>A due ACRIS pull claimed by this replica: the endpoint to call, its path, the site it feeds and what it last saw.</summary>
public sealed record AcrisPullClaim(OutboundTarget Target, string PullPath, string SiteCode, string LastModified);

/// <summary>
/// The database side of the ACRIS flight pull (ARV-045): claims due endpoints atomically (one replica polls each, the
/// rest skip), applies a chunk of mapped flights through <see cref="ISvcFlightIntake"/> in its own unit of work, and
/// records how a pull went. The HTTP side is <see cref="AcrisPoller"/>.
/// </summary>
internal sealed class SvcAcrisPull(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISvcFlightIntake intake)
    : SvcBase(unitOfWork, currentUser, timeProvider)
{
    public async Task<IReadOnlyList<AcrisPullClaim>> ClaimDueAsync(int max, CancellationToken ct)
    {
        var claimed = await ExecuteCommandAsync<IdRow>("""
            UPDATE outbound_endpoint SET last_poll_utc = :now
             WHERE id IN (SELECT id FROM outbound_endpoint
                           WHERE purpose = 'AcrisFlights' AND status = 'Active'
                             AND (last_poll_utc IS NULL OR last_poll_utc <= :now - poll_seconds * interval '1 second')
                           ORDER BY last_poll_utc NULLS FIRST LIMIT :max FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Id"
            """, new Dictionary<string, object> { ["now"] = UtcNow, ["max"] = max }, ct);
        var claims = new List<AcrisPullClaim>();
        foreach (var row in claimed)
        {
            var endpoint = await GetAsync<OutboundEndpoint>(row.Id, ct);
            if (endpoint is not null)
                claims.Add(new AcrisPullClaim(OutboundTarget.Of(endpoint), endpoint.PullPath, endpoint.Sites[0], endpoint.LastModified));
        }

        return claims;
    }

    /// <summary>The site's airports (IATA and ICAO codes of airports with a live terminal in the site).</summary>
    public async Task<IReadOnlySet<string>> AirportsAsync(string siteCode, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<CodeRow>("""
            SELECT DISTINCT a.iata_code AS "Iata", a.icao_code AS "Icao"
              FROM airport a JOIN terminal t ON t.airport_id = a.id
             WHERE t.site_code = :site AND t.deleted_on IS NULL AND a.deleted_on IS NULL
            """, new Dictionary<string, object> { ["site"] = siteCode ?? string.Empty }, ct);
        return AidxReader.Airports(rows.SelectMany(r => new[] { r.Iata, r.Icao }));
    }

    public Task<IReadOnlyList<FlightItemResult>> ApplyAsync(string siteCode, string feed, IReadOnlyList<FlightLegData> legs, DateTime? sourceUtc, CancellationToken ct) =>
        intake.ApplyLegsAsync(siteCode, feed, legs, sourceUtc, ct);

    public async Task RecordAsync(Guid id, bool success, string status, string lastModified, CancellationToken ct)
    {
        var text = status is { Length: > 200 } ? status[..200] : status ?? string.Empty;
        await ExecuteCommandAsync<IdRow>("""
            UPDATE outbound_endpoint
               SET last_status = :status,
                   last_modified = COALESCE(NULLIF(:modified, ''), last_modified),
                   consecutive_failures = CASE WHEN :success THEN 0 ELSE consecutive_failures + 1 END
             WHERE id = :id
            RETURNING id AS "Id"
            """, new Dictionary<string, object>
        {
            ["status"] = new string(text.Where(c => !char.IsControl(c)).ToArray()),
            ["modified"] = lastModified is { Length: <= 64 } && !lastModified.Any(char.IsControl) ? lastModified : string.Empty,
            ["success"] = success,
            ["id"] = id
        }, ct);
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class CodeRow
    {
        public string Iata { get; set; }
        public string Icao { get; set; }
    }
}
