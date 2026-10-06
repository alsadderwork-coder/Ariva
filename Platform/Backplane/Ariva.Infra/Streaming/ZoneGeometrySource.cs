using Ariva.Core.Queueing;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Streaming;

/// <summary>A queue zone's geometry in the site's published zone profile, and the profile's version.</summary>
public sealed record ZoneGeometry(QueueZoneGeometry Geometry, int ProfileVersion);

/// <summary>
/// Reads a queue zone's lines and zones from the site's published zone profile (ARV-017): the queue zone's entry and
/// exit lines, its overflow bands and their entry lines, and the count lines of the queue zone and its bands (ARV-113,
/// counted per line and minute only), and the physical capacities of the queue zone and its bands (ARV-114a, for the
/// occupancy sanity check of F18). A zone that is not in the published profile has no geometry (its events are counted
/// and skipped by the stream worker).
/// </summary>
public class ZoneGeometrySource(DatabaseSettings database)
{
    public virtual Task<ZoneGeometry> LoadAsync(string siteCode, string queueZoneName, CancellationToken ct) => LoadAsync(siteCode, queueZoneName, null, ct);

    /// <summary>
    /// A queue zone's geometry in the given version of the site's zone profile (published or retired since), or in the
    /// published version when <paramref name="version"/> is null (ARV-036: a replay runs under the version it names).
    /// </summary>
    public virtual async Task<ZoneGeometry> LoadAsync(string siteCode, string queueZoneName, int? version, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH profile AS (
                SELECT id, version FROM zone_profile
                WHERE site_code = @site AND ((@version IS NULL AND status = 'Published') OR (version = @version AND status IN ('Published', 'Retired')))
                ORDER BY version DESC LIMIT 1
            ), queue AS (
                SELECT z.id, z.physical_capacity FROM zone z JOIN profile p ON p.id = z.profile_id WHERE z.name = @zone AND z.kind = 'Queue' LIMIT 1
            ), bands AS (
                SELECT z.id, z.name, z.physical_capacity FROM zone z JOIN profile p ON p.id = z.profile_id
                WHERE z.kind = 'Overflow' AND z.queue_zone_id = (SELECT id FROM queue)
            )
            SELECT 'version' AS what, (SELECT version FROM profile)::text AS name, NULL AS role, (SELECT physical_capacity FROM queue) AS capacity
                WHERE EXISTS (SELECT 1 FROM queue)
            UNION ALL SELECT 'band', b.name, NULL, b.physical_capacity FROM bands b
            UNION ALL SELECT 'line', l.name, l.role, NULL::integer FROM line l JOIN profile p ON p.id = l.profile_id
                WHERE l.zone_id = (SELECT id FROM queue) OR l.zone_id IN (SELECT id FROM bands)
            """, connection);
        command.Parameters.AddWithValue("site", siteCode);
        command.Parameters.AddWithValue("zone", queueZoneName);
        command.Parameters.Add(new NpgsqlParameter<int?>("version", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = version });
        int? found = null;
        var bands = new HashSet<string>(StringComparer.Ordinal);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var exits = new HashSet<string>(StringComparer.Ordinal);
        var overflowEntries = new HashSet<string>(StringComparer.Ordinal);
        var counts = new HashSet<string>(StringComparer.Ordinal);
        var capacities = new Dictionary<string, int>(StringComparer.Ordinal);
        // The table allows 1 to 5,000 only; anything else is not used (CWE-501: read back from storage).
        void Capacity(string zone, NpgsqlDataReader row)
        {
            if (zone is not null && !row.IsDBNull(3) && row.GetInt32(3) is var capacity and >= 1 and <= Ariva.Core.Domain.Entities.Zone.MaxPhysicalCapacity)
                capacities[zone] = capacity;
        }

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var what = reader.GetString(0);
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            switch (what)
            {
                case "version":
                    found = int.Parse(name!, System.Globalization.CultureInfo.InvariantCulture);
                    Capacity(queueZoneName, reader);
                    break;
                case "band":
                    bands.Add(name);
                    Capacity(name, reader);
                    break;
                default:
                    switch (reader.IsDBNull(2) ? null : reader.GetString(2))
                    {
                        case "Entry":
                            entries.Add(name);
                            break;
                        case "Exit":
                            exits.Add(name);
                            break;
                        case "OverflowEntry":
                            overflowEntries.Add(name);
                            break;
                        case "Count":
                            counts.Add(name);
                            break;
                    }

                    break;
            }
        }

        return found is { } v ? new ZoneGeometry(new QueueZoneGeometry(queueZoneName, entries, exits, overflowEntries, bands, counts) { Capacities = capacities }, v) : null;
    }

    /// <summary>The queue zones of a version of the site's zone profile (published or retired), or of the published one; in name order.</summary>
    public virtual async Task<(int Version, IReadOnlyList<string> Zones)?> QueueZonesAsync(string siteCode, int? version, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH profile AS (
                SELECT id, version FROM zone_profile
                WHERE site_code = @site AND ((@version IS NULL AND status = 'Published') OR (version = @version AND status IN ('Published', 'Retired')))
                ORDER BY version DESC LIMIT 1
            )
            SELECT p.version, z.name FROM profile p LEFT JOIN zone z ON z.profile_id = p.id AND z.kind = 'Queue' ORDER BY z.name
            """, connection);
        command.Parameters.AddWithValue("site", siteCode);
        command.Parameters.Add(new NpgsqlParameter<int?>("version", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = version });
        int? found = null;
        var zones = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found = reader.GetInt32(0);
            if (!reader.IsDBNull(1))
                zones.Add(reader.GetString(1));
        }

        return found is { } v ? (v, zones.Order(StringComparer.Ordinal).ToList()) : null;
    }
}
