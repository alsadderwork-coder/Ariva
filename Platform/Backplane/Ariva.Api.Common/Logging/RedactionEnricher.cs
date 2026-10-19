using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// Protects every log event before any sink sees it (CWE-532, ARV-007, ARV-091). It is added in code after the
/// configuration is read, so no appsettings change can remove it. The levels are <see cref="LogProtection"/>'s:
/// <list type="bullet">
/// <item>Omit: a property, structure member or dictionary key with a sensitive name (Authorization, Cookie, Set-Cookie,
/// X-TOTP-Code, access_token, password and the others in <see cref="SensitiveNames"/>) has its value replaced.</item>
/// <item>Hash: a track id or desk code (<see cref="LogProtection.HashedNames"/>) becomes its keyed pseudonym, every value
/// and dictionary key beneath such a name included.</item>
/// <item>Mask: an email address (<see cref="LogProtection.MaskedNames"/>) keeps its first character and its domain.</item>
/// <item>Inside any other string: bearer and basic credentials, JWTs, token query parameters and device or display keys are
/// masked, so a request URL or a header dump logged as text is covered too; so is any email address.</item>
/// </list>
/// The request-level security events (<see cref="SecurityEvents.RequestEventIds"/>) lose the hosting scope's
/// <c>RequestPath</c>: they name the endpoint by its route pattern, and a path can hold the very value a request was refused for.
/// Exception messages are not rewritten (Serilog exposes the exception object, not its text); code must not put
/// secrets in exception messages, which the ARV-010a log test checks for the login flow.
/// </summary>
/// <param name="protection">The hashing key and rules.</param>
/// <param name="metrics">Counts the properties whose protection failed (<c>ariva.security.log_protection_failures</c>); none in tests.</param>
public sealed partial class RedactionEnricher(LogProtection protection, SecurityEventMetrics metrics = null) : ILogEventEnricher
{
    public const string Redacted = "[REDACTED]";

    /// <summary>The hosting scope's property that holds the request path.</summary>
    public const string RequestPathProperty = "RequestPath";

    /// <summary>
    /// Names compared without case; '-', '_' and ':' are ignored, so X-TOTP-Code matches XTotpCode, and a setting matches in
    /// each of its shapes: Loki:Password, Loki__Password and LokiPassword alike (ARV-091a).
    /// </summary>
    public static readonly IReadOnlySet<string> SensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "ProxyAuthorization", "Cookie", "SetCookie", "XTotpCode", "TotpCode", "Otp",
        "AccessToken", "RefreshToken", "IdToken", "Token", "BearerToken", "ApiKey", "XApiKey",
        "Password", "NewPassword", "CurrentPassword", "Secret", "ClientSecret", "RecoveryCode", "XArivaCsrf", "XArivaDeviceKey", "DeviceKey", "XArivaDisplayKey", "DisplayKey", "Credential",
        "PseudonymKey",
        // ARV-091a: the log shipping secrets as settings (a configuration dump, an environment listing).
        "LoggingPseudonymKey", "LokiPassword",
        "OtelExporterOtlpHeaders", "OtelExporterOtlpTracesHeaders", "OtelExporterOtlpMetricsHeaders", "OtelExporterOtlpLogsHeaders"
    };

    /// <summary>
    /// ARV-091a: any longer name ending in one of these is omitted too (ResetPassword, Loki:Password, KafkaSaslPassword,
    /// ClientSecret, Otlp:Headers, OTEL_EXPORTER_OTLP_TRACES_HEADERS). A name that only holds one elsewhere keeps its level
    /// (PasswordPolicy, SecretName, HeadersCount). The bare name Headers is not omitted whole: a header dictionary logged under
    /// it keeps its harmless values, and each credential header in it (Authorization, Cookie and the others) is omitted by key.
    /// </summary>
    public static readonly IReadOnlyList<string> SensitiveSuffixes = ["Password", "Secret", "Headers"];

    /// <summary>Put between a protected dictionary key and its collision index, when two keys protect alike.</summary>
    public const string CollisionMarker = "~";

    private static readonly ScalarValue RedactedValue = new(Redacted);

    private readonly LogProtection _protection = protection ?? throw new ArgumentNullException(nameof(protection));
    private readonly SecurityEventMetrics _metrics = metrics;

    /// <summary>With a random pseudonym key of this process's own (tests and tools; hosts pass the configured one).</summary>
    public RedactionEnricher()
        : this(new LogProtection())
    {
    }

    /// <summary>
    /// Protects every property of the event. It fails closed, property by property: a property whose protection throws (a
    /// value whose text cannot be read, for example) is replaced by <see cref="Redacted"/> and counted, and the other
    /// properties are still protected. An enricher that throws would let Serilog emit the event as it is, unprotected.
    /// </summary>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        bool requestEvent;
        try
        {
            requestEvent = SecurityEvents.IsRequestEvent(logEvent);
        }
#pragma warning disable CA1031 // fail closed: when in doubt, the request path goes
        catch (Exception)
#pragma warning restore CA1031
        {
            requestEvent = true;
            _metrics?.ProtectionFailed();
        }

        if (requestEvent)
            logEvent.RemovePropertyIfPresent(RequestPathProperty);

        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            LogEventPropertyValue protectedValue;
            try
            {
                protectedValue = Protect(value, LogProtection.LevelOf(name));
            }
