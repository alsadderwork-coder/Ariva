using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// Removes credentials from every log event before any sink sees it (CWE-532, ARV-007). It is added in code after the
/// configuration is read, so no appsettings change can remove it.
/// <list type="bullet">
/// <item>A property, structure member or dictionary key with a sensitive name (Authorization, Cookie, Set-Cookie,
/// X-TOTP-Code, access_token, password and the others in <see cref="SensitiveNames"/>) has its value replaced.</item>
/// <item>Inside any other string: bearer and basic credentials, JWTs and token query parameters are masked, so a
/// request URL or a header dump logged as text is covered too.</item>
/// </list>
/// Exception messages are not rewritten (Serilog exposes the exception object, not its text); code must not put
/// secrets in exception messages, which the ARV-010a log test checks for the login flow.
/// </summary>
public sealed partial class RedactionEnricher : ILogEventEnricher
{
    public const string Redacted = "[REDACTED]";

    /// <summary>Names compared without case; '-' and '_' are ignored, so X-TOTP-Code matches XTotpCode.</summary>
    public static readonly IReadOnlySet<string> SensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "ProxyAuthorization", "Cookie", "SetCookie", "XTotpCode", "TotpCode", "Otp",
        "AccessToken", "RefreshToken", "IdToken", "Token", "BearerToken", "ApiKey", "XApiKey",
        "Password", "NewPassword", "CurrentPassword", "Secret", "ClientSecret", "RecoveryCode", "XArivaCsrf"
    };

    private static readonly ScalarValue RedactedValue = new(Redacted);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = IsSensitive(name) ? RedactedValue : Redact(value);
            if (!ReferenceEquals(redacted, value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
        }
    }

    public static bool IsSensitive(string name) =>
        !string.IsNullOrEmpty(name) && SensitiveNames.Contains(name.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal));

    /// <summary>Masks credentials inside free text. Returns the same instance when nothing changed.</summary>
    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = AuthorizationScheme().Replace(text, m => m.Groups["scheme"].Value + " " + Redacted);
        result = TokenQueryParameter().Replace(result, m => m.Groups["key"].Value + Redacted);
        result = Jwt().Replace(result, Redacted);
        return string.Equals(result, text, StringComparison.Ordinal) ? text : result;
    }

    private static LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                var redacted = RedactText(text);
                return ReferenceEquals(redacted, text) ? value : new ScalarValue(redacted);

            case StructureValue structure:
            {
                var changed = false;
                var properties = structure.Properties.Select(p =>
                {
                    var inner = IsSensitive(p.Name) ? RedactedValue : Redact(p.Value);
                    changed |= !ReferenceEquals(inner, p.Value);
                    return new LogEventProperty(p.Name, inner);
                }).ToList();
                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var elements = dictionary.Elements.Select(e =>
                {
                    var inner = e.Key.Value is string key && IsSensitive(key) ? RedactedValue : Redact(e.Value);
                    changed |= !ReferenceEquals(inner, e.Value);
                    return new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, inner);
                }).ToList();
                return changed ? new DictionaryValue(elements) : value;
            }

            case SequenceValue sequence:
            {
                var changed = false;
                var elements = sequence.Elements.Select(e =>
                {
                    var inner = Redact(e);
                    changed |= !ReferenceEquals(inner, e);
                    return inner;
                }).ToList();
                return changed ? new SequenceValue(elements) : value;
            }

            default:
                return value;
        }
    }

    // "Bearer abc.def", "Basic dXNlcjpwYXNz" (also inside "Authorization: Bearer ..." header dumps).
    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    // ?access_token=... in SignalR negotiate and WebSocket URLs, and other token-bearing query parameters.
    [GeneratedRegex(@"(?<key>[?&#](access_token|refresh_token|id_token|token|code|totp|otp|password)=)[^&#\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenQueryParameter();

    // A compact JWS anywhere in text (header.payload.signature, header starting with eyJ).
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();
}
