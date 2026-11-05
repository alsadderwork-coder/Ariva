using System.Globalization;
using System.Text.RegularExpressions;

namespace Ariva.Infra.Security;

/// <summary>
/// The protection the logs give free text (CWE-532; ARV-007, ARV-091, ARV-212), kept here so that text written outside the log
/// pipeline is protected the same way (ARV-285): a dead letter's error (<c>DeadLetters.Create</c>), the outbox's
/// <c>last_error</c> (<c>OutboxRelay</c>) and a job's exception as TickerQ keeps it and its dashboard shows it
/// (<c>JobRun</c> in Ariva.Api.Cronz). <see cref="Protect"/> masks the credentials Ariva issues or stores by their shapes and
/// any value under a credential's name (<see cref="Redact"/>), then every email address (<see cref="MaskAddressesIn"/>). The
/// log enricher (<c>RedactionEnricher</c> in Ariva.Api.Common) applies the same rules to every string property of no level of
/// its own, and <c>ProtectedExceptionSink</c> to an exception's text, so text protected here reads as the log writes it.
/// </summary>
public static partial class SecretText
{
    #region Constants

    /// <summary>What a masked credential is replaced with.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>What a masked value keeps after its first character.</summary>
    public const string MaskText = "***";

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

    /// <summary>
    /// ARV-293 (CWE-400): the most characters of one text the rules read. Nothing else bounds a text the logs protect: the
    /// enricher protects every string property of every event and the exception sink every exception's text, and a text of
    /// 1,000,000 characters took about a third of a second. A longer text is cut (<see cref="KeptLength"/>), the part kept
    /// protected and <see cref="CutNote"/> added, so one text costs at most what this many characters cost
    /// (docs/security/costly-operations.md, section 2).
    /// </summary>
    public const int MaxProtectedLength = 65_536;

    /// <summary>What follows the protected part of a text cut at <see cref="MaxProtectedLength"/>: the characters left out and the bound.</summary>
    public const string CutNote = " [{0} more characters left out: protection reads {1} at most]";

    #endregion

    #region Methods

    /// <summary>
    /// The protection the logs give a string of no level of its own: <see cref="Redact"/>, then every email address masked
    /// (<see cref="MaskAddressesIn"/>). Returns the same instance when nothing changed; null and empty stay as they are. A
    /// text longer than <see cref="MaxProtectedLength"/> is cut once, first (ARV-293, <see cref="Bounded"/>).
    /// </summary>
    public static string Protect(string text) => Bounded(text, kept => MaskAddressesInWhole(RedactWhole(kept)));

    /// <summary>
    /// ARV-285: <see cref="Protect"/>, then cut at <paramref name="maxLength"/> characters. Protected first, so a credential the
    /// cut would split is masked whole and no part of it is kept. Null becomes empty.
    /// </summary>
    public static string ProtectAndCut(string text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        var kept = Protect(text ?? string.Empty) ?? string.Empty;
        return kept.Length <= maxLength ? kept : kept[..maxLength];
    }

