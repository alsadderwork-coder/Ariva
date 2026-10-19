using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Ariva.Api.Common.Logging;

/// <summary>How a logged value is protected (ARV-091, ASVS 5.0 V16.2.5), chosen by the name of the property that holds it.</summary>
public enum LogProtectionLevel
{
    /// <summary>Logged as it is (credentials inside free text are still masked, and so are email addresses).</summary>
    None,

    /// <summary>Credentials and keys: the value is replaced by <see cref="RedactionEnricher.Redacted"/>.</summary>
    Omit,

    /// <summary>Hashed: pseudonymous or border-only data (sensor track ids, desk codes) that airport operators must not read in the logs.</summary>
    Hash,

    /// <summary>Masked: staff email addresses, as <c>a***@example.com</c>.</summary>
    Mask
}

/// <summary>
/// The log protection levels (ARV-091, ASVS 5.0 V16.2.5; CWE-532), applied by <see cref="RedactionEnricher"/> to every event
/// before any sink sees it, by property name (compared ignoring case, '-' and '_'), also inside destructured objects,
/// dictionaries (their string keys too) and arrays:
/// <list type="bullet">
/// <item><b>Omit</b>: credentials and keys (<see cref="RedactionEnricher.SensitiveNames"/>), as before.</item>
/// <item><b>Hash</b>: sensor track ids and desk codes (<see cref="HashedNames"/>, and names ending as they do), which are
/// pseudonymous or border-only data: HMAC-SHA256 under the day's key, truncated to <see cref="HashLength"/> hex characters
/// after <see cref="HashMarker"/>, so lines about the same desk or track correlate within a UTC day and never across days.</item>
/// <item><b>Mask</b>: staff email addresses (<see cref="MaskedNames"/>, and names ending as they do), as <c>a***@example.com</c>;
/// an address inside any other text is masked the same way (<see cref="EmailAddress"/>, linear in the text's length).</item>
/// </list>
/// The day's key is HMAC-SHA256 of the UTC date under the master key, <see cref="KeySetting"/> (base64, at least
/// <see cref="MinimumKeyBytes"/> bytes) when it is set, otherwise a random key each process draws. So a pseudonym links lines
/// of one day only (wiki/14-Privacy-and-Data-Protection.md section 3, the logs row: no track ids or desk codes beyond the
/// operating day), with or without a configured key; the configured key only keeps a day's pseudonyms equal across the hosts
/// and their restarts. Whoever holds the
/// master key can recompute a day's pseudonym of a known desk code, so it is a secret: outside vm-local it must come from the
/// environment (a Kubernetes Secret), and a host refuses it from a configuration file. A key that is set but malformed stops the
/// host. Exception messages are not rewritten (Serilog hands sinks the exception object): code must not put these values in
/// exception messages either.
/// </summary>
public sealed partial class LogProtection
{
    #region Constants

    /// <summary>The configuration key of the pseudonym key (base64).</summary>
    public const string KeySetting = "Logging:PseudonymKey";

    /// <summary>The shortest key accepted, in bytes.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>Hex characters kept of the HMAC (48 bits): enough to tell desks and tracks apart in a day's logs.</summary>
    public const int HashLength = 12;

    /// <summary>Put before every hash, so a reader sees that the value is a pseudonym.</summary>
    public const string HashMarker = "#";

    /// <summary>What a masked value keeps after its first character.</summary>
    public const string MaskText = "***";

    /// <summary>Put before the date whose key is derived, so the day keys serve nothing else.</summary>
    private const string DayKeyLabel = "ariva-log-pseudonym:";

    #endregion

    #region Fields

