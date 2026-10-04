using System.Globalization;
using System.Text;

namespace Ariva.Core.Reports;

/// <summary>
/// CSV for the daily report (ARV-060): RFC 4180 quoting, invariant numbers, UTF-8. A text cell that a spreadsheet would
/// read as a formula (it starts with =, +, -, @, a tab or a carriage return, after any leading spaces) is prefixed with
/// an apostrophe, so opening the file never runs anything (CWE-1236, OWASP CSV injection). Numbers the report computes
/// are written as numbers.
/// </summary>
public static class ReportCsv
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>A text cell, neutralised and quoted when it needs to be.</summary>
    public static string Text(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var trimmed = value.TrimStart(' ');
        var text = trimmed.Length > 0 && Array.IndexOf(FormulaStarts, trimmed[0]) >= 0 ? "'" + value : value;
        // Quoted also for ';' and tab: spreadsheets set to those separators (Arabic and many European locales use ';') would
        // otherwise split the cell, and a part after the separator could start like a formula.
        return text.IndexOfAny([',', ';', '\t', '"', '\n', '\r']) >= 0 || text != text.Trim()
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    public static string Number(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;

    public static string Number(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>The report's sections as CSV files.</summary>
    public enum Section
    {
        Hours,
        Alerts,
        Devices
    }

    public static string Write(DailyReport report, Section section)
    {
        ArgumentNullException.ThrowIfNull(report);
        var csv = new StringBuilder();
        void Row(params string[] cells) => csv.Append(string.Join(',', cells)).Append("\r\n");
        var date = report.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        switch (section)
        {
            case Section.Hours:
                Row("site", "date", "hour_start_local", "hour_end_local", "zone", "lane", "passengers", "waits", "p50_wait_min", "p90_wait_min",
                    "max_queue_length", "status", "histogram");
                foreach (var lane in report.Lanes)
                {
                    foreach (var h in lane.Hours)
                    {
                        Row(Text(report.SiteCode), date, h.Start, h.End, Text(lane.Zone), Text(lane.LaneCategory), Number(h.Passengers), Number(h.Waits),
                            Number(h.P50Minutes), Number(h.P90Minutes), Number(h.MaxQueueLength), h.Provisional ? "Provisional" : "Final",
                            h.HistogramMissing ? "Missing" : "Complete");
                    }
                }

                break;
            case Section.Alerts:
                Row("site", "date", "rule", "rule_name", "zone", "device", "severity", "state", "raised_local", "cleared_local", "open_minutes");
                foreach (var a in report.Alerts)
                {
                    Row(Text(report.SiteCode), date, Text(a.RuleCode), Text(a.RuleName), Text(a.Zone), Text(a.Device), Text(a.Severity), Text(a.State),
                        a.RaisedLocal, a.ClearedLocal ?? string.Empty, Number(a.OpenMinutes));
                }

                break;
            case Section.Devices:
                Row("site", "date", "device", "zone", "uptime_percent", "outage_minutes", "outages");
                foreach (var d in report.Devices)
                    Row(Text(report.SiteCode), date, Text(d.Code), Text(d.Zone), Number(d.UptimePercent), Number(d.OutageMinutes), Number(d.Outages));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section));
        }

        return csv.ToString();
    }
}
