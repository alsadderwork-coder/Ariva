using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.Infra.Streaming;

/// <summary>What to replay: a site's queue zones (all of the profile version when none are named) over a UTC range.</summary>
public sealed record ReplayRequest(string SiteCode, IReadOnlyList<string> Zones, DateTime FromUtc, DateTime ToUtc, int? ProfileVersion, string RequestedBy);

/// <summary>
/// A replay that ran: its id (the replay_run row), what it covered, its hashes, each zone's counters, and the record's
/// row hash (the head of the replay_run chain after it; keep it outside the database as the anchor of this run).
/// </summary>
public sealed record ReplayRun(Guid Id, ReplayManifest Manifest, ReplayHashes Hashes, IReadOnlyDictionary<string, ZoneProcessorCounters> Counters, string RowHash = null);

/// <summary>A replay as recorded in replay_run: when (the database's time), by which login, and its place in the chain.</summary>
public sealed record ReplayRecord(Guid Id, DateTime CreatedOn, string RequestedBy, string RecordedBy, string InputHead, string OutputHead, string PreviousHash, string RowHash);

/// <summary>
/// The golden replay over the archive (ARV-036): for each queue zone of the request, the archived sensing batches and
/// device health reports of the range, in the stream's order, through <see cref="ZoneReplay"/> under the profile version
/// it names (or the published one) and the stream's settings, into one <see cref="ReplayLedger"/>. The run is recorded in
/// replay_run (append-only) with the chains' heads, so an exported replay can be checked against it later. Input is
/// checked before anything is read: a valid site code, zone names as the topology allows, at most 64 zones, a UTC range
/// of at most 31 days, and at most <see cref="MaxEventsPerZone"/> archived events per zone held in memory.
/// </summary>
public class ReplayRunner(
    ISensingArchive sensing,
    IDeviceHealthArchive health,
    ZoneGeometrySource geometries,
    ReplayStore store,
    ZoneProcessorSettings settings = null,
    ILogger<ReplayRunner> logger = null)
{
    public const int MaxZones = 64;
    /// <summary>
    /// Archived rows (and, separately, health reports) held per zone at most: about 400 bytes each, so a zone's replay stays
    /// within the Stream pod's memory; a longer period is replayed in shorter ranges.
    /// </summary>
    public const int MaxEventsPerZone = 1_000_000;

    private readonly ZoneProcessorSettings _settings = settings ?? new ZoneProcessorSettings();
    private readonly ILogger<ReplayRunner> _logger = logger ?? NullLogger<ReplayRunner>.Instance;

    /// <summary>The engine version a replay names in its manifest (Ariva.Core's informational version).</summary>
    public static string EngineVersion =>
        typeof(ZoneProcessor).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    public static IReadOnlyList<string> Problems(ReplayRequest request)
    {
        var problems = new List<string>();
        if (request is null)
            return ["The request is empty."];
        if (!Site.IsValidCode(request.SiteCode))
            problems.Add("The site code is not valid.");
        if (request.FromUtc.Kind != DateTimeKind.Utc || request.ToUtc.Kind != DateTimeKind.Utc || request.ToUtc <= request.FromUtc ||
            request.ToUtc - request.FromUtc > SensingReplayQuery.MaxRange)
            problems.Add("The range is UTC, from before to, at most 31 days.");
        if ((request.Zones?.Count ?? 0) > MaxZones)
            problems.Add($"At most {MaxZones} zones per replay.");
        if ((request.Zones ?? []).Any(z => string.IsNullOrWhiteSpace(z) || z.Length > 200 || !DisplayText.IsClean(z)))
            problems.Add("A zone name is 1 to 200 printable characters.");
        if (request.ProfileVersion is < 1)
            problems.Add("A profile version is 1 or more.");
        return problems;
    }

    public virtual async Task<ReplayRun> RunAsync(ReplayRequest request, TextWriter export, CancellationToken ct)
    {
        var problems = Problems(request);
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(request));

        var profile = await geometries.QueueZonesAsync(request.SiteCode, request.ProfileVersion, ct)
                      ?? throw new InvalidOperationException(request.ProfileVersion is { } v
                          ? $"Site {request.SiteCode} has no zone profile version {v} (published or retired)."
                          : $"Site {request.SiteCode} has no published zone profile.");
        var zones = (request.Zones is { Count: > 0 } named ? named : profile.Zones).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var known = profile.Zones.ToHashSet(StringComparer.Ordinal);
        var unknown = zones.Where(z => !known.Contains(z)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Not queue zones of profile version {profile.Version}: {string.Join(", ", unknown)}.");
        if (zones.Count is 0 or > MaxZones)
            throw new InvalidOperationException($"A replay covers 1 to {MaxZones} queue zones; the profile version has {zones.Count}.");

        var manifest = new ReplayManifest(ReplayManifest.CurrentFormat, request.SiteCode, zones, request.FromUtc, request.ToUtc, profile.Version, EngineVersion,
            ZoneReplay.SettingsHash(_settings));
        var ledger = new ReplayLedger(manifest, export);
        var counters = new Dictionary<string, ZoneProcessorCounters>(StringComparer.Ordinal);
        foreach (var zone in zones)
        {
            var geometry = await geometries.LoadAsync(request.SiteCode, zone, profile.Version, ct)
                           ?? throw new InvalidOperationException($"Zone {zone} has no geometry in profile version {profile.Version}.");
            var rows = new List<ArchivedSensingEvent>();
            await foreach (var row in sensing.ReadAsync(new SensingReplayQuery(request.SiteCode, zone, request.FromUtc, request.ToUtc), ct))
            {
                if (rows.Count >= MaxEventsPerZone)
                    throw new InvalidOperationException($"Zone {zone} has more than {MaxEventsPerZone} archived events in the range; replay a shorter range.");
                rows.Add(row);
            }

            var reports = new List<ArchivedDeviceHealth>();
            await foreach (var report in health.ReadAsync(request.SiteCode, zone, request.FromUtc, request.ToUtc, ct))
            {
                if (reports.Count >= MaxEventsPerZone)
                    throw new InvalidOperationException($"Zone {zone} has more than {MaxEventsPerZone} health reports in the range; replay a shorter range.");
                reports.Add(report);
            }

            var key = ZoneKeys.For(request.SiteCode, zone);
            counters[key] = ZoneReplay.Run(ledger, key, geometry.Geometry, geometry.ProfileVersion, _settings,
                ReplayInput.InOrder(ArchivedBatch.Group(rows), reports), request.FromUtc, request.ToUtc);
            _logger.LogInformation("Replayed zone {Zone}: {Events} archived events, {Reports} health reports", key, rows.Count, reports.Count);
        }

        var hashes = ledger.End();
        var run = new ReplayRun(Guid.CreateVersion7(), manifest, hashes, counters);
        return run with { RowHash = await store.RecordAsync(run, request.RequestedBy, ct) };
    }
}

