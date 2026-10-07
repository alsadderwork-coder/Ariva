using System.Data;
using System.Security.Cryptography;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Storage;
using NHibernate.Linq;
using static Ariva.Infra.Services.Seed.AuhTerminalALayout;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The second demo site (ARV-139a): the arrivals immigration hall of Zayed International Airport (AUH) Terminal A, built
/// from public information only and flagged "Illustrative, not surveyed" (<see cref="Site.IsIllustrative"/>). Site
/// AUH-TA, airport AUH, terminal A, the lower arrivals level ARR with the checkpoint IMM: 38 counters IC-01 to IC-38 and
/// 34 smart gates SG-01 to SG-34 (both counts reported), queue zones per lane (A-CRW, A-DIP, A-CIT, A-RES, A-GCC, A-VIS,
/// A-TRF, A-EG) with entry and exit lines, overflow bands A-VIS-OV and A-EG-OV, a staff and a service zone per counter
/// named "IC-01 staff" and "IC-01 service" and linked to its desk (ARV-116, so the desk engine derives the counters'
/// states from sensors alone), the illustrative floor plan (<see cref="AuhTerminalAPlan"/>) through the floor plan
/// pipeline, sensors in commissioning with their coverage, and zone profile version 1, published. What is a public fact
/// and what is an assumption is in <see cref="AuhTerminalALayout"/> and docs/demo/auh-terminal-a.md.
/// <para>
/// The same guard and manners as the DMO seed (<see cref="DemoTopologySeed"/>): only in vm-local, k8s-dev and k8s-demo
/// (DemoSeedExtensions); idempotent (every record is looked up by its code and created only when missing; the profile
/// only when the site has none, the plan only when the level never had one, the sensors only when the site never had
/// any), so a re-run creates, updates and audits nothing; under a transaction-scoped advisory lock and the site's row
/// lock. It never writes into a site AUH-TA that is not illustrative (a real deployment's site of that code), nor into
/// AUH terminal A of another site. Topology only: no users, roles, site grants, credentials or AMAN codes (CWE-269), and
/// no officer, traveller or document identity or real staff or system name (data boundary).
/// </para>
/// </summary>
internal sealed class AuhTerminalASeed(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, AuditTrail audit, IFileStorage files)
    : IDemoTopologySeed
{
    public const string SystemUserName = "demo-seed";
    public const int ProfileVersion = 1;
    public const string ProfileName = "AUH Terminal A arrivals v1 (illustrative)";
    public const string SensorModel = "Overhead stereo counter (illustrative)";

    private const long SeedLock = 0x41524956_00000139;

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
        var level = terminal.Levels.FirstOrDefault(l => !l.IsDeleted && l.Code == LevelCode)
                    ?? await Save(terminal.AddLevel(LevelCode, LevelName, FloorNumber, WidthMetres, DepthMetres));
        var checkpoint = level.Checkpoints.FirstOrDefault(c => !c.IsDeleted && c.Code == CheckpointCode)
                         ?? await Save(level.AddCheckpoint(CheckpointCode, CheckpointName, CheckpointKind.Immigration));
        if (checkpoint.Kind != CheckpointKind.Immigration)
            throw new InvalidOperationException($"Checkpoint {CheckpointCode} of {SiteCode} is a {checkpoint.Kind}, not Immigration.");

        async Task<Desk> DeskAsync(string code, DeskKind kind, string lane)
        {
            var desk = checkpoint.Desks.FirstOrDefault(d => !d.IsDeleted && d.Code == code);
            if (desk is null)
                return await Save(checkpoint.AddDesk(code, null, kind, [lane]));
            if (desk.Kind != kind)
                throw new InvalidOperationException($"Desk {code} of {SiteCode} is a {desk.Kind}, not a {kind}.");
            return desk;
        }

        var counters = new Dictionary<int, Desk>();
        for (var n = 1; n <= Counters; n++)
            counters[n] = await DeskAsync(CounterCode(n), DeskKind.Desk, LaneOfCounter(n).Code);
        for (var n = 1; n <= SmartGates; n++)
            await DeskAsync(SmartGateCode(n), DeskKind.EGate, LaneCategory.EGateEligible);

        // The plan, once: only while the level never had one (a plan someone replaced or deleted stays theirs).
        if (await CountAsync("""SELECT CAST(count(*) AS integer) AS "Value" FROM floor_plan WHERE level_id = :id""", level.Id!.Value, ct) == 0)
            created += await PlanAsync(level, ct);

        string skipped = null;
        var existing = await Storage.Query<ZoneProfile>().Where(p => p.SiteCode == SiteCode).Select(p => p.Status).ToListAsync(ct);
        if (existing.Count > 0 && existing.All(s => s == ZoneProfileStatus.Draft))
            skipped = $"site {SiteCode} has a zone profile draft and no published version; publish or discard the draft to get the illustrative geometry";
        else if (existing.Count == 0)
        {
            var profile = BuildProfile(level, counters.ToDictionary(c => c.Key, c => c.Value.Id!.Value));
            await Save(profile);
            foreach (var zone in profile.Zones.OrderBy(z => z.QueueZone is not null))
                await Save(zone);
            foreach (var line in profile.Lines)
                await Save(line);
            var problems = profile.Publish(ProfileVersion, new Dictionary<Guid, Level> { [level.Id!.Value] = level }, SystemUserName, timeProvider.GetUtcNow().UtcDateTime);
            if (problems.Count > 0)
                throw new InvalidOperationException("The illustrative zone profile does not validate: " + string.Join("; ", problems));
            await Storage.UpdateAsync(profile, ct);
            await audit.RecordAsync("ZoneProfile.Published", "ZoneProfile", profile.Id, SiteCode, null,
                $"site={profile.SiteCode}; name={profile.Name}; status={profile.Status}; version={profile.Version}; zones={profile.Zones.Count}; lines={profile.Lines.Count}; hash={profile.GeometryHash}", ct);
        }

        // The sensors, once: only while the site never had a device (one someone removed does not come back), and only
        // when the published profile has every queue zone they belong to. Commissioning, without credentials: whoever
        // runs them (ARV-139b's scenario, an installer) issues a credential and calibrates them through the devices API.
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
                    await Save(NewSensor(sensor, level, now));
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
    /// Zone profile v1: a queue zone per lane with its entry line on the hall side and its exit line at the counters or
    /// gates, the lane's category (the immigration screen's waits per lane) and an assumed capacity; the two overflow
    /// bands; and each counter's service and staff zones, hanging off its lane's queue and naming its desk. The level must
    /// be saved (it needs an id); <paramref name="counterIds"/> are the counters' desk ids by number.
    /// </summary>
    public static ZoneProfile BuildProfile(Level level, IReadOnlyDictionary<int, Guid> counterIds)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(counterIds);
        var profile = new ZoneProfile(SiteCode, ProfileName);
        var queues = new Dictionary<string, Zone>(StringComparer.Ordinal);
        foreach (var lane in Lanes)
        {
            var rect = QueueRect(lane);
            var name = QueueName(lane.Code);
            var queue = profile.AddZone(name, ZoneKind.Queue, level, rect);
            var (x0, y0, x1, y1) = (rect[0].X, rect[0].Y, rect[2].X, rect[2].Y);
            profile.AddLine($"{name} entry", LineRole.Entry, level, new FloorPoint(x0, y0 + 0.2), new FloorPoint(x0, y1 - 0.2), queue);
            profile.AddLine($"{name} exit", LineRole.Exit, level, new FloorPoint(x1, y0 + 0.2), new FloorPoint(x1, y1 - 0.2), queue);
            profile.SetZoneLane(queue, lane.Code);
            profile.SetZoneCapacity(queue, CapacityOf(rect));
            queues[lane.Code] = queue;

            if (!lane.HasOverflow)
                continue;
            var band = OverflowRect(lane);
            var overflow = profile.AddZone(OverflowName(lane.Code), ZoneKind.Overflow, level, band, queue);
            profile.AddLine($"{OverflowName(lane.Code)} entry", LineRole.OverflowEntry, level, new FloorPoint(band[0].X, band[0].Y + 0.2),
                new FloorPoint(band[0].X, band[2].Y - 0.2), overflow);
            profile.SetZoneCapacity(overflow, CapacityOf(band));
        }

        for (var n = 1; n <= Counters; n++)
        {
            if (!counterIds.TryGetValue(n, out var deskId))
                throw new ArgumentException($"Counter {n} has no desk id.", nameof(counterIds));
            var queue = queues[LaneOfCounter(n).Code];
            profile.AddZone($"{CounterCode(n)} staff", ZoneKind.Staff, level, StaffRect(n), queue, deskId);
            profile.AddZone($"{CounterCode(n)} service", ZoneKind.Service, level, ServiceRect(n), queue, deskId);
        }

        return profile;
    }

    /// <summary>A sensor of the layout as a device in commissioning, with the BOQ's assumed footprint for its height.</summary>
    public static Device NewSensor(Sensor sensor, Level level, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return new Device(sensor.Code, DeviceFamily.StereoVision, SensorModel, DeviceTransport.HttpsPush, DeviceDialect.Canonical, ClockSource.Ntp, level,
            sensor.X, sensor.Y, MountingHeightMetres, 0, CoverageFootprint.Assumed(DeviceFamily.StereoVision, MountingHeightMetres), sensor.QueueZoneName, utcNow);
    }

    /// <summary>The illustrative plan through the upload pipeline's inspection and sanitising, stored under a generated key.</summary>
    private async Task<int> PlanAsync(Level level, CancellationToken ct)
    {
        var file = FloorPlanFiles.Inspect(AuhTerminalAPlan.Svg())
                   ?? throw new InvalidOperationException("The illustrative floor plan does not pass the floor plan inspection.");
        var plan = new FloorPlan(level, $"{Guid.CreateVersion7():N}.{file.Extension}", file.ContentType, file.Bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), AuhTerminalAPlan.FileName, file.WidthPixels, file.HeightPixels, MetresPerPixel, 0, 0);
        var key = plan.StorageKey;
        unitOfWork.RegisterPostRollbackAction(() => files.DeleteAsync(key));
        await using (var content = new MemoryStream(file.Bytes, writable: false))
            await files.SaveAsync(key, content, ct);
        await Storage.SaveAsync(plan, ct);
        await audit.RecordAsync("FloorPlan.Uploaded", "FloorPlan", plan.Id, LevelCode, null,
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
