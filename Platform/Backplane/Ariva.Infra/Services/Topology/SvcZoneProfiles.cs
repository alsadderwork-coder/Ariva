using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Topology;

/// <summary>
/// The zone profile workflow (ARV-017) around the ARV-016 aggregate. The aggregate keeps the geometry rules; this
/// service loads and saves the graph (children are saved and deleted explicitly, NHibernate cascades only merges),
/// scopes everything to the caller's sites (404 outside), serialises drafting and publishing per site with a row lock on
/// the site, audits every change, and lets the unit of work write ZoneProfilePublished to the outbox. Script 0012 backs
/// the rules with unique indexes (one draft and one published version per site) and triggers (published geometry never
/// changes).
/// </summary>
internal sealed class SvcZoneProfiles(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcZoneProfiles
{
    private const string Target = "ZoneProfile";

    /// <summary>The history answers the newest versions; a site publishing daily reaches this in more than half a year.</summary>
    public const int HistoryLimit = 200;

    public async Task<Result<IReadOnlyList<ZoneProfileSummaryViewModel>>> HistoryAsync(string siteCode, CancellationToken ct = default)
    {
        var access = await siteScope.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(siteCode) || !access.Allows(siteCode))
            return Result.Error<IReadOnlyList<ZoneProfileSummaryViewModel>>(TopologyErrors.NotFound);

        // A projection: counts come from SQL, so the history never loads every zone of every version (retired versions
        // stay forever). Newest first, at most HistoryLimit versions.
        var rows = await Query<ZoneProfile>().WithinSites(access).Where(p => p.SiteCode == siteCode)
            .OrderByDescending(p => p.Version == null).ThenByDescending(p => p.Version)
            .Take(HistoryLimit)
            .Select(p => new
            {
                p.Id, p.SiteCode, p.Name, p.Status, p.Version, p.BasedOnVersion, p.GeometryHash, p.CreatedBy, p.CreatedOn,
                p.PublishedBy, p.PublishedOn, p.RetiredOn, Zones = p.Zones.Count(), Lines = p.Lines.Count()
            })
            .ToListAsync(ct);
        IReadOnlyList<ZoneProfileSummaryViewModel> history =
        [
            .. rows.Select(r => new ZoneProfileSummaryViewModel(r.Id.GetValueOrDefault(), r.SiteCode, r.Name, r.Status.ToString(), r.Version, r.BasedOnVersion,
                r.GeometryHash, r.CreatedBy, r.CreatedOn, r.PublishedBy, r.PublishedOn, r.RetiredOn, r.Zones, r.Lines))
        ];
        return new Result<IReadOnlyList<ZoneProfileSummaryViewModel>>(history);
    }

    public async Task<Result<ZoneProfileViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await ProfileAsync(id, ct);
        return profile is null ? Result.Error<ZoneProfileViewModel>(TopologyErrors.NotFound) : new Result<ZoneProfileViewModel>(View(profile));
    }

    public Task<Result<ZoneProfileViewModel>> CreateDraftAsync(CreateZoneProfileDraftRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var access = await siteScope.GetAsync(ct);
            var siteCode = request?.SiteCode;
            if (siteCode is null || !access.Allows(siteCode) || !await Query<Site>().AnyAsync(s => s.Code == siteCode, ct))
                return Result.Error<ZoneProfileViewModel>(TopologyErrors.UnknownSite);

            await LockSiteAsync(siteCode, ct);
            if (await Query<ZoneProfile>().AnyAsync(p => p.SiteCode == siteCode && p.Status == ZoneProfileStatus.Draft, ct))
                return Result.Error<ZoneProfileViewModel>(ZoneProfileErrors.DraftExists);

            var published = await PublishedAsync(siteCode, ct);
            var name = string.IsNullOrWhiteSpace(request.Name) ? published?.Name ?? $"{siteCode} zones" : request.Name;
            var draft = published?.CreateDraft(name) ?? new ZoneProfile(siteCode, name);

            await SaveAsync(draft, ct);
            // Queue zones first: hanging zones reference them.
            foreach (var zone in draft.Zones.OrderBy(z => z.QueueZone is not null))
                await SaveAsync(zone, ct);
            foreach (var line in draft.Lines)
                await SaveAsync(line, ct);

            await AuditAsync("DraftCreated", draft, null, ct);
            return new Result<ZoneProfileViewModel>(View(draft));
        });

    public Task<Result<ZoneProfileViewModel>> RenameAsync(Guid id, RenameZoneProfileRequest request, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            var before = AuditSummary(draft);
            draft.Rename(request?.Name);
            await UpdateAsync(draft, ct);
            await AuditAsync("Renamed", draft, before, ct);
            return new Result<ZoneProfileViewModel>(View(draft));
        });

    public Task<Result<ZoneViewModel>> AddZoneAsync(Guid id, AddZoneRequest request, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            if (request is null || !TryParse(request.Kind, out ZoneKind kind))
                return Result.Error<ZoneViewModel>(TopologyErrors.UnknownKind);
            var level = await LevelAsync(request.LevelId, draft.SiteCode, ct);
            if (level is null)
                return Result.Error<ZoneViewModel>(TopologyErrors.NotFound);
            var polygon = Geometry.ParseRing(request.Polygon, Zone.MaxVertices);
            if (polygon is null)
                return Result.Error<ZoneViewModel>(ZoneProfileErrors.PolygonFormat);
            Zone queueZone = null;
            if (request.QueueZoneId is { } queueZoneId)
            {
                queueZone = draft.Zones.FirstOrDefault(z => z.Id == queueZoneId);
                if (queueZone is null)
                    return Result.Error<ZoneViewModel>(TopologyErrors.NotFound);
            }

            if (request.DeskId is { } deskId && !await Query<Desk>().AnyAsync(d => d.Id == deskId && d.SiteCode == draft.SiteCode, ct))
                return Result.Error<ZoneViewModel>(TopologyErrors.NotFound);

            var zone = draft.AddZone(request.Name, kind, level, polygon, queueZone, request.DeskId);
            draft.SetZoneLane(zone, Lane(request.LaneCategory));
            await SaveAsync(zone, ct);
            await TouchAsync(draft, ct);
            await AuditPartAsync("ZoneAdded", draft, zone.Name, null, Describe(zone), ct);
            return new Result<ZoneViewModel>(View(zone));
        });

    public Task<Result<ZoneViewModel>> UpdateZoneAsync(Guid id, Guid zoneId, UpdateZoneRequest request, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            var zone = draft.Zones.FirstOrDefault(z => z.Id == zoneId);
            if (zone is null)
                return Result.Error<ZoneViewModel>(TopologyErrors.NotFound);
            var polygon = Geometry.ParseRing(request?.Polygon, Zone.MaxVertices);
            if (polygon is null)
                return Result.Error<ZoneViewModel>(ZoneProfileErrors.PolygonFormat);
            var level = await LevelAsync(zone.LevelId, draft.SiteCode, ct);
            if (level is null)
                return Result.Error<ZoneViewModel>(TopologyErrors.NotFound);

            var before = Describe(zone);
            if (!string.Equals(zone.Name, request.Name?.Trim(), StringComparison.Ordinal))
                draft.RenameZone(zone, request.Name);
            draft.MoveZone(zone, level, polygon);
            // An absent lane keeps the zone's lane; an empty one clears it.
            if (request.LaneCategory is not null)
                draft.SetZoneLane(zone, Lane(request.LaneCategory));
            await UpdateAsync(zone, ct);
            await TouchAsync(draft, ct);
            await AuditPartAsync("ZoneChanged", draft, zone.Name, before, Describe(zone), ct);
            return new Result<ZoneViewModel>(View(zone));
        });

    public Task<Result<bool>> RemoveZoneAsync(Guid id, Guid zoneId, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            var zone = draft.Zones.FirstOrDefault(z => z.Id == zoneId);
            if (zone is null)
                return Result.Error<bool>(TopologyErrors.NotFound);

            var before = Describe(zone);
            var lines = draft.RemoveZone(zone);
            foreach (var line in lines)
                await DeleteAsync(line, ct);
            await DeleteAsync(zone, ct);
            await TouchAsync(draft, ct);
            await AuditPartAsync("ZoneRemoved", draft, zone.Name, before + (lines.Count > 0 ? $"; with lines {string.Join(", ", lines.Select(l => l.Name))}" : ""), null, ct);
            return new Result<bool>(true);
        });

    public Task<Result<LineViewModel>> AddLineAsync(Guid id, AddLineRequest request, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            if (request is null || !TryParse(request.Role, out LineRole role))
                return Result.Error<LineViewModel>(TopologyErrors.UnknownKind);
            var level = await LevelAsync(request.LevelId, draft.SiteCode, ct);
            if (level is null)
                return Result.Error<LineViewModel>(TopologyErrors.NotFound);
            Zone zone = null;
            if (request.ZoneId is { } zoneId)
            {
                zone = draft.Zones.FirstOrDefault(z => z.Id == zoneId);
                if (zone is null)
                    return Result.Error<LineViewModel>(TopologyErrors.NotFound);
            }

            var line = draft.AddLine(request.Name, role, level, new FloorPoint(request.StartX, request.StartY), new FloorPoint(request.EndX, request.EndY), zone);
            await SaveAsync(line, ct);
            await TouchAsync(draft, ct);
            await AuditPartAsync("LineAdded", draft, line.Name, null, Describe(line), ct);
            return new Result<LineViewModel>(View(line));
        });

    public Task<Result<bool>> RemoveLineAsync(Guid id, Guid lineId, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            var line = draft.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null)
                return Result.Error<bool>(TopologyErrors.NotFound);

            var before = Describe(line);
            draft.RemoveLine(line);
            await DeleteAsync(line, ct);
            await TouchAsync(draft, ct);
            await AuditPartAsync("LineRemoved", draft, line.Name, before, null, ct);
            return new Result<bool>(true);
        });

    public async Task<Result<ZoneProfileValidationViewModel>> ValidateAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await ProfileAsync(id, ct);
        if (profile is null)
            return Result.Error<ZoneProfileValidationViewModel>(TopologyErrors.NotFound);
        if (!profile.IsDraft)
            return Result.Error<ZoneProfileValidationViewModel>(ZoneProfileErrors.NotADraft);
        var problems = profile.Validate(await LevelsAsync(profile.SiteCode, ct));
        return new Result<ZoneProfileValidationViewModel>(new ZoneProfileValidationViewModel(problems.Count == 0, problems, profile.ComputeGeometryHash()));
    }

    public Task<Result<ZoneProfileSummaryViewModel>> PublishAsync(Guid id, PublishZoneProfileRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            // Lock the profile, then its site, before reading anything: edits wait (they lock the profile too) and a
            // second publish of the site waits, so what is validated and hashed is exactly what is stored.
            var siteCode = await LockProfileAsync(id, ct);
            if (siteCode is null)
                return Result.Error<ZoneProfileSummaryViewModel>(TopologyErrors.NotFound);
            await LockSiteAsync(siteCode, ct);
            var draft = await ProfileAsync(id, ct);
            if (draft is null)
                return Result.Error<ZoneProfileSummaryViewModel>(TopologyErrors.NotFound);
            if (!draft.IsDraft)
                return Result.Error<ZoneProfileSummaryViewModel>(ZoneProfileErrors.NotADraft);

            var levels = await LevelsAsync(draft.SiteCode, ct);
            var problems = draft.Validate(levels);
            if (problems.Count > 0)
                return Result.Error<ZoneProfileSummaryViewModel>(problems.Prepend(ZoneProfileErrors.NotPublishable).ToList());
            // The publisher signs off on the geometry they reviewed: the hash from the validation they saw.
            if (!string.Equals(request?.GeometryHash, draft.ComputeGeometryHash(), StringComparison.Ordinal))
                return Result.Error<ZoneProfileSummaryViewModel>(ZoneProfileErrors.ChangedSinceReview);

            var now = UtcNow;
            var version = (await Query<ZoneProfile>().Where(p => p.SiteCode == draft.SiteCode).Select(p => p.Version).MaxAsync(ct) ?? 0) + 1;
            var previous = await PublishedAsync(draft.SiteCode, ct);
            if (previous is not null)
            {
                var retiredBefore = AuditSummary(previous);
                previous.Retire(now);
                await UpdateAsync(previous, ct);
                // The retirement reaches the database before the draft becomes the site's published version (one
                // published version per site is a unique index).
                await FlushAsync(ct);
                await AuditAsync("Retired", previous, retiredBefore, ct);
            }

            var before = AuditSummary(draft);
            var refused = draft.Publish(version, levels, CurrentUser.UserName, now, previous?.Version);
            if (refused.Count > 0)
                return Result.Error<ZoneProfileSummaryViewModel>(refused.Prepend(ZoneProfileErrors.NotPublishable).ToList());
            await UpdateAsync(draft, ct);
            await AuditAsync("Published", draft, before, ct);
            return new Result<ZoneProfileSummaryViewModel>(Summary(draft));
        });

    public Task<Result<bool>> DiscardAsync(Guid id, CancellationToken ct = default) =>
        DraftAsync(id, ct, async draft =>
        {
            var before = AuditSummary(draft);
            foreach (var line in draft.Lines.ToList())
                await DeleteAsync(line, ct);
            // Hanging zones before the queue zones they reference.
            foreach (var zone in draft.Zones.OrderBy(z => z.QueueZone is null).ToList())
                await DeleteAsync(zone, ct);
            await DeleteAsync(draft, ct);
            await audit.RecordAsync($"{Target}.Discarded", Target, draft.Id, draft.SiteCode, before, null, ct);
            return new Result<bool>(true);
        });

    #region Helpers

    /// <summary>Runs <paramref name="work"/> on a draft of the caller's sites; NotFound or NotADraft otherwise.</summary>
    private Task<Result<T>> DraftAsync<T>(Guid id, CancellationToken ct, Func<ZoneProfile, Task<Result<T>>> work) =>
        GuardedAsync(async () =>
        {
            // Edits of one profile run one at a time and never alongside its publishing (row lock before the load).
            if (await LockProfileAsync(id, ct) is null)
                return Result.Error<T>(TopologyErrors.NotFound);
            var profile = await ProfileAsync(id, ct);
            if (profile is null)
                return Result.Error<T>(TopologyErrors.NotFound);
            return profile.IsDraft ? await work(profile) : Result.Error<T>(ZoneProfileErrors.NotADraft);
        });

    /// <summary>The aggregate's rule messages become errors; nothing half-done is committed.</summary>
    private async Task<Result<T>> GuardedAsync<T>(Func<Task<Result<T>>> work)
    {
        try
        {
            var result = await work();
            if (result.HasErrors)
                UnitOfWork.PromiseNotToCommit();
            return result;
        }
        // Only the domain's rule messages are shown (CWE-209): an exception thrown by the framework or the driver keeps its
        // text to the logs and becomes a 500 without details.
        catch (ArgumentException e) when (FromDomain(e))
        {
            UnitOfWork.PromiseNotToCommit();
            return Result.Error<T>(e.Message.Split(" (Parameter", 2)[0]);
        }
        catch (InvalidOperationException e) when (FromDomain(e))
        {
            UnitOfWork.PromiseNotToCommit();
            return Result.Error<T>(e.Message == ZoneProfileErrors.NotADraft ? ZoneProfileErrors.NotADraft : e.Message);
        }
    }

    private static bool FromDomain(Exception e) =>
        e.TargetSite?.DeclaringType?.Assembly == typeof(ZoneProfile).Assembly ||
        e.TargetSite?.DeclaringType?.Assembly == typeof(Geometry).Assembly;

    /// <summary>Locks the profile row for this transaction and returns its site, or null when there is no such profile.</summary>
    private async Task<string> LockProfileAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        var rows = await ExecuteCommandAsync<SiteRow>("""SELECT site_code AS "Value" FROM zone_profile WHERE id = :id FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = id }, ct);
        return rows.Count == 1 ? rows[0].Value : null;
    }

    private async Task<ZoneProfile> ProfileAsync(Guid id, CancellationToken ct)
    {
        var profile = id == Guid.Empty ? null : await GetAsync<ZoneProfile>(id, ct);
        return profile is not null && (await siteScope.GetAsync(ct)).Allows(profile.SiteCode) ? profile : null;
    }

    private Task<ZoneProfile> PublishedAsync(string siteCode, CancellationToken ct) =>
        Query<ZoneProfile>().FirstOrDefaultAsync(p => p.SiteCode == siteCode && p.Status == ZoneProfileStatus.Published, ct);

    private async Task<Level> LevelAsync(Guid? levelId, string siteCode, CancellationToken ct)
    {
        var level = levelId is { } id && id != Guid.Empty ? await GetAsync<Level>(id, ct) : null;
        return level is null || level.IsDeleted || level.SiteCode != siteCode ? null : level;
    }

    private async Task<IReadOnlyDictionary<Guid, Level>> LevelsAsync(string siteCode, CancellationToken ct) =>
        (await Query<Level>().Where(l => l.SiteCode == siteCode).ToListAsync(ct)).ToDictionary(l => l.Id.GetValueOrDefault());

    /// <summary>Drafting and publishing for one site happen one at a time.</summary>
    private Task LockSiteAsync(string siteCode, CancellationToken ct) =>
        ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR UPDATE""",
            new Dictionary<string, object> { ["code"] = siteCode }, ct);

    /// <summary>Stamps the draft's modification (audit fields) when only its zones or lines changed.</summary>
    private Task TouchAsync(ZoneProfile draft, CancellationToken ct) => UpdateAsync(draft, ct);

    private static bool TryParse<TEnum>(string value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        return !string.IsNullOrEmpty(value) && char.IsLetter(value[0]) && Enum.TryParse(value, ignoreCase: false, out result) && Enum.IsDefined(result);
    }

    private Task AuditAsync(string verb, ZoneProfile profile, string before, CancellationToken ct, string detail = null) =>
        audit.RecordAsync($"{Target}.{verb}", Target, profile.Id, detail is null ? profile.SiteCode : $"{profile.SiteCode}/{detail}", before, AuditSummary(profile), ct);

    /// <summary>A zone or line change: what the part looked like before and after, so a version's geometry can be traced to its edits.</summary>
    private Task AuditPartAsync(string verb, ZoneProfile profile, string part, string before, string after, CancellationToken ct) =>
        audit.RecordAsync($"{Target}.{verb}", Target, profile.Id, $"{profile.SiteCode}/{part}", before, after, ct);

    /// <summary>An empty lane is no lane; a lane is given in capitals (the entity checks its shape).</summary>
    private static string Lane(string laneCategory) => string.IsNullOrWhiteSpace(laneCategory) ? null : laneCategory.Trim();

    private static string Describe(Zone z) =>
        $"zone={z.Name}; kind={z.Kind}; level={z.LevelId}; queueZone={z.QueueZone?.Name}; desk={z.DeskId}; lane={z.LaneCategory}; points={z.Points.Count}; " +
        $"polygonSha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(z.Polygon ?? string.Empty)))}";

    private static string Describe(Line l) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"line={l.Name}; role={l.Role}; zone={l.Zone?.Name}; level={l.LevelId}; start={l.StartX:0.000} {l.StartY:0.000}; end={l.EndX:0.000} {l.EndY:0.000}");

    private static string AuditSummary(ZoneProfile p) =>
        $"site={p.SiteCode}; name={p.Name}; status={p.Status}; version={p.Version}; zones={p.Zones.Count}; lines={p.Lines.Count}; hash={p.GeometryHash}";

    private static ZoneProfileSummaryViewModel Summary(ZoneProfile p) => new(
        p.Id.GetValueOrDefault(), p.SiteCode, p.Name, p.Status.ToString(), p.Version, p.BasedOnVersion, p.GeometryHash,
        p.CreatedBy, p.CreatedOn, p.PublishedBy, p.PublishedOn, p.RetiredOn, p.Zones.Count, p.Lines.Count);

    private static ZoneProfileViewModel View(ZoneProfile p) =>
        new(Summary(p), [.. p.Zones.OrderBy(z => z.Name, StringComparer.Ordinal).Select(View)], [.. p.Lines.OrderBy(l => l.Name, StringComparer.Ordinal).Select(View)]);

    private static ZoneViewModel View(Zone z) =>
        new(z.Id.GetValueOrDefault(), z.Name, z.Kind.ToString(), z.LevelId, z.QueueZone?.Id, z.DeskId, z.Polygon, Math.Round(z.AreaSquareMetres, 3), z.LaneCategory);

    private static LineViewModel View(Line l) =>
        new(l.Id.GetValueOrDefault(), l.Name, l.Role.ToString(), l.Zone?.Id, l.LevelId, l.StartX, l.StartY, l.EndX, l.EndY, Math.Round(l.LengthMetres, 3));

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    private sealed class SiteRow
    {
        public string Value { get; set; }
    }

    #endregion
}
