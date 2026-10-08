using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A validation campaign (ARV-104a; formulas F18; wiki 07 section 8): the ground truth a site's numbers are checked against
/// before anyone relies on them. It binds a site, one published zone profile version (number and geometry hash), the queue
/// zones and lines in scope and the planned local days; observers record manual counts per line and 15-minute bin while it
/// runs (<see cref="Capture"/>), correct them as new revisions with a reason (<see cref="Correct"/>), and a manager closes it
/// (a step-up critical action). Planned, Running, Closed; a closed campaign never changes. The only people it names are
/// Ariva user ids (who created, started and closed it; the observer of each count), never a name (data boundary).
/// <para>
/// The targets (bins per line, tracer runs) are placeholders until the pilot's KPI annex answers TC-04: a campaign created
/// without them takes <see cref="DefaultTargetBinsPerLine"/> and <see cref="DefaultTargetTracerRuns"/> and says so
/// (<see cref="TargetsPlaceholder"/>).
/// </para>
/// </summary>
public class ValidationCampaign : EntityBase<ValidationCampaign>, ISiteBound
{
    #region Constants

    public const int MaxNameLength = 200;
    public const int MaxZones = 50;
    public const int MaxLines = 200;
    public const int MaxDays = 31;

    /// <summary>The earliest planned day, in days before the site's today at creation (counts kept on paper are entered later).</summary>
    public const int DaysBack = 31;

    /// <summary>The latest planned day, in days after the site's today at creation.</summary>
    public const int DaysAhead = 366;

    /// <summary>Bins of a 24-hour local day.</summary>
    public const int BinsPerDay = 96;

    /// <summary>Placeholder until TC-04 (sample size per line, KPI annex): 20 bins, for example one peak hour on each of five days.</summary>
    public const int DefaultTargetBinsPerLine = 20;

    /// <summary>Placeholder until TC-04 (number of tracers, KPI annex): 30 runs spread from short to long waits.</summary>
    public const int DefaultTargetTracerRuns = 30;

    public const int MaxTargetBinsPerLine = MaxDays * BinsPerDay;
    public const int MaxTargetTracerRuns = 1_000;

    /// <summary>31 dates of "yyyy-MM-dd" separated by commas.</summary>
    public const int PlannedDaysLength = (MaxDays * 11) - 1;

    /// <summary>The bin of F18, as line_minute_15m buckets it (UTC, on the quarter hour).</summary>
    public static readonly TimeSpan BinLength = TimeSpan.FromMinutes(15);

    /// <summary>A bin may be submitted this long before its end, for the difference between the tablet's clock and the server's.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    #endregion

    #region Constructors

    protected ValidationCampaign()
    {
    }

    /// <summary>
    /// A planned campaign over the published <paramref name="profile"/>, its queue zones <paramref name="zoneIds"/>, the lines
    /// <paramref name="lineIds"/> of those zones (or of their overflow bands) and the local <paramref name="days"/>; the
    /// service has checked each rule (<see cref="ScopeProblem"/>, <see cref="AreValidDays"/>, <see cref="AreValidTargets"/>)
    /// and this constructor enforces them again.
    /// </summary>
    public ValidationCampaign(string name, ZoneProfile profile, IReadOnlyCollection<Guid> zoneIds, IReadOnlyCollection<Guid> lineIds,
        IReadOnlyCollection<DateOnly> days, DateOnly today, int? targetBinsPerLine, int? targetTracerRuns, Guid createdById, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RequireUtc(utcNow, nameof(utcNow));
        if (profile.Status != ZoneProfileStatus.Published || profile.Version is not { } version || profile.Id is not { } profileId)
            throw new InvalidOperationException(ValidationErrors.NotPublished);
        if (createdById == Guid.Empty)
            throw new ArgumentException("A campaign is created by an Ariva user.", nameof(createdById));
        if (ScopeProblem(profile, zoneIds, lineIds) is { } scope)
            throw new ArgumentException(scope, nameof(lineIds));
        if (!AreValidDays(days, today))
            throw new ArgumentException(ValidationErrors.InvalidDays, nameof(days));
        if (!AreValidTargets(targetBinsPerLine, targetTracerRuns))
            throw new ArgumentException(ValidationErrors.InvalidTargets, nameof(targetBinsPerLine));

        SiteCode = profile.SiteCode;
        Name = DisplayText.Require(name, MaxNameLength, nameof(name));
        Status = ValidationCampaignStatus.Planned;
        ProfileId = profileId;
        ProfileVersion = version;
        GeometryHash = profile.GeometryHash;
        PlannedDays = string.Join(',', days.Order().Select(FormatDay));
        TargetBinsPerLine = targetBinsPerLine ?? DefaultTargetBinsPerLine;
        TargetTracerRuns = targetTracerRuns ?? DefaultTargetTracerRuns;
        TargetsPlaceholder = targetBinsPerLine is null || targetTracerRuns is null;
        CreatedById = createdById;
        CreatedUtc = utcNow;

        foreach (var zone in zoneIds.Select(id => profile.Zones.First(z => z.Id == id)).OrderBy(z => z.Name, StringComparer.Ordinal))
            Zones.Add(new ValidationCampaignZone(this, zone));
        foreach (var line in lineIds.Select(id => profile.Lines.First(l => l.Id == id)).OrderBy(l => l.Name, StringComparer.Ordinal))
            Lines.Add(new ValidationCampaignLine(this, line, OwningQueueZone(line)));
    }