/// <summary>
/// The record of every replay (ARV-036, replay_run): append-only for the runtime role, with the time, login and chain
/// link set by the database, the anchor an export is checked against.
/// </summary>
public class ReplayStore(DatabaseSettings database)
{
    /// <summary>Records the run; returns its row hash.</summary>
    public virtual async Task<string> RecordAsync(ReplayRun run, string requestedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO replay_run (id, format, site_code, zones, from_utc, to_utc, profile_version, settings_hash, input_records, output_records,
                                    input_head, output_head, replay_hash, requested_by)
            VALUES (@id, @format, @site, @zones, @from, @to, @version, @settings, @inputs, @outputs, @input, @output, @replay, @by)
            RETURNING row_hash
            """, connection);
        insert.Parameters.AddWithValue("id", run.Id);
        insert.Parameters.AddWithValue("format", run.Manifest.Format);
        insert.Parameters.AddWithValue("site", run.Manifest.SiteCode);
        insert.Parameters.AddWithValue("zones", string.Join('\n', run.Manifest.Zones));
        insert.Parameters.AddWithValue("from", run.Manifest.FromUtc);
        insert.Parameters.AddWithValue("to", run.Manifest.ToUtc);
        insert.Parameters.AddWithValue("version", run.Manifest.ProfileVersion);
        insert.Parameters.AddWithValue("settings", run.Manifest.SettingsHash);
        insert.Parameters.AddWithValue("inputs", run.Hashes.Inputs);
        insert.Parameters.AddWithValue("outputs", run.Hashes.Outputs);
        insert.Parameters.AddWithValue("input", run.Hashes.InputHead);
        insert.Parameters.AddWithValue("output", run.Hashes.OutputHead);
        insert.Parameters.AddWithValue("replay", run.Hashes.ReplayHash);
        insert.Parameters.AddWithValue("by", Truncate(requestedBy, 100));
        return (string)await insert.ExecuteScalarAsync(ct);
    }

    /// <summary>The recorded runs with this replay hash (normally one), oldest first.</summary>
    public virtual async Task<IReadOnlyList<ReplayRecord>> FindAsync(string replayHash, CancellationToken ct)
    {
        if (replayHash is not { Length: 64 } || !replayHash.All(char.IsAsciiHexDigitLower))
            return [];
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT id, created_on, requested_by, recorded_by, input_head, output_head, previous_hash, row_hash
            FROM replay_run WHERE replay_hash = @hash ORDER BY created_on, id LIMIT 100
            """, connection);
        query.Parameters.AddWithValue("hash", replayHash);
        var found = new List<ReplayRecord>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(new ReplayRecord(reader.GetGuid(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }

        return found;
    }

    private static string Truncate(string value, int max) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Length <= max ? value : value[..max];
}
