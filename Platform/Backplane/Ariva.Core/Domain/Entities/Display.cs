using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A passenger display (ARV-058, wiki 12 Passenger display): a 16:9 board at one place of a site showing, per entry, the
/// nowcast of one queue zone as a band of minutes ("10 to 15 min") in the site's languages. The band changes only when
/// the nowcast leaves it by the hysteresis; a degraded nowcast shows a band twice as wide; a nowcast older than the stale
/// threshold shows the display's neutral message instead of a number. The player authenticates with the display's own
/// credential (stored as a prefix and a SHA-256, like a device's), never as a user.
/// </summary>
public class Display : BaseSoftDeletableEntity<Display>, ISiteBound
{
    public const int MaxEntries = 12;
    public const int MaxLabelLength = 80;
    public const int MaxMessageLength = 200;
    public const int MaxNameLength = 200;

    /// <summary>The stored JSON's room (script 0035): characters outside the basic plane are stored escaped, 12 for 2.</summary>
    public const int MaxEntriesJsonLength = 16000;

    public const int MaxFallbackJsonLength = 4000;

    /// <summary>The languages a board has words for (resource files of the web board): English, Arabic, Portuguese, Swahili.</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = ["en", "ar", "pt", "sw"];

    protected Display()
    {
    }

    public Display(string siteCode, string code, DisplayValues values)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        TopologyCodes.Require(code, nameof(code));
        SiteCode = siteCode;
        Code = code;
        Set(values);
    }

    public virtual string SiteCode { get; protected set; }

    /// <summary>The display's code, unique among live displays; the player's address names it.</summary>
    public virtual string Code { get; protected set; }

    public virtual string Name { get; protected set; }
    public virtual string Location { get; protected set; }
    public virtual DisplayOrientation Orientation { get; protected set; }

    /// <summary>The board's languages in order, comma separated (for example "ar,en").</summary>
    public virtual string Languages { get; protected set; }

    public virtual int BandMinutes { get; protected set; }
    public virtual double HysteresisMinutes { get; protected set; }
    public virtual int StaleSeconds { get; protected set; }

    /// <summary>The entries as JSON: <c>[{"zone":"A-CIT","labels":{"en":"Citizens","ar":"..."}}]</c>.</summary>
    public virtual string Entries { get; protected set; }

    /// <summary>The neutral message per language, as JSON, shown when the data is stale.</summary>
    public virtual string FallbackMessages { get; protected set; }

    public virtual bool Enabled { get; protected set; }

    /// <summary>The first characters of the player's credential, to find the display and to show administrators.</summary>
    public virtual string CredentialPrefix { get; protected set; }

    public virtual string CredentialHash { get; protected set; }
    public virtual DateTime? CredentialIssuedOn { get; protected set; }

    public virtual IReadOnlyList<string> LanguageList => (Languages ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);

    public virtual IReadOnlyList<DisplayEntry> EntryList => Entries is null ? [] : JsonSerializer.Deserialize<List<DisplayEntry>>(Entries, Json) ?? [];

    public virtual IReadOnlyDictionary<string, string> FallbackMap =>
        FallbackMessages is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(FallbackMessages, Json) ?? [];

    public virtual void Set(DisplayValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = values.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(values));
        Name = values.Name.Trim();
        Location = string.IsNullOrWhiteSpace(values.Location) ? null : values.Location.Trim();
        Orientation = values.Orientation;
        Languages = string.Join(',', values.Languages);
        BandMinutes = values.BandMinutes;
        HysteresisMinutes = values.HysteresisMinutes;
        StaleSeconds = values.StaleSeconds;
        Entries = EntriesJson(values.Entries);
        FallbackMessages = FallbackJson(values.Fallback);
        Enabled = values.Enabled;
    }

    /// <summary>A new credential replaces the old one at once.</summary>
    public virtual void SetCredential(string prefix, string hash, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (hash is not { Length: 64 })
            throw new ArgumentException("A credential hash is 64 hex characters.", nameof(hash));
        CredentialPrefix = prefix;
        CredentialHash = hash;
        CredentialIssuedOn = utcNow;
    }

    public virtual DisplayValues Values() =>
        new(Name, Location, Orientation, LanguageList, BandMinutes, HysteresisMinutes, StaleSeconds, EntryList, FallbackMap, Enabled);

    /// <summary>What the audit trail keeps of a display: its values, never its credential.</summary>
    public virtual string AuditSummary() =>
        JsonSerializer.Serialize(new
        {
            Code, SiteCode, Name, Location, Orientation = Orientation.ToString(), Languages, BandMinutes, HysteresisMinutes, StaleSeconds, Enabled,
            Zones = EntryList.Select(e => e.Zone).ToList(), CredentialPrefix
        }, Json);

    /// <summary>
    /// Stored as JSON text without escaping letters outside ASCII (Arabic labels keep their length); the values are never
    /// written into HTML by the server, and every reader parses them as JSON.
    /// </summary>
    internal static string EntriesJson(IEnumerable<DisplayEntry> entries) => JsonSerializer.Serialize(entries.Select(e => e.Trimmed()).ToList(), Json);

    internal static string FallbackJson(IReadOnlyDictionary<string, string> fallback) =>
        JsonSerializer.Serialize(fallback.ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal), Json);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>One line of a board: the queue zone whose nowcast it shows and its label in each of the display's languages.</summary>
