using System.Linq.Expressions;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Topology;

/// <summary>
/// Topology administration (ARV-014). Every record below the airport carries its site code: searches are filtered to
/// the caller's sites and a lookup outside them answers NotFound, exactly like an unknown id (CWE-863). Airports are
/// deployment-wide reference data, readable by every topology reader and changed only by an all-sites administrator.
/// Entity invariants (ARV-013) are the validation; their messages become 400 answers. Every change is audited in the
/// same transaction, and single-record reads are cached under the "topology" tag, evicted after every change.
/// </summary>
internal sealed class SvcTopology(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    IFusionCache cache) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcTopology
{
    public const string CacheTag = "topology";

    #region Airports

    public async Task<Result<PageViewModel<AirportViewModel>>> SearchAirportsAsync(TopologyCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new TopologyCriteria();
        if (!ValidSort(criteria))
            return Result.Error<PageViewModel<AirportViewModel>>(TopologyErrors.InvalidCriteria);

        var query = QueryAsNoTracking<Airport>();
        if (Text(criteria) is { } text)
            query = query.Where(a => a.IataCode.Contains(text.Upper) || a.Name.ToLower().Contains(text.Lower));
        return await PageAsync(query, criteria, a => a.IataCode, a => a.Name, a => a.CreatedOn, ToView, ct);
    }

    public async Task<Result<AirportViewModel>> GetAirportAsync(Guid id, CancellationToken ct = default) =>
        await CachedAsync($"airport:{id:N}", async token => await LiveAsync<Airport>(id, token) is { } airport ? ToView(airport) : null, null, ct) is { } view
            ? new Result<AirportViewModel>(view)
            : Result.Error<AirportViewModel>(TopologyErrors.NotFound);

    public Task<Result<AirportViewModel>> CreateAirportAsync(CreateAirportRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!(await siteScope.GetAsync(ct)).AllSites)
                return Result.Error<AirportViewModel>(TopologyErrors.DeploymentWide);
            if (await Query<Airport>().AnyAsync(a => a.IataCode == request.IataCode, ct))
                return Result.Error<AirportViewModel>(TopologyErrors.Duplicate);

