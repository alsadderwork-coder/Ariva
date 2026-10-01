using System.Security.Cryptography;
using Ariva.Core.Security;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010a (ADR-0026): PBKDF2-SHA256 at 600,000 iterations, verified in constant time and upgraded when weaker; the
/// password policy (12 to 128 code points, breached list, no username, product name or site code); username
/// normalisation so look-alike accounts cannot exist.
/// </summary>
public sealed class PasswordTests
{
    private const string Password = "violet tram ladder 9031";

    private static readonly PasswordPolicy Policy = new(["AMM"]);

    #region PasswordHasher

    [Fact]
    public void Hash_Should_UsePbkdf2Sha256With600kIterationsAndARandomSalt_When_Called()
    {
        var first = PasswordHasher.Hash(Password);
        var second = PasswordHasher.Hash(Password);

        first.Algorithm.Should().Be("pbkdf2-sha256");
        first.Iterations.Should().Be(600_000);
        Convert.FromBase64String(first.Salt).Should().HaveCount(16);
        Convert.FromBase64String(first.Hash).Should().HaveCount(32);
        second.Salt.Should().NotBe(first.Salt);
        second.Hash.Should().NotBe(first.Hash);
    }

    [Fact]
    public void Verify_Should_MatchOnlyTheExactPassword_When_HashIsCurrent()
    {
        var stored = PasswordHasher.Hash(Password);

        PasswordHasher.Verify(Password, stored, out var needsRehash).Should().BeTrue();
        needsRehash.Should().BeFalse();
        PasswordHasher.Verify(Password + " ", stored, out _).Should().BeFalse("passwords are never trimmed");
        PasswordHasher.Verify(Password.ToUpperInvariant(), stored, out _).Should().BeFalse();
        PasswordHasher.Verify(string.Empty, stored, out _).Should().BeFalse();
        PasswordHasher.Verify(null, stored, out _).Should().BeFalse();
    }

    [Fact]
    public void Verify_Should_AskForRehash_When_StoredHashUsedFewerIterations()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var weak = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(Password), salt, 10_000, HashAlgorithmName.SHA256, 32);
        var stored = new PasswordHashValue("pbkdf2-sha256", 10_000, Convert.ToBase64String(salt), Convert.ToBase64String(weak));

        PasswordHasher.Verify(Password, stored, out var needsRehash).Should().BeTrue();
        needsRehash.Should().BeTrue();
        PasswordHasher.Verify("wrong password value", stored, out needsRehash).Should().BeFalse();
        needsRehash.Should().BeFalse("only a correct password is upgraded");
    }

    [Theory]
    [InlineData("md5", 600_000, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256", 0, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256", 600_000, "not base64", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256", 600_000, "AAAAAAAAAAAAAAAAAAAAAA==", "")]
    public void Verify_Should_NeverMatch_When_StoredHashIsUnknownOrDamaged(string algorithm, int iterations, string salt, string hash)
    {
        PasswordHasher.Verify(Password, new PasswordHashValue(algorithm, iterations, salt, hash), out _).Should().BeFalse();
        PasswordHasher.Verify(Password, null, out _).Should().BeFalse();
    }

    #endregion

    #region PasswordPolicy

    [Fact]
    public void Validate_Should_AcceptALongUncommonPassword_When_ItHasNoContextWords()
    {
        Policy.Validate(Password, "officer.one").Should().BeEmpty();
    }

    [Theory]
    [InlineData("short pass1")] // 11 characters
    [InlineData("")]
    [InlineData(null)]
    public void Validate_Should_Refuse_When_PasswordIsShorterThan12(string password)
    {
        Policy.Validate(password, "officer.one").Should().ContainSingle(e => e.Contains("12 to 128"));
    }

    [Fact]
    public void Validate_Should_CountCodePoints_When_PasswordHasCharactersOutsideTheBmp()
    {
        // Eleven emoji are 22 UTF-16 units but 11 characters: too short. Twelve are enough.
        var eleven = string.Concat(Enumerable.Repeat("\U0001F6C2", 11));
        var twelve = string.Concat(Enumerable.Repeat("\U0001F6C2", 12));

        Policy.Validate(eleven, "officer.one").Should().ContainSingle(e => e.Contains("12 to 128"));
        Policy.Validate(twelve, "officer.one").Should().BeEmpty();
        Policy.Validate(new string('x', 128) + "y", "officer.one").Should().Contain(e => e.Contains("12 to 128"));
    }

    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    [InlineData("ｐａｓｓｗｏｒｄ１２３４")] // full width, folded by NFKC
    public void Validate_Should_Refuse_When_PasswordIsOnTheBreachedList(string password)
    {
        Policy.Validate(password, "officer.one").Should().Contain(e => e.Contains("breached"));
    }

    [Fact]
    public void Blocklist_Should_HoldTheBundledList_When_Loaded()
    {
        PasswordPolicy.BlocklistSize.Should().BeGreaterThan(40_000);
    }

    [Theory]
    [InlineData("my officer.one key 2026", "officer.one")]
    [InlineData("Ariva rocks at night 7", "officer.one")]
    [InlineData("gate amm night shift 4", "officer.one")]
    [InlineData("ＡＲＩＶＡ night shift 44", "officer.one")]
    public void Validate_Should_Refuse_When_PasswordContainsUserNameProductOrSiteCode(string password, string userName)
    {
        Policy.Validate(password, userName).Should().Contain(e => e.Contains("username"));
    }

    #endregion

    #region UserNames

    [Theory]
    [InlineData("  Officer.One ", "officer.one")]
    [InlineData("ＯＦＦＩＣＥＲ", "officer")]
    [InlineData("ahmad@dalil", "ahmad@dalil")]
    public void Normalize_Should_TrimFoldAndLowerCase_When_Called(string input, string expected)
    {
        UserNames.Normalize(input).Should().Be(expected);
        UserNames.IsValid(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("officer one")]
    [InlineData("officer;drop")]
    [InlineData("off​icer")] // zero width space
    [InlineData(null)]
    public void IsValid_Should_BeFalse_When_UserNameIsTooShortOrHasOtherCharacters(string input)
    {
        UserNames.IsValid(input).Should().BeFalse();
    }

    [Fact]
    public void IsValid_Should_BeFalse_When_UserNameIsLongerThan64()
    {
        UserNames.IsValid(new string('a', 65)).Should().BeFalse();
        UserNames.IsValid(new string('a', 64)).Should().BeTrue();
    }

    #endregion
}
