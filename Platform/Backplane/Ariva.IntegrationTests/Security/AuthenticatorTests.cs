using System.Security.Claims;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using static Ariva.IntegrationTests.Security.AccountsHost;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-010a against PostgreSQL with the shipped scripts and the Ariva.Core mapping: sign-in, the identical failure
/// for every reason, lockout (counted atomically, also under parallel attempts), the lock ending on its own, hash
/// upgrade, password change and the pending scope, and permissions resolved from stored grants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthenticatorTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    #region Sign-in

    [Fact]
    public async Task Login_Should_IssueAFullTokenWithANewSession_When_PasswordIsCorrect()
    {
        var userId = await _host.CreateUserAsync("it.success", roles: [RoleCodes.SystemAdministrator]);

        var result = await _host.LoginAsync("  IT.Success ", Password);

        result.HasErrors.Should().BeFalse();
        result.Data.Token.Scope.Should().BeNull();
        result.Data.Token.ExpiresIn.Should().Be(900);
        result.Data.RefreshLifetime.Should().Be(TimeSpan.FromHours(12));
        var token = new JsonWebToken(result.Data.Token.AccessToken);
        token.Subject.Should().Be(userId.ToString());
        token.Alg.Should().Be("ES256");
        (await _host.SessionStateAsync(SessionOf(result.Data.Token.AccessToken))).Should().Be(SessionState.Active);
        (await _host.ReadAsync<DateTime?>("SELECT last_login_on FROM \"user\" WHERE user_name = 'it.success'")).Should().Be(_host.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Login_Should_FailTheSameWay_When_PasswordIsWrongUserIsUnknownOrDisabled()
    {
        await _host.CreateUserAsync("it.same");
        await _host.CreateUserAsync("it.disabled", disabled: true);

        var wrong = await _host.LoginAsync("it.same", WrongPassword);
        var unknown = await _host.LoginAsync("it.nobody", WrongPassword);
        var malformed = await _host.LoginAsync("x", WrongPassword);
        var disabled = await _host.LoginAsync("it.disabled", Password);

        foreach (var result in new[] { wrong, unknown, malformed, disabled })
        {
            result.HasErrors.Should().BeTrue();
            result.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials);
        }

        (await _host.ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE user_name = 'it.same'")).Should().Be(1);
    }

    [Fact]
    public async Task Login_Should_IssueThePendingScope_When_PasswordIsTemporary()
    {
        await _host.CreateUserAsync("it.temporary", temporary: true);

        var result = await _host.LoginAsync("it.temporary", Password);

        result.Data.Token.Scope.Should().Be("pending");
        new JsonWebToken(result.Data.Token.AccessToken).GetClaim("scope").Value.Should().Be("pending");
    }

    [Fact]
    public async Task Login_Should_UpgradeTheHash_When_StoredHashIsWeaker()
    {
        await _host.CreateUserAsync("it.rehash", iterations: 1_000);

        (await _host.LoginAsync("it.rehash", Password)).HasErrors.Should().BeFalse();

        (await _host.ReadAsync<int>("SELECT password_iterations FROM \"user\" WHERE user_name = 'it.rehash'")).Should().Be(600_000);
        (await _host.LoginAsync("it.rehash", Password)).HasErrors.Should().BeFalse("the upgraded hash verifies");
    }

    #endregion

    #region Lockout

    [Fact]
    public async Task Login_Should_LockAtTheTenthFailureAndUnlockOnItsOwn_When_FailuresAreConsecutive()
    {
        await _host.CreateUserAsync("it.lockout");

        for (var attempt = 1; attempt < 10; attempt++)
            await _host.LoginAsync("it.lockout", WrongPassword);
        (await LockedUntilAsync()).Should().BeNull("nine failures do not lock");

        await _host.LoginAsync("it.lockout", WrongPassword);
        (await LockedUntilAsync()).Should().Be(_host.Clock.GetUtcNow().UtcDateTime.AddMinutes(15));

        (await _host.LoginAsync("it.lockout", Password)).HasErrors.Should().BeTrue("a locked account refuses the correct password");
        _host.Clock.Advance(TimeSpan.FromMinutes(15));
        (await _host.LoginAsync("it.lockout", Password)).HasErrors.Should().BeFalse("the lock ends on its own");
        (await _host.ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE user_name = 'it.lockout'")).Should().Be(0);

        Task<DateTime?> LockedUntilAsync() => _host.ReadAsync<DateTime?>("SELECT locked_until FROM \"user\" WHERE user_name = 'it.lockout'");
    }

    [Fact]
    public async Task Login_Should_LockAfterExactlyTenFailures_When_AttemptsArriveInParallel()
    {
        await _host.CreateUserAsync("it.parallel");

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => _host.LoginAsync("it.parallel", WrongPassword)));

        (await _host.ReadAsync<DateTime?>("SELECT locked_until FROM \"user\" WHERE user_name = 'it.parallel'")).Should().NotBeNull("parallel attempts cannot lose counts");
        (await _host.ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE user_name = 'it.parallel'")).Should().Be(0,
            "the tenth failure locked the account and reset the count; failures while locked are not counted");
    }

    [Fact]
    public async Task Unlock_Should_ClearTheLock_When_AdministratorUnlocks()
    {
        var userId = await _host.CreateUserAsync("it.unlock");
        for (var attempt = 0; attempt < 10; attempt++)
            await _host.LoginAsync("it.unlock", WrongPassword);

        var administrator = await _host.CreateUserAsync("it.unlock.admin", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var result = await _host.AsAsync(administrator, null, service => service.UnlockAsync(userId, TestContext.Current.CancellationToken));

        result.HasErrors.Should().BeFalse();
        (await _host.LoginAsync("it.unlock", Password)).HasErrors.Should().BeFalse();
    }

    #endregion

    #region Password change

    [Fact]
    public async Task ChangePassword_Should_RefuseWeakReusedOrUnverifiedPasswords_When_Asked()
    {
        var userId = await _host.CreateUserAsync("it.change", temporary: true);
        var sessionId = SessionOf((await _host.LoginAsync("it.change", Password)).Data.Token.AccessToken);

        (await _host.ChangeAsync(userId, sessionId, Password, "password1234")).ErrorMessages.Should().Contain(e => e.Contains("breached"));
        (await _host.ChangeAsync(userId, sessionId, Password, "my it.change key 2026")).ErrorMessages.Should().Contain(e => e.Contains("username"));
        (await _host.ChangeAsync(userId, sessionId, Password, Password)).ErrorMessages.Should().Contain(e => e.Contains("different"));
        (await _host.ChangeAsync(userId, sessionId, WrongPassword, "amber kiosk river 5520")).ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials);

        var changed = await _host.ChangeAsync(userId, sessionId, Password, "amber kiosk river 5520");

        changed.HasErrors.Should().BeFalse();
        changed.Data.Scope.Should().BeNull("the temporary password is gone, so the account leaves the pending scope");
        SessionOf(changed.Data.AccessToken).Should().Be(sessionId, "the session that changed the password continues");
        (await _host.LoginAsync("it.change", Password)).HasErrors.Should().BeTrue();
        (await _host.LoginAsync("it.change", "amber kiosk river 5520")).Data.Token.Scope.Should().BeNull();
    }

    [Fact]
    public async Task ChangePassword_Should_Refuse_When_SessionBelongsToSomeoneElse()
    {
        var userId = await _host.CreateUserAsync("it.change.owner");
        await _host.CreateUserAsync("it.change.other");
        var otherSession = SessionOf((await _host.LoginAsync("it.change.other", Password)).Data.Token.AccessToken);

        var result = await _host.ChangeAsync(userId, otherSession, Password, "amber kiosk river 5520");

        result.HasErrors.Should().BeTrue();
    }

    #endregion

    #region Permissions

    [Fact]
    public async Task GetPermissions_Should_ComeFromStoredGrants_When_TokenHasASubject()
    {
        var userId = await _host.CreateUserAsync("it.grants", roles: [RoleCodes.HandlerStationManager]);
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", userId.ToString()), new Claim(ClaimTypes.Role, RoleCodes.SystemAdministrator)], "Bearer"));

        await using var scope = _host.Provider.CreateAsyncScope();
        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionResolver>().GetPermissionsAsync(claims, TestContext.Current.CancellationToken);

        permissions.Should().BeEquivalentTo(RolePermissions.HandlerStationManager, "a role claim in a token is ignored; the stored grant decides");
    }

    [Fact]
    public async Task GetPermissions_Should_BeEmpty_When_TokenIsPendingOrAccountIsDisabled()
    {
        var disabledId = await _host.CreateUserAsync("it.grants.off", roles: [RoleCodes.SystemAdministrator], disabled: true);
        var activeId = await _host.CreateUserAsync("it.grants.pending", roles: [RoleCodes.SystemAdministrator]);
        var disabled = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", disabledId.ToString())], "Bearer"));
        var pending = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", activeId.ToString()), new Claim("scope", "pending")], "Bearer"));

        await using var scope = _host.Provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        (await resolver.GetPermissionsAsync(disabled, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await resolver.GetPermissionsAsync(pending, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Current_Should_DescribeTheCallerAndWhatTheFirstSignInNeeds_When_Asked()
    {
        var fullId = await _host.CreateUserAsync("it.me.full", roles: [RoleCodes.TerminalDutyManager]);
        var pendingId = await _host.CreateUserAsync("it.me.pending", temporary: true, roles: [RoleCodes.SystemAdministrator]);
        var fullSession = SessionOf((await _host.LoginAsync("it.me.full", Password)).Data.Token.AccessToken);
        var pendingSession = SessionOf((await _host.LoginAsync("it.me.pending", Password)).Data.Token.AccessToken);

        var full = await _host.AsAsync(fullId, fullSession, service => service.CurrentAsync(TestContext.Current.CancellationToken));
        var pending = await _host.AsAsync(pendingId, pendingSession, service => service.CurrentAsync(TestContext.Current.CancellationToken));
        var nobody = await _host.AsAsync(null, null, service => service.CurrentAsync(TestContext.Current.CancellationToken));

        full.HasErrors.Should().BeFalse();
        full.Data.Should().Match<CurrentUserViewModel>(u => u.UserName == "it.me.full" && !u.Pending && !u.MustChangePassword && !u.TotpEnrolled);
        full.Data.Roles.Should().Equal(RoleCodes.TerminalDutyManager);
        full.Data.Permissions.Should().BeEquivalentTo(RolePermissions.TerminalDutyManager.Select(p => p.Code)).And.BeInAscendingOrder(StringComparer.Ordinal);
        full.Data.Permissions.Should().Contain("LiveQueue.View").And.NotContain("User.View");

        pending.Data.Should().Match<CurrentUserViewModel>(u => u.Pending && u.MustChangePassword);
        pending.Data.Permissions.Should().BeEmpty("a pending account holds no permission until its first sign-in is done");
        pending.Data.Roles.Should().Equal(RoleCodes.SystemAdministrator);

        nobody.HasErrors.Should().BeTrue();
    }

    #endregion

    internal static Guid SessionOf(string accessToken) => Guid.Parse(new JsonWebToken(accessToken).GetClaim("sid").Value);
}
