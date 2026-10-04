using System.ComponentModel.DataAnnotations;
using Ariva.Core.Reports;

namespace Ariva.Core.Services.Reports;

public static class ReportErrors
{
    public const string NotFound = "The report or schedule does not exist.";
    public const string InvalidDate = "The date is YYYY-MM-DD, at most 400 days back and not after today at the site.";
    public const string InvalidName = "The name is 1 to 120 characters without control or invisible characters.";
    public const string InvalidSendAt = "The send time is HH:MM, 00:00 to 23:59, in the site's local time.";
    public const string InvalidTemplate = "Unknown report template.";
    public const string InvalidRecipients = "A schedule has 1 to 20 recipients, each an enabled account that may read reports of the site.";
    public const string TooMany = "A site has at most 100 report schedules.";
}

/// <summary>The daily report of a site (ARV-060), for the caller's sites; alerts are the ones the caller's roles see.</summary>
public interface ISvcReports : ISvcScoped
{
    /// <summary>How far back a report can be asked for.</summary>
    const int MaxDaysBack = 400;

    /// <summary>The report of a local day; a null date (not given or not a date) is refused after the site is checked.</summary>
    Task<Result<DailyReport>> DailyAsync(string siteCode, DateOnly? date, CancellationToken ct = default);
}

/// <summary>A report schedule as a request: the site is given on creation and never changes.</summary>
public sealed record ReportScheduleRequest(
    [MaxLength(17)] string SiteCode,
    [Required, MaxLength(120)] string Name,
    [MaxLength(20)] string Template,
    [Required, MaxLength(5)] string SendAt,
    [Required, MaxLength(20)] IReadOnlyList<Guid> RecipientIds,
    bool Enabled = true);

public sealed record ReportRecipientViewModel(Guid Id, string UserName, string DisplayName);

public sealed record ReportScheduleViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string Template,
    string SendAt,
    bool Enabled,
    Guid OwnerId,
    IReadOnlyList<ReportRecipientViewModel> Recipients,
    DateTime? LastSentUtc);

/// <summary>Report schedules (ARV-060) of the caller's sites.</summary>
public interface ISvcReportSchedules : ISvcScoped
{
    const int MaxSchedulesPerSite = 100;

    Task<Result<IReadOnlyList<ReportScheduleViewModel>>> SearchAsync(string siteCode, CancellationToken ct = default);

    Task<Result<ReportScheduleViewModel>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The accounts a schedule of the site may send to: enabled, reaching the site, allowed to read reports.</summary>
    Task<Result<IReadOnlyList<ReportRecipientViewModel>>> RecipientsAsync(string siteCode, CancellationToken ct = default);

    Task<Result<ReportScheduleViewModel>> CreateAsync(ReportScheduleRequest request, CancellationToken ct = default);

    Task<Result<ReportScheduleViewModel>> UpdateAsync(Guid id, ReportScheduleRequest request, CancellationToken ct = default);

    Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>What one delivery round did (ARV-060).</summary>
public sealed record ReportDeliveryRun(int Due, int Sent, int Skipped, int Failed);

/// <summary>What became of one delivery: nothing (someone else has it, or it is no longer due), sent, skipped or failed.</summary>
public enum ReportDeliveryOutcome
{
    None,
    Sent,
    Skipped,
    Failed
}

/// <summary>
/// The scheduled reports (ARV-060, run by TickerQ in Ariva.Api.Cronz): every enabled schedule whose local send time has
/// passed today owes the previous local day once to each recipient. Idempotent: a delivery already sent is never sent
/// again, so the job can be re-run at any time. Each call is one transaction.
/// </summary>
public interface ISvcReportDeliveries : ISvcScoped
{
    /// <summary>Attempts per delivery before it stays failed.</summary>
    const int MaxAttempts = 5;

    /// <summary>Records what is owed and answers the deliveries due now (none while another round prepares, or with email off).</summary>
    Task<IReadOnlyList<Guid>> PrepareAsync(CancellationToken ct = default);

    /// <summary>Sends one due delivery to its recipient, or skips it when the recipient may no longer have it.</summary>
    Task<ReportDeliveryOutcome> DeliverAsync(Guid deliveryId, CancellationToken ct = default);
}
