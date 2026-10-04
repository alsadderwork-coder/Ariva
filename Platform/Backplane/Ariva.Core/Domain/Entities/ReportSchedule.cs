using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A scheduled report (ARV-060): one site's daily report for the previous local day, sent at a local time to Ariva
/// accounts. Each recipient gets the report its own roles and sites allow, checked at every delivery, so a schedule
/// never sends a recipient more than it could read on the screen.
/// </summary>
public class ReportSchedule : BaseSoftDeletableEntity<ReportSchedule>, ISiteBound
{
    public const int MaxNameLength = 120;
    public const int MaxRecipients = 20;

    protected ReportSchedule()
    {
    }

    public ReportSchedule(string siteCode, Guid ownerId, string name, ReportTemplate template, string sendAt, bool enabled)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        SiteCode = siteCode;
        OwnerId = ownerId;
        Set(name, template, sendAt, enabled);
    }

    public virtual string SiteCode { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual ReportTemplate Template { get; protected set; }

    /// <summary>The local time of the site the report goes out, "HH:MM".</summary>
    public virtual string SendAt { get; protected set; }

    public virtual bool Enabled { get; protected set; }

    /// <summary>The account that created the schedule.</summary>
    public virtual Guid OwnerId { get; protected set; }

    public virtual void Set(string name, ReportTemplate template, string sendAt, bool enabled)
    {
        Name = DisplayText.Require(name, MaxNameLength, nameof(name));
        if (!Enum.IsDefined(template))
            throw new ArgumentException("Unknown report template.", nameof(template));
        if (!TryParseSendAt(sendAt, out _))
            throw new ArgumentException("The send time is HH:MM, 00:00 to 23:59.", nameof(sendAt));
        Template = template;
        SendAt = sendAt;
        Enabled = enabled;
    }

    /// <summary>"HH:MM" with two digits each, 00:00 to 23:59.</summary>
    public static bool TryParseSendAt(string value, out TimeOnly time)
    {
        time = default;
        return value is { Length: 5 } && value[2] == ':' && char.IsAsciiDigit(value[0]) && char.IsAsciiDigit(value[1]) &&
               char.IsAsciiDigit(value[3]) && char.IsAsciiDigit(value[4]) &&
               TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);
    }

    public virtual string AuditSummary(IEnumerable<string> recipients) =>
        $"site={SiteCode}; name={System.Text.Json.JsonSerializer.Serialize(Name)}; template={Template}; sendAt={SendAt}; enabled={Enabled}; recipients={string.Join(",", recipients ?? [])}";
}

/// <summary>An account a schedule sends its report to.</summary>
public class ReportScheduleRecipient : EntityBase<ReportScheduleRecipient>
{
    protected ReportScheduleRecipient()
    {
    }

    public ReportScheduleRecipient(ReportSchedule schedule, Guid userId)
    {
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        UserId = userId;
    }

    public virtual ReportSchedule Schedule { get; protected set; }
    public virtual Guid UserId { get; protected set; }
}