    #endregion

    #region Properties

    public virtual string SiteCode { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual ValidationCampaignStatus Status { get; protected set; }

    /// <summary>The zone profile version the campaign validates; every count is of one of its lines.</summary>
    public virtual Guid ProfileId { get; protected set; }

    public virtual int ProfileVersion { get; protected set; }

    /// <summary>The version's geometry hash (F22), for the report.</summary>
    [MaxLength(64)]
    public virtual string GeometryHash { get; protected set; }

    /// <summary>The planned local days, "yyyy-MM-dd" in ascending order separated by commas.</summary>
    [MaxLength(PlannedDaysLength)]
    public virtual string PlannedDays { get; protected set; }

    public virtual int TargetBinsPerLine { get; protected set; }
    public virtual int TargetTracerRuns { get; protected set; }

    /// <summary>True when a target took its placeholder default (TC-04 not answered for this campaign).</summary>
    public virtual bool TargetsPlaceholder { get; protected set; }

    public virtual Guid CreatedById { get; protected set; }
    public virtual DateTime CreatedUtc { get; protected set; }
    public virtual Guid? StartedById { get; protected set; }
    public virtual DateTime? StartedUtc { get; protected set; }
    public virtual Guid? ClosedById { get; protected set; }
    public virtual DateTime? ClosedUtc { get; protected set; }

    public virtual IList<ValidationCampaignZone> Zones { get; protected set; } = [];
    public virtual IList<ValidationCampaignLine> Lines { get; protected set; } = [];

    /// <summary>The planned local days in ascending order (none when the stored text is not valid).</summary>
    public virtual IReadOnlyList<DateOnly> Days =>
        (PlannedDays ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => TryParseDay(text, out var day) ? (DateOnly?)day : null)
            .Where(day => day is not null)
            .Select(day => day.Value)
            .ToList();

    #endregion

    #region Lifecycle

    /// <summary>Why the campaign cannot start now, or null.</summary>
    public virtual string StartProblem(ZoneProfileStatus profileStatus) =>
        Status switch
        {
            ValidationCampaignStatus.Closed => ValidationErrors.Closed,
            ValidationCampaignStatus.Running => ValidationErrors.NotPlanned,
            _ when profileStatus != ZoneProfileStatus.Published => ValidationErrors.ProfileRetired,
            _ => null
        };

    /// <summary>Planned to Running, while its profile version is still the published one.</summary>
    public virtual void Start(Guid startedById, DateTime utcNow, ZoneProfileStatus profileStatus)
    {
        RequireUtc(utcNow, nameof(utcNow));
        if (StartProblem(profileStatus) is { } problem)
            throw new InvalidOperationException(problem);
        if (startedById == Guid.Empty)
            throw new ArgumentException("A campaign is started by an Ariva user.", nameof(startedById));
        Status = ValidationCampaignStatus.Running;
        StartedById = startedById;
        StartedUtc = utcNow;
    }

    /// <summary>Why the campaign cannot be closed, or null (a planned campaign may be closed without starting).</summary>
    public virtual string CloseProblem() => Status == ValidationCampaignStatus.Closed ? ValidationErrors.Closed : null;

    /// <summary>Planned or Running to Closed; from then on nothing is captured or corrected.</summary>
    public virtual void Close(Guid closedById, DateTime utcNow)
    {
        RequireUtc(utcNow, nameof(utcNow));
        if (CloseProblem() is { } problem)
            throw new InvalidOperationException(problem);
        if (closedById == Guid.Empty)
            throw new ArgumentException("A campaign is closed by an Ariva user.", nameof(closedById));
        Status = ValidationCampaignStatus.Closed;
        ClosedById = closedById;
        ClosedUtc = utcNow;
    }

    #endregion

    #region Capture

    /// <summary>The line in scope with this id, or null.</summary>
    public virtual ValidationCampaignLine LineOf(Guid lineId) => Lines.FirstOrDefault(l => l.LineId == lineId);

    /// <summary>
    /// Why <paramref name="observerId"/> may never count for this campaign, or null (separation of duties, owner decision
    /// 2026-10-08): the account that created or started it produces none of its ground truth, also when it holds the
    /// Validation observer role besides a manager role.
    /// </summary>
    public virtual string ObserverProblem(Guid observerId) =>
        observerId == Guid.Empty ? ValidationErrors.NotFound
        : observerId == CreatedById || observerId == StartedById ? ValidationErrors.OwnCampaign
        : null;

    /// <summary>
    /// Why <paramref name="observerId"/>'s count of <paramref name="lineId"/> for the bin starting at
    /// <paramref name="binStartUtc"/> cannot be recorded now, or null: the observer did not create or start the campaign
    /// (<see cref="ObserverProblem"/>), the campaign runs, the line is in scope, the bin is a 15-minute UTC bin that starts on
    /// a planned day in the site's local time, it has ended (within <see cref="ClockTolerance"/>), and it ended before the
    /// profile version was retired (when it was).
    /// </summary>
    public virtual string CaptureProblem(Guid lineId, DateTime binStartUtc, Guid observerId, DateTime nowUtc, TimeZoneInfo siteZone, DateTime? profileRetiredUtc)
    {
        ArgumentNullException.ThrowIfNull(siteZone);
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (RunningProblem() is { } running)
            return running;
        if (LineOf(lineId) is null)
            return ValidationErrors.LineNotInScope;
        if (!IsBinStart(binStartUtc))
            return ValidationErrors.InvalidBin;
        if (!Days.Contains(LocalDay(binStartUtc, siteZone)))
            return ValidationErrors.BinOutsideCampaign;
        if (binStartUtc + BinLength > nowUtc + ClockTolerance)
            return ValidationErrors.BinNotEnded;
        if (profileRetiredUtc is { } retired && binStartUtc + BinLength > retired)
            return ValidationErrors.BinAfterRetirement;
        return null;
    }

    /// <summary>The observer's first count (revision 1) of a line and bin; <see cref="CaptureProblem"/> must be null.</summary>
    public virtual ManualCount Capture(Guid lineId, DateTime binStartUtc, Guid observerId, int crossingsIn, int crossingsOut, DateTime nowUtc,
        TimeZoneInfo siteZone, DateTime? profileRetiredUtc, string idempotencyKey = null)
    {
        RequireUtc(nowUtc, nameof(nowUtc));
        if (IsTransient)
            throw new InvalidOperationException("Save the campaign before its counts.");
        if (CaptureProblem(lineId, binStartUtc, observerId, nowUtc, siteZone, profileRetiredUtc) is { } problem)
            throw new InvalidOperationException(problem);
        return new ManualCount(this, lineId, binStartUtc, observerId, 1, crossingsIn, crossingsOut, null, null, nowUtc, idempotencyKey);
    }

    /// <summary>
    /// Why <paramref name="observerId"/> cannot correct <paramref name="current"/> now, or null: the count is one of this
    /// campaign's and the observer's own (another observer's answers like a missing one), the observer did not create or start
    /// the campaign (<see cref="ObserverProblem"/>), the campaign runs, and the count has fewer than
    /// <see cref="ManualCount.MaxRevisions"/> revisions. Whether it is the latest revision is the service's check (it needs
    /// the stored revisions).
    /// </summary>
    public virtual string CorrectionProblem(ManualCount current, Guid observerId)
    {
        if (current is null || current.CampaignId != Id || current.ObserverId != observerId || observerId == Guid.Empty)
            return ValidationErrors.NotFound;
        if (ObserverProblem(observerId) is { } observer)
            return observer;
        if (RunningProblem() is { } running)
            return running;
        return current.Revision >= ManualCount.MaxRevisions ? ValidationErrors.TooManyRevisions : null;
    }

    /// <summary>
    /// A correction: a new revision of the same line, bin and observer with the corrected crossings and a reason. The
    /// corrected revision is never changed (counts are evidence).
    /// </summary>
    public virtual ManualCount Correct(ManualCount current, Guid observerId, int crossingsIn, int crossingsOut, string reason, DateTime nowUtc,
        string idempotencyKey = null)
    {
        RequireUtc(nowUtc, nameof(nowUtc));
        if (CorrectionProblem(current, observerId) is { } problem)
            throw new InvalidOperationException(problem);
        return new ManualCount(this, current.LineId, current.BinStartUtc, observerId, current.Revision + 1, crossingsIn, crossingsOut,
            DisplayText.Require(reason, ManualCount.MaxReasonLength, nameof(reason)), current.Id, nowUtc, idempotencyKey);
    }

    private string RunningProblem() =>
        Status switch
        {
            ValidationCampaignStatus.Planned => ValidationErrors.NotStarted,
            ValidationCampaignStatus.Closed => ValidationErrors.Closed,
            _ => null
        };

    #endregion

    #region Rules

    /// <summary>
    /// Why the scope is not valid for <paramref name="profile"/>, or null: 1 to <see cref="MaxZones"/> distinct queue zones of
    /// the version, and at most <see cref="MaxLines"/> distinct lines of the version, each on a queue zone in scope or on one of
    /// its overflow bands (the lines whose crossings the queue engine counts for that zone, line_minute).
    /// </summary>
    public static string ScopeProblem(ZoneProfile profile, IReadOnlyCollection<Guid> zoneIds, IReadOnlyCollection<Guid> lineIds)
    {
        if (profile is null || zoneIds is null || lineIds is null)
            return ValidationErrors.InvalidScope;
        if (zoneIds.Count is 0 or > MaxZones || lineIds.Count > MaxLines || zoneIds.Distinct().Count() != zoneIds.Count || lineIds.Distinct().Count() != lineIds.Count)
            return ValidationErrors.InvalidScope;
        var zones = new HashSet<Guid>();
        foreach (var id in zoneIds)
        {
            var zone = profile.Zones.FirstOrDefault(z => z.Id == id);
            if (zone is null || zone.Kind != ZoneKind.Queue)
                return ValidationErrors.InvalidScope;
            zones.Add(id);
        }

        foreach (var id in lineIds)
        {
            var line = profile.Lines.FirstOrDefault(l => l.Id == id);
            if (line is null || OwningQueueZone(line) is not { Id: { } owner } || !zones.Contains(owner))
                return ValidationErrors.InvalidScope;
        }

        return null;
    }

    /// <summary>1 to <see cref="MaxDays"/> distinct days from <see cref="DaysBack"/> days before <paramref name="today"/> to <see cref="DaysAhead"/> after.</summary>
    public static bool AreValidDays(IReadOnlyCollection<DateOnly> days, DateOnly today) =>
        days is { Count: > 0 and <= MaxDays } && days.Distinct().Count() == days.Count &&
        days.All(day => day >= today.AddDays(-DaysBack) && day <= today.AddDays(DaysAhead));

    /// <summary>Each target is empty (its placeholder) or within its bounds.</summary>
    public static bool AreValidTargets(int? binsPerLine, int? tracerRuns) =>
        (binsPerLine is null or (>= 1 and <= MaxTargetBinsPerLine)) && (tracerRuns is null or (>= 0 and <= MaxTargetTracerRuns));

    /// <summary>A UTC instant on a 15-minute boundary (the start of a bin of F18).</summary>
    public static bool IsBinStart(DateTime value) => value.Kind == DateTimeKind.Utc && value.Ticks % BinLength.Ticks == 0;

    /// <summary>The local day a bin starts on in the site's time zone.</summary>
    public static DateOnly LocalDay(DateTime binStartUtc, TimeZoneInfo siteZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(binStartUtc, DateTimeKind.Utc), siteZone));

