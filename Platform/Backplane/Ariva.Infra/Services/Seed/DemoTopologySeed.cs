using System.Data;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Services.Administration;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Seed;

/// <summary>What one run of the demo seed created (zero on a re-run), and why zone profile v12 was left out, if it was.</summary>
public sealed record SeedOutcome(int Created, string ProfileSkipped = null);

/// <summary>One run of the demo topology seed; an interface so the hosted retry loop can be tested on its own.</summary>
internal interface IDemoTopologySeed
{
    Task<SeedOutcome> RunAsync(CancellationToken ct);
}

/// <summary>
/// The fictional Demo International Airport (DMO) of the prototype and the reference scenario (ARV-019): site DMO,
/// terminal T1 with an arrivals and a departures level, check-in islands A to D with 12 counters each (48), security
/// North and South with 5 lanes each, arrival immigration with 22 desks and 6 e-gates, departure immigration with 22
/// desks and 4 e-gates, and zone profile version 12 (the prototype's base geometry, docs/design/prototype/app/assets/
/// floorplan.js, at 0.1 m per drawing unit).
/// <para>
/// Idempotent: every record is looked up by its code and created only when missing, and zone profile v12 only when the
/// site has no zone profile at all, so a re-run changes nothing (no updates, no audit entries). A draft someone started
/// is left alone and v12 is skipped with a note (a site has one draft at a time, and the seed never discards work). Runs under a transaction-scoped advisory lock, so two pods starting together do not race,
/// and holds the site row lock that the zone profile API takes, so it cannot interleave with a draft or a publish.
/// Records that already exist must belong to site DMO and have the expected kind; anything else stops the seed with a
/// clear message rather than writing into another site. It creates topology only: no users, roles or site grants
/// (CWE-269), and it never runs in production (<c>DemoSeedExtensions</c> refuses it there).
/// </para>
/// </summary>
internal sealed class DemoTopologySeed(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, AuditTrail audit) : IDemoTopologySeed
{
    public const string SiteCode = "DMO";
    public const string SystemUserName = "demo-seed";
    public const int ProfileVersion = 12;

    /// <summary>The prototype draws in units of 0.1 m.</summary>
    private const double Metres = 0.1;

    private const long SeedLock = 0x41524956_00000019;

