using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights.Aidx;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Flights;

/// <summary>
/// AIDX legs into the flight model (ARV-044): the site's airports (IATA and ICAO codes of the airports with a live
/// terminal of the site) decide each leg's direction (<see cref="AidxMapping"/>); the mapped legs go through
/// <see cref="ISvcFlightIntake"/> in the same unit of work, and every FlightLeg of the message gets its result at its
/// own index, refused ones included.
/// </summary>
internal sealed class SvcAidxIntake(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISvcFlightIntake intake)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAidxIntake
{
    public async Task<IReadOnlyList<FlightItemResult>> ApplyAsync(string siteCode, string feed, AidxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var rows = await ExecuteSqlAsync<CodeRow>("""
            SELECT DISTINCT a.iata_code AS "Iata", a.icao_code AS "Icao"
              FROM airport a JOIN terminal t ON t.airport_id = a.id
             WHERE t.site_code = :site AND t.deleted_on IS NULL AND a.deleted_on IS NULL
            """, new Dictionary<string, object> { ["site"] = siteCode ?? string.Empty }, ct);
        var airports = AidxReader.Airports(rows.SelectMany(r => new[] { r.Iata, r.Icao }));

        var results = new FlightItemResult[message.Legs.Count];
        var mapped = new List<FlightLegData>();
        var positions = new List<int>();
        for (var i = 0; i < message.Legs.Count; i++)
        {
            var (leg, error) = AidxMapping.ToLeg(message.Legs[i], airports);
            if (error is not null)
            {
                results[i] = new FlightItemResult(i, null, false, [error], []);
                continue;
            }

            mapped.Add(leg);
            positions.Add(i);
        }

        if (mapped.Count > 0)
        {
            var applied = await intake.ApplyLegsAsync(siteCode, feed, mapped, message.TimeStamp, ct);
            foreach (var result in applied)
                results[positions[result.Index]] = result with { Index = positions[result.Index] };
        }

        return results;
    }

    private sealed class CodeRow
    {
        public string Iata { get; set; }
        public string Icao { get; set; }
    }
}
