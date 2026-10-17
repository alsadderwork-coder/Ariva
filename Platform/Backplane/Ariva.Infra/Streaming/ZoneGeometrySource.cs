using Ariva.Core.Desks;
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
/// occupancy sanity check of F18), and the staff and service zones hanging off the queue zone that name a desk (ARV-116:
/// matched to the desk's key, site, checkpoint and desk code, so their readings reach the desk engine; a desk removed
/// since, an e-gate, a key longer than the desk tables hold, or a second zone of the same role for one desk is left
/// out, the zone with the first name in ordinal order kept). A zone that is not in the published profile has no
/// geometry (its events are counted and skipped by the stream worker).
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
            SELECT 'version' AS what, (SELECT version FROM profile)::text AS name, NULL AS role, (SELECT physical_capacity FROM queue) AS capacity,
                NULL::text AS checkpoint_code, NULL::text AS desk_code
                WHERE EXISTS (SELECT 1 FROM queue)
            UNION ALL SELECT 'band', b.name, NULL, b.physical_capacity, NULL, NULL FROM bands b
            UNION ALL SELECT 'line', l.name, l.role, NULL::integer, NULL, NULL FROM line l JOIN profile p ON p.id = l.profile_id
                WHERE l.zone_id = (SELECT id FROM queue) OR l.zone_id IN (SELECT id FROM bands)
            UNION ALL SELECT 'desk', z.name, z.kind, NULL::integer, c.code, d.code FROM zone z JOIN profile p ON p.id = z.profile_id
                JOIN desk d ON d.id = z.desk_id AND d.site_code = @site AND d.deleted_on IS NULL AND d.kind <> 'EGate'
                JOIN checkpoint c ON c.id = d.checkpoint_id
                WHERE z.kind IN ('Staff', 'Service') AND z.queue_zone_id = (SELECT id FROM queue)
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
        var deskZones = new List<(string Zone, DeskZoneLink Link)>();
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
                case "desk":
                    var source = (reader.IsDBNull(2) ? null : reader.GetString(2)) switch
                    {
                        "Staff" => DeskSource.StaffZone,
                        "Service" => DeskSource.ServiceZone,
                        _ => (DeskSource?)null
                    };
                    var key = reader.IsDBNull(4) || reader.IsDBNull(5) ? null : DeskKeys.For(siteCode, reader.GetString(4), reader.GetString(5));
                    if (name is not null && source is { } role && DeskKeys.Fits(key))
                        deskZones.Add((name, new DeskZoneLink(key, role)));
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

        // One staff and one service zone per desk: the first by name in ordinal order (a profile drawn with two is still read the same way).
        var links = new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal);
        foreach (var group in deskZones.OrderBy(d => d.Zone, StringComparer.Ordinal).GroupBy(d => d.Link).Take(DeskZoneReadings.MaxLinks))
            links[group.First().Zone] = group.Key;
        return found is { } v
            ? new ZoneGeometry(new QueueZoneGeometry(queueZoneName, entries, exits, overflowEntries, bands, counts) { Capacities = capacities, DeskZones = links }, v)
            : null;
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
