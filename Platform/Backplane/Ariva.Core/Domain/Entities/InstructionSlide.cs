using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A short multilingual instruction for passenger displays (ARV-141, wiki 11 section 9): a title and an optional body per
/// language, a built-in icon, how long it stays on screen, and the displays of its site it applies to (every display, those
/// showing one of its queue zones, or those named). Split displays rotate the slides that apply to them in their second
/// region, in the site's order; a slide can instead be pinned under the waits. Its words are plain text: checked here
/// (bounded, no control or invisible characters) and only ever shown as text. A retired slide is kept and shown nowhere.
/// </summary>
public class InstructionSlide : BaseAuditableEntity<InstructionSlide>, ISiteBound
{
    public const int MaxTitleLength = 80;
    public const int MaxBodyLength = 240;
    public const int MinDurationSeconds = 5;
    public const int MaxDurationSeconds = 60;
    public const int MaxTargets = 20;
    public const int MaxTargetLength = 200;
    public const int MaxSlidesPerSite = 100;

    /// <summary>The stored JSON's room (script 0056): characters outside the basic plane are stored escaped, 12 for 2.</summary>
    public const int MaxTextsJsonLength = 8000;

    public const int MaxTargetsJsonLength = 4000;

    protected InstructionSlide()
    {
    }

    public InstructionSlide(string siteCode, int position, InstructionSlideValues values)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        SiteCode = siteCode;
        MoveTo(position);
        Status = InstructionStatus.Active;
        Set(values);
    }

    public virtual string SiteCode { get; protected set; }

    /// <summary>The slide's place in its site's order, from 1: displays rotate their slides in this order.</summary>
    public virtual int Position { get; protected set; }

    public virtual InstructionScope Scope { get; protected set; }

    /// <summary>The queue zone names (Zone) or display codes (Display) it applies to, as a JSON array; empty for Site.</summary>
    [MaxLength(MaxTargetsJsonLength)]
    public virtual string Targets { get; protected set; }

    public virtual InstructionIcon Icon { get; protected set; }
    public virtual int DurationSeconds { get; protected set; }

    /// <summary>Shown under the waits instead of rotating (a display pins the first pinned slide that applies to it).</summary>
    public virtual bool Pinned { get; protected set; }

    public virtual InstructionStatus Status { get; protected set; }

    /// <summary>The words per language as JSON: <c>{"en":{"title":"...","body":"..."},"ar":{...}}</c>.</summary>
    [MaxLength(MaxTextsJsonLength)]
    public virtual string Texts { get; protected set; }

    public virtual IReadOnlyList<string> TargetList => Targets is null ? [] : JsonSerializer.Deserialize<List<string>>(Targets, Display.Json) ?? [];

    public virtual IReadOnlyDictionary<string, InstructionText> TextMap =>
        Texts is null ? new Dictionary<string, InstructionText>() : JsonSerializer.Deserialize<Dictionary<string, InstructionText>>(Texts, Display.Json) ?? [];

    public virtual bool IsActive => Status == InstructionStatus.Active;

    public virtual void Set(InstructionSlideValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = values.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(values));
        Scope = values.Scope;
        Targets = TargetsJson(values.Targets);
        Icon = values.Icon;
        DurationSeconds = values.DurationSeconds;
        Pinned = values.Pinned;
        Texts = TextsJson(values.Texts);
    }

    public virtual void MoveTo(int position)
    {
        if (position is < 1 or > MaxSlidesPerSite * 10)
            throw new ArgumentOutOfRangeException(nameof(position), "A slide's position is from 1.");
        Position = position;
    }

    public virtual void Retire() => Status = InstructionStatus.Retired;

    public virtual void Restore() => Status = InstructionStatus.Active;

    public virtual InstructionSlideValues Values() => new(Scope, TargetList, Icon, DurationSeconds, Pinned, TextMap);

    /// <summary>
    /// What the audit trail keeps of a slide: everything it shows and where. The values are refused when this would not fit
    /// an audit entry whole (<see cref="InstructionSlideValues.Problems"/>), so no word a display shows is ever cut from it.
    /// </summary>
    public virtual string AuditSummary() => SummaryOf(SiteCode, Position, Status, Scope, TargetList, Icon, DurationSeconds, Pinned, TextMap);

    internal static string SummaryOf(string siteCode, int position, InstructionStatus status, InstructionScope scope, IEnumerable<string> targets,
        InstructionIcon icon, int durationSeconds, bool pinned, IReadOnlyDictionary<string, InstructionText> texts) =>
        JsonSerializer.Serialize(new
        {
            SiteCode = siteCode, Position = position, Scope = scope.ToString(), Targets = targets, Icon = icon.ToString(), DurationSeconds = durationSeconds,
            Pinned = pinned, Status = status.ToString(),
            Texts = Display.SupportedLanguages.Where(texts.ContainsKey).ToDictionary(l => l, l => texts[l].Trimmed(), StringComparer.Ordinal)
        }, Display.Json);

    /// <summary>
    /// Whether the slide applies to a display of its site: Site to every one, Zone to those showing one of its queue zones,
    /// Display to those it names; never to another site's display, and never while retired.
    /// </summary>
    public virtual bool AppliesTo(string siteCode, string displayCode, IReadOnlyCollection<string> zones)
    {
        if (!IsActive || !string.Equals(siteCode, SiteCode, StringComparison.Ordinal))
            return false;
        return Scope switch
        {
            InstructionScope.Site => true,
            InstructionScope.Zone => TargetList.Any(t => Enumerable.Contains(zones, t, StringComparer.Ordinal)),
            InstructionScope.Display => Enumerable.Contains(TargetList, displayCode, StringComparer.Ordinal),
            _ => false
        };
    }

    internal static string TargetsJson(IEnumerable<string> targets) => JsonSerializer.Serialize((targets ?? []).Select(t => t.Trim()).ToList(), Display.Json);

    internal static string TextsJson(IReadOnlyDictionary<string, InstructionText> texts) =>
        JsonSerializer.Serialize(
            Display.SupportedLanguages.Where(texts.ContainsKey).ToDictionary(l => l, l => texts[l].Trimmed(), StringComparer.Ordinal), Display.Json);
}

