using Ariva.Core.Availability;
using Ariva.Core.Security;
using Ariva.Core.Services.Quality;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Quality;

/// <summary>
/// A site's operating calendar (ARV-118, <see cref="ISvcSiteCalendar"/>, script 0042). The site is checked first (outside
/// the caller's sites, or unknown, answers NotFound, CWE-204, CWE-863), then the request (<see cref="SiteCalendarRules"/>,
/// with today and now from the server's clock in the site's time zone), then the entry. Entries are recorded before they
/// take effect and change only until then, so the availability ledger never sees a minute's calendar change after the
/// minute. Every change is audited in the same unit of work.
/// </summary>
internal sealed class SvcSiteCalendar(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit,
    ReportReader reader)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcSiteCalendar
{
    private const int MaxWeeksListed = 100;
    private const int MaxEntriesListed = 500;

    #region Reads

    public async Task<Result<SiteCalendarViewModel>> GetAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, await reader.TimeZoneAsync(siteCode, ct), ct));
    }

    #endregion

    #region Weekly hours

    public async Task<Result<SiteCalendarViewModel>> SetWeekAsync(string siteCode, SetOperatingWeekRequest request, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var zone = await reader.TimeZoneAsync(siteCode, ct);
        var today = Today(zone);
        request ??= new SetOperatingWeekRequest(null, null);
        var valid = await SiteCalendarRules.Week(today).ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<SiteCalendarViewModel>(valid.ErrorMessages.FirstOrDefault() ?? SiteCalendarErrors.InvalidDate);

        var hours = SiteCalendarRules.WeekHours(request).Hours;
        var effectiveFrom = request.EffectiveFrom;
        var existing = await Query<OperatingWeek>().FirstOrDefaultAsync(w => w.SiteCode == siteCode && w.EffectiveFrom == effectiveFrom, ct);
        if (existing is not null)
        {
            var before = existing.AuditSummary();
            existing.Replace(hours, UtcNow);
            await UpdateAsync(existing, ct);
            await audit.RecordAsync(Actions.WeekSet, nameof(OperatingWeek), existing.Id, siteCode, before, existing.AuditSummary(), ct);
        }
        else
        {
            var todayText = CalendarDates.Format(today);
            if (await QueryAsNoTracking<OperatingWeek>().CountAsync(w => w.SiteCode == siteCode && w.EffectiveFrom.CompareTo(todayText) > 0, ct) >= ISvcSiteCalendar.MaxFutureEntries)
                return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.TooMany);
            CalendarDates.TryParse(effectiveFrom, out var date);
            var week = new OperatingWeek(siteCode, date, hours, UtcNow);
            await SaveAsync(week, ct);
            // Before the audit entry: a version of the same day inserted concurrently (ux_operating_week_site_from) is a 409
            // here, with nothing of this request committed, rather than a failed commit.
            if (!await FlushUniqueAsync(ct))
                return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.WeekConcurrent);
            await audit.RecordAsync(Actions.WeekSet, nameof(OperatingWeek), week.Id, siteCode, null, week.AuditSummary(), ct);
        }

        await FlushAsync(ct);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, zone, ct));
    }

    public async Task<Result<SiteCalendarViewModel>> RemoveWeekAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var week = await Query<OperatingWeek>().FirstOrDefaultAsync(w => w.Id == id && w.SiteCode == siteCode, ct);
        if (week is null)
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var zone = await reader.TimeZoneAsync(siteCode, ct);
        if (week.EffectiveDate <= Today(zone))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.Started);

        week.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(week, ct);
        await audit.RecordAsync(Actions.WeekRemoved, nameof(OperatingWeek), week.Id, siteCode, week.AuditSummary(), null, ct);
        await FlushAsync(ct);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, zone, ct));
    }

    #endregion

    #region Dated exceptions

    public async Task<Result<SiteCalendarViewModel>> AddExceptionAsync(string siteCode, AddOperatingExceptionRequest request, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var zone = await reader.TimeZoneAsync(siteCode, ct);
        var today = Today(zone);
        request ??= new AddOperatingExceptionRequest(null, false, null, null);
        var valid = await SiteCalendarRules.Exception(today).ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<SiteCalendarViewModel>(valid.ErrorMessages.FirstOrDefault() ?? SiteCalendarErrors.InvalidDate);

        var day = request.Date;
        if (await QueryAsNoTracking<OperatingDayException>().AnyAsync(e => e.SiteCode == siteCode && e.Date == day, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.ExceptionTaken);
        var todayText = CalendarDates.Format(today);
        if (await QueryAsNoTracking<OperatingDayException>().CountAsync(e => e.SiteCode == siteCode && e.Date.CompareTo(todayText) > 0, ct) >= ISvcSiteCalendar.MaxFutureEntries)
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.TooMany);

        CalendarDates.TryParse(day, out var date);
        var exception = new OperatingDayException(siteCode, date, request.Closed ? [] : SiteCalendarRules.DayHours(request).Hours, request.Reason, UtcNow);
        await SaveAsync(exception, ct);
        // Before the audit entry: an exception of the same day added concurrently (ux_operating_day_exception_site_date) is
        // a 409 here, with nothing of this request committed, rather than a failed commit.
        if (!await FlushUniqueAsync(ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.ExceptionTaken);
        await audit.RecordAsync(Actions.ExceptionAdded, nameof(OperatingDayException), exception.Id, siteCode, null, exception.AuditSummary(), ct);
        await FlushAsync(ct);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, zone, ct));
    }

    public async Task<Result<SiteCalendarViewModel>> RemoveExceptionAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var exception = await Query<OperatingDayException>().FirstOrDefaultAsync(e => e.Id == id && e.SiteCode == siteCode, ct);
        if (exception is null)
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var zone = await reader.TimeZoneAsync(siteCode, ct);
        if (exception.LocalDate <= Today(zone))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.Started);

        exception.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(exception, ct);
        await audit.RecordAsync(Actions.ExceptionRemoved, nameof(OperatingDayException), exception.Id, siteCode, exception.AuditSummary(), null, ct);
        await FlushAsync(ct);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, zone, ct));
    }

    #endregion

    #region Maintenance windows

    public async Task<Result<SiteCalendarViewModel>> AddMaintenanceAsync(string siteCode, AddMaintenanceWindowRequest request, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var now = UtcNow;
        request ??= new AddMaintenanceWindowRequest(null, null, null);
        var valid = await SiteCalendarRules.Maintenance(now).ValidateAllAsync(request);
        if (valid.HasErrors || SiteCalendarRules.Window(request, now) is not { } window)
            return Result.Error<SiteCalendarViewModel>(valid.ErrorMessages.FirstOrDefault() ?? SiteCalendarErrors.InvalidWindow);
        if (await QueryAsNoTracking<MaintenanceWindow>().CountAsync(w => w.SiteCode == siteCode && w.StartsUtc > now, ct) >= ISvcSiteCalendar.MaxFutureEntries)
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.TooMany);

        var maintenance = new MaintenanceWindow(siteCode, window.Starts, window.Ends, request.Reason, now);
        await SaveAsync(maintenance, ct);
        await audit.RecordAsync(Actions.MaintenanceAdded, nameof(MaintenanceWindow), maintenance.Id, siteCode, null, maintenance.AuditSummary(), ct);
        await FlushAsync(ct);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, await reader.TimeZoneAsync(siteCode, ct), ct));
    }

    public async Task<Result<SiteCalendarViewModel>> CancelMaintenanceAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        var maintenance = await Query<MaintenanceWindow>().FirstOrDefaultAsync(w => w.Id == id && w.SiteCode == siteCode, ct);
        if (maintenance is null)
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.NotFound);
        if (maintenance.StartsUtc <= UtcNow)
            return Result.Error<SiteCalendarViewModel>(SiteCalendarErrors.Started);

        maintenance.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(maintenance, ct);
        await audit.RecordAsync(Actions.MaintenanceCancelled, nameof(MaintenanceWindow), maintenance.Id, siteCode, maintenance.AuditSummary(), null, ct);
        await FlushAsync(ct);
        return new Result<SiteCalendarViewModel>(await ViewAsync(siteCode, await reader.TimeZoneAsync(siteCode, ct), ct));
    }

    #endregion

    #region Helpers

    /// <summary>Stable audit action codes; never rename one that has shipped.</summary>
    internal static class Actions
    {
        public const string WeekSet = "SiteCalendar.WeekSet";
        public const string WeekRemoved = "SiteCalendar.WeekRemoved";
        public const string ExceptionAdded = "SiteCalendar.ExceptionAdded";
        public const string ExceptionRemoved = "SiteCalendar.ExceptionRemoved";
        public const string MaintenanceAdded = "SiteCalendar.MaintenanceAdded";
        public const string MaintenanceCancelled = "SiteCalendar.MaintenanceCancelled";
    }

    /// <summary>
    /// Flushes the pending insert; false, with the unit of work promised not to commit, when a unique index refused it
    /// (23505: the same day or version recorded by a concurrent request).
    /// </summary>
    private async Task<bool> FlushUniqueAsync(CancellationToken ct)
    {
        try
        {
            await FlushAsync(ct);
            return true;
        }
        catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException { SqlState: "23505" })
        {
            UnitOfWork.PromiseNotToCommit();
            return false;
        }
    }

    private DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone));

    /// <summary>The site is the caller's and exists; checked before anything else, one answer for both (CWE-204).</summary>
    private async Task<bool> VisibleAsync(string siteCode, CancellationToken ct) =>
        siteCode is not null && Site.IsValidCode(siteCode) && (await siteScope.GetAsync(ct)).Allows(siteCode) && await reader.SiteExistsAsync(siteCode, ct);

    private async Task<SiteCalendarViewModel> ViewAsync(string siteCode, TimeZoneInfo zone, CancellationToken ct)
    {
        var today = Today(zone);
        var from = CalendarDates.Format(today.AddDays(-ISvcSiteCalendar.HistoryDays));
        var since = UtcNow.AddDays(-ISvcSiteCalendar.HistoryDays);
        var weeks = await QueryAsNoTracking<OperatingWeek>().Where(w => w.SiteCode == siteCode)
            .OrderByDescending(w => w.EffectiveFrom).Take(MaxWeeksListed).ToListAsync(ct);
        var exceptions = await QueryAsNoTracking<OperatingDayException>().Where(e => e.SiteCode == siteCode && e.Date.CompareTo(from) >= 0)
            .OrderBy(e => e.Date).Take(MaxEntriesListed).ToListAsync(ct);
        var windows = await QueryAsNoTracking<MaintenanceWindow>().Where(w => w.SiteCode == siteCode && w.EndsUtc >= since)
            .OrderBy(w => w.StartsUtc).Take(MaxEntriesListed).ToListAsync(ct);

        return new SiteCalendarViewModel(siteCode, zone.Id, CalendarDates.Format(today),
            [.. weeks.Select(w => new OperatingWeekViewModel(w.Id.GetValueOrDefault(), w.EffectiveFrom, w.Week?.Intervals() ?? [], w.RecordedUtc))],
            [.. exceptions.Select(e => new OperatingExceptionViewModel(e.Id.GetValueOrDefault(), e.Date, e.Intervals is { Count: 0 }, WeeklyHours.DayTexts(e.Intervals ?? []), e.Reason, e.RecordedUtc))],
            [.. windows.Select(w => new MaintenanceWindowViewModel(w.Id.GetValueOrDefault(), w.StartsUtc, w.EndsUtc, w.Reason, w.RecordedUtc))]);
    }

    #endregion
}