    private static readonly (string Lane, int From, int To)[] BorderDesks = [("CRW", 1, 1), ("CIT", 2, 4), ("RES", 5, 7), ("VIS", 8, 22)];

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    public async Task<SeedOutcome> RunAsync(CancellationToken ct)
    {
        // A system identity like the development user seed: no user row, so it can never be mistaken for a person.
        currentUser.SetSystemUser(Guid.Empty, SystemUserName);
        if (!Storage.IsTransactionActive())
            Storage.BeginTransaction(IsolationLevel.ReadCommitted);
        await Storage.ExecuteSqlAsync<LockRow>("""SELECT 1 AS "Value" FROM pg_advisory_xact_lock(:key)""", new Dictionary<string, object> { ["key"] = SeedLock }, ct);

        var created = 0;
        async Task<T> Save<T>(T entity) where T : class, IDomain
        {
            created++;
            return await Storage.SaveAsync(entity, ct);
        }

        if (!await Storage.Query<Site>().AnyAsync(s => s.Code == SiteCode, ct))
            await Save(new Site(SiteCode, "Demo International Airport"));
        else
            await Storage.ExecuteSqlAsync<LockRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR UPDATE""", new Dictionary<string, object> { ["code"] = SiteCode }, ct);

        var airport = await Storage.Query<Airport>().FirstOrDefaultAsync(a => a.IataCode == SiteCode, ct)
                      ?? await Save(new Airport(SiteCode, null, "Demo International Airport", "Asia/Dubai"));
        var terminal = airport.Terminals.FirstOrDefault(t => !t.IsDeleted && t.Code == "T1")
                       ?? await Save(airport.AddTerminal("T1", "Terminal 1", SiteCode));
        if (terminal.SiteCode != SiteCode)
            throw new InvalidOperationException($"Terminal T1 of airport {SiteCode} belongs to site {terminal.SiteCode}, not {SiteCode}; the demo seed does not write into another site.");
        var arrivals = terminal.Levels.FirstOrDefault(l => !l.IsDeleted && l.Code == "ARR")
                       ?? await Save(terminal.AddLevel("ARR", "Arrivals", 0, 100, 60));
        var departures = terminal.Levels.FirstOrDefault(l => !l.IsDeleted && l.Code == "DEP")
                         ?? await Save(terminal.AddLevel("DEP", "Departures", 1, 100, 60));

        async Task<Checkpoint> CheckpointAsync(Level level, string code, string name, CheckpointKind kind)
        {
            var checkpoint = level.Checkpoints.FirstOrDefault(c => !c.IsDeleted && c.Code == code) ?? await Save(level.AddCheckpoint(code, name, kind));
            if (checkpoint.Kind != kind)
                throw new InvalidOperationException($"Checkpoint {code} of the demo airport is a {checkpoint.Kind}, not a {kind}.");
            return checkpoint;
        }

        async Task DeskAsync(Checkpoint checkpoint, string code, DeskKind kind, params string[] lanes)
        {
            var desk = checkpoint.Desks.FirstOrDefault(d => !d.IsDeleted && d.Code == code);
            if (desk is null)
                await Save(checkpoint.AddDesk(code, null, kind, lanes));
            else if (desk.Kind != kind)
                throw new InvalidOperationException($"Desk {code} of the demo airport is a {desk.Kind}, not a {kind}.");
        }

        var checkIn = await CheckpointAsync(departures, "CI", "Check-in", CheckpointKind.CheckIn);
        foreach (var island in "ABCD")
        {
            for (var n = 1; n <= 12; n++)
                await DeskAsync(checkIn, $"{island}{n:00}", DeskKind.Counter);
        }

        foreach (var (code, name, prefix) in new[] { ("SEC-N", "Security North", "N"), ("SEC-S", "Security South", "S") })
        {
            var security = await CheckpointAsync(departures, code, name, CheckpointKind.Security);
            for (var n = 1; n <= 5; n++)
                await DeskAsync(security, $"{prefix}{n}", DeskKind.SecurityLane);
        }

        foreach (var (level, code, name, kind, desk, gate, gates) in new[]
                 {
                     (arrivals, "IMM", "Arrival immigration", CheckpointKind.Immigration, "AR-", "AG-", 6),
                     (departures, "EMI", "Departure immigration", CheckpointKind.Emigration, "DP-", "DG-", 4)
                 })
        {
            var border = await CheckpointAsync(level, code, name, kind);
            foreach (var (lane, from, to) in BorderDesks)
            {
                for (var n = from; n <= to; n++)
                    await DeskAsync(border, $"{desk}{n:00}", DeskKind.Desk, lane);
            }

            for (var n = 1; n <= gates; n++)
                await DeskAsync(border, $"{gate}{n}", DeskKind.EGate, LaneCategory.EGateEligible);
        }

        // v12 is inserted as a draft (zones and lines can only be added to a draft) and published in the same transaction,
        // so a draft someone already started (one per site) means the site's geometry is theirs: skip v12 and say so.
        string skipped = null;
        var existing = await Storage.Query<ZoneProfile>().Where(p => p.SiteCode == SiteCode).Select(p => p.Status).ToListAsync(ct);
        if (existing.Count > 0 && existing.All(s => s == ZoneProfileStatus.Draft))
            skipped = "site DMO has a zone profile draft and no published version; publish or discard the draft to get the demo geometry";
        else if (existing.Count == 0)
        {
            var profile = BuildProfile(arrivals, departures);
            await Save(profile);
            foreach (var zone in profile.Zones.OrderBy(z => z.QueueZone is not null))
                await Save(zone);
            foreach (var line in profile.Lines)
                await Save(line);
            var levels = new Dictionary<Guid, Level> { [arrivals.Id!.Value] = arrivals, [departures.Id!.Value] = departures };
            var problems = profile.Publish(ProfileVersion, levels, SystemUserName, timeProvider.GetUtcNow().UtcDateTime);
            if (problems.Count > 0)
                throw new InvalidOperationException("The demo zone profile does not validate: " + string.Join("; ", problems));
            await Storage.UpdateAsync(profile, ct);
            // The same evidence the API records for a publish (SvcZoneProfiles), so v12's geometry hash is in the audit trail.
            await audit.RecordAsync("ZoneProfile.Published", "ZoneProfile", profile.Id, SiteCode, null,
                $"site={profile.SiteCode}; name={profile.Name}; status={profile.Status}; version={profile.Version}; zones={profile.Zones.Count}; lines={profile.Lines.Count}; hash={profile.GeometryHash}", ct);
        }

        if (created > 0)
        {
            await audit.RecordAsync("Seed.DemoTopology", "Site", null, SiteCode, null, $"created={created}", ct);
            unitOfWork.PromiseToCommit();
        }

        return new SeedOutcome(created, skipped);
    }

    /// <summary>
    /// Zone profile v12 of the prototype: snake queues per lane at arrival and departure immigration, check-in islands
    /// A to D, security North and South, each with an entry line and an exit line on its edges, and the overflow bands
    /// with their overflow entry lines. The levels must be saved (they need ids).
    /// </summary>
    public static ZoneProfile BuildProfile(Level arrivals, Level departures)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(departures);
        var profile = new ZoneProfile(SiteCode, "Demo International Airport v12");
        var laneY = new Dictionary<string, (double Top, double Bottom)>
        {
            ["CRW"] = (45, 66), ["CIT"] = (69, 122), ["RES"] = (125, 179), ["VIS"] = (182, 466), ["EG"] = (478, 590)
        };
        var queues = new Dictionary<string, Zone>(StringComparer.Ordinal);

        Zone Queue(string name, Level level, double x, double y, double w, double h, (double, double, double, double) entry, (double, double, double, double) exit)
        {
            var zone = profile.AddZone(name, ZoneKind.Queue, level, Rect(x, y, w, h));
            profile.AddLine($"{name} entry", LineRole.Entry, level, P(entry.Item1, entry.Item2), P(entry.Item3, entry.Item4), zone);
            profile.AddLine($"{name} exit", LineRole.Exit, level, P(exit.Item1, exit.Item2), P(exit.Item3, exit.Item4), zone);
            queues[name] = zone;
            return zone;
        }

        foreach (var (lane, (top, bottom)) in laneY)
        {
            foreach (var (prefix, level, x0, x1) in new[] { ("A", arrivals, 190.0, 480.0), ("D", departures, 746.0, 894.0) })
            {
                Queue($"{prefix}-{lane}", level, x0, top, x1 - x0, bottom - top,
                    (x0, top + 3, x0, Math.Min(top + 19, bottom - 3)),
                    (x1, top + 3, x1, bottom - 3));
            }
        }

        foreach (var (island, x) in new[] { ("A", 56.0), ("B", 168.0), ("C", 280.0), ("D", 392.0) })
            Queue($"CI-{island}", departures, x, 56, 80, 370, (x + 6, 426, x + 36, 426), (x + 80, 60, x + 80, 422));

        Queue("SEC-N", departures, 532, 50, 80, 200, (532, 222, 532, 246), (612, 54, 612, 246));
        Queue("SEC-S", departures, 532, 330, 80, 200, (532, 334, 532, 358), (612, 334, 612, 526));

        void Overflow(string name, string feeds, Level level, double x, double y, double w, double h, (double, double, double, double) entry)
        {
            var zone = profile.AddZone(name, ZoneKind.Overflow, level, Rect(x, y, w, h), queues[feeds]);
            profile.AddLine($"{name} entry", LineRole.OverflowEntry, level, P(entry.Item1, entry.Item2), P(entry.Item3, entry.Item4), zone);
        }

        // An overflow band hangs off one queue (the prototype's zoneDef: A-OV feeds A-VIS, SEC-OV feeds SEC-N).
        Overflow("A-OV", "A-VIS", arrivals, 116, 182, 62, 284, (116, 190, 116, 220));
        Overflow("D-OV", "D-VIS", departures, 718, 182, 20, 284, (718, 190, 718, 220));
        Overflow("SEC-OV", "SEC-N", departures, 532, 262, 80, 56, (532, 270, 532, 300));
        foreach (var (island, x) in new[] { ("A", 56.0), ("B", 168.0), ("C", 280.0), ("D", 392.0) })
            Overflow($"CI-{island}-OV", $"CI-{island}", departures, x, 436, 80, 56, (x + 6, 492, x + 36, 492));

        return profile;
    }

    private static FloorPoint P(double x, double y) => new(x * Metres, y * Metres);

    private static FloorPoint[] Rect(double x, double y, double w, double h) => [P(x, y), P(x + w, y), P(x + w, y + h), P(x, y + h)];

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
