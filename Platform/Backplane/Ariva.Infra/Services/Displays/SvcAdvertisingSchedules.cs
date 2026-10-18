using System.Globalization;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The advertising schedules (ARV-143a). Every call names a site of the caller and every schedule is looked up by its id and
/// that site together (another site's answers 404, CWE-863, IDOR). A schedule plays an active playlist of the same site
/// (the database holds it too, by a key over both columns); its zones are queue zones of the site's published profile and
/// its displays live displays of the site, as for instruction slides. Creation takes a lock on the site's schedules
/// (class 1432) so the cap of 200 holds; a change locks the schedule's row first. The timeline resolves a display's week as
/// the board will (<see cref="AdvertisingSchedules"/>), from the site's local midnight in its time zone. Every change is
/// audited, and a schedule is retired rather than deleted.
/// </summary>
internal sealed class SvcAdvertisingSchedules(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    ReportReader sites)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAdvertisingSchedules
{
    private const string Target = "AdvertisingSchedule";
    private const int TimelineDays = 7;

    public async Task<Result<IReadOnlyList<AdvertisingScheduleViewModel>>> ListAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<IReadOnlyList<AdvertisingScheduleViewModel>>(TopologyErrors.NotFound);
        var schedules = await QueryAsNoTracking<AdvertisingSchedule>()
            .Where(s => s.SiteCode == siteCode)
            .OrderBy(s => s.CreatedOn)
            .Take(AdvertisingSchedule.MaxSchedulesPerSite)
            .ToListAsync(ct);
        return new Result<IReadOnlyList<AdvertisingScheduleViewModel>>(await ViewsAsync(siteCode, schedules, ct));
    }

    public async Task<Result<AdvertisingScheduleViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        var schedule = id == Guid.Empty || !await VisibleSiteAsync(siteCode, ct)
            ? null
            : await QueryAsNoTracking<AdvertisingSchedule>().Where(s => s.Id == id && s.SiteCode == siteCode).FirstOrDefaultAsync(ct);
        return schedule is null
            ? Result.Error<AdvertisingScheduleViewModel>(TopologyErrors.NotFound)
            : new Result<AdvertisingScheduleViewModel>((await ViewsAsync(siteCode, [schedule], ct))[0]);
    }

    public async Task<Result<AdvertisingScheduleViewModel>> CreateAsync(string siteCode, AdvertisingScheduleRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingScheduleViewModel>(TopologyErrors.NotFound);
        var (values, problems) = await CheckAsync(siteCode, request, ct);
        if (problems.Count > 0)
            return Result.Error<AdvertisingScheduleViewModel>(problems);
        await LockSiteAsync(siteCode, ct);
        if (await QueryAsNoTracking<AdvertisingSchedule>().CountAsync(s => s.SiteCode == siteCode, ct) >= AdvertisingSchedule.MaxSchedulesPerSite)
            return Result.Error<AdvertisingScheduleViewModel>(AdvertisingPlanErrors.TooManySchedules);

        var schedule = new AdvertisingSchedule(siteCode, values);
        await SaveAsync(schedule, ct);
        await audit.RecordAsync($"{Target}.Created", Target, schedule.Id, siteCode, null, schedule.AuditSummary(), ct);
        return new Result<AdvertisingScheduleViewModel>((await ViewsAsync(siteCode, [schedule], ct))[0]);
    }

    public async Task<Result<AdvertisingScheduleViewModel>> UpdateAsync(string siteCode, Guid id, AdvertisingScheduleRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingScheduleViewModel>(TopologyErrors.NotFound);
        var (values, problems) = await CheckAsync(siteCode, request, ct);
        var schedule = await LockAsync(siteCode, id, ct);
        if (schedule is null)
            return Result.Error<AdvertisingScheduleViewModel>(TopologyErrors.NotFound);
        if (problems.Count > 0)
            return Result.Error<AdvertisingScheduleViewModel>(problems);

        var before = schedule.AuditSummary();
        schedule.Set(values);
        await UpdateAsync(schedule, ct);
        await audit.RecordAsync($"{Target}.Updated", Target, schedule.Id, siteCode, before, schedule.AuditSummary(), ct);
        return new Result<AdvertisingScheduleViewModel>((await ViewsAsync(siteCode, [schedule], ct))[0]);
    }

    public Task<Result<AdvertisingScheduleViewModel>> RetireAsync(string siteCode, Guid id, CancellationToken ct = default) =>
        ChangeStatusAsync(siteCode, id, InstructionStatus.Retired, "Retired", ct);

    public Task<Result<AdvertisingScheduleViewModel>> RestoreAsync(string siteCode, Guid id, CancellationToken ct = default) =>
        ChangeStatusAsync(siteCode, id, InstructionStatus.Active, "Restored", ct);

    public async Task<Result<AdvertisingTimelineViewModel>> TimelineAsync(string siteCode, string displayCode, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct) || string.IsNullOrWhiteSpace(displayCode) || !TopologyCodes.IsValid(displayCode))
            return Result.Error<AdvertisingTimelineViewModel>(TopologyErrors.NotFound);
        var display = await QueryAsNoTracking<Display>().Where(d => d.SiteCode == siteCode && d.Code == displayCode && d.DeletedOn == null).FirstOrDefaultAsync(ct);
        if (display is null)
            return Result.Error<AdvertisingTimelineViewModel>(TopologyErrors.NotFound);

        var schedules = await QueryAsNoTracking<AdvertisingSchedule>()
            .Where(s => s.SiteCode == siteCode && s.Status == InstructionStatus.Active)
            .OrderBy(s => s.CreatedOn).ThenBy(s => s.Id)
            .Take(AdvertisingSchedule.MaxSchedulesPerSite)
            .ToListAsync(ct);
        // Names and states only: the items are not needed to say which playlist plays.
        var playlists = (await QueryAsNoTracking<AdvertisingPlaylist>().Where(p => p.SiteCode == siteCode)
                .Select(p => new { p.Id, p.Name, p.Status }).ToListAsync(ct))
            .ToDictionary(p => p.Id.GetValueOrDefault());
        var active = playlists.Values.Where(p => p.Status == InstructionStatus.Active).Select(p => p.Id.GetValueOrDefault()).ToHashSet();
        var zone = await sites.TimeZoneAsync(siteCode, ct);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone);
        var from = AdvertisingSchedules.LocalMidnightUtc(DateOnly.FromDateTime(localNow), zone);
        var segments = AdvertisingSchedules.Timeline(schedules, active, siteCode, display.Code, display.EntryList.Select(e => e.Zone).ToList(), zone, from, TimelineDays);

        string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        return new Result<AdvertisingTimelineViewModel>(new AdvertisingTimelineViewModel(siteCode, display.Code, zone.Id, from, from.AddDays(TimelineDays),
            segments.Select(s => new AdvertisingTimelineSegmentViewModel(s.FromUtc, s.ToUtc, Local(s.FromUtc), Local(s.ToUtc), s.Schedule?.Id,
                s.Schedule?.PlaylistId, s.Schedule is null ? null : playlists.GetValueOrDefault(s.Schedule.PlaylistId)?.Name)).ToList(),
            display.SecondRegion.ToString()));
    }


    private async Task<Result<AdvertisingScheduleViewModel>> ChangeStatusAsync(string siteCode, Guid id, InstructionStatus status, string action, CancellationToken ct)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingScheduleViewModel>(TopologyErrors.NotFound);
        var schedule = await LockAsync(siteCode, id, ct);
        if (schedule is null)
            return Result.Error<AdvertisingScheduleViewModel>(TopologyErrors.NotFound);
        if (schedule.Status != status)
        {
            var before = schedule.AuditSummary();
            if (status == InstructionStatus.Retired)
                schedule.Retire();
            else
                schedule.Restore();
            await UpdateAsync(schedule, ct);
            await audit.RecordAsync($"{Target}.{action}", Target, schedule.Id, siteCode, before, schedule.AuditSummary(), ct);
        }

        return new Result<AdvertisingScheduleViewModel>((await ViewsAsync(siteCode, [schedule], ct))[0]);
    }

    private async Task<(AdvertisingScheduleValues Values, IReadOnlyList<string> Problems)> CheckAsync(string siteCode, AdvertisingScheduleRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return (null, [AdvertisingPlanErrors.ScheduleRequired]);
        if (!SvcAdvertisingPlaylists.Exactly<InstructionScope>(request.Scope, out var scope))
            return (null, [AdvertisingPlanErrors.UnknownScope]);
        var values = new AdvertisingScheduleValues(request.PlaylistId, scope, request.Targets ?? [], request.Days ?? [], request.Opens, request.Closes);
        var problems = values.Problems().ToList();
        if (problems.Count > 0)
            return (values, problems);
        if (!await QueryAsNoTracking<AdvertisingPlaylist>()
                .AnyAsync(p => p.Id == request.PlaylistId && p.SiteCode == siteCode && p.Status == InstructionStatus.Active, ct))
            problems.Add(AdvertisingPlanErrors.UnknownPlaylist);

        var targets = values.Targets.Select(t => t.Trim()).ToList();
        if (scope == InstructionScope.Zone)
        {
            var known = await Query<Zone>()
                .Where(z => z.Profile.SiteCode == siteCode && z.Profile.Status == ZoneProfileStatus.Published && targets.Contains(z.Name) &&
                            z.Kind == ZoneKind.Queue)
                .Select(z => z.Name)
                .ToListAsync(ct);
            if (known.Distinct(StringComparer.Ordinal).Count() != targets.Count)
                problems.Add(InstructionSlideErrors.UnknownZones);
        }
        else if (scope == InstructionScope.Display)
        {
            var known = await QueryAsNoTracking<Display>()
                .Where(d => d.SiteCode == siteCode && d.DeletedOn == null && targets.Contains(d.Code))
                .Select(d => d.Code)
                .ToListAsync(ct);
            if (known.Distinct(StringComparer.Ordinal).Count() != targets.Count)
                problems.Add(InstructionSlideErrors.UnknownDisplays);
        }

        return (values, problems);
    }

    private async Task<IReadOnlyList<AdvertisingScheduleViewModel>> ViewsAsync(string siteCode, IReadOnlyList<AdvertisingSchedule> schedules, CancellationToken ct)
    {
        var ids = schedules.Select(s => s.PlaylistId).Distinct().ToList();
        var playlists = ids.Count == 0
            ? new Dictionary<Guid, AdvertisingPlaylist>()
            : (await QueryAsNoTracking<AdvertisingPlaylist>().Where(p => p.SiteCode == siteCode && ids.Contains(p.Id.Value)).ToListAsync(ct))
            .ToDictionary(p => p.Id.GetValueOrDefault());
        return schedules.Select(s =>
        {
            var playlist = playlists.GetValueOrDefault(s.PlaylistId);
            return new AdvertisingScheduleViewModel(s.Id.GetValueOrDefault(), s.SiteCode, s.PlaylistId, playlist?.Name, playlist?.IsActive ?? false,
                s.Scope.ToString(), s.TargetList, s.DayList.Select(d => d.ToString()).ToList(), s.Window.Opens, s.Window.Closes, s.Status.ToString(),
                s.CreatedOn, s.ModifiedOn);
        }).ToList();
    }

    private async Task<AdvertisingSchedule> LockAsync(string siteCode, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        var locked = await ExecuteCommandAsync<LockRow>(
            """SELECT 1 AS "Value" FROM advertising_schedule WHERE id = :id AND site_code = :site FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = id, ["site"] = siteCode }, ct);
        return locked.Count == 0 ? null : await Query<AdvertisingSchedule>().Where(s => s.Id == id && s.SiteCode == siteCode).FirstOrDefaultAsync(ct);
    }

    /// <summary>A transaction-scoped lock on the site's schedules (class 1432, the site code's hash), released at the commit.</summary>
    private async Task LockSiteAsync(string siteCode, CancellationToken ct)
    {
        await FlushAsync(ct);
        await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(1432, hashtext(:site))) l""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct);
    }

    private async Task<bool> VisibleSiteAsync(string siteCode, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(siteCode) && (await siteScope.GetAsync(ct)).Allows(siteCode) &&
        await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct);

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