    /// <summary>A local date written "yyyy-MM-dd".</summary>
    public static bool TryParseDay(string text, out DateOnly day)
    {
        day = default;
        return text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    public static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The queue zone whose key counts a line: its own zone when that is a queue zone, the band's queue zone for an overflow band, otherwise none.</summary>
    public static Zone OwningQueueZone(Line line) =>
        line?.Zone switch
        {
            { Kind: ZoneKind.Queue } zone => zone,
            { Kind: ZoneKind.Overflow, QueueZone: { Kind: ZoneKind.Queue } queue } => queue,
            _ => null
        };

    /// <summary>What the audit log keeps of the campaign: no names of people, only its own name quoted as a JSON string.</summary>
    public virtual string AuditSummary() =>
        string.Create(CultureInfo.InvariantCulture,
            $"site={SiteCode}; name={System.Text.Json.JsonSerializer.Serialize(Name)}; status={Status}; profileVersion={ProfileVersion}; zones={Zones.Count}; lines={Lines.Count}; days={PlannedDays}; " +
            $"targets={TargetBinsPerLine}/{TargetTracerRuns}{(TargetsPlaceholder ? " (placeholder)" : string.Empty)}");

    private static void RequireUtc(DateTime value, string paramName)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Instants are UTC.", paramName);
    }

