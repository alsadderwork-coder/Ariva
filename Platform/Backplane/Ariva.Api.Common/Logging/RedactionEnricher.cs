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
/// masked, so a request URL or a header dump logged as text is covered too; so is any email address. ARV-212: so are the
/// other credentials Ariva issues or stores, by their shapes (an integration client secret, a temporary password, a recovery
/// code, a TOTP seed, a private key block or a PKCS#12 or DER key in base64), and any value under a sensitive name written as
/// a JSON member, a <c>name=value</c> pair (a form body, a connection string) or a credential header line.</item>
/// </list>
/// The request-level security events (<see cref="SecurityEvents.RequestEventIds"/>) lose the hosting scope's
/// <c>RequestPath</c>: they name the endpoint by its route pattern, and a path can hold the very value a request was refused for.
/// An enricher cannot change an event's exception (Serilog exposes the object, not its text), so ARV-212 protects exception
/// text in <see cref="ProtectedExceptionSink"/>, which every host sink sits behind; code still keeps secrets out of the
/// messages it writes, which the ARV-010a log test checks for the login flow.
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
        // ARV-212: a connection string's short form of Password, the outbound secrets by their request names, and the refresh
        // cookie and the TickerQ hub ticket by their names (__Secure-ariva_rt, __Secure-ariva_jobs_hub, compared without '-' and
        // '_'), so a cookie written as name=value is omitted too.
        "Pwd", "TotpSeed", "HmacKey", "CertificatePfxBase64", "SecureArivaRt", "SecureArivaJobsHub",
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
        // ARV-212: the other credentials by their shapes, the longer shapes first (a temporary password holds two groups that
        // look like a recovery code), then any value under a sensitive name.
        result = PrivateKeyBlock().Replace(result, Redacted);
        result = DerBase64().Replace(result, Redacted);
        result = IntegrationClientSecret().Replace(result, IntegrationSecretMarker + Redacted);
        result = TemporaryPassword().Replace(result, Redacted);
        result = RecoveryCode().Replace(result, Redacted);
        result = Base32Seed().Replace(result, Redacted);
        result = CredentialHeader().Replace(result, HeaderValue);
        result = JsonMember().Replace(result, JsonValue);
        result = Assignment().Replace(result, AssignedValue);
        // A quoted value under a harmless name (ConnectionStrings="Host=db;Password=...") is passed over whole by the pass
        // above; this pass takes no quoted value, so it walks into the quotes and finds the pairs inside them.
        result = UnquotedAssignment().Replace(result, AssignedValue);
        // A password's value runs to the next ';' or the line's end, so one holding ',', '&', '?' or an angle bracket, which
        // the passes above stop at, goes whole too.
        result = PasswordAssignment().Replace(result, AssignedValue);
        return string.Equals(result, text, StringComparison.Ordinal) ? text : result;
    }

    /// <summary>
    /// ARV-212: the same protection as a string property of no level of its own (<see cref="RedactText"/>, then email
    /// addresses masked), for text that is not a property, such as an exception's (<see cref="ProtectedExceptionSink"/>).
    /// </summary>
    public static string ProtectText(string text) => LogProtection.MaskAddressesIn(RedactText(text));

    /// <summary>An integration client secret's marker (<c>IntegrationCredentials.SecretMarker</c> in Ariva.Infra), kept before the mask.</summary>
    private const string IntegrationSecretMarker = "ics_";

    /// <summary>
    /// A credential header's value: the scheme of an Authorization value stays when it is a known one (Bearer, Basic, Digest and
    /// the others of <see cref="AuthorizationSchemeWord"/>), the rest goes; a bare key is masked whole.
    /// </summary>
    private static string HeaderValue(Match match)
    {
        var value = match.Groups["value"];
        var prefix = match.Value[..(value.Index - match.Index)];
        if (match.Groups["name"].Value.EndsWith("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = AuthorizationSchemeWord().Match(value.Value);
            if (scheme.Success)
                return prefix + scheme.Groups["scheme"].Value + " " + Redacted;
        }

        return prefix + Redacted;
    }

    /// <summary>A JSON member's value, when the member's name is a credential's (<see cref="IsSensitive"/>).</summary>
    private static string JsonValue(Match match) =>
        IsSensitive(match.Groups["name"].Value)
            ? match.Value[..(match.Groups["value"].Index - match.Index)] + "\"" + Redacted + "\""
            : match.Value;

    /// <summary>A <c>name=value</c> pair's value (quoted or not), when the name is a credential's (<see cref="IsSensitive"/>).</summary>
    private static string AssignedValue(Match match) =>
        IsSensitive(match.Groups["name"].Value)
            ? match.Value[..(match.Groups["value"].Index - match.Index)] + Redacted
            : match.Value;

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

    // ARV-212: every rule below runs on the linear engine without a timeout (ARV-163; a timeout here would let the event
    // out unprotected, as Serilog emits an event whose enricher threw).

    /// <summary>A PEM private key, whatever its kind (PKCS#8, RSA, EC, encrypted), from its BEGIN line through its END line.</summary>
    [GeneratedRegex(@"-{5}BEGIN[A-Z0-9 ]{0,40}PRIVATE KEY-{5}(?:[^-]|-[^-])*(?:-{5}END[A-Z0-9 ]{0,40}PRIVATE KEY-{5})?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex PrivateKeyBlock();

    /// <summary>
    /// A DER or BER structure of 128 bytes or more in base64 on one line (an outbound endpoint's PKCS#12 client certificate,
    /// up to its 90,000 characters, a key or a certificate): a SEQUENCE (0x30, M) followed by a long form length, so MIA to
    /// MID for an indefinite length (0x80), MIG and MIH for one length byte (0x81), MII to MIL for two (0x82: MII under 16 KiB,
    /// MIJ to MIL from 16 to 64 KiB) and MIM to MIP for three (0x83). Four length bytes (MIQ onwards) would be over 16 MiB.
    /// </summary>
    [GeneratedRegex(@"\bMI[A-P][A-Za-z0-9+/]{60,}={0,2}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex DerBase64();

    /// <summary>An integration client secret (ARV-042): ics_ and 43 base64url characters, the marker kept.</summary>
    [GeneratedRegex(@"\bics_[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex IntegrationClientSecret();

    /// <summary>A temporary password (ARV-011): four groups of five from its alphabet without look-alikes, joined by dashes.</summary>
    [GeneratedRegex(@"\b[a-hjkmnp-zA-HJ-NP-Z2-9]{5}(?:-[a-hjkmnp-zA-HJ-NP-Z2-9]{5}){3}\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex TemporaryPassword();

    /// <summary>
    /// A recovery code as Ariva shows it (ARV-010c): two groups of five upper case Crockford base32 characters. A code typed
    /// otherwise reaches the log only in a request body, whose recoveryCode member the JSON rule omits.
    /// </summary>
    [GeneratedRegex(@"\b[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex RecoveryCode();

    /// <summary>
    /// A TOTP seed (a user's, an integration client's, an outbound endpoint's): RFC 4648 base32, upper case, 16 characters or
    /// more as a whole word (Ariva's are 32; an outbound seed 16 to 128).
    /// </summary>
    [GeneratedRegex(@"\b[A-Z2-7]{16,}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex Base32Seed();

    /// <summary>A credential header written as a line of a header dump (Cookie: ..., X-Api-Key: ...): its value to the line's end.</summary>
    [GeneratedRegex(@"\b(?<name>Proxy-Authorization|Authorization|Set-Cookie|Cookie|X-TOTP-Code|X-Api-Key|Api-Key|X-Ariva-Csrf|X-Ariva-Device-Key|X-Ariva-Display-Key)[ \t]*:[ \t]*(?<value>[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex CredentialHeader();

    /// <summary>A known scheme at the start of an Authorization value, kept so the line still says what was masked; any other first word goes too.</summary>
    [GeneratedRegex(@"^(?<scheme>Bearer|Basic|Digest|Negotiate|NTLM|Kerberos|HOBA|Mutual|DPoP|ApiKey|SCRAM-SHA-256|SCRAM-SHA-1|AWS4-HMAC-SHA256) ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    private static partial Regex AuthorizationSchemeWord();

    /// <summary>A JSON member with a string or number value; the value goes when the name is a credential's.</summary>
    [GeneratedRegex(@"""(?<name>[A-Za-z0-9_.:-]{1,64})""[ \t\r\n]*:[ \t\r\n]*(?<value>""(?:[^""\\\r\n]|\\.)*""?|[0-9]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex JsonMember();

    /// <summary>
    /// A <c>name=value</c> pair (a form body, a connection string, a settings line); the value goes when the name is a
    /// credential's (Password=, client_secret=, sasl.password=, __Secure-ariva_rt=). A value quoted as a connection string
    /// builder quotes one (in double quotes, a double quote doubled; or in single quotes) goes whole, to its closing quote or
    /// the line's end; an unquoted one goes to the next white space, ';', ',', '&amp;', '?', quote or angle bracket. A '?'
    /// ends it so that a pair in an address's query (<c>next=https://idp/cb?client_secret=...</c>) is matched as a pair of its
    /// own, with no second pass over the value: one pass, linear in the text's length.
    /// </summary>
    [GeneratedRegex(@"\b(?<name>[A-Za-z_][A-Za-z0-9_.-]{0,63})[ \t]*=[ \t]*(?<value>""(?:[^""\r\n]|"""")*""?|'[^'\r\n]*'?|[^\s;,&?""'<>]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex Assignment();

    /// <summary><see cref="Assignment"/> without its quoted values: the second pass, which reads the pairs inside a quoted value.</summary>
    [GeneratedRegex(@"\b(?<name>[A-Za-z_][A-Za-z0-9_.-]{0,63})[ \t]*=[ \t]*(?<value>[^\s;,&?""'<>]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex UnquotedAssignment();

    /// <summary>
    /// A password written as a pair (Password=, Pwd=, sasl.password=), its value to the next ';', quote or the line's end: a
    /// connection string builder leaves a value holding ',', '&amp;', '?' or an angle bracket unquoted. The value goes when the
    /// name is a credential's (<see cref="IsSensitive"/>); it only ever masks more.
    /// </summary>
    [GeneratedRegex(@"\b(?<name>[A-Za-z0-9_.-]{0,60}(?:password|pwd))[ \t]*=[ \t]*(?<value>[^;\r\n""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex PasswordAssignment();
}
