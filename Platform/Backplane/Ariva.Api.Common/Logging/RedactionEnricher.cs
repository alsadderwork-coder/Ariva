using System.Globalization;
using System.Text.RegularExpressions;
using Ariva.Infra.Security;
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
    public const string Redacted = SecretText.Redacted;

    /// <summary>The hosting scope's property that holds the request path.</summary>
    public const string RequestPathProperty = "RequestPath";

    /// <summary>The credential names (<see cref="SecretText.SensitiveNames"/>; ARV-285: kept in Ariva.Infra with the text rules).</summary>
    public static readonly IReadOnlySet<string> SensitiveNames = SecretText.SensitiveNames;

    /// <summary>The endings of a longer credential name (<see cref="SecretText.SensitiveSuffixes"/>).</summary>
    public static readonly IReadOnlyList<string> SensitiveSuffixes = SecretText.SensitiveSuffixes;

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

    /// <summary>Masks credentials inside free text (<see cref="SecretText.Redact"/>). Returns the same instance when nothing changed.</summary>
    public static string RedactText(string text) => SecretText.Redact(text);

    /// <summary>
    /// ARV-212: the same protection as a string property of no level of its own (<see cref="RedactText"/>, then email
    /// addresses masked: <see cref="SecretText.Protect"/>), for text that is not a property, such as an exception's
    /// (<see cref="ProtectedExceptionSink"/>). ARV-285: the dead letters, the outbox and the jobs protect their text with the same rules.
    /// </summary>
    public static string ProtectText(string text) => SecretText.Protect(text);

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
                    _ => SecretText.Protect(text)
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

    // ARV-285: the rules live in SecretText (Ariva.Infra), so the dead letters, the outbox and the jobs protect text as the logs
    // do; these name them where the tests and the inventory look for them.
    public static Regex AuthorizationScheme() => SecretText.AuthorizationScheme();

    public static Regex TokenQueryParameter() => SecretText.TokenQueryParameter();

    public static Regex DeviceKey() => SecretText.DeviceKey();

    public static Regex Jwt() => SecretText.Jwt();

    public static Regex PrivateKeyBlock() => SecretText.PrivateKeyBlock();

    public static Regex DerBase64() => SecretText.DerBase64();

    public static Regex IntegrationClientSecret() => SecretText.IntegrationClientSecret();

    public static Regex TemporaryPassword() => SecretText.TemporaryPassword();

    public static Regex RecoveryCode() => SecretText.RecoveryCode();

    public static Regex Base32Seed() => SecretText.Base32Seed();

    public static Regex CredentialHeader() => SecretText.CredentialHeader();

    public static Regex JsonMember() => SecretText.JsonMember();

    public static Regex Assignment() => SecretText.Assignment();

    public static Regex UnquotedAssignment() => SecretText.UnquotedAssignment();

    public static Regex PasswordAssignment() => SecretText.PasswordAssignment();
}
