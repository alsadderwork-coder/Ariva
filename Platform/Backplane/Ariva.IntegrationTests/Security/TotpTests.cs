using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using static Ariva.IntegrationTests.Security.AccountsHost;
using static Ariva.IntegrationTests.Security.AuthenticatorTests;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-010c against PostgreSQL: enrolment only after a valid first code, the pending scope until then, codes at sign-in
/// with the replay guard and the next step accepted, recovery codes used once and regenerated, wrong codes counted
/// towards lockout, and the break-glass account (installer only, recovery code at every sign-in, never locked out,
/// never re-enabled through the API).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TotpTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, new Dictionary<string, string> { ["Auth:TotpRequired"] = "true" });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private string CodeNow(byte[] secret, int offset = 0) => Totp.Code(secret, Totp.StepAt(_host.Clock.GetUtcNow()) + offset);

    /// <summary>Signs in a fresh account, enrols and confirms TOTP; returns the user, session, secret and recovery codes.</summary>
    private async Task<(Guid UserId, Guid SessionId, byte[] Secret, IReadOnlyList<string> Codes)> EnrolledAsync(string userName)
    {
        var userId = await _host.CreateUserAsync(userName);
        var sessionId = SessionOf((await _host.LoginAsync(userName, Password)).Data.Token.AccessToken);
        var secret = Base32.Decode((await _host.EnrolAsync(userId, sessionId)).Data.Secret);
        var confirmed = await _host.ConfirmAsync(userId, sessionId, CodeNow(secret));
        confirmed.HasErrors.Should().BeFalse(string.Join(", ", confirmed.ErrorMessages ?? []));
        return (userId, sessionId, secret, confirmed.Data.RecoveryCodes);
    }

    [Fact]
    public async Task Enrolment_Should_KeepThePendingScopeUntilAValidFirstCode_When_TotpIsRequired()
    {
        var userId = await _host.CreateUserAsync("it.t.enrol");
        var signIn = (await _host.LoginAsync("it.t.enrol", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);
        signIn.Token.Scope.Should().Be("pending", "no TOTP yet");

        var enrolment = (await _host.EnrolAsync(userId, sessionId)).Data;
        var secret = Base32.Decode(enrolment.Secret);
        var wrong = await _host.ConfirmAsync(userId, sessionId, "000000");
        var confirmed = await _host.ConfirmAsync(userId, sessionId, CodeNow(secret));

        secret.Should().HaveCount(20, "160-bit secret");
        enrolment.OtpAuthUri.Should().StartWith("otpauth://totp/Ariva:it.t.enrol?secret=" + enrolment.Secret);
        wrong.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCode);
        confirmed.HasErrors.Should().BeFalse();
        confirmed.Data.Token.Scope.Should().BeNull("a confirmed enrolment lifts the pending scope");
        new JsonWebToken(confirmed.Data.Token.AccessToken).Claims.Where(c => c.Type == "amr").Select(c => c.Value).Should().Equal("pwd", "otp");
        confirmed.Data.RecoveryCodes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        (await _host.ReadAsync<string>("SELECT totp_secret_protected FROM \"user\" WHERE id = @id", userId)).Should().NotContain(enrolment.Secret, "the secret is stored encrypted");
        (await _host.ReadAsync<long>("SELECT count(*) FROM recovery_code WHERE user_id = @id AND length(code_hash) = 64", userId)).Should().Be(10);
        (await _host.EnrolAsync(userId, sessionId)).ErrorMessages.Should().Equal(ISvcAuthenticator.AlreadyEnrolled, "the secret is never handed out again");
    }

    [Fact]
    public async Task Login_Should_NeedTheCodeAndRefuseReplays_When_TotpIsEnrolled()
    {
        var (_, _, secret, _) = await EnrolledAsync("it.t.login");
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        var noCode = await _host.LoginAsync("it.t.login", Password);
        var wrongPassword = await _host.LoginAsync("it.t.login", WrongPassword);
        var code = CodeNow(secret);
        var accepted = await _host.LoginAsync("it.t.login", Password, code: code);
        var replayed = await _host.LoginAsync("it.t.login", Password, code: code);
        var nextStep = await _host.LoginAsync("it.t.login", Password, code: CodeNow(secret, 1));

        noCode.ErrorMessages.Should().Equal(ISvcAuthenticator.MfaRequired);
        wrongPassword.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials, "a wrong password never reveals that a code is needed");
        accepted.HasErrors.Should().BeFalse();
        replayed.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials, "CWE-294: the same code twice is refused");
        nextStep.HasErrors.Should().BeFalse("the code from the next step is accepted once");
    }

    [Fact]
    public async Task Login_Should_AcceptEachRecoveryCodeOnce_When_TheAuthenticatorIsLost()
    {
        var (userId, sessionId, secret, codes) = await EnrolledAsync("it.t.recovery");

        var first = await _host.LoginAsync("it.t.recovery", Password, recoveryCode: codes[0].ToLowerInvariant());
        var again = await _host.LoginAsync("it.t.recovery", Password, recoveryCode: codes[0]);

        first.HasErrors.Should().BeFalse();
        first.Data.Token.RecoveryCodesRemaining.Should().Be(9);
        new JsonWebToken(first.Data.Token.AccessToken).Claims.Where(c => c.Type == "amr").Select(c => c.Value).Should().Equal("pwd", "rc");
        again.HasErrors.Should().BeTrue("a recovery code is single use");

        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        var regenerated = await _host.RegenerateAsync(userId, sessionId, CodeNow(secret));
        regenerated.Data.RecoveryCodes.Should().HaveCount(10);
        (await _host.LoginAsync("it.t.recovery", Password, recoveryCode: codes[1])).HasErrors.Should().BeTrue("regeneration retires the old codes");
        (await _host.LoginAsync("it.t.recovery", Password, recoveryCode: regenerated.Data.RecoveryCodes[0])).HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Login_Should_LockTheAccount_When_TenCodesAreWrong()
    {
        var (_, _, secret, _) = await EnrolledAsync("it.t.lockout");
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        for (var attempt = 0; attempt < 10; attempt++)
            await _host.LoginAsync("it.t.lockout", Password, code: "000000");

        (await _host.ReadAsync<DateTime?>("SELECT locked_until FROM \"user\" WHERE user_name = 'it.t.lockout'")).Should().NotBeNull();
        (await _host.LoginAsync("it.t.lockout", Password, code: CodeNow(secret))).HasErrors.Should().BeTrue("wrong codes count towards the password lockout");
    }

    [Fact]
    public async Task BreakGlass_Should_SignInWithARecoveryCodeNeverLockAndStayOutOfTheApi_When_Issued()
    {
        var credential = await _host.IssueBreakGlassAsync(rotate: false);
        var create = () => _host.IssueBreakGlassAsync(rotate: false);

        await create.Should().ThrowAsync<InvalidOperationException>("one break-glass account per deployment");
        credential.Password.Should().HaveLength(32);
        credential.RecoveryCodes.Should().HaveCount(10);

        (await _host.LoginAsync(credential.UserName, credential.Password)).ErrorMessages.Should().Equal(ISvcAuthenticator.MfaRequired);
        for (var attempt = 0; attempt < 15; attempt++)
            await _host.LoginAsync(credential.UserName, WrongPassword);
        var signIn = await _host.LoginAsync(credential.UserName, credential.Password, recoveryCode: credential.RecoveryCodes[0]);

        signIn.HasErrors.Should().BeFalse("the break-glass account is never locked out");
        signIn.Data.Token.Scope.Should().BeNull("its second factor is the recovery code, so it is not pending for TOTP");
        var userId = Guid.Parse(new JsonWebToken(signIn.Data.Token.AccessToken).Subject);
        (await _host.AsAsync(Guid.CreateVersion7(), null, s => s.EnableAsync(userId, TestContext.Current.CancellationToken))).HasErrors.Should().BeTrue();

        var rotated = await _host.IssueBreakGlassAsync(rotate: true);
        (await _host.SessionStateAsync(SessionOf(signIn.Data.Token.AccessToken))).Should().NotBe(Ariva.Core.Security.SessionState.Active);
        (await _host.LoginAsync(credential.UserName, credential.Password, recoveryCode: credential.RecoveryCodes[1])).HasErrors.Should().BeTrue("rotation retires the old credential");
        (await _host.LoginAsync(rotated.UserName, rotated.Password, recoveryCode: rotated.RecoveryCodes[0])).HasErrors.Should().BeFalse();
    }
}