/// <summary>A slide's words in one language: a title and an optional body, plain text.</summary>
public sealed record InstructionText(string Title, string Body)
{
    internal InstructionText Trimmed() => new(Title.Trim(), string.IsNullOrWhiteSpace(Body) ? null : Body.Trim());
}

/// <summary>An instruction slide's settings as checked values (ARV-141).</summary>
public sealed record InstructionSlideValues(
    InstructionScope Scope,
    IReadOnlyList<string> Targets,
    InstructionIcon Icon,
    int DurationSeconds,
    bool Pinned,
    IReadOnlyDictionary<string, InstructionText> Texts)
{
    public const string TooLongToAudit =
        "The words and targets together are too long to keep whole in the audit trail; use shorter text, fewer symbols or fewer targets.";

    private static bool Clean(string text, int max) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= max && DisplayText.IsClean(text.Trim());

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(Scope))
            problems.Add("A slide applies to its Site, a Zone or a Display.");
        var targets = Targets ?? [];
        if (Scope == InstructionScope.Site && targets.Count > 0)
            problems.Add("A slide for the whole site names no zone or display.");
        if (Scope is InstructionScope.Zone or InstructionScope.Display)
        {
            if (targets.Count is 0 or > InstructionSlide.MaxTargets)
                problems.Add($"A slide for zones or displays names 1 to {InstructionSlide.MaxTargets} of them.");
            else if (targets.Any(t => !Clean(t, InstructionSlide.MaxTargetLength)) ||
                     (Scope == InstructionScope.Display && targets.Any(t => !TopologyCodes.IsValid(t.Trim()))))
                problems.Add(Scope == InstructionScope.Zone ? "Every target is a queue zone's name." : "Every target is a display's code.");
            else if (targets.Select(t => t.Trim()).Distinct(StringComparer.Ordinal).Count() != targets.Count)
                problems.Add("A slide names each zone or display once.");
        }

        if (!Enum.IsDefined(Icon))
            problems.Add("The icon is one of the built-in set.");
        if (DurationSeconds is < InstructionSlide.MinDurationSeconds or > InstructionSlide.MaxDurationSeconds)
            problems.Add($"A slide stays on screen {InstructionSlide.MinDurationSeconds} to {InstructionSlide.MaxDurationSeconds} seconds.");
        var texts = Texts ?? new Dictionary<string, InstructionText>();
        if (texts.Count == 0 || texts.Keys.Any(k => !Enumerable.Contains(Display.SupportedLanguages, k, StringComparer.Ordinal)))
            problems.Add("A slide has words in at least one of the languages en, ar, pt and sw, and no other.");
        // Only the board's languages are judged by name: a key the caller chose is never repeated in a refusal (the line
        // above refuses any other).
        foreach (var language in Display.SupportedLanguages.Where(texts.ContainsKey))
        {
            var text = texts[language];
            if (text is null || !Clean(text.Title, InstructionSlide.MaxTitleLength))
                problems.Add($"The {language} title is 1 to {InstructionSlide.MaxTitleLength} characters without control or invisible characters.");
            else if (!string.IsNullOrWhiteSpace(text.Body) && !Clean(text.Body, InstructionSlide.MaxBodyLength))
                problems.Add($"The {language} text is at most {InstructionSlide.MaxBodyLength} characters without control or invisible characters.");
        }

        // Within those limits, words written mostly in emoji or other characters outside the basic plane can still outgrow
        // the stored JSON: refuse them here rather than at the database.
        if (problems.Count == 0 && (InstructionSlide.TextsJson(texts).Length > InstructionSlide.MaxTextsJsonLength ||
                                    InstructionSlide.TargetsJson(targets).Length > InstructionSlide.MaxTargetsJsonLength))
            problems.Add("The words are too long to store; use fewer symbols or shorter text.");
        // The audit trail keeps every word a display shows: the summary of the slide at its longest (a site code of 17
        // characters, a position of four digits, retired) must fit one audit entry, or the values are refused.
        else if (problems.Count == 0 && InstructionSlide.SummaryOf(new string('X', 17), 1000, InstructionStatus.Retired, Scope,
                     targets.Select(t => t.Trim()), Icon, DurationSeconds, Pinned, texts).Length > AuditEntry.SummaryLength)
            problems.Add(TooLongToAudit);
        return problems;
    }
}

/// <summary>
/// The slides a display shows (ARV-141): those of its site that apply to it and have words in at least one of its
/// languages, in the site's order, each with only the display's languages. The first pinned one goes under the waits;
/// every other one rotates in the second region.
/// </summary>
public static class InstructionSlides
{
    public sealed record Shown(InstructionSlide Slide, IReadOnlyList<(string Language, InstructionText Text)> Texts);

    public static (Shown Pinned, IReadOnlyList<Shown> Rotation) ForDisplay(IEnumerable<InstructionSlide> slides, Display display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var zones = display.EntryList.Select(e => e.Zone).ToList();
        var languages = display.LanguageList;
        var shown = (slides ?? [])
            .Where(s => s.AppliesTo(display.SiteCode, display.Code, zones))
            .OrderBy(s => s.Position)
            .Select(s =>
            {
                var map = s.TextMap;
                return new Shown(s, languages.Where(map.ContainsKey).Select(l => (l, map[l])).ToList());
            })
            .Where(s => s.Texts.Count > 0)
            .ToList();
        var pinned = shown.FirstOrDefault(s => s.Slide.Pinned);
        return (pinned, shown.Where(s => !ReferenceEquals(s, pinned)).ToList());
    }
}
