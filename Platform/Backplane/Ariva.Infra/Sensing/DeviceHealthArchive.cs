using System.Runtime.CompilerServices;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Sensing;
using Ariva.Infra.Settings;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.Infra.Sensing;

/// <summary>
/// The device health archive on PostgreSQL and TimescaleDB (ARV-036, script 0019): every report as received, so that a
/// replay sees the same device liveness the queue stream saw. Reports are checked again (they crossed Kafka): site, zone
/// and device as Ingest writes them, received within the archive's window, and the lines the device declared it counts
/// (ARV-259e5, script 0067) in the registry's shape; others are skipped and counted. A report is archived once (its event id
/// and receive time). Every value is a parameter; no SQL is built from input (CWE-89).
/// </summary>
public sealed class DeviceHealthArchive(DatabaseSettings database, TimeProvider timeProvider, ILogger<DeviceHealthArchive> logger = null,
    SensingArchiveMetrics metrics = null) : IDeviceHealthArchive
{
    private static readonly TimeSpan MaxLate = TimeSpan.FromDays(3) + TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxAhead = TimeSpan.FromHours(1);
    private readonly ILogger<DeviceHealthArchive> _logger = logger ?? NullLogger<DeviceHealthArchive>.Instance;

    public async Task<int> WriteAsync(IReadOnlyList<DeviceHealthReported> reports, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var inBounds = reports.Where(r => r is not null && Usable(r, now)).ToList();
        var usable = inBounds.GroupBy(r => r.Id).Select(g => g.First()).ToList();
        if (inBounds.Count < reports.Count)
        {
            // ARV-159: the reports out of bounds, logged and counted (a repeat of a report in the same list is no skip).
            _logger.LogWarning("Device health archive skipped {Count} reports out of bounds", reports.Count - inBounds.Count);
            metrics?.Skipped(SensingArchiveMetrics.HealthReportKind, reports.Count - inBounds.Count);
        }

        if (usable.Count == 0)
            return 0;

        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO device_health_event (received_utc, site_code, queue_zone_name, device_id, device_code, event_id, online, commissioned, status_utc, clock_state,
                covered_lines)
            SELECT r, s, z, d, c, i, o, m, t, k, CASE WHEN l IS NULL THEN NULL ELSE string_to_array(l, E'\n') END
            FROM unnest(@received, @sites, @zones, @devices, @codes, @ids, @online, @commissioned, @status, @clocks, @lines) AS u (r, s, z, d, c, i, o, m, t, k, l)
            ON CONFLICT (event_id, received_utc) DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue("received", usable.Select(r => Utc(r.ReceivedUtc)).ToArray());
        insert.Parameters.AddWithValue("sites", usable.Select(r => r.SiteCode).ToArray());
        insert.Parameters.AddWithValue("zones", usable.Select(r => r.QueueZoneName).ToArray());
        insert.Parameters.AddWithValue("devices", usable.Select(r => r.DeviceId).ToArray());
        insert.Parameters.AddWithValue("codes", usable.Select(r => r.DeviceCode).ToArray());
        insert.Parameters.AddWithValue("ids", usable.Select(r => r.Id).ToArray());
        insert.Parameters.AddWithValue("online", usable.Select(r => r.Status?.Online ?? true).ToArray());
        insert.Parameters.AddWithValue("commissioned", usable.Select(r => r.Commissioned).ToArray());
        insert.Parameters.AddWithValue("status", usable.Select(r => r.Status is { } s && s.TimeUtc.Kind == DateTimeKind.Utc ? (DateTime?)s.TimeUtc : null).ToArray());
        insert.Parameters.AddWithValue("clocks", usable.Select(r => r.Clock?.State.ToString()).ToArray());
        // ARV-259e5: one text per report, the names joined by line feeds and split back into the array column in SQL (unnest takes no
        // array of arrays). Lossless because Usable took only declarations LineDeclarations.IsWellFormed passes, whose names hold no line
        // feed or other control character (the registry's name rule, ARV-159). NULL for All, an empty text for None (an empty array).
        insert.Parameters.Add(new NpgsqlParameter("lines", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = usable.Select(r => r.CoveredLines is null ? null : string.Join('\n', r.CoveredLines)).ToArray()
        });
        return await insert.ExecuteNonQueryAsync(ct);
    }

    public async IAsyncEnumerable<ArchivedDeviceHealth> ReadAsync(string siteCode, string queueZoneName, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueZoneName);
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc || toUtc - fromUtc > SensingReplayQuery.MaxRange)
            throw new ArgumentOutOfRangeException(nameof(toUtc), "A replay covers a UTC range of at most 31 days, from before to.");
        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT received_utc, site_code, queue_zone_name, device_id, device_code, event_id, online, commissioned, status_utc, clock_state, covered_lines
            FROM device_health_event
            WHERE site_code = @site AND queue_zone_name = @zone AND received_utc >= @from AND received_utc < @to
            ORDER BY received_utc, event_id
            """, connection) { CommandTimeout = DatabaseSettings.LongestRuntimeCommandSeconds };
        command.Parameters.AddWithValue("site", siteCode);
        command.Parameters.AddWithValue("zone", queueZoneName);
        command.Parameters.AddWithValue("from", fromUtc);
        command.Parameters.AddWithValue("to", toUtc);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new ArchivedDeviceHealth(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetGuid(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                reader.IsDBNull(9) ? null : Enum.Parse<ClockState>(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<string[]>(10));
        }
    }

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    private static bool Usable(DeviceHealthReported r, DateTime now)
    {
        if (r.Id == Guid.Empty || r.DeviceId == Guid.Empty || r.ReceivedUtc.Kind != DateTimeKind.Utc)
            return false;
        if (r.SiteCode is not { Length: >= 2 and <= 17 } || !Site.IsValidCode(r.SiteCode) || r.QueueZoneName is not { Length: >= 1 and <= 200 } ||
            !Ariva.Core.Domain.Components.DisplayText.IsClean(r.QueueZoneName))
            return false;
        if (!DeviceCodes.IsValid(r.DeviceCode) || !LineDeclarations.IsWellFormed(r.CoveredLines))
            return false;
        return r.ReceivedUtc > now - MaxLate && r.ReceivedUtc < now + MaxAhead;
    }
}

/// <summary>Archives every device health report (ARV-036), through the Ariva consume pipe; a database outage is waited out as for sensing batches.</summary>
public sealed class DeviceHealthArchiveConsumer(IDeviceHealthArchive archive, TimeProvider timeProvider) : IConsumer<DeviceHealthReported>
{
    private static readonly TimeSpan[] OutageDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    public async Task Consume(ConsumeContext<DeviceHealthReported> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await archive.WriteAsync([context.Message], context.CancellationToken);
                return;
            }
            catch (Exception e) when (SensingArchiveOutage.Is(e) && attempt < OutageDelays.Length)
            {
                await Task.Delay(OutageDelays[attempt], timeProvider, context.CancellationToken);
            }
        }
    }
}
