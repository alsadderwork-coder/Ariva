using System.Data;
using System.Security.Cryptography;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Storage;
using NHibernate.Linq;
using static Ariva.Infra.Services.Seed.NbjBc1Layout;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The third demo site (ARV-139c), for development only and opt-in (<c>Seed:NbjSite</c>): the border control halls of
/// terminal BC1 of Dr. António Agostinho Neto International Airport (NBJ), Luanda, built from the counts and measures
/// of the terminal's 2018 design drawings and flagged "Illustrative, not surveyed" (<see cref="Site.IsIllustrative"/>).
/// Site NBJ-BC1, airport NBJ, terminal BC1, two levels: ARR with the checkpoint IMM (26 desks IM-01 to IM-26 in 13
/// double booths, 5 e-gates EGA-01 to EGA-05) and DEP with the checkpoint EMI (26 desks EM-01 to EM-26, 5 e-gates
/// EGD-01 to EGD-05). Each hall has one shared queue (lane ALL: no segregation), an overflow band and an e-gates' queue
/// (lane EG), a staff and a service zone per desk linked to its desk (ARV-116), Ariva's schematic plan per level
/// (<see cref="NbjBc1Plan"/>), sensors in commissioning, and zone profile version 1, published. The drawings themselves
/// are never committed or stored (docs/demo/nbj-bc1.md).
/// <para>
/// The same guard and manners as the AUH-TA seed (<see cref="AuhTerminalASeed"/>): idempotent, under a transaction-scoped
/// advisory lock and the site's row lock; it never writes into a site NBJ-BC1 that is not illustrative, nor into NBJ
/// terminal BC1 of another site. Topology only: no users, roles, site grants, credentials or AMAN codes (CWE-269), and no
/// officer, traveller or document identity or real staff or system name (data boundary).
/// </para>
/// </summary>
internal sealed class NbjBc1Seed(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, AuditTrail audit, IFileStorage files)
    : IDemoTopologySeed
{
    public const string SystemUserName = "demo-seed";
    public const int ProfileVersion = 1;
    public const string ProfileName = "NBJ BC1 border control v1 (illustrative)";
    public const string SensorModel = "Overhead stereo counter (illustrative)";

    private const long SeedLock = 0x41524956_0000139C;

    public string Name => SiteCode;

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    public async Task<SeedOutcome> RunAsync(CancellationToken ct)
    {
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

        var site = await Storage.Query<Site>().FirstOrDefaultAsync(s => s.Code == SiteCode, ct);
        if (site is null)
            await Save(Site.CreateIllustrative(SiteCode, SiteName));
        else if (!site.IsIllustrative)
            throw new InvalidOperationException($"Site {SiteCode} exists and is not illustrative; the illustrative seed never writes into a real site.");
        else
            await Storage.ExecuteSqlAsync<LockRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR UPDATE""", new Dictionary<string, object> { ["code"] = SiteCode }, ct);

        var airport = await Storage.Query<Airport>().FirstOrDefaultAsync(a => a.IataCode == AirportIata, ct)
                      ?? await Save(new Airport(AirportIata, null, AirportName, TimeZoneId));
        var terminal = airport.Terminals.FirstOrDefault(t => !t.IsDeleted && t.Code == TerminalCode)
                       ?? await Save(airport.AddTerminal(TerminalCode, TerminalName, SiteCode));
        if (terminal.SiteCode != SiteCode)
            throw new InvalidOperationException($"Terminal {TerminalCode} of airport {AirportIata} belongs to site {terminal.SiteCode}, not {SiteCode}; the illustrative seed does not write into another site.");

        var levels = new Dictionary<string, Level>(StringComparer.Ordinal);
        var deskIds = new Dictionary<string, IReadOnlyDictionary<int, Guid>>(StringComparer.Ordinal);
        foreach (var hall in Halls)
        {
            var level = terminal.Levels.FirstOrDefault(l => !l.IsDeleted && l.Code == hall.LevelCode)
                        ?? await Save(terminal.AddLevel(hall.LevelCode, hall.LevelName, hall.FloorNumber, hall.WidthMetres, hall.DepthMetres));
            var kind = hall.Arrivals ? CheckpointKind.Immigration : CheckpointKind.Emigration;
            var checkpoint = level.Checkpoints.FirstOrDefault(c => !c.IsDeleted && c.Code == hall.CheckpointCode)
                             ?? await Save(level.AddCheckpoint(hall.CheckpointCode, hall.CheckpointName, kind));
            if (checkpoint.Kind != kind)
                throw new InvalidOperationException($"Checkpoint {hall.CheckpointCode} of {SiteCode} is a {checkpoint.Kind}, not {kind}.");

            async Task<Desk> DeskAsync(string code, DeskKind deskKind, string lane)
            {
                var desk = checkpoint.Desks.FirstOrDefault(d => !d.IsDeleted && d.Code == code);
                if (desk is null)
                    return await Save(checkpoint.AddDesk(code, null, deskKind, [lane]));
                if (desk.Kind != deskKind)
                    throw new InvalidOperationException($"Desk {code} of {SiteCode} is a {desk.Kind}, not a {deskKind}.");
                return desk;
            }

            var ids = new Dictionary<int, Guid>();
            for (var n = 1; n <= DesksPerRow; n++)
                ids[n] = (await DeskAsync(hall.DeskCode(n), DeskKind.Desk, SharedLane)).Id!.Value;
            for (var n = 1; n <= EGatesPerRow; n++)
                await DeskAsync(hall.EGateCode(n), DeskKind.EGate, LaneCategory.EGateEligible);

            // The schematic, once: only while the level never had a plan (a plan someone replaced or deleted stays theirs).
            if (await CountAsync("""SELECT CAST(count(*) AS integer) AS "Value" FROM floor_plan WHERE level_id = :id""", level.Id!.Value, ct) == 0)
                created += await PlanAsync(hall, level, ct);

            levels[hall.LevelCode] = level;
            deskIds[hall.LevelCode] = ids;
        }

        string skipped = null;
        var existing = await Storage.Query<ZoneProfile>().Where(p => p.SiteCode == SiteCode).Select(p => p.Status).ToListAsync(ct);
        if (existing.Count > 0 && existing.All(s => s == ZoneProfileStatus.Draft))
            skipped = $"site {SiteCode} has a zone profile draft and no published version; publish or discard the draft to get the illustrative geometry";
        else if (existing.Count == 0)
        {
            var profile = BuildProfile(levels, deskIds);
            await Save(profile);
            foreach (var zone in profile.Zones.OrderBy(z => z.QueueZone is not null))
                await Save(zone);
            foreach (var line in profile.Lines)
                await Save(line);
            var problems = profile.Publish(ProfileVersion, levels.Values.ToDictionary(l => l.Id!.Value), SystemUserName, timeProvider.GetUtcNow().UtcDateTime);
            if (problems.Count > 0)
                throw new InvalidOperationException("The illustrative zone profile does not validate: " + string.Join("; ", problems));
            await Storage.UpdateAsync(profile, ct);
            await audit.RecordAsync("ZoneProfile.Published", "ZoneProfile", profile.Id, SiteCode, null,
                $"site={profile.SiteCode}; name={profile.Name}; status={profile.Status}; version={profile.Version}; zones={profile.Zones.Count}; lines={profile.Lines.Count}; hash={profile.GeometryHash}", ct);
        }

        // The sensors, once: only while the site never had a device, and only when the published profile has every queue
        // zone they belong to. Commissioning, without credentials (whoever runs them issues one through the devices API).
        if (skipped is null && await CountAsync("""SELECT CAST(count(*) AS integer) AS "Value" FROM device WHERE site_code = :id""", SiteCode, ct) == 0)
        {
            await Storage.FlushAsync(ct);
            var sensors = Sensors();
            var owners = sensors.Select(s => s.QueueZoneName).Distinct(StringComparer.Ordinal).ToList();
            var published = await Storage.Query<Zone>()
                .Where(z => z.Profile.SiteCode == SiteCode && z.Profile.Status == ZoneProfileStatus.Published && z.Kind == ZoneKind.Queue && owners.Contains(z.Name))
                .Select(z => z.Name)
                .ToListAsync(ct);
            if (published.Distinct(StringComparer.Ordinal).Count() == owners.Count)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                foreach (var sensor in sensors)
                    await Save(NewSensor(sensor, levels[sensor.LevelCode], now));
            }
        }

        if (created > 0)
        {
            await audit.RecordAsync("Seed.IllustrativeTopology", "Site", null, SiteCode, null, $"created={created}; illustrative=true", ct);
            unitOfWork.PromiseToCommit();
        }

        return new SeedOutcome(created, skipped);
    }

    /// <summary>
    /// Zone profile v1 over both levels: per hall, the shared queue (lane ALL) with its entry line on the hall side and
    /// its exit line at the booths, its overflow band with an overflow entry line, the e-gates' queue (lane EG) with its
    /// lines, and each desk's service and staff zones hanging off the shared queue and naming the desk. The levels must be
    /// saved (they need ids); <paramref name="deskIds"/> are the desks' ids by level code and desk number.
    /// </summary>
    public static ZoneProfile BuildProfile(IReadOnlyDictionary<string, Level> levels, IReadOnlyDictionary<string, IReadOnlyDictionary<int, Guid>> deskIds)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(deskIds);
        var profile = new ZoneProfile(SiteCode, ProfileName);
        foreach (var hall in Halls)
        {
            if (!levels.TryGetValue(hall.LevelCode, out var level))
                throw new ArgumentException($"Level {hall.LevelCode} is missing.", nameof(levels));
            if (!deskIds.TryGetValue(hall.LevelCode, out var ids))
                throw new ArgumentException($"Level {hall.LevelCode} has no desk ids.", nameof(deskIds));

            var queue = Queue(profile, level, hall.QueueName, hall.QueueRect(), SharedLane);
            var band = hall.OverflowRect();
            var overflow = profile.AddZone(hall.OverflowName, ZoneKind.Overflow, level, band, queue);
            profile.AddLine($"{hall.OverflowName} entry", LineRole.OverflowEntry, level, new FloorPoint(band[0].X + 0.2, band[0].Y),
                new FloorPoint(band[2].X - 0.2, band[0].Y), overflow);
            profile.SetZoneCapacity(overflow, CapacityOf(band));
            Queue(profile, level, hall.EGateQueueName, hall.EGateQueueRect(), LaneCategory.EGateEligible);

            for (var n = 1; n <= DesksPerRow; n++)
            {
                if (!ids.TryGetValue(n, out var deskId))
                    throw new ArgumentException($"Desk {hall.DeskCode(n)} has no desk id.", nameof(deskIds));
                profile.AddZone($"{hall.DeskCode(n)} staff", ZoneKind.Staff, level, hall.StaffRect(n), queue, deskId);
                profile.AddZone($"{hall.DeskCode(n)} service", ZoneKind.Service, level, hall.ServiceRect(n), queue, deskId);
            }
        }

        return profile;
    }

    /// <summary>A queue zone across the flow (passengers walk towards larger y): entry at its top edge, exit at its bottom.</summary>
    private static Zone Queue(ZoneProfile profile, Level level, string name, IReadOnlyList<FloorPoint> rect, string lane)
    {
        var queue = profile.AddZone(name, ZoneKind.Queue, level, rect);
        var (x0, y0, x1, y1) = (rect[0].X, rect[0].Y, rect[2].X, rect[2].Y);
        profile.AddLine($"{name} entry", LineRole.Entry, level, new FloorPoint(x0 + 0.2, y0), new FloorPoint(x1 - 0.2, y0), queue);
        profile.AddLine($"{name} exit", LineRole.Exit, level, new FloorPoint(x0 + 0.2, y1), new FloorPoint(x1 - 0.2, y1), queue);
        profile.SetZoneLane(queue, lane);
        profile.SetZoneCapacity(queue, CapacityOf(rect));
        return queue;
    }

    /// <summary>A sensor of the layout as a device in commissioning, with the BOQ's assumed footprint for its height.</summary>
    public static Device NewSensor(Sensor sensor, Level level, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return new Device(sensor.Code, DeviceFamily.StereoVision, SensorModel, DeviceTransport.HttpsPush, DeviceDialect.Canonical, ClockSource.Ntp, level,
            sensor.X, sensor.Y, MountingHeightMetres, 0, CoverageFootprint.Assumed(DeviceFamily.StereoVision, MountingHeightMetres), sensor.QueueZoneName, utcNow);
    }

    /// <summary>A hall's schematic through the upload pipeline's inspection and sanitising, stored under a generated key.</summary>
    private async Task<int> PlanAsync(Hall hall, Level level, CancellationToken ct)
    {
        var file = FloorPlanFiles.Inspect(NbjBc1Plan.Svg(hall))
                   ?? throw new InvalidOperationException($"The schematic of {hall.LevelCode} does not pass the floor plan inspection.");
        var plan = new FloorPlan(level, $"{Guid.CreateVersion7():N}.{file.Extension}", file.ContentType, file.Bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), NbjBc1Plan.FileName(hall), file.WidthPixels, file.HeightPixels, MetresPerPixel, 0, 0);
        var key = plan.StorageKey;
        unitOfWork.RegisterPostRollbackAction(() => files.DeleteAsync(key));
        await using (var content = new MemoryStream(file.Bytes, writable: false))
            await files.SaveAsync(key, content, ct);
        await Storage.SaveAsync(plan, ct);
        await audit.RecordAsync("FloorPlan.Uploaded", "FloorPlan", plan.Id, hall.LevelCode, null,
            $"type={plan.ContentType}; bytes={plan.SizeBytes}; sha256={plan.Sha256}; metresPerPixel={plan.MetresPerPixel}; origin={plan.OriginX},{plan.OriginY}", ct);
        return 1;
    }

    private async Task<int> CountAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, object id, CancellationToken ct) =>
        (await Storage.ExecuteSqlAsync<LockRow>(sql, new Dictionary<string, object> { ["id"] = id }, ct)).Single().Value;

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
