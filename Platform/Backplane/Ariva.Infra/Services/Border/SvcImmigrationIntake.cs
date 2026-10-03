using System.Collections.Concurrent;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Services.Border;
using Ariva.Infra.Border;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Border;

/// <summary>
/// The immigration intake (ARV-048, <see cref="ISvcImmigrationIntake"/>). Each record is checked by
/// <see cref="ImmigrationRules"/>, its site checked (the call's site, or a site that exists), its desk or gate code
/// resolved through the site's AMAN desk code mappings (an unmapped code, or one mapped to the wrong kind of desk, is
/// stored with no desk and a warning, and logged once per code), and inserted once per site and source event id
/// (parameterised SQL, <c>ON CONFLICT DO NOTHING</c>); lane demand is replaced only by a later computation. Everything
/// runs in the caller's unit of work.
/// </summary>
internal sealed class SvcImmigrationIntake(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, BorderFeedSettings settings,
    BorderMetrics metrics, ILogger<SvcImmigrationIntake> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcImmigrationIntake
{
    private const string Duplicate = "Already received (the same source event id at this site); nothing changed.";
    private const string NotThisSite = "siteCode is the site of the call.";
    private const string UnknownSite = "siteCode is not a site Ariva knows.";
    private static readonly Guid NoDesk = Guid.Empty;
    private const int MaxLoggedPerSite = 1_000;

    // Codes already logged as unmapped, per site (a site is one Ariva knows, so the sites are bounded; each site's set is
    // capped, so one client filling its own site's set never silences another site). Metrics count every record.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> LoggedUnmapped = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Dictionary<string, DeskRow>> mappings = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<ImmigrationItemResult>> ApplyDeskSessionsAsync(ImmigrationScope scope, string feed, IReadOnlyList<DeskSessionChanged> items, CancellationToken ct = default) =>
        ApplyAsync("desk-sessions", scope, feed, items, i => i.SiteCode, i => Echo(i.DeskCode), ImmigrationRules.Check, async (item, site, name, now) =>
        {
            var (desk, warning) = await DeskAsync(site, item.DeskCode, "Desk", ct);
            var rows = await ExecuteCommandAsync<IdRow>("""
                INSERT INTO border_desk_session (id, site_code, desk_code, desk_id, state, lane_category, occurred_utc, feed, source_event_id, received_utc)
                VALUES (:id, :site, :code, NULLIF(CAST(:desk AS uuid), CAST('00000000-0000-0000-0000-000000000000' AS uuid)), :state, NULLIF(:lane, ''),
                        :occurred, :feed, :event, :now)
                ON CONFLICT (site_code, source_event_id) DO NOTHING
                RETURNING id AS "Id"
                """, new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid(), ["site"] = site, ["code"] = item.DeskCode, ["desk"] = desk, ["state"] = item.State.ToString(),
                ["lane"] = item.State == DeskSessionState.Closed ? string.Empty : item.LaneCategory, ["occurred"] = item.OccurredAtUtc.UtcDateTime, ["feed"] = name,
                ["event"] = item.SourceEventId, ["now"] = now
            }, ct);
            return (rows.Count > 0, warning);
        }, ct);

    public Task<IReadOnlyList<ImmigrationItemResult>> ApplyDeskIntervalsAsync(ImmigrationScope scope, string feed, IReadOnlyList<DeskIntervalStats> items, CancellationToken ct = default) =>
        ApplyAsync("desk-interval-stats", scope, feed, items, i => i.SiteCode, i => Echo(i.DeskCode), ImmigrationRules.Check, async (item, site, name, now) =>
        {
            var (desk, warning) = await DeskAsync(site, item.DeskCode, "Desk", ct);
            var rows = await ExecuteCommandAsync<IdRow>("""
                INSERT INTO border_desk_interval (id, site_code, desk_code, desk_id, interval_start_utc, transactions_processed, documents_processed,
                                                  mean_service_seconds, p90_service_seconds, mean_cycle_seconds, lane_category, feed, source_event_id, received_utc)
                VALUES (:id, :site, :code, NULLIF(CAST(:desk AS uuid), CAST('00000000-0000-0000-0000-000000000000' AS uuid)), :start, :transactions, :documents,
                        :service, :p90, :cycle, :lane, :feed, :event, :now)
                ON CONFLICT (site_code, source_event_id) DO NOTHING
                RETURNING id AS "Id"
                """, new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid(), ["site"] = site, ["code"] = item.DeskCode, ["desk"] = desk, ["start"] = item.IntervalStartUtc.UtcDateTime,
                ["transactions"] = item.TransactionsProcessed, ["documents"] = item.DocumentsProcessed, ["service"] = item.MeanServiceSeconds,
                ["p90"] = item.P90ServiceSeconds, ["cycle"] = item.MeanCycleSeconds, ["lane"] = item.LaneCategory, ["feed"] = name, ["event"] = item.SourceEventId,
                ["now"] = now
            }, ct);
            return (rows.Count > 0, warning);
        }, ct);

    public Task<IReadOnlyList<ImmigrationItemResult>> ApplyEgateIntervalsAsync(ImmigrationScope scope, string feed, IReadOnlyList<EGateIntervalStats> items, CancellationToken ct = default) =>
        ApplyAsync("egate-interval-stats", scope, feed, items, i => i.SiteCode, i => Echo(i.GateCode), ImmigrationRules.Check, async (item, site, name, now) =>
        {
            var (desk, warning) = await DeskAsync(site, item.GateCode, "EGate", ct);
            int Rejects(EGateRejectCategory category) => item.RejectsByCategory?.GetValueOrDefault(category) ?? 0;
            var rows = await ExecuteCommandAsync<IdRow>("""
                INSERT INTO border_egate_interval (id, site_code, gate_code, desk_id, interval_start_utc, attempts, accepted, rejected, rejects_other,
                                                   rejects_document_read, rejects_biometric_capture, rejects_eligibility, rejects_referred_to_officer,
                                                   rejects_technical, mean_cycle_seconds, feed, source_event_id, received_utc)
                VALUES (:id, :site, :code, NULLIF(CAST(:desk AS uuid), CAST('00000000-0000-0000-0000-000000000000' AS uuid)), :start, :attempts, :accepted,
                        :rejected, :other, :document, :biometric, :eligibility, :referred, :technical, :cycle, :feed, :event, :now)
                ON CONFLICT (site_code, source_event_id) DO NOTHING
                RETURNING id AS "Id"
                """, new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid(), ["site"] = site, ["code"] = item.GateCode, ["desk"] = desk, ["start"] = item.IntervalStartUtc.UtcDateTime,
                ["attempts"] = item.Attempts, ["accepted"] = item.Accepted, ["rejected"] = item.Rejected, ["other"] = Rejects(EGateRejectCategory.Other),
                ["document"] = Rejects(EGateRejectCategory.DocumentRead), ["biometric"] = Rejects(EGateRejectCategory.BiometricCapture),
                ["eligibility"] = Rejects(EGateRejectCategory.Eligibility), ["referred"] = Rejects(EGateRejectCategory.ReferredToOfficer),
                ["technical"] = Rejects(EGateRejectCategory.Technical), ["cycle"] = item.MeanCycleSeconds, ["feed"] = name, ["event"] = item.SourceEventId,
                ["now"] = now
            }, ct);
            return (rows.Count > 0, warning);
        }, ct);

    public Task<IReadOnlyList<ImmigrationItemResult>> ApplyLaneDemandAsync(ImmigrationScope scope, string feed, IReadOnlyList<InboundFlightLaneDemand> items, CancellationToken ct = default) =>
        ApplyAsync("inbound-lane-demand", scope, feed, items, i => i.SiteCode, i => FlightRules.NormalizeKey(i.FlightKey), ImmigrationRules.Check, async (item, site, name, now) =>
        {
            int Lane(string lane) => item.PassengersByLane?.GetValueOrDefault(lane) ?? 0;
            var rows = await ExecuteCommandAsync<IdRow>("""
                INSERT INTO inbound_lane_demand (id, site_code, flight_key, scheduled_arrival_utc, boarded_total, cit, res, vis, crw, egate_eligible, computed_utc,
                                                 feed, source_event_id, received_utc)
                VALUES (:id, :site, :key, :scheduled, :boarded, :cit, :res, :vis, :crw, :eligible, :computed, :feed, :event, :now)
                ON CONFLICT (site_code, flight_key) DO UPDATE SET scheduled_arrival_utc = EXCLUDED.scheduled_arrival_utc, boarded_total = EXCLUDED.boarded_total,
                    cit = EXCLUDED.cit, res = EXCLUDED.res, vis = EXCLUDED.vis, crw = EXCLUDED.crw, egate_eligible = EXCLUDED.egate_eligible,
                    computed_utc = EXCLUDED.computed_utc, feed = EXCLUDED.feed, source_event_id = EXCLUDED.source_event_id, received_utc = EXCLUDED.received_utc
                WHERE inbound_lane_demand.computed_utc < EXCLUDED.computed_utc
                RETURNING id AS "Id"
                """, new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid(), ["site"] = site, ["key"] = FlightRules.NormalizeKey(item.FlightKey), ["scheduled"] = item.ScheduledArrivalUtc.UtcDateTime,
                ["boarded"] = item.BoardedTotal, ["cit"] = Lane("CIT"), ["res"] = Lane("RES"), ["vis"] = Lane("VIS"), ["crw"] = Lane("CRW"),
                ["eligible"] = item.EGateEligible, ["computed"] = item.ComputedAtUtc.UtcDateTime, ["feed"] = name, ["event"] = item.SourceEventId, ["now"] = now
            }, ct);
            return (rows.Count > 0, (string)null);
        }, ct, unchanged: "Not newer than the computation already received for this flight; nothing changed.");

    private async Task<IReadOnlyList<ImmigrationItemResult>> ApplyAsync<T>(string contract, ImmigrationScope scope, string feed, IReadOnlyList<T> items,
        Func<T, string> siteOf, Func<T, string> keyOf, Func<T, DateTime, TimeSpan, IReadOnlyList<string>> check,
        Func<T, string, string, DateTime, Task<(bool Applied, string Warning)>> store, CancellationToken ct, string unchanged = Duplicate)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!scope.IsChosen)
            throw new ArgumentException("An immigration call writes one site or, for AMAN's Kafka feed, the site each record names.", nameof(scope));
        var name = FlightRules.NormalizeFeed(feed) ?? throw new ArgumentException("A feed name is 2 to 32 lower case letters, digits or hyphens.", nameof(feed));
        var kind = scope.Transport;
        var now = UtcNow;
        var sites = new Dictionary<string, bool>(StringComparer.Ordinal);
        var results = new List<ImmigrationItemResult>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = item is null ? null : keyOf(item);
            var errors = check(item, now, settings.Ahead);
            if (errors.Count == 0)
            {
                var site = siteOf(item);
                if (!scope.AnyKnownSite && !string.Equals(site, scope.SiteCode, StringComparison.Ordinal))
                    errors = [NotThisSite];
                else if (scope.AnyKnownSite && !await KnownSiteAsync(site, sites, ct))
                    errors = [UnknownSite];
            }

            if (errors.Count > 0)
            {
                metrics.Record(contract, kind, "refused");
                results.Add(new ImmigrationItemResult(i, key, false, errors, []));
                continue;
            }

            var (applied, warning) = await store(item, siteOf(item), name, now);
            metrics.Record(contract, kind, applied ? "applied" : "unchanged");
            var warnings = new List<string>(2);
            if (warning is not null)
                warnings.Add(warning);
            if (!applied)
                warnings.Add(unchanged);
            results.Add(new ImmigrationItemResult(i, key, applied, [], warnings));
        }

        return results;
    }

    private async Task<bool> KnownSiteAsync(string site, Dictionary<string, bool> known, CancellationToken ct)
    {
        if (known.TryGetValue(site, out var exists))
            return exists;
        exists = (await ExecuteSqlAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = site }, ct)).Count > 0;
        known[site] = exists;
        return exists;
    }

    /// <summary>
    /// A record's code as its result key: only a code in the desk code mapping shape (CWE-501), so a refused record's
    /// text is never echoed in the answer or the stored idempotency answer.
    /// </summary>
    private static string Echo(string code) => code is not null && DeskCodeMapping.NormalizeCode(code) == code ? code : null;

    /// <summary>
    /// The Ariva desk an AMAN code stands for at the site, or none with the reason as a warning. The site's AMAN mappings
    /// are read once per call (a mapping counts only when its desk is at the same site, CWE-863).
    /// </summary>
    private async Task<(Guid Desk, string Warning)> DeskAsync(string site, string code, string kind, CancellationToken ct)
    {
        if (!mappings.TryGetValue(site, out var codes))
        {
            var rows = await ExecuteSqlAsync<DeskRow>("""
                SELECT m.external_code AS "Code", m.desk_id AS "DeskId", d.kind AS "Kind"
                  FROM desk_code_mapping m JOIN desk d ON d.id = m.desk_id AND d.site_code = m.site_code
                 WHERE m.site_code = :site AND m.system = 'Aman' AND m.deleted_on IS NULL AND d.deleted_on IS NULL
                """, new Dictionary<string, object> { ["site"] = site }, ct);
            codes = rows.GroupBy(r => r.Code, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
            mappings[site] = codes;
        }

        if (codes.TryGetValue(code, out var mapped) && mapped.Kind == kind)
            return (mapped.DeskId, null);

        metrics.Unmapped(site);
        var logged = LoggedUnmapped.GetOrAdd(site, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (logged.Count < MaxLoggedPerSite && logged.TryAdd(code, 0))
            logger.LogWarning("Border feed code {Code} of site {Site} has no AMAN desk code mapping to a {Kind}; its records are kept apart", code, site, kind);
        return (NoDesk, mapped is not null
            ? $"The code maps to a {mapped.Kind}, not a {(kind == "EGate" ? "e-gate" : "desk")}; the record is kept apart until the mapping is corrected."
            : "The code has no AMAN desk code mapping at the site; the record is kept apart until one exists.");
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }

    private sealed class DeskRow
    {
        public string Code { get; set; }
        public Guid DeskId { get; set; }
        public string Kind { get; set; }
    }
}
