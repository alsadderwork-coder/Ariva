using System.Text;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010c: TOTP against the RFC 6238 test vectors (SHA-1, truncated to six digits), the one-step window, the replay
/// guard, base32 secrets and Crockford recovery codes.
/// </summary>
public sealed class TotpTests
{
    // RFC 6238 Appendix B: the SHA-1 seed is the ASCII string "12345678901234567890".
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Code_Should_MatchTheRfc6238Vectors_When_TruncatedToSixDigits(long unixSeconds, string expected)
    {
        Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))).Should().Be(expected);
    }

    [Fact]
    public void Match_Should_AcceptOneStepEitherSideOnly_When_ClocksDrift()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var step = Totp.StepAt(now);

        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now, null).Should().Be(step);
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null).Should().Be(step - 1);
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now, null).Should().Be(step + 1, "the code from the next step is accepted");
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 2), now, null).Should().BeNull();
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null).Should().BeNull();
    }

    [Fact]
    public void Match_Should_RefuseTheLastAcceptedStepAndEarlier_When_ACodeIsReplayed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Code(RfcSecret, step);

        Totp.Match(RfcSecret, code, now, lastStep: step).Should().BeNull("CWE-294: a code for the last accepted step is a replay");
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now, lastStep: step).Should().BeNull();
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now, lastStep: step).Should().Be(step + 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("１２３４５６")]
    public void Match_Should_RefuseAnythingButSixAsciiDigits_When_CodeIsMalformed(string code)
    {
        Totp.Match(RfcSecret, code, DateTimeOffset.UtcNow, null).Should().BeNull();
    }

    [Fact]
    public void Match_Should_AcceptTheCode_When_TheAppShowsItWithASpace()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var code = Totp.Code(RfcSecret, Totp.StepAt(now));

        Totp.Match(RfcSecret, code[..3] + " " + code[3..], now, null).Should().NotBeNull();
    }

    [Fact]
    public void NewSecret_Should_Be160RandomBits_When_Generated()
    {
        var first = Totp.NewSecret();
        var second = Totp.NewSecret();

        first.Should().HaveCount(20);
        second.Should().NotEqual(first);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_Should_FollowRfc4648WithoutPadding_When_Encoding(string text, string expected)
    {
        var bytes = Encoding.ASCII.GetBytes(text);

        Base32.Encode(bytes).Should().Be(expected);
        Base32.Decode(expected).Should().Equal(bytes);
        Base32.Decode(expected.ToLowerInvariant() + "==").Should().Equal(bytes);
    }

    [Fact]
    public void OtpAuthUri_Should_NameTheIssuerAndAccount_When_Built()
    {
        var uri = Totp.OtpAuthUri("Ariva AUH", "officer.one", Encoding.ASCII.GetBytes("foobar"));

        uri.Should().Be("otpauth://totp/Ariva%20AUH:officer.one?secret=MZXW6YTBOI&issuer=Ariva%20AUH&algorithm=SHA1&digits=6&period=30");
    }

    [Fact]
    public void RecoveryCodes_Should_BeTenCrockfordCharactersInTwoGroups_When_Generated()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => RecoveryCodes.New()).ToList();

        codes.Should().OnlyContain(code => code.Length == 11 && code[5] == '-');
        codes.Should().OnlyContain(code => code.Replace("-", string.Empty).All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)));
        codes.Distinct().Should().HaveCount(200, "50 random bits per code");
    }

    [Theory]
    [InlineData("ABCDE-FGH12", "ABCDEFGH12")]
    [InlineData("abcde fgh12", "ABCDEFGH12")]
    [InlineData("ABCDEFGHI2", "ABCDEFGH12")]
    [InlineData("0O1IL-ABCDE", "00111ABCDE")]
    public void Normalize_Should_ApplyCrockfordRules_When_UserTypesACode(string typed, string expected)
    {
        RecoveryCodes.Normalize(typed).Should().Be(expected);
        RecoveryCodes.Hash(RecoveryCodes.Normalize(typed)).Should().HaveLength(64);
    }

    [Theory]
    [InlineData("ABCDE")]
    [InlineData("ABCDE-FGH12-X")]
    [InlineData("ABCDE-FGHU2")]
    [InlineData(null)]
    public void Normalize_Should_RefuseText_When_ItCannotBeACode(string typed)
    {
        RecoveryCodes.Normalize(typed).Should().BeNull();
    }
}