#pragma warning disable CA1031 // fail closed: the property is replaced, never left as it is, and the event goes on
            catch (Exception)
#pragma warning restore CA1031
            {
                protectedValue = RedactedValue;
                _metrics?.ProtectionFailed();
            }

            if (!ReferenceEquals(protectedValue, value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, protectedValue));
        }
    }

    public static bool IsSensitive(string name) => LogProtection.LevelOf(name) == LogProtectionLevel.Omit;

    /// <summary>Masks credentials inside free text. Returns the same instance when nothing changed.</summary>
    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = AuthorizationScheme().Replace(text, m => m.Groups["scheme"].Value + " " + Redacted);
        result = TokenQueryParameter().Replace(result, m => m.Groups["key"].Value + Redacted);
        result = Jwt().Replace(result, Redacted);
        result = DeviceKey().Replace(result, m => m.Value[..5] + Redacted);
        return string.Equals(result, text, StringComparison.Ordinal) ? text : result;
    }

    /// <summary>
    /// Applies <paramref name="level"/> to a value and everything beneath it; a member or key beneath with a level of its own
    /// takes that level instead, and Omit always wins. Returns the same instance when nothing changed.
    /// </summary>
    private LogEventPropertyValue Protect(LogEventPropertyValue value, LogProtectionLevel level)
    {
        // A credential is replaced whole, whatever its shape: member names and elements can be secret too.
        if (level == LogProtectionLevel.Omit)
            return RedactedValue;

        switch (value)
        {
            case ScalarValue { Value: null }:
                return value;

            case ScalarValue { Value: string text }:
            {
                var changed = level switch
                {
                    LogProtectionLevel.Hash => _protection.Hash(text),
                    LogProtectionLevel.Mask => LogProtection.Mask(text),
                    _ => LogProtection.MaskAddressesIn(RedactText(text))
                };
                return ReferenceEquals(changed, text) ? value : new ScalarValue(changed);
            }

            case ScalarValue scalar when level == LogProtectionLevel.Hash:
                // A track id or desk code sent as a number, for example: hashed as its invariant text.
                return new ScalarValue(_protection.Hash(Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? string.Empty));

            case StructureValue structure:
            {
                var changed = false;
                var properties = structure.Properties.Select(p =>
                {
                    var inner = Protect(p.Value, Within(level, p.Name));
                    changed |= !ReferenceEquals(inner, p.Value);
                    return new LogEventProperty(p.Name, inner);
                }).ToList();
                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                // A key is data too (a dictionary keyed by desk code or by address): it takes the dictionary's level, as a value
                // would; its value takes the key's own level when the key is a name with one. Two keys can protect alike
                // (alice@x.com and anna@x.com both mask to a***@x.com): the later ones get a collision index, so every entry is
                // kept and no key is ever written in clear.
                var changed = false;
                var seen = new HashSet<ScalarValue>();
                var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                foreach (var (original, entry) in dictionary.Elements)
                {
                    var key = Distinct((ScalarValue)Protect(original, level), seen);
                    var inner = Protect(entry, Within(level, original.Value as string));
                    changed |= !ReferenceEquals(inner, entry) || !ReferenceEquals(key, original);
                    elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, inner));
                }

                return changed ? new DictionaryValue(elements) : value;
            }

            case SequenceValue sequence:
            {
                var changed = false;
                var elements = sequence.Elements.Select(e =>
                {
                    var inner = Protect(e, level);
                    changed |= !ReferenceEquals(inner, e);
                    return inner;
                }).ToList();
                return changed ? new SequenceValue(elements) : value;
            }

            default:
                return value;
        }
    }

    /// <summary>
    /// The key itself when no earlier key of the dictionary is equal to it, otherwise the key's text with the first free
    /// collision index (<c>a***@x.com~2</c>), built from the protected text only.
    /// </summary>
    private static ScalarValue Distinct(ScalarValue key, HashSet<ScalarValue> seen)
    {
        if (seen.Add(key))
            return key;

        var text = Convert.ToString(key.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        for (var index = 2; ; index++)
        {
            var candidate = new ScalarValue(text + CollisionMarker + index.ToString(CultureInfo.InvariantCulture));
            if (seen.Add(candidate))
                return candidate;
        }
    }

    /// <summary>The level beneath a member or key: its own when it has one, otherwise the level around it.</summary>
    private static LogProtectionLevel Within(LogProtectionLevel outer, string name)
    {
        var own = LogProtection.LevelOf(name);
        return own == LogProtectionLevel.None ? outer : own;
    }

    // "Bearer abc.def", "Basic dXNlcjpwYXNz" (also inside "Authorization: Bearer ..." header dumps).
    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    // ?access_token=... in SignalR negotiate and WebSocket URLs, and other token-bearing query parameters.
    [GeneratedRegex(@"(?<key>[?&#](access_token|refresh_token|id_token|token|code|totp|otp|password|key)=)[^&#\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenQueryParameter();

    // A compact JWS anywhere in text (header.payload.signature, header starting with eyJ).
    /// <summary>
    /// A device credential (ARV-022, ardk_) or a display player credential (ARV-058, ardp_), wherever it appears: the
    /// marker and 43 base64url characters.
    /// </summary>
    [GeneratedRegex(@"ard[kp]_[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();
}