            var airport = new Airport(request.IataCode, request.IcaoCode, request.Name, request.TimeZoneId);
            await SaveAsync(airport, ct);
            await AuditAsync("Airport", "Created", airport.Id, airport.IataCode, null, Summary(airport), ct);
            return new Result<AirportViewModel>(ToView(airport));
        });

    public Task<Result<AirportViewModel>> UpdateAirportAsync(Guid id, UpdateAirportRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!(await siteScope.GetAsync(ct)).AllSites)
                return Result.Error<AirportViewModel>(TopologyErrors.DeploymentWide);
            var airport = await LiveAsync<Airport>(id, ct);
            if (airport is null)
                return Result.Error<AirportViewModel>(TopologyErrors.NotFound);

            var before = Summary(airport);
            airport.Update(request.IcaoCode, request.Name, request.TimeZoneId);
            await UpdateAsync(airport, ct);
            await AuditAsync("Airport", "Updated", airport.Id, airport.IataCode, before, Summary(airport), ct);
            return new Result<AirportViewModel>(ToView(airport));
        });

    public Task<Result<bool>> DeleteAirportAsync(Guid id, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!(await siteScope.GetAsync(ct)).AllSites)
                return Result.Error<bool>(TopologyErrors.DeploymentWide);
            var airport = await LiveAsync<Airport>(id, ct);
            if (airport is null)
                return Result.Error<bool>(TopologyErrors.NotFound);
            if (await Query<Terminal>().AnyAsync(t => t.Airport.Id == id, ct))
                return Result.Error<bool>(TopologyErrors.HasChildren);

            return await SoftDeleteAsync(airport, "Airport", airport.IataCode, Summary(airport), ct);
        });

    #endregion

    #region Terminals

    public async Task<Result<PageViewModel<TerminalViewModel>>> SearchTerminalsAsync(TopologyCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new TopologyCriteria();
        if (!ValidSort(criteria))
            return Result.Error<PageViewModel<TerminalViewModel>>(TopologyErrors.InvalidCriteria);

        var query = (await ScopedAsync(QueryAsNoTracking<Terminal>(), criteria, ct));
        if (criteria.ParentId is { } airportId)
            query = query.Where(t => t.Airport.Id == airportId);
        if (Text(criteria) is { } text)
            query = query.Where(t => t.Code.Contains(text.Upper) || t.Name.ToLower().Contains(text.Lower));
        return await PageAsync(query, criteria, t => t.Code, t => t.Name, t => t.CreatedOn, ToView, ct);
    }

    public Task<Result<TerminalViewModel>> GetTerminalAsync(Guid id, CancellationToken ct = default) =>
        GetScopedAsync<Terminal, TerminalViewModel>(id, "terminal", ToView, v => v.SiteCode, ct);

    public Task<Result<TerminalViewModel>> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var access = await siteScope.GetAsync(ct);
            if (!access.Allows(request.SiteCode) || !await Query<Site>().AnyAsync(s => s.Code == request.SiteCode, ct))
                return Result.Error<TerminalViewModel>(TopologyErrors.UnknownSite);
            var airport = await LiveAsync<Airport>(request.AirportId.GetValueOrDefault(), ct);
            if (airport is null)
                return Result.Error<TerminalViewModel>(TopologyErrors.NotFound);

            var terminal = airport.AddTerminal(request.Code, request.Name, request.SiteCode);
            await SaveAsync(terminal, ct);
            await AuditAsync("Terminal", "Created", terminal.Id, Path(terminal), null, Summary(terminal), ct);
            return new Result<TerminalViewModel>(ToView(terminal));
        });

    public Task<Result<TerminalViewModel>> UpdateTerminalAsync(Guid id, UpdateTerminalRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var terminal = await ScopedLiveAsync<Terminal>(id, ct);
            if (terminal is null)
                return Result.Error<TerminalViewModel>(TopologyErrors.NotFound);

            var before = Summary(terminal);
            terminal.Rename(request.Name);
            await UpdateAsync(terminal, ct);
            await AuditAsync("Terminal", "Updated", terminal.Id, Path(terminal), before, Summary(terminal), ct);
            return new Result<TerminalViewModel>(ToView(terminal));
        });

    public Task<Result<bool>> DeleteTerminalAsync(Guid id, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var terminal = await ScopedLiveAsync<Terminal>(id, ct);
            if (terminal is null)
                return Result.Error<bool>(TopologyErrors.NotFound);
            if (await Query<Level>().AnyAsync(l => l.Terminal.Id == id, ct))
                return Result.Error<bool>(TopologyErrors.HasChildren);
            return await SoftDeleteAsync(terminal, "Terminal", Path(terminal), Summary(terminal), ct);
        });

    #endregion

    #region Levels

    public async Task<Result<PageViewModel<LevelViewModel>>> SearchLevelsAsync(TopologyCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new TopologyCriteria();
        if (!ValidSort(criteria))
            return Result.Error<PageViewModel<LevelViewModel>>(TopologyErrors.InvalidCriteria);

        var query = await ScopedAsync(QueryAsNoTracking<Level>(), criteria, ct);
        if (criteria.ParentId is { } terminalId)
            query = query.Where(l => l.Terminal.Id == terminalId);
        if (Text(criteria) is { } text)
            query = query.Where(l => l.Code.Contains(text.Upper) || l.Name.ToLower().Contains(text.Lower));
        return await PageAsync(query, criteria, l => l.Code, l => l.Name, l => l.CreatedOn, ToView, ct);
    }

    public Task<Result<LevelViewModel>> GetLevelAsync(Guid id, CancellationToken ct = default) =>
        GetScopedAsync<Level, LevelViewModel>(id, "level", ToView, v => v.SiteCode, ct);

    public Task<Result<LevelViewModel>> CreateLevelAsync(CreateLevelRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var terminal = await ScopedLiveAsync<Terminal>(request.TerminalId.GetValueOrDefault(), ct);
            if (terminal is null)
                return Result.Error<LevelViewModel>(TopologyErrors.NotFound);

            var level = terminal.AddLevel(request.Code, request.Name, request.FloorNumber, request.WidthMetres, request.DepthMetres);
            await SaveAsync(level, ct);
            await AuditAsync("Level", "Created", level.Id, Path(level), null, Summary(level), ct);
            return new Result<LevelViewModel>(ToView(level));
        });

    public Task<Result<LevelViewModel>> UpdateLevelAsync(Guid id, UpdateLevelRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var level = await ScopedLiveAsync<Level>(id, ct);
            if (level is null)
                return Result.Error<LevelViewModel>(TopologyErrors.NotFound);
            if (request.FloorNumber != level.FloorNumber &&
                await Query<Level>().AnyAsync(l => l.Terminal.Id == level.Terminal.Id && l.Id != id && l.FloorNumber == request.FloorNumber, ct))
            {
                return Result.Error<LevelViewModel>(TopologyErrors.Duplicate);
            }

            var before = Summary(level);
            level.Update(request.Name, request.FloorNumber, request.WidthMetres, request.DepthMetres);
            await UpdateAsync(level, ct);
            await AuditAsync("Level", "Updated", level.Id, Path(level), before, Summary(level), ct);
            return new Result<LevelViewModel>(ToView(level));
        });

    public Task<Result<bool>> DeleteLevelAsync(Guid id, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var level = await ScopedLiveAsync<Level>(id, ct);
            if (level is null)
                return Result.Error<bool>(TopologyErrors.NotFound);
            // A live floor plan counts as a child: deleting the level would orphan the plan and its file (ARV-018).
            if (await Query<Checkpoint>().AnyAsync(c => c.Level.Id == id, ct) || await Query<FloorPlan>().AnyAsync(p => p.Level.Id == id, ct))
                return Result.Error<bool>(TopologyErrors.HasChildren);
            return await SoftDeleteAsync(level, "Level", Path(level), Summary(level), ct);
        });

    #endregion

    #region Checkpoints

    public async Task<Result<PageViewModel<CheckpointViewModel>>> SearchCheckpointsAsync(TopologyCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new TopologyCriteria();
        if (!ValidSort(criteria))
            return Result.Error<PageViewModel<CheckpointViewModel>>(TopologyErrors.InvalidCriteria);

        var query = await ScopedAsync(QueryAsNoTracking<Checkpoint>(), criteria, ct);
        if (criteria.ParentId is { } levelId)
            query = query.Where(c => c.Level.Id == levelId);
        if (Text(criteria) is { } text)
            query = query.Where(c => c.Code.Contains(text.Upper) || c.Name.ToLower().Contains(text.Lower));
        return await PageAsync(query, criteria, c => c.Code, c => c.Name, c => c.CreatedOn, ToView, ct);
    }

    public Task<Result<CheckpointViewModel>> GetCheckpointAsync(Guid id, CancellationToken ct = default) =>
        GetScopedAsync<Checkpoint, CheckpointViewModel>(id, "checkpoint", ToView, v => v.SiteCode, ct);

    public Task<Result<CheckpointViewModel>> CreateCheckpointAsync(CreateCheckpointRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!TryKind<CheckpointKind>(request.Kind, out var kind))
                return Result.Error<CheckpointViewModel>(TopologyErrors.UnknownKind);
            var level = await ScopedLiveAsync<Level>(request.LevelId.GetValueOrDefault(), ct);
            if (level is null)
                return Result.Error<CheckpointViewModel>(TopologyErrors.NotFound);

            var checkpoint = level.AddCheckpoint(request.Code, request.Name, kind);
            await SaveAsync(checkpoint, ct);
            await AuditAsync("Checkpoint", "Created", checkpoint.Id, Path(checkpoint), null, Summary(checkpoint), ct);
            return new Result<CheckpointViewModel>(ToView(checkpoint));
        });

    public Task<Result<CheckpointViewModel>> UpdateCheckpointAsync(Guid id, UpdateCheckpointRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var checkpoint = await ScopedLiveAsync<Checkpoint>(id, ct);
            if (checkpoint is null)
                return Result.Error<CheckpointViewModel>(TopologyErrors.NotFound);

            var before = Summary(checkpoint);
            checkpoint.Rename(request.Name);
            await UpdateAsync(checkpoint, ct);
            await AuditAsync("Checkpoint", "Updated", checkpoint.Id, Path(checkpoint), before, Summary(checkpoint), ct);
            return new Result<CheckpointViewModel>(ToView(checkpoint));
        });

    public Task<Result<bool>> DeleteCheckpointAsync(Guid id, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var checkpoint = await ScopedLiveAsync<Checkpoint>(id, ct);
            if (checkpoint is null)
                return Result.Error<bool>(TopologyErrors.NotFound);
            if (await Query<Desk>().AnyAsync(d => d.Checkpoint.Id == id, ct))
                return Result.Error<bool>(TopologyErrors.HasChildren);
            return await SoftDeleteAsync(checkpoint, "Checkpoint", Path(checkpoint), Summary(checkpoint), ct);
        });

    #endregion

    #region Desks

    public async Task<Result<PageViewModel<DeskViewModel>>> SearchDesksAsync(TopologyCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new TopologyCriteria();
        if (!ValidSort(criteria))
            return Result.Error<PageViewModel<DeskViewModel>>(TopologyErrors.InvalidCriteria);

        var query = await ScopedAsync(QueryAsNoTracking<Desk>(), criteria, ct);
        if (criteria.ParentId is { } checkpointId)
            query = query.Where(d => d.Checkpoint.Id == checkpointId);
        if (Text(criteria) is { } text)
            query = query.Where(d => d.Code.Contains(text.Upper) || d.Name.ToLower().Contains(text.Lower));
        return await PageAsync(query, criteria, d => d.Code, d => d.Name, d => d.CreatedOn, ToView, ct);
    }

    public Task<Result<DeskViewModel>> GetDeskAsync(Guid id, CancellationToken ct = default) =>
        GetScopedAsync<Desk, DeskViewModel>(id, "desk", ToView, v => v.SiteCode, ct);

    public Task<Result<DeskViewModel>> CreateDeskAsync(CreateDeskRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!TryKind<DeskKind>(request.Kind, out var kind))
                return Result.Error<DeskViewModel>(TopologyErrors.UnknownKind);
            var checkpoint = await ScopedLiveAsync<Checkpoint>(request.CheckpointId.GetValueOrDefault(), ct);
            if (checkpoint is null)
                return Result.Error<DeskViewModel>(TopologyErrors.NotFound);

            var desk = checkpoint.AddDesk(request.Code, request.Name, kind, request.LaneCategories);
            await SaveAsync(desk, ct);
            await AuditAsync("Desk", "Created", desk.Id, Path(desk), null, Summary(desk), ct);
            return new Result<DeskViewModel>(ToView(desk));
        });

    public Task<Result<IReadOnlyList<DeskViewModel>>> CreateDeskRangeAsync(CreateDeskRangeRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!TryKind<DeskKind>(request.Kind, out var kind))
                return Result.Error<IReadOnlyList<DeskViewModel>>(TopologyErrors.UnknownKind);
            var checkpoint = await ScopedLiveAsync<Checkpoint>(request.CheckpointId.GetValueOrDefault(), ct);
            if (checkpoint is null)
                return Result.Error<IReadOnlyList<DeskViewModel>>(TopologyErrors.NotFound);

            var desks = checkpoint.AddDeskRange(request.Prefix, request.From, request.To, request.Width, kind, request.LaneCategories);
            foreach (var desk in desks)
                await SaveAsync(desk, ct);
            await AuditAsync("Desk", "RangeCreated", checkpoint.Id, Path(checkpoint), null,
                $"kind={kind}; codes={desks[0].Code}..{desks[^1].Code}; count={desks.Count}; lanes={desks[0].LaneCategoryCodes}", ct);
            return new Result<IReadOnlyList<DeskViewModel>>(desks.Select(ToView).ToList());
        });

    public Task<Result<DeskViewModel>> UpdateDeskAsync(Guid id, UpdateDeskRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var desk = await ScopedLiveAsync<Desk>(id, ct);
            if (desk is null)
                return Result.Error<DeskViewModel>(TopologyErrors.NotFound);

            var before = Summary(desk);
            desk.Rename(request.Name);
            desk.SetLaneCategories(request.LaneCategories);
            desk.SetInService(request.InService);
            await UpdateAsync(desk, ct);
            await AuditAsync("Desk", "Updated", desk.Id, Path(desk), before, Summary(desk), ct);
            return new Result<DeskViewModel>(ToView(desk));
        });

    public Task<Result<bool>> DeleteDeskAsync(Guid id, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            var desk = await ScopedLiveAsync<Desk>(id, ct);
            return desk is null
                ? Result.Error<bool>(TopologyErrors.NotFound)
                : await SoftDeleteAsync(desk, "Desk", Path(desk), Summary(desk), ct);
        });

    #endregion

    #region Helpers

    /// <summary>Domain rule violations (ARV-013) become 400 answers with the entity's message; duplicates become 409.</summary>
    private static async Task<Result<T>> GuardedAsync<T>(Func<Task<Result<T>>> work)
    {
        try
        {
            return await work();
        }
        catch (InvalidOperationException e) when (e.Message.Contains("already exists", StringComparison.Ordinal))
        {
            return Result.Error<T>(TopologyErrors.Duplicate);
        }
        catch (ArgumentException e)
        {
            return Result.Error<T>(e.Message.Split(" (Parameter", 2)[0]);
        }
        catch (InvalidOperationException e)
        {
            return Result.Error<T>(e.Message);
        }
    }

    /// <summary>The live entity (Get ignores the soft delete filter, so deleted rows are checked here).</summary>
    private async Task<T> LiveAsync<T>(Guid id, CancellationToken ct) where T : class, IDomain, ISoftDeletable
    {
        if (id == Guid.Empty)
            return null;
        var entity = await GetAsync<T>(id, ct);
        return entity is null || entity.IsDeleted ? null : entity;
    }

    /// <summary>The live entity when it lies within the caller's sites; null otherwise, answered as not found.</summary>
    private async Task<T> ScopedLiveAsync<T>(Guid id, CancellationToken ct) where T : class, IDomain, ISoftDeletable, ISiteBound
    {
        var entity = await LiveAsync<T>(id, ct);
        return entity is not null && (await siteScope.GetAsync(ct)).Allows(entity.SiteCode) ? entity : null;
    }

    private async Task<Result<TView>> GetScopedAsync<T, TView>(Guid id, string kind, Func<T, TView> view, Func<TView, string> siteOf, CancellationToken ct)
        where T : class, IDomain, ISoftDeletable, ISiteBound
    {
        var cached = await CachedAsync($"{kind}:{id:N}", async token => await LiveAsync<T>(id, token) is { } entity ? view(entity) : default, siteOf, ct);
        return cached is not null ? new Result<TView>(cached) : Result.Error<TView>(TopologyErrors.NotFound);
    }

    /// <summary>A cached single-record read; the site check runs on every call, after the cache.</summary>
    private async Task<TView> CachedAsync<TView>(string key, Func<CancellationToken, Task<TView>> load, Func<TView, string> siteOf, CancellationToken ct)
    {
        var view = await cache.GetOrSetAsync<TView>("topology:" + key, async (_, token) => await load(token),
            options => options.SetDuration(TimeSpan.FromMinutes(5)), tags: [CacheTag], token: ct);
        if (view is null || siteOf is null)
            return view;
        return (await siteScope.GetAsync(ct)).Allows(siteOf(view)) ? view : default;
    }

    private async Task<IQueryable<T>> ScopedAsync<T>(IQueryable<T> query, TopologyCriteria criteria, CancellationToken ct) where T : ISiteBound
    {
        var scoped = query.WithinSites(await siteScope.GetAsync(ct));
        if (!string.IsNullOrWhiteSpace(criteria.SiteCode))
            scoped = scoped.Where(x => x.SiteCode == criteria.SiteCode);
        return scoped;
    }

    private async Task<Result<PageViewModel<TView>>> PageAsync<T, TView>(
        IQueryable<T> query,
        TopologyCriteria criteria,
        Expression<Func<T, string>> code,
        Expression<Func<T, string>> name,
        Expression<Func<T, DateTime?>> createdOn,
        Func<T, TView> view,
        CancellationToken ct)
    {
        var ordered = (criteria.SortBy?.ToLowerInvariant(), criteria.SortDescending) switch
        {
            ("name", false) => query.OrderBy(name).ThenBy(code),
            ("name", true) => query.OrderByDescending(name).ThenBy(code),
            ("createdon", false) => query.OrderBy(createdOn).ThenBy(code),
            ("createdon", true) => query.OrderByDescending(createdOn).ThenBy(code),
            (_, true) => query.OrderByDescending(code),
            _ => query.OrderBy(code)
        };

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var rows = await ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<TView>>(new PageViewModel<TView>(rows.Select(view).ToList(), total, pageIndex, pageSize));
    }

    private static bool ValidSort(TopologyCriteria criteria) =>
        criteria.SortBy is null || TopologyCriteria.SortFields.Any(f => string.Equals(f, criteria.SortBy, StringComparison.OrdinalIgnoreCase));

    private static (string Upper, string Lower)? Text(TopologyCriteria criteria) =>
        string.IsNullOrWhiteSpace(criteria.Text) ? null : (criteria.Text.Trim().ToUpperInvariant(), criteria.Text.Trim().ToLowerInvariant());

    /// <summary>Kinds by name only; numbers and unknown names are refused.</summary>
    private static bool TryKind<TEnum>(string value, out TEnum kind) where TEnum : struct, Enum
    {
        kind = default;
        return !string.IsNullOrEmpty(value) && char.IsLetter(value[0]) && Enum.TryParse(value, ignoreCase: false, out kind) && Enum.IsDefined(kind);
    }

    private async Task<Result<bool>> SoftDeleteAsync<T>(T entity, string type, string name, string summary, CancellationToken ct) where T : BaseSoftDeletableEntity<T>
    {
        entity.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(entity, ct);
        await AuditAsync(type, "Deleted", entity.Id, name, summary, null, ct);
        return new Result<bool>(true);
    }

    private async Task AuditAsync(string type, string verb, Guid? id, string name, string before, string after, CancellationToken ct)
    {
        await audit.RecordAsync($"{type}.{verb}", type, id, name, before, after, ct);
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(CacheTag).AsTask());
    }

    private static string Path(Terminal t) => $"{t.Airport.IataCode}/{t.Code}";
    private static string Path(Level l) => $"{Path(l.Terminal)}/{l.Code}";
    private static string Path(Checkpoint c) => $"{Path(c.Level)}/{c.Code}";
    private static string Path(Desk d) => $"{Path(d.Checkpoint)}/{d.Code}";

    private static string Summary(Airport a) => $"iata={a.IataCode}; icao={a.IcaoCode}; name={a.Name}; timeZone={a.TimeZoneId}";
    private static string Summary(Terminal t) => $"code={t.Code}; name={t.Name}; site={t.SiteCode}";
    private static string Summary(Level l) => $"code={l.Code}; name={l.Name}; floor={l.FloorNumber}; width={l.WidthMetres}; depth={l.DepthMetres}";
    private static string Summary(Checkpoint c) => $"code={c.Code}; name={c.Name}; kind={c.Kind}";
    private static string Summary(Desk d) => $"code={d.Code}; name={d.Name}; kind={d.Kind}; lanes={d.LaneCategoryCodes}; inService={d.InService}";

    private static AirportViewModel ToView(Airport a) => new(a.Id.Value, a.IataCode, a.IcaoCode, a.Name, a.TimeZoneId, a.CreatedOn);
    private static TerminalViewModel ToView(Terminal t) => new(t.Id.Value, t.Airport.Id.Value, t.Airport.IataCode, t.Code, t.Name, t.SiteCode, t.CreatedOn);
    private static LevelViewModel ToView(Level l) => new(l.Id.Value, l.Terminal.Id.Value, l.Code, l.Name, l.FloorNumber, l.WidthMetres, l.DepthMetres, l.SiteCode, l.CreatedOn);
    private static CheckpointViewModel ToView(Checkpoint c) => new(c.Id.Value, c.Level.Id.Value, c.Code, c.Name, c.Kind.ToString(), c.SiteCode, c.CreatedOn);
    private static DeskViewModel ToView(Desk d) => new(d.Id.Value, d.Checkpoint.Id.Value, d.Code, d.Name, d.Kind.ToString(), d.LaneCategories, d.InService, d.SiteCode, d.CreatedOn);

    #endregion
}
