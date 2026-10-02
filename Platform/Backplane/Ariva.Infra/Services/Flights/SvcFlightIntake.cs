using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Flights;

/// <summary>
/// The flight model's intake (ARV-041). Each item is checked by <see cref="FlightRules"/> before it touches an entity
/// (CWE-501) and applied under a transaction advisory lock of its leg (class 41, <see cref="LegLockKey"/>), all taken in ascending key order before
/// anything is applied, so two feeds writing the same legs at once never both create one, overwrite each other's newer
/// values or deadlock. One transaction per call; a call with at least one item that passed the checks counts as a
/// message of its feed (<c>feed_freshness</c>). Domain events (FlightChanged) go through the outbox.
/// </summary>
internal sealed class SvcFlightIntake(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, FlightMetrics metrics,
    ILogger<SvcFlightIntake> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcFlightIntake
{
    public const int LockClass = 41;

    public async Task<IReadOnlyList<FlightItemResult>> ApplyLegsAsync(string siteCode, string feed, IReadOnlyList<FlightLegData> legs, DateTime? sourceUtc,
        CancellationToken ct = default)
    {
        var (name, source, now) = await BeginAsync(siteCode, feed, legs, sourceUtc, ct);
        await LockAsync(siteCode, legs.Select(l => l?.FlightKey), ct);
        var results = new List<FlightItemResult>(legs.Count);
        var known = new Dictionary<string, FlightLeg>(StringComparer.Ordinal);
        for (var i = 0; i < legs.Count; i++)
        {
            var (values, errors) = FlightRules.Check(legs[i], now);
            if (errors.Count > 0)
            {
                results.Add(Refused(i, legs[i]?.FlightKey, errors));
                continue;
            }

            var leg = await LegAsync(siteCode, values.FlightKey, known, ct);
            if (leg is null)
            {
                leg = new FlightLeg(siteCode, values, name, source, now);
                await SaveAsync(leg, ct);
                // Flushed now: an event or allocation later in the same call, or the next call, must find it.
                await FlushAsync(ct);
                known[values.FlightKey] = leg;
                results.Add(new FlightItemResult(i, values.FlightKey, true, [], []));
                continue;
            }

            if (leg.Direction != values.Direction)
            {
                results.Add(Refused(i, values.FlightKey, ["The flight key belongs to a leg of the other direction."]));
                continue;
            }

            var applied = leg.Apply(values, name, source, now);
            if (applied)
                await UpdateAsync(leg, ct);
            results.Add(new FlightItemResult(i, values.FlightKey, applied, [], applied ? [] : ["Nothing newer than what is known."]));
        }

        await EndAsync("legs", siteCode, name, now, results, ct);
        return results;
    }

    public async Task<IReadOnlyList<FlightItemResult>> ApplyEventsAsync(string siteCode, string feed, IReadOnlyList<FlightEventData> events, DateTime? sourceUtc,
        CancellationToken ct = default)
    {
        var (name, source, now) = await BeginAsync(siteCode, feed, events, sourceUtc, ct);
        await LockAsync(siteCode, events.Select(e => e?.FlightKey), ct);
        var results = new List<FlightItemResult>(events.Count);
        var known = new Dictionary<string, FlightLeg>(StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var (key, type, time, errors) = FlightRules.Check(events[i]);
            if (errors.Count > 0)
            {
                results.Add(Refused(i, events[i]?.FlightKey, errors));
                continue;
            }

            var leg = await LegAsync(siteCode, key, known, ct);
            if (leg is null)
            {
                results.Add(Refused(i, key, ["Unknown flight: send the leg before its events."]));
                continue;
            }

            if (!leg.Fits(type))
            {
                results.Add(Refused(i, key, [$"{type} is not a milestone of {(leg.Direction == FlightDirection.Arrival ? "an arrival" : "a departure")}."]));
                continue;
            }

            if (time < leg.ScheduledUtc - FlightRules.EarlyBy || time > leg.ScheduledUtc + FlightRules.LateBy)
            {
                results.Add(Refused(i, key, ["timeUtc is at most a day before and 3 days after the leg's scheduled time."]));
                continue;
            }

            var applied = leg.Record(type, time, name, source, now);
            if (applied)
                await UpdateAsync(leg, ct);
            await SaveAsync(new FlightEvent(leg, type, time, name, source, now, applied), ct);
            results.Add(new FlightItemResult(i, key, applied, [], applied ? [] : ["Nothing newer than what is known."]));
        }

        await EndAsync("events", siteCode, name, now, results, ct);
        return results;
    }

    public async Task<IReadOnlyList<FlightItemResult>> ApplyAllocationsAsync(string siteCode, string feed, IReadOnlyList<CounterAllocationData> allocations,
        DateTime? sourceUtc, CancellationToken ct = default)
    {
        var (name, source, now) = await BeginAsync(siteCode, feed, allocations, sourceUtc, ct);
        await LockAsync(siteCode, allocations.Select(a => a?.FlightKey), ct);
        var results = new List<FlightItemResult>(allocations.Count);
        var known = new Dictionary<string, FlightLeg>(StringComparer.Ordinal);
        var checkpoints = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        for (var i = 0; i < allocations.Count; i++)
        {
            var (values, errors) = FlightRules.Check(allocations[i]);
            if (errors.Count > 0)
            {
                results.Add(Refused(i, allocations[i]?.FlightKey, errors));
                continue;
            }

            var leg = await LegAsync(siteCode, values.FlightKey, known, ct);
            if (leg is null)
            {
                results.Add(Refused(i, values.FlightKey, ["Unknown flight: send the leg before its counters."]));
                continue;
            }

            if (leg.Direction != FlightDirection.Departure)
            {
                results.Add(Refused(i, values.FlightKey, ["Check-in counters are allocated to departures."]));
                continue;
            }

            if (values.OpenUtc < leg.ScheduledUtc - FlightRules.MaxCounterOpen || values.CloseUtc > leg.ScheduledUtc + FlightRules.LateBy)
            {
                results.Add(Refused(i, values.FlightKey, ["Counters open at most 24 hours before the scheduled time and close at most 3 days after it."]));
                continue;
            }

            if (!checkpoints.TryGetValue(values.CheckpointCode, out var mapping))
            {
                mapping = await CountersAsync(siteCode, values.CheckpointCode, ct);
                checkpoints[values.CheckpointCode] = mapping;
            }

            if (mapping is null)
            {
                results.Add(Refused(i, values.FlightKey, ["checkpointCode is not a check-in checkpoint of the site."]));
                continue;
            }

            var desks = values.CounterCodes.Where(mapping.ContainsKey).Select(code => mapping[code]).Distinct(StringComparer.Ordinal).ToList();
            var unresolved = values.CounterCodes.Where(code => !mapping.ContainsKey(code)).ToList();
            var allocation = await Query<CounterAllocation>().Where(a => a.FlightLegId == leg.Id.Value && a.CheckpointCode == values.CheckpointCode)
                .FirstOrDefaultAsync(ct);
            var created = allocation is null;
            allocation ??= new CounterAllocation(leg, values.CheckpointCode);
            var applied = allocation.Apply(values, desks, unresolved, name, source, now);
            if (created)
                await SaveAsync(allocation, ct);
            else if (applied)
                await UpdateAsync(allocation, ct);
            var warnings = new List<string>();
            if (!applied)
                warnings.Add("Nothing newer than what is known.");
            if (applied && unresolved.Count > 0)
            {
                warnings.Add($"{unresolved.Count} counter codes have no AODB desk code mapping at {values.CheckpointCode}; they are kept apart until mapped.");
                logger.LogWarning("Counter allocation for {Flight} at {Site}/{Checkpoint}: {Count} counter codes not mapped", values.FlightKey, siteCode,
                    values.CheckpointCode, unresolved.Count);
            }

            results.Add(new FlightItemResult(i, values.FlightKey, applied, [], warnings));
        }

        await EndAsync("allocations", siteCode, name, now, results, ct);
        return results;
    }

    // Checks the call and starts the transaction.
    private async Task<(string Feed, DateTime Source, DateTime Now)> BeginAsync<T>(string siteCode, string feed, IReadOnlyList<T> items, DateTime? sourceUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > FlightRules.MaxBatch)
            throw new ArgumentException($"At most {FlightRules.MaxBatch} items in one call.", nameof(items));
        var name = FlightRules.NormalizeFeed(feed) ?? throw new ArgumentException("A feed name is 2 to 32 lower case letters, digits or hyphens.", nameof(feed));
        var now = FlightRules.Micro(UtcNow);
        var source = FlightRules.Micro(sourceUtc ?? now);
        if (!FlightRules.IsPlausibleSource(source, now))
            throw new ArgumentException("The message time is in UTC, at most 5 minutes ahead of Ariva's clock and 30 days behind it.", nameof(sourceUtc));
        var site = await ExecuteCommandAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode ?? string.Empty },
            ct);
        if (site.Count == 0)
            throw new ArgumentException("Unknown site.", nameof(siteCode));
        return (name, source, now);
    }

    // Counts the call as a message of the feed (its freshness, runbook 4.3) when at least one item passed the checks, so a
    // feed that only sends empty or refused batches cannot keep itself Fresh; then the metrics.
    private async Task EndAsync(string kind, string siteCode, string feed, DateTime now, IReadOnlyList<FlightItemResult> results, CancellationToken ct)
    {
        if (results.Any(r => !r.HasErrors))
        {
            await ExecuteCommandAsync<CodeRow>("""
                INSERT INTO feed_freshness (id, site_code, feed, last_message_utc, state, state_since_utc, flights_due)
                VALUES (gen_random_uuid(), :site, :feed, :now, 'Fresh', :now, 0)
                ON CONFLICT (site_code, feed) DO UPDATE SET last_message_utc = GREATEST(feed_freshness.last_message_utc, EXCLUDED.last_message_utc)
                RETURNING feed AS "Code"
                """, new Dictionary<string, object> { ["site"] = siteCode, ["feed"] = feed, ["now"] = now }, ct);
        }

        metrics.Items(kind, siteCode, feed, results);
    }

    /// <summary>
    /// The advisory lock key of a leg (class <see cref="LockClass"/>): the first four bytes of the SHA-256 of
    /// <c>site/key</c>. Computed here, not by the database, so a call can take all its legs' locks in ascending key order
    /// before it applies anything: two calls of different feeds never wait for each other in a cycle.
    /// </summary>
    public static int LegLockKey(string siteCode, string flightKey) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{siteCode}/{flightKey}")));

    // The locks of every leg the call names (held to the end of the transaction), in ascending order.
    private async Task LockAsync(string siteCode, IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (var lockKey in keys.Select(FlightRules.NormalizeKey).Where(k => k is not null).Select(k => LegLockKey(siteCode, k)).Distinct().Order())
        {
            await ExecuteCommandAsync<CodeRow>("""SELECT CAST(pg_advisory_xact_lock(41, :key) AS text) AS "Code" """,
                new Dictionary<string, object> { ["key"] = lockKey }, ct);
        }
    }

    // The leg of the site (its lock is already held), or null when the site has none with this key.
    private async Task<FlightLeg> LegAsync(string siteCode, string key, Dictionary<string, FlightLeg> known, CancellationToken ct)
    {
        if (known.TryGetValue(key, out var cached))
            return cached;
        var leg = await Query<FlightLeg>().Where(l => l.SiteCode == siteCode && l.FlightKey == key).FirstOrDefaultAsync(ct);
        if (leg is not null)
            known[key] = leg;
        return leg;
    }

    // AODB counter code to desk code at one check-in checkpoint of the site, or null when it is not one.
    private async Task<Dictionary<string, string>> CountersAsync(string siteCode, string checkpointCode, CancellationToken ct)
    {
        var checkpoint = await ExecuteSqlAsync<CodeRow>("""
            SELECT c.code AS "Code" FROM checkpoint c WHERE c.site_code = :site AND c.code = :checkpoint AND c.kind = 'CheckIn' AND c.deleted_on IS NULL
            """, new Dictionary<string, object> { ["site"] = siteCode, ["checkpoint"] = checkpointCode }, ct);
        if (checkpoint.Count == 0)
            return null;
        var rows = await ExecuteSqlAsync<MappingRow>("""
            SELECT m.external_code AS "External", d.code AS "Desk"
            FROM desk_code_mapping m JOIN desk d ON d.id = m.desk_id JOIN checkpoint c ON c.id = d.checkpoint_id
            WHERE m.site_code = :site AND d.site_code = :site AND c.site_code = :site AND m.system = 'Aodb' AND m.deleted_on IS NULL AND d.deleted_on IS NULL
              AND c.code = :checkpoint AND c.deleted_on IS NULL
            """, new Dictionary<string, object> { ["site"] = siteCode, ["checkpoint"] = checkpointCode }, ct);
        return rows.ToDictionary(r => r.External, r => r.Desk, StringComparer.Ordinal);
    }

    private static FlightItemResult Refused(int index, string key, IReadOnlyList<string> errors) =>
        new(index, FlightRules.NormalizeKey(key), false, errors, []);

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }

    private sealed class MappingRow
    {
        public string External { get; set; }
        public string Desk { get; set; }
    }
}