public sealed record DisplayEntry(string Zone, IReadOnlyDictionary<string, string> Labels)
{
    internal DisplayEntry Trimmed() =>
        new(Zone.Trim(), Labels.ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal));
}

/// <summary>A display's settings as checked values (ARV-058).</summary>
public sealed record DisplayValues(
    string Name,
    string Location,
    DisplayOrientation Orientation,
    IReadOnlyList<string> Languages,
    int BandMinutes,
    double HysteresisMinutes,
    int StaleSeconds,
    IReadOnlyList<DisplayEntry> Entries,
    IReadOnlyDictionary<string, string> Fallback,
    bool Enabled)
{
    public const int MinBand = 1;
    public const int MaxBand = 30;
    public const int MinStale = 60;
    public const int MaxStale = 1800;

    private static bool Clean(string text, int max) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= max && DisplayText.IsClean(text.Trim());

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Clean(Name, Display.MaxNameLength))
            problems.Add($"A name is 1 to {Display.MaxNameLength} characters without control or invisible characters.");
        if (!string.IsNullOrWhiteSpace(Location) && !Clean(Location, Display.MaxNameLength))
            problems.Add($"A location is at most {Display.MaxNameLength} characters without control or invisible characters.");
        if (!Enum.IsDefined(Orientation))
            problems.Add("Unknown orientation.");
        var languages = Languages ?? [];
        if (languages.Count is 0 or > 4 || languages.Distinct(StringComparer.Ordinal).Count() != languages.Count ||
            languages.Any(l => !Enumerable.Contains(Display.SupportedLanguages, l, StringComparer.Ordinal)))
            problems.Add("A display shows 1 to 4 of the languages en, ar, pt and sw, each once.");
        if (BandMinutes is < MinBand or > MaxBand)
            problems.Add($"A band is {MinBand} to {MaxBand} minutes wide.");
        if (!double.IsFinite(HysteresisMinutes) || HysteresisMinutes < 0 || HysteresisMinutes >= BandMinutes)
            problems.Add("The hysteresis is from 0 to less than the band's width, in minutes.");
        if (StaleSeconds is < MinStale or > MaxStale)
            problems.Add($"Data is stale after {MinStale} to {MaxStale} seconds.");
        var entries = Entries ?? [];
        if (entries.Count is 0 or > Display.MaxEntries)
            problems.Add($"A display shows 1 to {Display.MaxEntries} entries.");
        foreach (var entry in entries)
        {
            if (entry is null || !Clean(entry.Zone, 200))
            {
                problems.Add("Every entry names a queue zone.");
                continue;
            }

            if (entry.Labels is null || languages.Any(l => !entry.Labels.TryGetValue(l, out var label) || !Clean(label, Display.MaxLabelLength)) ||
                entry.Labels.Keys.Any(k => !Enumerable.Contains(languages, k, StringComparer.Ordinal)))
                problems.Add($"Entry {entry.Zone.Trim()} needs a label of 1 to {Display.MaxLabelLength} characters in each of the display's languages, and no other.");
        }

        if (entries.Where(e => e?.Zone is not null).GroupBy(e => e.Zone.Trim(), StringComparer.Ordinal).Any(g => g.Count() > 1))
            problems.Add("A queue zone appears once on a display.");
        if (Fallback is null || languages.Any(l => !Fallback.TryGetValue(l, out var message) || !Clean(message, Display.MaxMessageLength)) ||
            Fallback.Keys.Any(k => !Enumerable.Contains(languages, k, StringComparer.Ordinal)))
            problems.Add($"The neutral message for stale data is 1 to {Display.MaxMessageLength} characters in each of the display's languages, and no other.");
        // Within those limits, labels and messages written mostly in emoji or other characters outside the basic plane can
        // still outgrow the stored JSON: refuse them here rather than at the database.
        if (problems.Count == 0 && (Display.EntriesJson(entries).Length > Display.MaxEntriesJsonLength ||
                                    Display.FallbackJson(Fallback).Length > Display.MaxFallbackJsonLength))
            problems.Add("The labels and messages are too long to store; use fewer symbols or shorter text.");
        return problems;
    }
}
