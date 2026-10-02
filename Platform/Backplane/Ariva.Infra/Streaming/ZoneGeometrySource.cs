using Ariva.Core.Queueing;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Streaming;

/// <summary>A queue zone's geometry in the site's published zone profile, and the profile's version.</summary>
public sealed record ZoneGeometry(QueueZoneGeometry Geometry, int ProfileVersion);

/// <summary>
/// Reads a queue zone's lines and zones from the site's published zone profile (ARV-017): the queue zone's entry and
/// exit lines, its overflow bands and their entry lines. A zone that is not in the published profile has no geometry
/// (its events are counted and skipped by the stream worker).
/// </summary>
public class ZoneGeometrySource(DatabaseSettings database)
{
    public virtual async Task<ZoneGeometry> LoadAsync(string siteCode, string queueZoneName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH profile AS (
                SELECT id, version FROM zone_profile WHERE site_code = @site AND status = 'Published' ORDER BY version DESC LIMIT 1
            ), queue AS (
                SELECT z.id FROM zone z JOIN profile p ON p.id = z.profile_id WHERE z.name = @zone AND z.kind = 'Queue' LIMIT 1
            ), bands AS (
                SELECT z.id, z.name FROM zone z JOIN profile p ON p.id = z.profile_id WHERE z.kind = 'Overflow' AND z.queue_zone_id = (SELECT id FROM queue)
            )
            SELECT 'version' AS what, (SELECT version FROM profile)::text AS name, NULL AS role WHERE EXISTS (SELECT 1 FROM queue)
            UNION ALL SELECT 'band', b.name, NULL FROM bands b
            UNION ALL SELECT 'line', l.name, l.role FROM line l JOIN profile p ON p.id = l.profile_id
                WHERE l.zone_id = (SELECT id FROM queue) OR l.zone_id IN (SELECT id FROM bands)
            """, connection);
        command.Parameters.AddWithValue("site", siteCode);
        command.Parameters.AddWithValue("zone", queueZoneName);
        int? version = null;
        var bands = new HashSet<string>(StringComparer.Ordinal);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var exits = new HashSet<string>(StringComparer.Ordinal);
        var overflowEntries = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var what = reader.GetString(0);
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            switch (what)
            {
                case "version":
                    version = int.Parse(name!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "band":
                    bands.Add(name);
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
                    }

                    break;
            }
        }

        return version is { } v ? new ZoneGeometry(new QueueZoneGeometry(queueZoneName, entries, exits, overflowEntries, bands), v) : null;
    }
}
