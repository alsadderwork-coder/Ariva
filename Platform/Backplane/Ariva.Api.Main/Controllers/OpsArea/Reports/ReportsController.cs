using System.Globalization;
using System.Text;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Reports;
using Ariva.Core.Services.Reports;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Reports;

/// <summary>
/// The daily report of a site (ARV-060): lane peaks, P50 and P90 per local hour, the day's alerts the caller's roles see
/// and device uptime, as JSON or as CSV (one file per section; cells that a spreadsheet would run are neutralised).
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/reports")]
[SiteScoped]
public sealed class ReportsController(ISvcReports reports) : ControllerBase
{
    [HttpGet("daily")]
    [Permission(nameof(Global.Defaults.Permissions.ViewReport))]
    [ProducesResponseType<DailyReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Daily(string siteCode, [FromQuery] string date, CancellationToken ct)
    {
        var result = await reports.DailyAsync(siteCode, TryDate(date, out var day) ? day : null, ct);
        return result.HasErrors ? ReportAnswers.Problem(this, result.ErrorMessages.FirstOrDefault()) : Ok(result.Data);
    }

    /// <summary>One section of the daily report as a CSV file: hours (default), alerts or devices.</summary>
    [HttpGet("daily.csv")]
    [Permission(nameof(Global.Defaults.Permissions.ViewReport))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DailyCsv(string siteCode, [FromQuery] string date, [FromQuery] string section, CancellationToken ct)
    {
        // The site and the date are checked first (an unknown site answers 404 whatever else is asked), then the section.
        var result = await reports.DailyAsync(siteCode, TryDate(date, out var day) ? day : null, ct);
        if (result.HasErrors)
            return ReportAnswers.Problem(this, result.ErrorMessages.FirstOrDefault());
        var part = ReportCsv.Section.Hours;
        // By exact name only: Enum.TryParse would also take "1" or "Hours, Alerts".
        if (section is not null)
        {
            var sectionName = Enum.GetNames<ReportCsv.Section>().FirstOrDefault(n => n.Equals(section, StringComparison.OrdinalIgnoreCase));
            if (sectionName is null)
                return ReportAnswers.Problem(this, "The section is hours, alerts or devices.");
            part = Enum.Parse<ReportCsv.Section>(sectionName);
        }
        Response.Headers.CacheControl = "no-store";
        var name = $"ariva-{result.Data.SiteCode}-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{part.ToString().ToLowerInvariant()}.csv";
        // With a byte order mark, so spreadsheet programs read Arabic names as UTF-8.
        return File([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(ReportCsv.Write(result.Data, part))], "text/csv; charset=utf-8", name);
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>Problem answers of the report endpoints: unknown or foreign records 404, the per-site limit 409, the rest 400.</summary>
internal static class ReportAnswers
{
    public static ObjectResult Problem(ControllerBase controller, string error)
    {
        var (status, title) = error switch
        {
            null or ReportErrors.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            ReportErrors.TooMany => (StatusCodes.Status409Conflict, "Conflict"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        return controller.Problem(statusCode: status, title: title, detail: status == StatusCodes.Status404NotFound ? null : error);
    }
}