    #endregion
}

/// <summary>A queue zone in a campaign's scope (ARV-104a): a zone of the campaign's profile version, with its name copied (a published version never changes).</summary>
public class ValidationCampaignZone : EntityBase<ValidationCampaignZone>
{
    protected ValidationCampaignZone()
    {
    }

    internal ValidationCampaignZone(ValidationCampaign campaign, Zone zone)
    {
        Campaign = campaign;
        ProfileId = campaign.ProfileId;
        ZoneId = zone.Id.GetValueOrDefault();
        ZoneName = zone.Name;
    }

    public virtual ValidationCampaign Campaign { get; protected set; }
    public virtual Guid ProfileId { get; protected set; }
    public virtual Guid ZoneId { get; protected set; }

    [MaxLength(200)]
    public virtual string ZoneName { get; protected set; }
}

/// <summary>
/// A line in a campaign's scope (ARV-104a): a line of the campaign's profile version on a queue zone in scope or one of its
/// overflow bands, with its name, role and that queue zone's name copied, so the comparison (ARV-104e) finds its line_minute
/// rows by zone key and line name.
/// </summary>
public class ValidationCampaignLine : EntityBase<ValidationCampaignLine>
{
    protected ValidationCampaignLine()
    {
    }

    internal ValidationCampaignLine(ValidationCampaign campaign, Line line, Zone queueZone)
    {
        Campaign = campaign;
        ProfileId = campaign.ProfileId;
        LineId = line.Id.GetValueOrDefault();
        LineName = line.Name;
        LineRole = line.Role;
        QueueZoneName = queueZone.Name;
    }

    public virtual ValidationCampaign Campaign { get; protected set; }
    public virtual Guid ProfileId { get; protected set; }
    public virtual Guid LineId { get; protected set; }

    [MaxLength(200)]
    public virtual string LineName { get; protected set; }

    public virtual LineRole LineRole { get; protected set; }

    [MaxLength(200)]
    public virtual string QueueZoneName { get; protected set; }
}
