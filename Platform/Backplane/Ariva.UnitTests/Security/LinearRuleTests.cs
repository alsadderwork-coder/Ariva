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
    /// The log text rules ARV-289 moved onto the linear engine and that stay there (the token parameter and device key rules
    /// went back to the backtracking engine with its checkpoint fix), as they were before (backtracking, no timeout), each with the replacement
    /// <see cref="SecretText.Redact"/> makes (the scheme, the query parameter's name and the credential's marker kept), and
    /// fragments of its own and near misses to build texts from.
    /// </summary>
    private static readonly Dictionary<string, (string Pattern, RegexOptions Options, Func<Regex> Rule, MatchEvaluator Replace, string[] Fragments)> LogRules =
        new(StringComparer.Ordinal)
        {
            ["AuthorizationScheme"] = (@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                SecretText.AuthorizationScheme, m => m.Groups["scheme"].Value + " " + SecretText.Redacted,
                ["Bearer", "Basic", "bearer", "BASIC", "Beare", "xBearer", " ", "\t", "\n", "abc", "Z9", "-", ".", "_", "~", "+", "/", "=", "==", ":", ",", "\u00e9", "\u200d", "\u017f", "\u212a", "\u00a0"]),
            ["Jwt"] = (@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, SecretText.Jwt, _ => SecretText.Redacted,
                ["eyJ", "eyJabcde", "eyJabcde.", "abcde.", "abcde", "12345", "eyj", "xeyJ", "ab", "-", "_", ".", "..", " ", "=", "\n", "\u00e9", "\u200d", "\u212a"])
        };

    public static TheoryData<string> LogRuleNames => [.. LogRules.Keys];

    /// <summary>
    /// ARV-289 (checkpoint fix): the token parameter and device key rules run on the backtracking engine without a timeout, linear
    /// by their shape (each rule's documentation says why); the format tests time them on 100,000-character runs.
    /// </summary>
    [Theory]
    [InlineData("TokenQueryParameter")]
    [InlineData("DeviceKey")]
    public void SecretTextRule_Should_RunOnTheBacktrackingEngineWithoutATimeout_When_ItIsTheTokenOrDeviceKeyRule(string name)
    {
        var rule = (Regex)typeof(SecretText).GetMethod(name, BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes).Invoke(null, null);

        rule.Options.Should().NotHaveFlag(RegexOptions.NonBacktracking, "the linear engine left these two matching wrong after long scans");
        rule.MatchTimeout.Should().Be(Regex.InfiniteMatchTimeout, "a timeout would let a log event out unprotected");
    }

    /// <summary>Every rule SecretText holds, public or not: its static methods that return a <see cref="Regex"/> and take nothing.</summary>
    public static TheoryData<string> SecretTextRuleNames =>
        [.. typeof(SecretText).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0).Select(m => m.Name).Order(StringComparer.Ordinal)];

    /// <summary>Each rule's shortest match, and a fragment whose runs make the rule's long scans.</summary>
    private static readonly Dictionary<string, (string Minimal, string Run)> LongScanCases = new(StringComparer.Ordinal)
    {
        ["TokenQueryParameter"] = ("?token=x", "?token="), ["DeviceKey"] = ("ardk_01234567890123456789", "ardk_-"),
        ["AuthorizationScheme"] = ("Bearer x", "Bearer "), ["Jwt"] = ("eyJabcde.abcde.", "eyJabcde."),
        ["PrivateKeyBlock"] = (new string('-', 5) + "BEGIN PRIVATE KEY" + new string('-', 5), "a"), ["DerBase64"] = ("MII" + new string('A', 60), "MIIA"),
        ["IntegrationClientSecret"] = ("ics_" + new string('a', 20), "ics_a"), ["TemporaryPassword"] = ("abcde-fghjk-mnpqr-stuvw", "abcde-"),
        ["RecoveryCode"] = ("ABCDE-FGHJK", "ABCDE-"), ["Base32Seed"] = ("ABCDEFGHIJKLMNOP", "ABCDEFGH"),
        ["CredentialHeader"] = ("Cookie: x", "Cookie:"), ["AuthorizationSchemeWord"] = ("Bearer ", "Bearer "),
        ["JsonMember"] = ("\"a\":1", "\"a\":\""), ["Assignment"] = ("a=b", "a=b"), ["UnquotedAssignment"] = ("a=b", "a="),
        ["PasswordAssignment"] = ("pwd=x", "pwd=a"), ["EmailAddress"] = ("a@b.c", "a@a.")
    };

    /// <summary>
    /// ARV-289 (checkpoint fix): on the runtime's linear engine (NonBacktracking, .NET 10.0.13) an instance of the token
    /// parameter or device key rule, after a long scan, missed or overran a match that ends a text: after one run of 60,000
    /// characters, after seven runs of about 8,000, after a match followed by 60,000 characters it could take, and in the long
    /// call's own last match. Every rule of SecretText, on instances of its own engine and options, must match texts that end at
    /// its shortest match (or one character past it) as the backtracking engine does after each of those, new and after the
    /// short texts first; either of the two rules put back on the linear engine fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(SecretTextRuleNames))]
    public void SecretTextRule_Should_MatchAsTheBacktrackingEngine_When_LongScansCameFirst(string name)
    {
        LongScanCases.Should().ContainKey(name, "every rule needs its shortest match and a run here");
        var (minimal, run) = LongScanCases[name];
        var shape = (Regex)typeof(SecretText).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes).Invoke(null, null);
        var reference = new Regex(shape.ToString(), shape.Options & ~RegexOptions.NonBacktracking);
        Regex Instance() => new(shape.ToString(), shape.Options, shape.MatchTimeout);
        string Run(int length) => string.Concat(Enumerable.Repeat(run, Math.Max(1, length / run.Length)));
        string[] leads = ["", " ", "\n", "x", "?", "#", "&", "-", "_", ".", "\u212a"];
        string[] tails = ["", "x", ".", " ", "\n", "\u00e9", "\u212a", "\u017f", "=", "-", "_", "1", "A", "\"", ";"];
        var probes = leads.SelectMany(lead => tails.Select(tail => lead + minimal + tail)).ToArray();
        var wrong = new List<string>();

        var last = Instance();
        foreach (var probe in probes.Where((_, i) => i % 15 == 0))
        {
            var text = minimal + new string('a', 30_000) + "\n" + probe;
            if (last.Replace(text, "<$0>") != reference.Replace(text, "<$0>"))
                wrong.Add($"the last match of a long call ending {Escaped(probe)}");
        }

        var scans = new (string Label, string[] Texts)[]
        {
            ("one run of 60,000", [Run(60_000)]),
            ("seven runs of about 8,000", [.. Enumerable.Range(0, 7).Select(k => Run(8_000) + run[..(k % run.Length)])]),
            ("a match then 60,000 a", [minimal + new string('a', 60_000)]),
            ("60,000 a then a match", [new string('a', 60_000) + minimal])
        };
        foreach (var shortTextsFirst in new[] { false, true })
            foreach (var (label, texts) in scans)
            {
                var instance = Instance();
                if (shortTextsFirst)
                    foreach (var probe in probes)
                        instance.Replace(probe, "<$0>");
                foreach (var text in texts)
                    instance.Replace(text, "<$0>");
                wrong.AddRange(probes.Where(probe => instance.Replace(probe, "<$0>") != reference.Replace(probe, "<$0>"))
                    .Select(probe => $"{Escaped(probe)} after {label}{(shortTextsFirst ? ", short texts first" : string.Empty)}"));
            }

        wrong.Should().BeEmpty();
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
