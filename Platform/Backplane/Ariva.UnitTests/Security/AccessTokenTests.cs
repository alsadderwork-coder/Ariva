using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using FluentAssertions;
using Ariva.UnitTests.Setup;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010a (ADR-0026): access tokens are ES256 with typ at+jwt and a kid derived from the public key; every host
/// accepts only those, from issuer ariva for audience ariva-users, within their lifetime plus 30 seconds. Key loading
/// requires P-256 and refuses a signing key whose public key the hosts would not accept.
/// </summary>
public sealed class AccessTokenTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ariva-token-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Now);
    private readonly AuthSettings _settings = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    #region Issuing

    [Fact]
    public void Issue_Should_WriteAnEs256AtJwtWithKidAndClaims_When_Called()
    {
        var keys = DevelopmentKeys();
        var userId = Guid.CreateVersion7();

        var token = new JsonWebToken(Issuer(keys).Issue(userId, "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), Now.UtcDateTime, ["pwd"], pending: false));

        token.Alg.Should().Be("ES256");
        token.Typ.Should().Be("at+jwt");
        token.Kid.Should().Be(keys.SigningKey.KeyId).And.Be(TokenKeys.KeyIdOf(keys.SigningKey.ECDsa));
        token.Issuer.Should().Be("ariva");
        token.Audiences.Should().Equal("ariva-users");
        token.Subject.Should().Be(userId.ToString());
        token.ValidTo.Should().Be(Now.UtcDateTime.AddMinutes(15));
        token.GetClaim("name").Value.Should().Be("officer.one");
        token.GetClaim("amr").Value.Should().Be("pwd");
        token.TryGetClaim("scope", out _).Should().BeFalse("a fully set up account has no scope restriction");
        token.TryGetClaim("role", out _).Should().BeFalse("permissions come from stored grants, never from the token");
    }

    [Fact]
    public void Issue_Should_AddThePendingScope_When_AccountIsNotSetUp()
    {
        var keys = DevelopmentKeys();

        var token = new JsonWebToken(Issuer(keys).Issue(Guid.CreateVersion7(), "temp.user", Guid.CreateVersion7(), Guid.CreateVersion7(), Now.UtcDateTime, ["pwd"], pending: true));

        token.GetClaim("scope").Value.Should().Be("pending");
    }

    [Fact]
    public void Issue_Should_Throw_When_HostHasNoSigningKey()
    {
        var keys = TokenKeys.Load(new TokenSettings { UseDevelopmentKeys = true, DevelopmentKeyDirectory = _directory }, requireSigningKey: false);

        var issue = () => Issuer(keys).Issue(Guid.CreateVersion7(), "x.y.z", Guid.Empty, Guid.Empty, Now.UtcDateTime, ["pwd"], false);

        issue.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region Validation

    [Fact]
    public async Task Validate_Should_Accept_When_TokenIsFromThisIssuerAndCurrent()
    {
        var keys = DevelopmentKeys();
        var token = Issuer(keys).Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), Now.UtcDateTime, ["pwd"], false);

        var result = await ValidateAsync(keys, token, Now.UtcDateTime.AddMinutes(14));

        result.IsValid.Should().BeTrue(result.Exception?.Message);
        result.ClaimsIdentity.Name.Should().Be("officer.one");
    }

    public static TheoryData<string> Tampering => ["alg none", "HS256 with the public key", "other audience", "other issuer", "typ JWT", "expired", "not yet valid", "unknown key", "no expiry"];

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task Validate_Should_Refuse_When_TokenIsTampered(string tampering)
    {
        var keys = DevelopmentKeys();
        var issued = new JsonWebToken(Issuer(keys).Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), Now.UtcDateTime, ["pwd"], false));
        var claims = JsonSerializer.Deserialize<Dictionary<string, object>>(Base64UrlEncoder.Decode(issued.EncodedPayload));
        var header = new Dictionary<string, object> { ["alg"] = "ES256", ["typ"] = "at+jwt", ["kid"] = keys.SigningKey.KeyId };
        var now = Now.ToUnixTimeSeconds();

        var token = tampering switch
        {
            "alg none" => Compose(new Dictionary<string, object> { ["alg"] = "none", ["typ"] = "at+jwt" }, claims, _ => []),
            "HS256 with the public key" => Compose(new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "at+jwt", ["kid"] = keys.SigningKey.KeyId }, claims,
                input => HMACSHA256.HashData(Encoding.UTF8.GetBytes(keys.SigningKey.ECDsa.ExportSubjectPublicKeyInfoPem()), input)),
            "other audience" => SignEs256(keys.SigningKey.ECDsa, header, With(claims, "aud", "another-api")),
            "other issuer" => SignEs256(keys.SigningKey.ECDsa, header, With(claims, "iss", "https://attacker.example")),
            "typ JWT" => SignEs256(keys.SigningKey.ECDsa, With(header, "typ", "JWT"), claims),
            "expired" => SignEs256(keys.SigningKey.ECDsa, header, With(With(With(claims, "iat", now - 1200), "nbf", now - 1200), "exp", now - 31)),
            "not yet valid" => SignEs256(keys.SigningKey.ECDsa, header, With(claims, "nbf", now + 120)),
            "no expiry" => SignEs256(keys.SigningKey.ECDsa, header, Without(claims, "exp")),
            "unknown key" => UnknownKey(header, claims),
            _ => throw new ArgumentOutOfRangeException(nameof(tampering))
        };

        var result = await ValidateAsync(keys, token, Now.UtcDateTime);

        result.IsValid.Should().BeFalse(tampering);
    }

    [Fact]
    public async Task Validate_Should_AcceptThePreviousKey_When_KeysAreRotated()
    {
        var previous = WriteKey("previous");
        var current = WriteKey("current");
        var settings = new TokenSettings { SigningKeyPath = current.Private, PublicKeyPaths = [current.Public, previous.Public] };
        var keys = TokenKeys.Load(settings, requireSigningKey: true);
        var oldKeys = TokenKeys.Load(new TokenSettings { SigningKeyPath = previous.Private, PublicKeyPaths = [previous.Public] }, requireSigningKey: true);
        var oldToken = Issuer(oldKeys).Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), Now.UtcDateTime, ["pwd"], false);

        keys.ValidationKeys.Should().HaveCount(2);
        (await ValidateAsync(keys, oldToken, Now.UtcDateTime)).IsValid.Should().BeTrue("tokens signed before the rotation stay valid until they expire");
    }

    #endregion

    #region Key loading

    [Fact]
    public void Load_Should_SkipAMissingPreviousKey_When_NoRotationIsUnderway()
    {
        var current = WriteKey("current");

        var keys = TokenKeys.Load(new TokenSettings { SigningKeyPath = current.Private, PublicKeyPaths = [current.Public, Path.Combine(_directory, "previous.pem")] }, requireSigningKey: true);

        keys.ValidationKeys.Should().ContainSingle();
    }

    [Fact]
    public void Load_Should_Throw_When_CurrentPublicKeyIsMissing()
    {
        var load = () => TokenKeys.Load(new TokenSettings { PublicKeyPaths = [Path.Combine(_directory, "missing.pem")] }, requireSigningKey: false);

        load.Should().Throw<IOException>();
    }

    [Fact]
    public void Load_Should_Throw_When_NoKeysAreConfiguredOutsideDevelopment()
    {
        var load = () => TokenKeys.Load(new TokenSettings(), requireSigningKey: false);

        load.Should().Throw<InvalidOperationException>().WithMessage("*PublicKeyPaths*");
    }

    [Fact]
    public void Load_Should_Throw_When_SigningKeyIsNotAmongThePublicKeys()
    {
        var current = WriteKey("current");
        var other = WriteKey("other");

        var load = () => TokenKeys.Load(new TokenSettings { SigningKeyPath = other.Private, PublicKeyPaths = [current.Public] }, requireSigningKey: true);

        load.Should().Throw<InvalidOperationException>().WithMessage("*not among*");
    }

    [Fact]
    public void Load_Should_Throw_When_KeyIsNotP256()
    {
        Directory.CreateDirectory(_directory);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var path = Path.Combine(_directory, "p384.pem");
        File.WriteAllText(path, p384.ExportSubjectPublicKeyInfoPem());

        var load = () => TokenKeys.Load(new TokenSettings { PublicKeyPaths = [path] }, requireSigningKey: false);

        load.Should().Throw<InvalidOperationException>().WithMessage("*P-256*");
    }

    [Fact]
    public async Task Load_Should_CreateOneSharedDevelopmentKey_When_HostsStartTogether()
    {
        var loads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            TokenKeys.Load(new TokenSettings { UseDevelopmentKeys = true, DevelopmentKeyDirectory = _directory }, requireSigningKey: true))));

        loads.Select(keys => keys.SigningKey.KeyId).Distinct().Should().ContainSingle();
        Directory.GetFiles(_directory).Should().ContainSingle().Which.Should().EndWith("token-signing-dev.key");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(_directory, "token-signing-dev.key")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    #endregion

    #region Helpers

    private TokenKeys DevelopmentKeys() =>
        TokenKeys.Load(new TokenSettings { UseDevelopmentKeys = true, DevelopmentKeyDirectory = _directory }, requireSigningKey: true);

    private AccessTokenIssuer Issuer(TokenKeys keys) => new(keys, _settings, _clock);

    private Task<TokenValidationResult> ValidateAsync(TokenKeys keys, string token, DateTime at)
    {
        var parameters = AccessTokenIssuer.ValidationParameters(keys, _settings.Tokens);
        // The handler checks lifetimes against the system clock; tests pin it to the fake one with the same 30 s skew.
        parameters.LifetimeValidator = (notBefore, expires, _, p) =>
            expires is { } e && e.AddSeconds(30) > at && (notBefore is not { } nb || nb.AddSeconds(-30) <= at);
        return new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }

    private (string Private, string Public) WriteKey(string name)
    {
        Directory.CreateDirectory(_directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePath = Path.Combine(_directory, name + ".key");
        var publicPath = Path.Combine(_directory, name + ".pem");
        File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
        return (privatePath, publicPath);
    }

    private static Dictionary<string, object> With(Dictionary<string, object> source, string name, object value) =>
        new(source) { [name] = value };

    private static Dictionary<string, object> Without(Dictionary<string, object> source, string name)
    {
        var copy = new Dictionary<string, object>(source);
        copy.Remove(name);
        return copy;
    }

    private static string SignEs256(ECDsa key, Dictionary<string, object> header, Dictionary<string, object> claims) =>
        Compose(header, claims, input => key.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string UnknownKey(Dictionary<string, object> header, Dictionary<string, object> claims)
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return SignEs256(other, With(header, "kid", TokenKeys.KeyIdOf(other)), claims);
    }

    private static string Compose(Dictionary<string, object> header, Dictionary<string, object> claims, Func<byte[], byte[]> sign)
    {
        var input = Base64UrlEncoder.Encode(JsonSerializer.Serialize(header)) + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));
        return input + "." + Base64UrlEncoder.Encode(sign(Encoding.ASCII.GetBytes(input)));
    }

    #endregion
}