    /// <summary>
    /// Whether a name is a credential's: compared without case, and without '-', '_' and ':' (<see cref="Normalize"/>), it is one
    /// of <see cref="SensitiveNames"/> or a longer name ending in one of <see cref="SensitiveSuffixes"/>. The logs omit a value
    /// under such a name whole (<c>LogProtection.LevelOf</c>), and <see cref="Redact"/> masks one written as a pair or a member.
    /// </summary>
    public static bool IsSensitiveName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var normalized = Normalize(name);
        if (SensitiveNames.Contains(normalized))
            return true;
        foreach (var suffix in SensitiveSuffixes)
        {
            if (normalized.Length > suffix.Length && normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>A name as names are compared: without '-', '_' and ':' (X-TOTP-Code is XTotpCode, Loki__Password is LokiPassword).</summary>
    public static string Normalize(string name) =>
        name.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Masks credentials inside free text: Bearer and Basic credentials, token query parameters, JWTs, device and display keys,
    /// then (ARV-212) the other credentials Ariva issues or stores by their shapes and any value under a credential's name
    /// (<see cref="IsSensitiveName"/>) as a JSON member, a <c>name=value</c> pair or a credential header line. Returns the same
    /// instance when nothing changed; null and empty stay as they are. A text longer than <see cref="MaxProtectedLength"/> is
    /// cut first (ARV-293, <see cref="Bounded"/>).
    /// </summary>
    public static string Redact(string text) => Bounded(text, RedactWhole);

    /// <summary>
    /// ARV-293: <paramref name="protect"/> on a text of at most <see cref="MaxProtectedLength"/> characters as it is; a longer
    /// one is cut at <see cref="KeptLength"/>, the part kept protected and <see cref="CutNote"/> added.
    /// </summary>
    private static string Bounded(string text, Func<string, string> protect)
    {
        if (text is null || text.Length <= MaxProtectedLength)
            return protect(text);

        var kept = KeptLength(text);
        return protect(text[..kept]) + string.Format(CultureInfo.InvariantCulture, CutNote, text.Length - kept, MaxProtectedLength);
    }

    /// <summary>
    /// ARV-293: how much of a text longer than <see cref="MaxProtectedLength"/> is kept, so that no credential is kept in
    /// part. The cut is at the bound (one character before it when a surrogate pair would be split). When the characters on
    /// both sides of it could belong to a credential's shape (<see cref="CanHoldCredential"/>), the run of them the cut falls
    /// in goes too: every rule whose match needs a length, a separator or an ending to match at all (a JWT, a key, a seed, a
    /// code, an address, a DER structure, a scheme's credential) holds only such characters, so a shape cut in two is dropped
    /// whole, never kept in part. Every rule that takes other characters (a header line, a JSON member's or a pair's value,
    /// a password's value, a token parameter's value, a private key block) takes them to the end of the part kept, so the part
    /// of it kept is masked. A rule ending in a word boundary matches at the part's end at least where it matched in the whole.
    /// </summary>
    internal static int KeptLength(string text)
    {
        var cut = char.IsHighSurrogate(text[MaxProtectedLength - 1]) ? MaxProtectedLength - 1 : MaxProtectedLength;
        if (!CanHoldCredential(text[cut]))
            return cut;
        while (cut > 0 && CanHoldCredential(text[cut - 1]))
            cut--;
        return cut;
    }

    /// <summary>
    /// ARV-293: whether a character can be part of a credential's shape: an ASCII letter or digit, one of <c>_-.+/=~%@</c>
    /// (base64 and base64url, a JWT's dots, an address, a scheme's credential), or a letter that matches an ASCII one without
    /// case (the Kelvin sign, which the rules that ignore case take as k; the long s is kept too, though .NET 10 takes it as no
    /// ASCII letter).
    /// </summary>
    private static bool CanHoldCredential(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+' or '/' or '=' or '~' or '%' or '@' || c == (char)0x212A || c == (char)0x017F;

    /// <summary><see cref="Redact"/> on the whole text, whatever its length.</summary>
    private static string RedactWhole(string text)
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

    /// <summary>
    /// Masks every email address inside free text. Returns the same instance when nothing changed. A text longer than
    /// <see cref="MaxProtectedLength"/> is cut first (ARV-293, <see cref="Bounded"/>).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with each address masked as <see cref="Mask"/> does.</returns>
    public static string MaskAddressesIn(string text) => Bounded(text, MaskAddressesInWhole);

    /// <summary><see cref="MaskAddressesIn"/> on the whole text, whatever its length.</summary>
    private static string MaskAddressesInWhole(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@', StringComparison.Ordinal))
            return text;

        var result = EmailAddress().Replace(text, m => Mask(m.Value));
        return string.Equals(result, text, StringComparison.Ordinal) ? text : result;
    }

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

    /// <summary>A JSON member's value, when the member's name is a credential's (<see cref="IsSensitiveName"/>).</summary>
    private static string JsonValue(Match match) =>
        IsSensitiveName(match.Groups["name"].Value)
            ? match.Value[..(match.Groups["value"].Index - match.Index)] + "\"" + Redacted + "\""
            : match.Value;

    /// <summary>A <c>name=value</c> pair's value (quoted or not), when the name is a credential's (<see cref="IsSensitiveName"/>).</summary>
    private static string AssignedValue(Match match) =>
        IsSensitiveName(match.Groups["name"].Value)
            ? match.Value[..(match.Groups["value"].Index - match.Index)] + Redacted
            : match.Value;

    #endregion

    #region Rules

    // ARV-289: these four run without a timeout (ARV-163): a timeout would let a log event out unprotected, and since ARV-285
    // would throw in DeadLetters.Create, fail the outbox relay's pass or turn a job's failure into Unprotectable. The scheme and
    // Jwt rules run on the linear engine, each proven to replace what its backtracking rule replaced (LinearRuleTests): the
    // backtracking Jwt could start at every eyJ of a long run of eyJ- and read to its end each time (time quadratic in the
    // text's length). Since its checkpoint fix the token parameter and device key rules run on the backtracking engine again,
    // linear by their shape (each says why): on the runtime's linear engine (NonBacktracking, .NET 10.0.13) an instance of
    // either, after a long scan (a long value or key, a long run that could belong to one, or many runs of about 8,000
    // characters), missed or overran a match that ends a text, in that call and on later ones (LinearRuleTests' long scan
    // theory reproduces it on either rule put back), while the other rules matched right after the same texts. A missed match
    // leaves a credential in the log (CWE-532); the backtracking engine keeps nothing from one call to the next.

    // "Bearer abc.def", "Basic dXNlcjpwYXNz" (also inside "Authorization: Bearer ..." header dumps).
    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex AuthorizationScheme();

    // ?access_token=... in SignalR negotiate and WebSocket URLs, and other token-bearing query parameters. Backtracking and linear:
    // an attempt reads one of ?&#, one of the nine names (13 characters at most) and '=', then a run of value characters that
    // nothing follows, so it succeeds at the run's first character and never reads it again; a failed attempt reads at most 16
    // characters, and the scan goes on after a match.
    [GeneratedRegex(@"(?<key>[?&#](access_token|refresh_token|id_token|token|code|totp|otp|password|key)=)[^&#\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout.Infinite)]
    public static partial Regex TokenQueryParameter();

    /// <summary>
    /// A device credential (ARV-022, ardk_) or a display player credential (ARV-058, ardp_), wherever it appears: the
    /// marker and 43 base64url characters. Backtracking and linear: an attempt reads the five-character marker, then a run of
    /// key characters that nothing follows, so it succeeds once the run holds 20 and never reads it again; a failed attempt
    /// reads at most 25 characters, and the scan goes on after a match.
    /// </summary>
    [GeneratedRegex(@"ard[kp]_[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant, Timeout.Infinite)]
    public static partial Regex DeviceKey();

    // A compact JWS anywhere in text (header.payload.signature, header starting with eyJ).
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex Jwt();

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

    /// <summary>
    /// A JSON member with a string or number value; the value goes when the name is a credential's. A string value runs to its
    /// closing quote, a line's end or the text's end, a backslash that ends it included (ARV-293: a text cut inside an escape).
    /// </summary>
    [GeneratedRegex(@"""(?<name>[A-Za-z0-9_.:-]{1,64})""[ \t\r\n]*:[ \t\r\n]*(?<value>""(?:[^""\\\r\n]|\\.)*\\?""?|[0-9]+)",
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
    /// name is a credential's (<see cref="IsSensitiveName"/>); it only ever masks more.
    /// </summary>
    [GeneratedRegex(@"\b(?<name>[A-Za-z0-9_.-]{0,60}(?:password|pwd))[ \t]*=[ \t]*(?<value>[^;\r\n""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    public static partial Regex PasswordAssignment();

    /// <summary>
    /// An email address in free text (CWE-1333): a run of local part characters, the literal <c>@</c>, then dot-separated
    /// labels. It runs on <see cref="RegexOptions.NonBacktracking"/>, linear in the text's length, without a timeout: a timeout
    /// would throw inside the enricher, and Serilog emits an event whose enricher threw as it is, unmasked. The leftmost match
    /// starts where a run of address characters starts, so no lookbehind is needed.
    /// </summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Timeout.Infinite)]
    public static partial Regex EmailAddress();

    #endregion
}