    /// <summary>Names whose values are hashed (exact names; <see cref="HashedSuffixes"/> covers the longer ones).</summary>
    public static readonly IReadOnlySet<string> HashedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Track", "Tracks", "TrackId", "TrackIds", "Desk", "Desks", "DeskCode", "DeskCodes", "DeskKey", "DeskKeys", "DeskZone", "DeskZoneKey"
    };

    /// <summary>Names whose values are masked as email addresses (exact names; <see cref="MaskedSuffixes"/> covers the longer ones).</summary>
    public static readonly IReadOnlySet<string> MaskedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Email", "Emails", "EmailAddress", "EmailAddresses", "Mail", "MailAddress", "Recipient", "Recipients", "RecipientAddress", "ReplyTo"
    };

    /// <summary>Endings that make a name hashed, for example AmanDeskCode or NamespacedTrackId.</summary>
    private static readonly string[] HashedSuffixes = ["TrackId", "TrackIds", "DeskCode", "DeskCodes", "DeskKey", "DeskKeys"];

    /// <summary>Endings that make a name masked, for example StaffEmail or AlertRecipients.</summary>
    private static readonly string[] MaskedSuffixes = ["Email", "Emails", "EmailAddress", "EmailAddresses", "MailAddress", "Recipient", "Recipients"];

    private readonly byte[] _key;
    private readonly TimeProvider _time;
    private volatile DayKey _dayKey;

    #endregion

    #region Constructors

    /// <summary>Protection with a random key of this process's own.</summary>
    public LogProtection()
        : this(RandomNumberGenerator.GetBytes(MinimumKeyBytes))
    {
    }

    /// <summary>Protection with the given master key.</summary>
    /// <param name="key">At least <see cref="MinimumKeyBytes"/> bytes.</param>
    /// <param name="timeProvider">The clock that names the day; the system's by default.</param>
    public LogProtection(byte[] key, TimeProvider timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinimumKeyBytes)
            throw new ArgumentException($"The pseudonym key must have at least {MinimumKeyBytes} bytes.", nameof(key));
        _key = [.. key];
        _time = timeProvider ?? TimeProvider.System;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Protection keyed by <see cref="KeySetting"/>, or by a random key of this process's own when the setting is empty. The
    /// setting is optional; one that is set must be base64 of at least <see cref="MinimumKeyBytes"/> bytes and, outside
    /// vm-local, must not come from a configuration file (appsettings): it is a secret, given by the environment.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's own environment name.</param>
    /// <returns>The protection.</returns>
    /// <exception cref="InvalidOperationException">The key is set but is not base64, is too short, or comes from a file outside vm-local.</exception>
    public static LogProtection From(IConfiguration configuration, string environment = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = configuration[KeySetting];
        if (string.IsNullOrWhiteSpace(text))
            return new LogProtection();

        if (environment != Hosting.ArivaEnvironment.VmLocal && FromAFile(configuration))
            throw new InvalidOperationException($"{KeySetting} comes from a configuration file; outside vm-local it is a secret, set from a Kubernetes Secret in the environment.");
        var key = new byte[text.Length];
        if (!Convert.TryFromBase64String(text.Trim(), key, out var written) || written < MinimumKeyBytes)
            throw new InvalidOperationException($"{KeySetting} must be base64 of at least {MinimumKeyBytes} random bytes, or not set at all.");
        return new LogProtection(key[..written]);
    }

    /// <summary>The level a property, member or dictionary key of this name gets.</summary>
    /// <param name="name">The name as logged.</param>
    /// <returns>The level; <see cref="LogProtectionLevel.None"/> for any other name.</returns>
    public static LogProtectionLevel LevelOf(string name)
    {
        if (string.IsNullOrEmpty(name))
            return LogProtectionLevel.None;

        var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (RedactionEnricher.SensitiveNames.Contains(normalized))
            return LogProtectionLevel.Omit;
        if (HashedNames.Contains(normalized) || EndsWithAny(normalized, HashedSuffixes))
            return LogProtectionLevel.Hash;
        if (MaskedNames.Contains(normalized) || EndsWithAny(normalized, MaskedSuffixes))
            return LogProtectionLevel.Mask;
        return LogProtectionLevel.None;
    }

    /// <summary>
    /// The value's pseudonym for today (UTC): <see cref="HashMarker"/> and the first <see cref="HashLength"/> hex characters of
    /// its HMAC-SHA256 under the day's key.
    /// </summary>
    /// <param name="value">The value, as logged.</param>
    /// <returns>The pseudonym; the same value gives the same pseudonym under the same key on the same day.</returns>
    public string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var mac = HMACSHA256.HashData(KeyOf(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)), Encoding.UTF8.GetBytes(value));
        return HashMarker + Convert.ToHexStringLower(mac, 0, HashLength / 2);
    }

    /// <summary>
    /// Masks one value: an email address keeps the first character of its local part and its domain
    /// (<c>a***@example.com</c>); any other text keeps its first character only.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The masked value; null and empty stay as they are.</returns>
    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var trimmed = value.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at > 0 && at < trimmed.Length - 1)
            return string.Concat(trimmed.AsSpan(0, 1), MaskText, trimmed.AsSpan(at));
        return string.Concat(trimmed.AsSpan(0, Math.Min(1, trimmed.Length)), MaskText);
    }

    /// <summary>Masks every email address inside free text. Returns the same instance when nothing changed.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with each address masked as <see cref="Mask"/> does.</returns>
    public static string MaskAddressesIn(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@', StringComparison.Ordinal))
            return text;

        var result = EmailAddress().Replace(text, m => Mask(m.Value));
        return string.Equals(result, text, StringComparison.Ordinal) ? text : result;
    }

    private static bool EndsWithAny(string name, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The key of a day: HMAC-SHA256 of the date under the master key, kept for the day.</summary>
    private byte[] KeyOf(DateOnly day)
    {
        var current = _dayKey;
        if (current is not null && current.Day == day)
            return current.Key;
        var key = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(DayKeyLabel + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        _dayKey = new DayKey(day, key);
        return key;
    }

    /// <summary>Whether the provider that gives the key, the last that holds it, reads a file (JSON, XML or INI).</summary>
    private static bool FromAFile(IConfiguration configuration) =>
        configuration is IConfigurationRoot root &&
        root.Providers.Reverse().FirstOrDefault(provider => provider.TryGet(KeySetting, out _)) is FileConfigurationProvider;

    /// <summary>
    /// An email address in free text (CWE-1333): a run of local part characters, the literal <c>@</c>, then dot-separated
    /// labels. It runs on <see cref="RegexOptions.NonBacktracking"/>, linear in the text's length, without a timeout: a timeout
    /// would throw inside the enricher, and Serilog emits an event whose enricher threw as it is, unmasked. The leftmost match
    /// starts where a run of address characters starts, so no lookbehind is needed.
    /// </summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Timeout.Infinite)]
    public static partial Regex EmailAddress();

    #endregion

    #region Types

    private sealed record DayKey(DateOnly Day, byte[] Key);

    #endregion
}
