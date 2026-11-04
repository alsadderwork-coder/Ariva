using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Infra.Imports;
using Ariva.Infra.Notifications;
using Ariva.Infra.Security;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-163 (CWE-1333, CWE-400): the production rules that backtracked under a match timeout (found by counting each
/// generated matcher's calls to RegexRunner.CheckTimeout, ARV-156) run on the linear engine without one. A timeout is
/// wall-clock time, so a loaded node could refuse a short valid value; the linear engine bounds the work by the input's
/// length instead. Each rule's accepted set is the old rule's: a property compares the two on random strings over the
/// rule's own characters and its near misses, and a table holds each rule's edges.
/// </summary>
public class LinearRuleTests
{
    #region Rules

    private static readonly Dictionary<string, Func<Regex>> Rules = new(StringComparer.Ordinal)
    {
        ["Site.CodePattern"] = Site.CodePattern,
        ["TopologyCodes.Code"] = TopologyCodes.Code,
        ["FlightRules.Carrier"] = FlightRules.Carrier,
        ["FlightRules.Codeshare"] = FlightRules.Codeshare,
        ["OutboundRules.PathAndQuery"] = OutboundRules.PathAndQuery,
        ["OutboundRules.Scope"] = OutboundRules.Scope,
        ["EmailAddresses.Rule"] = EmailAddresses.Rule,
        ["EmailTemplates.Placeholder"] = EmailTemplates.Placeholder,
        ["EmailAttachment.FileNameRule"] = EmailAttachment.FileNameRule,
        ["CsvImportReader.Number"] = CsvImportReader.Number,
        ["ClamdScanner.VersionPattern"] = ClamdScanner.VersionPattern,
        ["LocalDiskFileStorage.KeyPattern"] = LocalDiskFileStorage.KeyPattern,
        ["SecretText.AuthorizationScheme"] = SecretText.AuthorizationScheme,
        ["SecretText.TokenQueryParameter"] = SecretText.TokenQueryParameter,
        ["SecretText.DeviceKey"] = SecretText.DeviceKey,
        ["SecretText.Jwt"] = SecretText.Jwt
    };

    public static TheoryData<string> RuleNames => [.. Rules.Keys];

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Rule_Should_BeLinearWithoutATimeout_When_Matched(string name)
    {
        var rule = Rules[name]();

        rule.Options.Should().HaveFlag(RegexOptions.NonBacktracking, "linear time in the input's length is the bound against ReDoS");
        rule.MatchTimeout.Should().Be(Regex.InfiniteMatchTimeout, "a wall-clock timeout refuses a short valid value on a loaded node");
    }

    #endregion

    #region Log text rules (ARV-289)

    /// <summary>
    /// The four log text rules as they were before ARV-289 (backtracking, no timeout), each with the replacement
    /// <see cref="SecretText.Redact"/> makes (the scheme, the query parameter's name and the credential's marker kept), and
    /// fragments of its own and near misses to build texts from.
    /// </summary>
    private static readonly Dictionary<string, (string Pattern, RegexOptions Options, Func<Regex> Rule, MatchEvaluator Replace, string[] Fragments)> LogRules =
        new(StringComparer.Ordinal)
        {
            ["AuthorizationScheme"] = (@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                SecretText.AuthorizationScheme, m => m.Groups["scheme"].Value + " " + SecretText.Redacted,
                ["Bearer", "Basic", "bearer", "BASIC", "Beare", "xBearer", " ", "\t", "\n", "abc", "Z9", "-", ".", "_", "~", "+", "/", "=", "==", ":", ",", "\u00e9", "\u200d", "\u017f", "\u212a", "\u00a0"]),
            ["TokenQueryParameter"] = (@"(?<key>[?&#](access_token|refresh_token|id_token|token|code|totp|otp|password|key)=)[^&#\s""']+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, SecretText.TokenQueryParameter, m => m.Groups["key"].Value + SecretText.Redacted,
                ["?token=", "&access_token=", "#code=", "?", "&", "#", "=", "access_token", "refresh_token", "id_token", "token", "TOKEN", "code", "totp", "otp", "password", "key", "keys", "tok", "abc",
                 " ", "\"", "'", "/", "%20", "\n", "\u017f", "\u212a", "\u200d"]),
            ["DeviceKey"] = (@"ard[kp]_[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant, SecretText.DeviceKey, m => m.Value[..5] + SecretText.Redacted,
                ["ardk_", "ardp_", "ardx_", "ARDK_", "ard", "k_", "abcdefghij", "0123456789", "-", "_", ".", " ", "\u00e9", "\u200d", "\u212a"]),
            ["Jwt"] = (@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, SecretText.Jwt, _ => SecretText.Redacted,
                ["eyJ", "eyJabcde", "eyJabcde.", "abcde.", "abcde", "12345", "eyj", "xeyJ", "ab", "-", "_", ".", "..", " ", "=", "\n", "\u00e9", "\u200d", "\u212a"])
        };

    public static TheoryData<string> LogRuleNames => [.. LogRules.Keys];

    /// <summary>Every rule SecretText holds, public or not: its static methods that return a <see cref="Regex"/> and take nothing.</summary>
    public static TheoryData<string> SecretTextRuleNames =>
        [.. typeof(SecretText).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0).Select(m => m.Name).Order(StringComparer.Ordinal)];

    private static readonly string[] TokenFragments = ["?token=", "&access_token=", "#code=", "?", "&", "#", "=", "access_token", "refresh_token", "id_token", "token", "TOKEN",
        "code", "totp", "otp", "password", "key", "keys", "tok", "abc", " ", "\"", "'", "/", "%20", "\n", "\u017f", "\u212a", "\u200d"];

    private static readonly string[] DeviceFragments = ["ardk_", "ardp_", "ardx_", "ARDK_", "ard", "k_", "abcdefghij", "0123456789", "-", "_", ".", " ", "\u00e9", "\u200d", "\u212a"];

    private static readonly string[] OtherFragments = ["Bearer ", "Basic ", "eyJ", "eyJabcde.", "abcde.", "MIIB", "ics_", "ABCDE", "ABCDE-", "-----BEGIN PRIVATE KEY-----",
        "-----END PRIVATE KEY-----", "password=", "pwd=", "\"secret\":", ":", ";", ",", "a@b.c", "Authorization: ", "Cookie: ", "{", "}", "AAAAAAAAAAAAAAAA", "2345", "\t"];

    /// <summary>The long texts the format tests redact (a private key block left open, then 100,000 characters of a fragment).</summary>
    private static IEnumerable<string> LongRuns()
    {
        var marker = new string('-', 5);
        var opening = marker + "BEGIN PRIVATE KEY" + marker;
        foreach (var fragment in new[] { "a=", "a=b", "\"a\":\"", "Cookie:", "ABCDEFGH", "A-", "eyJ-", "eyJabcde.", "Bearer ", "?token=", "ardk_-" })
        {
            var text = opening + string.Concat(Enumerable.Repeat(fragment, 100_000 / fragment.Length));
            yield return text;
            yield return text[opening.Length..];
        }
    }

    private static string[] Texts(int count, int seed, params string[][] fragments)
    {
        var random = new Bogus.Randomizer(seed);
        var all = fragments.SelectMany(f => f).ToArray();
        return [.. Enumerable.Range(0, count).Select(_ => string.Concat(Enumerable.Range(0, random.Number(0, 24)).Select(_ => random.ArrayElement(all))))];
    }

    /// <summary>
    /// ARV-289 (checkpoint fix): on the runtime's linear engine one search that scans 32,000 characters or more leaves the
    /// instance matching wrong on its later calls (the token parameter rule on a new instance, the device key rule after other
    /// texts), so SecretText matches a text longer than <see cref="SecretText.LongestSharedText"/> on an instance of its own. The
    /// instance given here then matches as the backtracking engine does; with the long texts on it, it does not (the two orders
    /// below each left one rule wrong, about one text in 400 and one in 15,000).
    /// </summary>
    [Theory]
    [InlineData("TokenQueryParameter", false)]
    [InlineData("DeviceKey", true)]
    public void LongText_Should_LeaveTheSharedInstanceMatchingAsBefore_When_ItIsRedacted(string name, bool shortTextsFirst)
    {
        var shape = (Regex)typeof(SecretText).GetMethod(name, BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes).Invoke(null, null);
        var shared = new Regex(shape.ToString(), shape.Options, shape.MatchTimeout);
        var reference = new Regex(shape.ToString(), shape.Options & ~RegexOptions.NonBacktracking);
        var own = name == "TokenQueryParameter" ? TokenFragments : DeviceFragments;
        if (shortTextsFirst)
            foreach (var text in Texts(30_000, 1, own))
                SecretText.Replace(shared, text, "<$0>");

        foreach (var text in LongRuns().Concat(LongRuns()))
            SecretText.Replace(shared, text, "<$0>").Should().Be(reference.Replace(text, "<$0>"), "a long text's own call matches right");

        Texts(60_000, 2, own).Where(text => shared.Replace(text, "<$0>") != reference.Replace(text, "<$0>")).Select(Escaped).Should().BeEmpty();
    }

    /// <summary>
    /// ARV-289 (checkpoint fix): no rule of SecretText is left matching wrong by the long texts SecretText matches (each on an
    /// instance of its own), in either order (new, or after short texts), so a change of pattern or of runtime that brings the
    /// defect to another rule's shared instance fails here as long as the guard holds, and the guard's own test above fails if
    /// it goes.
    /// </summary>
    [Theory]
    [MemberData(nameof(SecretTextRuleNames))]
    public void SecretTextRule_Should_MatchAsTheBacktrackingEngine_When_LongTextsWereRedacted(string name)
    {
        var shape = (Regex)typeof(SecretText).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes).Invoke(null, null);
        var reference = new Regex(shape.ToString(), shape.Options & ~RegexOptions.NonBacktracking);
        var samples = Texts(10_000, 3, TokenFragments, DeviceFragments, OtherFragments);
        var hit = samples.FirstOrDefault(reference.IsMatch) ?? "x";
        string[] shapes = [hit + new string(hit[^1], 40_000), new string(hit[0], 40_000) + hit, new string('q', 40_000) + hit, hit + new string('q', 40_000)];

        foreach (var shortTextsFirst in new[] { false, true })
        {
            var shared = new Regex(shape.ToString(), shape.Options, shape.MatchTimeout);
            if (shortTextsFirst)
                foreach (var text in samples)
                    SecretText.Replace(shared, text, "<$0>");
            foreach (var text in LongRuns().Concat(shapes))
                SecretText.Replace(shared, text, "<$0>");

            samples.Where(text => shared.Replace(text, "<$0>") != reference.Replace(text, "<$0>")).Select(Escaped).Should().BeEmpty(shortTextsFirst ? "after short texts" : "new");
        }
    }

    [Fact]
    public void LongestSharedText_Should_BeWellBelowTheShortestScanThatLeftAnInstanceWrong_When_Read()
    {
        // 16,000 characters never left an instance wrong, 32,000 did (the token parameter and device key rules, measured on
        // .NET 10.0.13); the bound keeps a shared instance's scans at half the first.
        SecretText.LongestSharedText.Should().BeLessThanOrEqualTo(8_192);
    }

    private static string Escaped(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    [Theory]
    [MemberData(nameof(LogRuleNames))]
    public void LogRule_Should_ReplaceWhatTheBacktrackingRuleReplaced_When_RandomTextsAreRedacted(string name)
    {
        var (pattern, options, rule, replace, fragments) = LogRules[name];
        var old = new Regex(pattern, options);
        var linear = rule();

        linear.Options.Should().HaveFlag(RegexOptions.NonBacktracking);
        var matched = 0;
        Gen.OneOfConst(fragments).Array[0, 24].Select(parts => string.Concat(parts))
            .Sample(text =>
            {
                if (old.IsMatch(text))
                    Interlocked.Increment(ref matched);
                return linear.Replace(text, replace) == old.Replace(text, replace);
            }, iter: Iterations,
            // A difference prints the text and both replacements escaped (the texts hold line breaks and invisible characters),
            // and what a new instance gives, which tells a pattern's difference from an instance left matching wrong.
            print: text => $"{Escaped(text)} gives {Escaped(linear.Replace(text, replace))} (a new instance "
                + $"{Escaped(new Regex(pattern, options | RegexOptions.NonBacktracking).Replace(text, replace))}), the backtracking rule {Escaped(old.Replace(text, replace))}");
        // Texts the old rule replaces in, not only misses: at least one in a hundred of those sampled.
        matched.Should().BeGreaterThan(Iterations / 100, "the property compares replacements, so the texts must hold matches");
    }

    #endregion

    #region Same accepted set

    // Near misses every rule must treat as before: line breaks (a $ would take one at the end), white space, a letter
    // outside ASCII, an Arabic digit, an invisible character, and lower case letters (a rule that ignored case would take them).
    private const string Near = "\n\r\t \u00e9\u0661\u200baz";

    private const int Iterations = 100_000;

    /// <summary>
    /// Strings of 0 to <paramref name="maxLength"/> characters, eight in nine from the rule's own alphabet, so long valid
    /// values (a code of 16 characters next to one of 17) come up often, not once in millions.
    /// </summary>
    internal static Gen<string> Strings(string alphabet, int maxLength) =>
        Gen.Frequency((8, Gen.Char[alphabet]), (1, Gen.Char[Near])).Array[0, maxLength].Select(chars => new string(chars));

    /// <summary>The old rule (backtracking, as it was before ARV-163) and the new one agree on random strings.</summary>
    internal static void SameAs(string oldPattern, RegexOptions oldOptions, Func<string, bool> accepts, string alphabet, int maxLength)
    {
        var old = new Regex(oldPattern, oldOptions);
        Strings(alphabet, maxLength).Sample(text => accepts(text) == old.IsMatch(text), iter: Iterations);
    }

    [Fact]
    public void TopologyCode_Should_AcceptWhatTheLookaheadRuleAccepted_When_RandomStringsAreChecked()
    {
        // ARV-013's rule with its lookahead, which the linear engine cannot run: IsValid's length check (1 to 16) replaces it.
        SameAs("^(?=.{1,16}\\z)[A-Z0-9]+(-[A-Z0-9]+)*\\z", RegexOptions.CultureInvariant, TopologyCodes.IsValid, "ABZ09-", 20);
    }

    [Fact]
    public void Rules_Should_AcceptWhatTheBacktrackingRulesAccepted_When_RandomStringsAreChecked()
    {
        const RegexOptions Old = RegexOptions.CultureInvariant;
        SameAs("^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?\\z", Old, Site.CodePattern().IsMatch, "ABZ09-", 20);
        SameAs(@"^([A-Z0-9]{2}|[A-Z]{3})\z", Old, FlightRules.Carrier().IsMatch, "ABZ09a", 5);
        SameAs(@"^([A-Z0-9]{2}|[A-Z]{3})[0-9]{1,4}[A-Z]?\z", Old, FlightRules.Codeshare().IsMatch, "ABZ0189a", 10);
        SameAs(@"^/[A-Za-z0-9._~/-]{0,199}(\?[A-Za-z0-9._~=&-]{1,99})?\z", Old, OutboundRules.PathAndQuery().IsMatch, "/aZ09._~-?=&#%", 24);
        SameAs(@"^[\x21-\x7E]+( [\x21-\x7E]+)*\z", Old, OutboundRules.Scope().IsMatch, "ab.:/ ~!", 16);
        SameAs(@"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+\z",
            Old, EmailAddresses.Rule().IsMatch, "aZ09.+-_@", 24);
        SameAs("^-?[0-9]{1,9}(\\.[0-9]{1,9})?\\z", Old, CsvImportReader.Number().IsMatch, "0189-.", 22);
        SameAs("^[0-9a-f]{32}\\.(png|jpg|svg|webp|mp4|webm)\\z", Old, LocalDiskFileStorage.KeyPattern().IsMatch, "0af.pngjsvwebm", 40);
    }

    [Fact]
    public void KeyPattern_Should_AcceptWhatTheBacktrackingRuleAccepted_When_KeysAreAlmostRight()
    {
        // Random strings over the key's characters almost never hold 32 hex digits: build keys and spoil them instead.
        var old = new Regex("^[0-9a-f]{32}\\.(png|jpg|svg|webp|mp4|webm)\\z", RegexOptions.CultureInvariant);
        var keys = Gen.Select(Gen.Char["0123456789abcdefA"].Array[30, 34], Gen.OneOfConst("png", "jpg", "svg", "webp", "mp4", "webm", "gif", "PNG", "pn", "webmm", "png\n"))
            .Select((hex, extension) => $"{new string(hex)}.{extension}");
        keys.Sample(key => LocalDiskFileStorage.KeyPattern().IsMatch(key) == old.IsMatch(key), iter: Iterations);
    }

    [Fact]
    public void VersionPattern_Should_MatchAndCaptureWhatTheBacktrackingRuleDid_When_VersionLinesAreAlmostRight()
    {
        // clamd's VERSION answer built from its parts, each part sometimes spoilt, so both matches and refusals are common.
        // ARV-092b's pattern as it was, so a later change to the rule's text is compared with it, not with itself.
        var old = new Regex(
            @"\AClamAV (?<engine>[0-9][0-9A-Za-z.-]{0,31})/(?<database>[1-9][0-9]{0,8})/(?<weekday>Sun|Mon|Tue|Wed|Thu|Fri|Sat) (?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (?<day> [1-9]|[12][0-9]|3[01]) (?<hour>[01][0-9]|2[0-3]):(?<minute>[0-5][0-9]):(?<second>[0-5][0-9]) (?<year>[1-9][0-9]{3})\z",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        var lines = Gen.Select(
                Gen.OneOfConst("1.4.6", "0.103.12", "1.4.6-rc1", "1", ".4", "1/4", new string('9', 33)),
                Gen.OneOfConst("28157", "1", "0", "123456789", "1234567890", "-1"),
                Gen.OneOfConst("Sun", "Sat", "sun", "Su"),
                Gen.OneOfConst("Oct", "Jan", "Dec", "oct", "Sept"),
                Gen.OneOfConst(" 1", " 9", "10", "29", "31", "32", "1", "  1", "0"),
                Gen.Int[0, 25].Select(h => h.ToString("00", System.Globalization.CultureInfo.InvariantCulture)),
                Gen.OneOfConst("00:00", "59:59", "60:00", "5:00"),
                Gen.OneOfConst("2026", "0999", "10000", "1999\n", "2026 "))
            .Select((engine, database, weekday, month, day, hour, minutesSeconds, year) =>
                $"ClamAV {engine}/{database}/{weekday} {month} {day} {hour}:{minutesSeconds} {year}");
        string[] groups = ["engine", "database", "weekday", "month", "day", "hour", "minute", "second", "year"];
        lines.Sample(line =>
        {
            Match now = ClamdScanner.VersionPattern().Match(line), before = old.Match(line);
            return now.Success == before.Success && groups.All(g => now.Groups[g].Value == before.Groups[g].Value);
        }, iter: Iterations);
    }

    [Fact]
    public void Placeholder_Should_FindAndReplaceWhatTheBacktrackingRuleDid_When_RandomTemplatesAreFilled()
    {
        var old = new Regex(@"\{\{([A-Za-z]+)\}\}", RegexOptions.CultureInvariant);
        var rule = EmailTemplates.Placeholder();
        static string Fill(Match m) => $"<{m.Groups[1].Value}>";
        Strings("{}aZ", 30).Sample(text =>
            rule.Replace(text, Fill) == old.Replace(text, Fill) &&
            rule.Matches(text).Select(m => (m.Index, m.Groups[1].Value)).SequenceEqual(old.Matches(text).Select(m => (m.Index, m.Groups[1].Value))), iter: Iterations);
    }

    [Fact]
    public void AttachmentName_Should_AcceptWhatTheOldCallAcceptedButALineFeedAtTheEnd_When_RandomNamesAreChecked()
    {
        // The interpreted call ended at $, which also matches before a final line feed; the rule ends at \z.
        var old = new Regex("^[A-Za-z0-9._-]{1,100}$", RegexOptions.None);
        Strings("aZ09._-", 8).Sample(name => EmailAttachment.FileNameRule().IsMatch(name) == (old.IsMatch(name) && !name.EndsWith('\n')), iter: Iterations);
    }

    #endregion

    #region Edges

    public static TheoryData<string, string, bool> Edges => new()
    {
        { "TopologyCodes.Code", new string('A', 16), true },
        { "TopologyCodes.Code", new string('A', 17), false },
        { "TopologyCodes.Code", "A-" + new string('B', 14), true },
        { "TopologyCodes.Code", "A\n", false },
        { "Site.CodePattern", "DMO", true },
        { "Site.CodePattern", "ABCDEFGH-ABCDEFGH", true },
        { "Site.CodePattern", "ABCDEFGHI", false },
        { "Site.CodePattern", "DMO-", false },
        { "FlightRules.Carrier", "QR", true },
        { "FlightRules.Carrier", "QTR", true },
        { "FlightRules.Carrier", "Q1R", false },
        { "FlightRules.Codeshare", "XR1214", true },
        { "FlightRules.Codeshare", "QTR1A", true },
        { "FlightRules.Codeshare", "XR12345", false },
        { "OutboundRules.PathAndQuery", "/" + new string('a', 199), true },
        { "OutboundRules.PathAndQuery", "/" + new string('a', 200), false },
        { "OutboundRules.PathAndQuery", "/flights?" + new string('a', 99), true },
        { "OutboundRules.PathAndQuery", "/flights?" + new string('a', 100), false },
        { "OutboundRules.PathAndQuery", "/flights?", false },
        { "OutboundRules.Scope", "flights.read flights.write", true },
        { "OutboundRules.Scope", "flights.read  flights.write", false },
        { "OutboundRules.Scope", "flights.read ", false },
        { "EmailAddresses.Rule", "duty.manager@ariva.test", true },
        { "EmailAddresses.Rule", "a@" + new string('d', 63) + ".test", true },
        { "EmailAddresses.Rule", "a@" + new string('d', 64) + ".test", false },
        { "EmailAddresses.Rule", "a@-d.test", false },
        { "EmailAddresses.Rule", "a@ariva", false },
        { "EmailAttachment.FileNameRule", new string('a', 100), true },
        { "EmailAttachment.FileNameRule", new string('a', 101), false },
        { "EmailAttachment.FileNameRule", "hours.csv\n", false },
        { "CsvImportReader.Number", "-123456789.123456789", true },
        { "CsvImportReader.Number", "1234567890", false },
        { "CsvImportReader.Number", "1.", false },
        { "CsvImportReader.Number", "12\n", false },
        { "ClamdScanner.VersionPattern", "ClamAV 1.4.6/28157/Sun Oct 18 06:24:14 2026", true },
        { "ClamdScanner.VersionPattern", "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 2026", true },
        { "ClamdScanner.VersionPattern", "ClamAV 1.4.3/27927/Sun Mar 1 07:24:39 2026", false },
        { "ClamdScanner.VersionPattern", "ClamAV 1.4.6/28157/Sun Oct 18 24:24:14 2026", false },
        { "LocalDiskFileStorage.KeyPattern", new string('a', 32) + ".webm", true },
        { "LocalDiskFileStorage.KeyPattern", new string('a', 31) + ".png", false },
        { "LocalDiskFileStorage.KeyPattern", new string('A', 32) + ".png", false },
        { "LocalDiskFileStorage.KeyPattern", "../" + new string('a', 29) + ".png", false },
        { "EmailTemplates.Placeholder", "{{Site}}", true },
        { "EmailTemplates.Placeholder", "Wait at {{Zone}} is {{Minutes}}", true },
        { "EmailTemplates.Placeholder", "{{{a}}}", true },
        { "EmailTemplates.Placeholder", "{{}}", false },
        { "EmailTemplates.Placeholder", "{{a1}}", false },
        { "EmailTemplates.Placeholder", "{{a b}}", false },
        { "EmailTemplates.Placeholder", "{{Sit\u00e9}}", false },
        { "EmailTemplates.Placeholder", "{Site}", false }
    };

    [Theory]
    [MemberData(nameof(Edges))]
    public void Rule_Should_KeepItsEdges_When_Checked(string name, string text, bool accepted)
    {
        var matched = name == "TopologyCodes.Code" ? TopologyCodes.IsValid(text) : Rules[name]().IsMatch(text);

        matched.Should().Be(accepted);
    }

    [Fact]
    public void Placeholder_Should_TakeTheInnerBraces_When_ThreeAreOpen()
    {
        var match = EmailTemplates.Placeholder().Match("{{{a}}}");

        match.Index.Should().Be(1);
        match.Groups[1].Value.Should().Be("a");
    }

    [Fact]
    public void TopologyCode_Should_BeMatchedOnlyByIsValid_When_TheSourcesAreRead()
    {
        // Code() is the shape only; IsValid holds the length (1 to 16). Every other check goes through IsValid or Require.
        var sources = Directory.EnumerateFiles(RepositoryPaths.Platform, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}Ariva.UnitTests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var topology = sources.Single(f => f.EndsWith(Path.Combine("Ariva.Core", "Domain", "Entities", "Topology.cs"), StringComparison.Ordinal));
        var calls = File.ReadAllLines(topology).Where(l => Regex.IsMatch(l, @"(?<![\w.])Code\(\)") && !l.Contains("partial Regex Code()", StringComparison.Ordinal)).ToList();

        calls.Should().ContainSingle().Which.Should().Contain("public static bool IsValid(string code) => code is { Length: >= 1 and <= MaxCodeLength } && Code().IsMatch(code);");
        sources.Where(f => File.ReadAllText(f).Contains("TopologyCodes.Code(", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public void Rules_Should_FinishQuickly_When_TheInputIsALongAdversarialRun()
    {
        // Runs that made a backtracking matcher try every split; the linear engine reads each character a bounded number of times.
        var runs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EmailAddresses.Rule"] = "a@" + string.Concat(Enumerable.Repeat("a-", 50_000)) + "!",
            ["OutboundRules.Scope"] = string.Concat(Enumerable.Repeat("a ", 50_000)) + "\n",
            ["EmailTemplates.Placeholder"] = string.Concat(Enumerable.Repeat("{{a", 50_000)),
            ["TopologyCodes.Code"] = string.Concat(Enumerable.Repeat("A-", 50_000)) + "-"
        };
        foreach (var (name, text) in runs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _ = Rules[name]().Matches(text).Count;
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), $"{name} is linear in the input's length");
        }
    }

    #endregion
}
