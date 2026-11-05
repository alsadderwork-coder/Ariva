using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Logging;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-293 (CWE-400, CWE-532): the rules read at most <see cref="SecretText.MaxProtectedLength"/> characters of one text. A
/// longer text is cut, the part kept protected and a note of the cut added, and no credential across the bound is kept in
/// part. Rows name rules, never a credential (ARV-252): each test builds its own.
/// </summary>
public sealed class SecretTextBoundTests
{
    #region Credentials across the bound

    private const int Max = SecretText.MaxProtectedLength;

    private static readonly string Dashes = new('-', 5);

    private static readonly string Digits = string.Concat(Enumerable.Repeat("0123456789", 10));

    /// <summary>
    /// A credential of each rule, its secret written in characters (digits mostly) that appear nowhere else in the text: not in
    /// the filler, the names and markers a rule keeps, the mask or the note. Whatever of the credential the cut keeps, the
    /// protected text may hold none of them.
    /// </summary>
    private static readonly Dictionary<string, (string Credential, string Secret)> Credentials = new(StringComparer.Ordinal)
    {
        ["AuthorizationScheme"] = ($"Bearer 0123{(char)0x212A}456-._~+/789==", $"0123456789{(char)0x212A}~+/=._-"),
        ["TokenQueryParameter"] = ("https://h/x?access_token=0123{4567}89", "0123456789{}"),
        ["Jwt"] = ("eyJ012345.678901.234567", "eyJ0123456789."),
        ["DeviceKey"] = ("ardk_" + Digits[..43], "0123456789"),
        ["PrivateKeyBlock"] = ($"{Dashes}BEGIN PRIVATE KEY{Dashes}\n{Digits[..64]}\n{Digits[..40]}\n{Dashes}END PRIVATE KEY{Dashes}", "0123456789"),
        ["DerBase64"] = ("MII" + Digits + "==", "MI0123456789="),
        ["IntegrationClientSecret"] = ("ics_" + Digits[..43], "0123456789"),
        ["TemporaryPassword"] = ("23456-78923-45678-92345", "23456789-"),
        ["RecoveryCode"] = ("01234-56789", "0123456789-"),
        ["Base32Seed"] = ("2345672345672345", "234567"),
        ["CredentialHeader"] = ("Cookie: a=0123; b=4567 89", "0123456789"),
        ["AuthorizationSchemeWord"] = ("Authorization: Digest response=0123456789", "0123456789"),
        ["JsonMember"] = ("\"password\": \"0123 4567 89\"", "0123456789"),
        ["Assignment"] = ("client_secret=0123456789", "0123456789"),
        ["UnquotedAssignment"] = ("ConnectionStrings=\"Host=db;Password=0123 4567\"", "01234567"),
        ["PasswordAssignment"] = ("Pwd=0123,4567&89", "0123456789,&"),
        ["EmailAddress"] = ("s0123456789@example.com", "0123456789")
    };

