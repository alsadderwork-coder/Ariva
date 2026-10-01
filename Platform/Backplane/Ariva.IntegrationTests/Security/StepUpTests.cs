using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using static Ariva.IntegrationTests.Security.AccountsHost;
using static Ariva.IntegrationTests.Security.AuthenticatorTests;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-010d against PostgreSQL: step-up with a TOTP or recovery code moves the session's auth_time forward and puts
/// otp (or rc) in amr; it shares the replay guard and the lockout with sign-in, and needs an enrolled authenticator.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StepUpTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, new Dictionary<string, string> { ["Auth:TotpRequired"] = "true" });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private string CodeNow(byte[] secret, int offset = 0) => Totp.Code(secret, Totp.StepAt(_host.Clock.GetUtcNow()) + offset);

    private async Task<(Guid UserId, Guid SessionId, byte[] Secret, IReadOnlyList<string> Codes)> EnrolledAsync(string userName)
    {
        var userId = await _host.CreateUserAsync(userName);
        var sessionId = SessionOf((await _host.LoginAsync(userName, Password)).Data.Token.AccessToken);
        var secret = Base32.Decode((await _host.EnrolAsync(userId, sessionId)).Data.Secret);
        var confirmed = await _host.ConfirmAsync(userId, sessionId, CodeNow(secret));
        confirmed.HasErrors.Should().BeFalse(string.Join(", ", confirmed.ErrorMessages ?? []));
        return (userId, sessionId, secret, confirmed.Data.RecoveryCodes);
    }

    private static long AuthTimeOf(string token) => long.Parse(new JsonWebToken(token).GetClaim("auth_time").Value, System.Globalization.CultureInfo.InvariantCulture);

    private static string[] MethodsOf(string token) => new JsonWebToken(token).Claims.Where(c => c.Type == "amr").Select(c => c.Value).ToArray();

    [Fact]
    public async Task StepUp_Should_RefreshAuthTimeAndKeepTheSession_When_TheCodeIsValid()
    {
        var (userId, sessionId, secret, _) = await EnrolledAsync("it.su.code");
        var enrolledAt = _host.Clock.GetUtcNow().ToUnixTimeSeconds();
        _host.Clock.Advance(TimeSpan.FromMinutes(20));

        var token = await _host.StepUpAsync(userId, sessionId, code: CodeNow(secret));

        token.HasErrors.Should().BeFalse(string.Join(", ", token.ErrorMessages ?? []));
        AuthTimeOf(token.Data.AccessToken).Should().Be(enrolledAt + 20 * 60);
        MethodsOf(token.Data.AccessToken).Should().Equal("pwd", "otp");
        SessionOf(token.Data.AccessToken).Should().Be(sessionId, "step-up never starts a new session");
        (await _host.ReadAsync<DateTime>("SELECT authenticated_on FROM user_session WHERE id = @id", sessionId))
            .Should().Be(_host.Clock.GetUtcNow().UtcDateTime, "a refreshed access token keeps the stepped-up auth_time");
    }

    [Fact]
    public async Task StepUp_Should_RefuseAReplayedCode_When_ItWasAlreadyUsed()
    {
        var (userId, sessionId, secret, _) = await EnrolledAsync("it.su.replay");
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        var code = CodeNow(secret);

        (await _host.StepUpAsync(userId, sessionId, code: code)).HasErrors.Should().BeFalse();
        (await _host.StepUpAsync(userId, sessionId, code: code)).ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCode);
        (await _host.LoginAsync("it.su.replay", Password, code: code)).HasErrors.Should().BeTrue("sign-in and step-up share the replay guard");
    }

    [Fact]
    public async Task StepUp_Should_AcceptARecoveryCodeOnce_When_TheAuthenticatorIsLost()
    {
        var (userId, sessionId, _, codes) = await EnrolledAsync("it.su.recovery");

        var first = await _host.StepUpAsync(userId, sessionId, recoveryCode: codes[0]);
        var again = await _host.StepUpAsync(userId, sessionId, recoveryCode: codes[0]);

        first.HasErrors.Should().BeFalse();
        MethodsOf(first.Data.AccessToken).Should().Equal("pwd", "rc");
        again.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCode);
    }

    [Fact]
    public async Task StepUp_Should_CountTowardsTheLockout_When_CodesAreWrong()
    {
        var (userId, sessionId, _, _) = await EnrolledAsync("it.su.lockout");

        for (var attempt = 0; attempt < 10; attempt++)
            await _host.StepUpAsync(userId, sessionId, code: "000000");

        (await _host.ReadAsync<DateTime?>("SELECT locked_until FROM \"user\" WHERE user_name = 'it.su.lockout'")).Should().NotBeNull();
    }

    [Fact]
    public async Task StepUp_Should_AskForEnrolment_When_NoAuthenticatorIsEnrolled()
    {
        var userId = await _host.CreateUserAsync("it.su.none");
        var sessionId = SessionOf((await _host.LoginAsync("it.su.none", Password)).Data.Token.AccessToken);

        (await _host.StepUpAsync(userId, sessionId, code: "123456")).ErrorMessages.Should().Equal(ISvcAuthenticator.NotEnrolled);
        (await _host.StepUpAsync(userId, Guid.NewGuid(), code: "123456")).ErrorMessages.Should().Equal(new[] { ISvcAuthenticator.InvalidCredentials }, "an unknown session is refused");
    }
}