    /// <summary>Every rule SecretText holds, public or not (as <c>LinearRuleTests</c> lists them).</summary>
    public static TheoryData<string> RuleNames =>
        [.. typeof(SecretText).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0).Select(m => m.Name).Order(StringComparer.Ordinal)];

    /// <summary>A filler of the given length that no rule matches and that ends in a space: '|' and ' ' in turn.</summary>
    private static string Filler(int length)
    {
        var chars = Enumerable.Range(0, length).Select(i => i % 2 == 0 ? '|' : ' ').ToArray();
        chars[^1] = ' ';
        return new string(chars);
    }

    private static string Note(int leftOut) => string.Format(CultureInfo.InvariantCulture, SecretText.CutNote, leftOut, Max);

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Protect_Should_KeepNoPartOfACredential_When_TheBoundCutsIt(string name)
    {
        Credentials.Should().ContainKey(name, "every rule needs a credential across the bound here");
        var (credential, secret) = Credentials[name];
        var wrong = new List<string>();

        // Every split of the credential at the bound: the first `before` characters of it inside, the rest past it.
        for (var before = 1; before < credential.Length; before++)
        {
            var text = Filler(Max - before) + credential + " and more text after it " + Filler(200);
            var protectedText = SecretText.Protect(text);
            var kept = SecretText.KeptLength(text);

            var note = Note(text.Length - kept);
            if (!protectedText.EndsWith(note, StringComparison.Ordinal) || !protectedText.StartsWith(Filler(Max - before), StringComparison.Ordinal))
            {
                wrong.Add($"split {before}: not the filler, the protected part and the note");
                continue;
            }

            var part = protectedText[(Max - before)..^note.Length];
            if (part.Any(secret.Contains))
                wrong.Add($"split {before}: the part kept holds some of the credential ({part.Length} characters)");
        }

        wrong.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Protect_Should_MaskTheCredential_When_ItEndsBeforeTheBound(string name)
    {
        // The samples are credentials the rules mask: whole, inside the bound, none of their secret is left.
        var (credential, secret) = Credentials[name];

        var protectedText = SecretText.Protect("| " + credential + " |");

        protectedText.Should().NotBe("| " + credential + " |");
        protectedText.Where(secret.Contains).Should().BeEmpty("the sample is a credential its rule masks");
    }

    #endregion

    #region The cut

    [Fact]
    public void Protect_Should_LeaveTheTextAsItIs_When_ItIsAtTheBound()
    {
        var text = Filler(Max);

        SecretText.Protect(text).Should().BeSameAs(text);
        SecretText.Redact(text).Should().BeSameAs(text);
        SecretText.MaskAddressesIn(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Protect_Should_CutAtTheBoundAndSaySo_When_TheTextIsLonger()
    {
        var text = Filler(Max) + new string('|', 1_000);

        var protectedText = SecretText.Protect(text);

        protectedText.Should().Be(Filler(Max) + Note(1_000));
        SecretText.Redact(text).Should().Be(Filler(Max) + Note(1_000), "Redact reads no more than Protect");
        SecretText.MaskAddressesIn(text).Should().Be(Filler(Max) + Note(1_000), "and nor does the address rule");
    }

    [Fact]
    public void Protect_Should_CutOnce_When_BothRulesRun()
    {
        var text = Filler(Max) + new string('|', 10) + " a@b.c";

        var protectedText = SecretText.Protect(text);

        protectedText.Should().Be(Filler(Max) + Note(16), "the cut is made once, before both rules");
    }

    [Fact]
    public void Protect_Should_DropTheWholeRun_When_TheCutFallsInOne()
    {
        // A run the bound cuts, longer than any credential: it goes whole, back to the character before it that no shape holds.
        var text = Filler(1_000) + new string('A', Max) + " after";

        SecretText.KeptLength(text).Should().Be(1_000);
        SecretText.Protect(text).Should().Be(Filler(1_000) + Note(Max + 6));
    }

    [Fact]
    public void Protect_Should_KeepNothingButTheNote_When_TheTextIsOneRun()
    {
        var text = new string('a', Max + 10);

        SecretText.Protect(text).Should().Be(Note(Max + 10));
    }

    [Fact]
    public void Protect_Should_KeepARunWhole_When_ItEndsAtTheBound()
    {
        // The run ends where the bound is: whole inside it, so its rule reads it as the whole text holds it, and it is masked.
        var key = "ardk_" + Digits[..43];
        var text = Filler(Max - key.Length) + key + " " + Filler(100);

        SecretText.KeptLength(text).Should().Be(Max);
        var protectedText = SecretText.Protect(text);
        protectedText.Should().Be(Filler(Max - key.Length) + "ardk_" + SecretText.Redacted + Note(101));
    }

    [Fact]
    public void Protect_Should_NotSplitASurrogatePair_When_TheBoundFallsInOne()
    {
        var face = new string([(char)0xD83D, (char)0xDE00]);
        var text = Filler(Max - 1) + face + Filler(10);

        SecretText.KeptLength(text).Should().Be(Max - 1);
        SecretText.Protect(text).Should().Be(Filler(Max - 1) + Note(12));
    }

    [Fact]
    public void ProtectAndCut_Should_CutTheProtectedText_When_ItIsLongerThanTheBound()
    {
        var text = Filler(Max) + new string('|', 1_000);

        SecretText.ProtectAndCut(text, 100).Should().Be(Filler(100));
        SecretText.ProtectAndCut(text, Max + 1_000).Should().Be(Filler(Max) + Note(1_000));
    }

    [Fact]
    public void ProtectText_Should_ReadAtMostTheBound_When_AnExceptionTextIsAMillionCharacters()
    {
        // The exception sink and the enricher protect through SecretText: a text of a million characters comes back at the
        // bound and its note, a credential at its start masked.
        var text = "token ardk_" + Digits[..43] + " " + Filler(1_000_000);

        var protectedText = RedactionEnricher.ProtectText(text);

        protectedText.Length.Should().BeLessThanOrEqualTo(Max + Note(1_000_000).Length);
        protectedText.Should().StartWith("token ardk_" + SecretText.Redacted).And.EndWith(Note(text.Length - SecretText.KeptLength(text)));
    }

    #endregion
}
