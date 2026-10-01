using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using static Ariva.IntegrationTests.Security.AccountsHost;
using static Ariva.IntegrationTests.Security.AuthenticatorTests;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-010b against PostgreSQL: every sign-in is a new session and refresh family; refresh rotates the token, the
/// grace window returns the same successor once, other reuse revokes the family; idle and absolute expiry with a
/// manual clock; logout, password change and disable end sessions, and the session check sees it at once on this node.
/// The grace window is 3 seconds here so a test can cross it with the clock.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, new Dictionary<string, string> { ["Auth:Sessions:RefreshGraceSeconds"] = "3" });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Login_Should_StoreTheSessionAndOnlyAHashOfTheRefreshToken_When_SignInSucceeds()
    {
        await _host.CreateUserAsync("it.s.store", roles: [RoleCodes.SystemAdministrator]);

        var signIn = (await _host.LoginAsync("it.s.store", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);

        signIn.RefreshToken.Should().HaveLength(43, "256 random bits, base64url");
        (await _host.ReadAsync<int>("SELECT idle_timeout_seconds FROM user_session WHERE id = @id", sessionId)).Should().Be(1800, "administrators idle out after 30 minutes");
        (await _host.ReadAsync<string>("SELECT ip_address FROM user_session WHERE id = @id", sessionId)).Should().Be("10.1.2.3");
        (await _host.ReadAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id = @id AND length(token_hash) = 64", sessionId)).Should().Be(1);
    }

    [Fact]
    public async Task Login_Should_GiveOperationalRolesTheLongIdleTime_When_UserHasNoAdministratorRole()
    {
        await _host.CreateUserAsync("it.s.operational", roles: [RoleCodes.TerminalDutyManager]);

        var sessionId = SessionOf((await _host.LoginAsync("it.s.operational", Password)).Data.Token.AccessToken);

        (await _host.ReadAsync<int>("SELECT idle_timeout_seconds FROM user_session WHERE id = @id", sessionId)).Should().Be(4 * 3600);
    }

    [Fact]
    public async Task Login_Should_IssueANewSessionAndRevokeThePresentedOne_When_BrowserStillHoldsACookie()
    {
        await _host.CreateUserAsync("it.s.fixation");
        var first = (await _host.LoginAsync("it.s.fixation", Password)).Data;

        var second = (await _host.LoginAsync("it.s.fixation", Password, presentedRefreshToken: first.RefreshToken)).Data;

        SessionOf(second.Token.AccessToken).Should().NotBe(SessionOf(first.Token.AccessToken), "CWE-384: never continue a session across sign-in");
        (await _host.SessionStateAsync(SessionOf(first.Token.AccessToken))).Should().Be(SessionState.Revoked);
        (await _host.SessionStateAsync(SessionOf(second.Token.AccessToken))).Should().Be(SessionState.Active);
        (await _host.RefreshAsync(first.RefreshToken)).HasErrors.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_Should_RotateTheTokenAndKeepTheSession_When_TokenIsCurrent()
    {
        await _host.CreateUserAsync("it.s.rotate");
        var signIn = (await _host.LoginAsync("it.s.rotate", Password)).Data;

        var refreshed = await _host.RefreshAsync(signIn.RefreshToken);

        refreshed.HasErrors.Should().BeFalse();
        refreshed.Data.RefreshToken.Should().NotBe(signIn.RefreshToken);
        SessionOf(refreshed.Data.Token.AccessToken).Should().Be(SessionOf(signIn.Token.AccessToken));
        (await _host.RefreshAsync(refreshed.Data.RefreshToken)).HasErrors.Should().BeFalse("the successor is current");
    }

    [Fact]
    public async Task Refresh_Should_ReturnTheSameSuccessorOnceThenRevokeTheFamily_When_TokenIsReusedWithinGrace()
    {
        await _host.CreateUserAsync("it.s.grace");
        var signIn = (await _host.LoginAsync("it.s.grace", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);

        var first = await _host.RefreshAsync(signIn.RefreshToken);
        var second = await _host.RefreshAsync(signIn.RefreshToken);
        var third = await _host.RefreshAsync(signIn.RefreshToken);

        second.HasErrors.Should().BeFalse();
        second.Data.RefreshToken.Should().Be(first.Data.RefreshToken, "a second tab refreshing at the same moment gets the same successor");
        third.ErrorMessages.Should().Equal(ISvcAuthenticator.SessionExpired);
        (await _host.SessionStateAsync(sessionId)).Should().Be(SessionState.Revoked, "a further reuse is theft: the family is revoked");
        (await _host.RefreshAsync(first.Data.RefreshToken)).HasErrors.Should().BeTrue("the successor dies with its family");
        (await _host.ReadAsync<string>("SELECT revoked_reason FROM user_session WHERE id = @id", sessionId)).Should().Be("refresh-token-reused");
    }

    [Fact]
    public async Task Refresh_Should_RevokeTheFamily_When_TokenIsReusedAfterGrace()
    {
        await _host.CreateUserAsync("it.s.theft");
        var signIn = (await _host.LoginAsync("it.s.theft", Password)).Data;
        (await _host.RefreshAsync(signIn.RefreshToken)).HasErrors.Should().BeFalse();

        _host.Clock.Advance(TimeSpan.FromSeconds(4));
        var reused = await _host.RefreshAsync(signIn.RefreshToken);

        reused.HasErrors.Should().BeTrue();
        (await _host.SessionStateAsync(SessionOf(signIn.Token.AccessToken))).Should().Be(SessionState.Revoked);
    }

    [Fact]
    public async Task Refresh_Should_GiveBothCallersTheSameSuccessor_When_TwoTabsRefreshTogether()
    {
        await _host.CreateUserAsync("it.s.tabs");
        var signIn = (await _host.LoginAsync("it.s.tabs", Password)).Data;

        var results = await Task.WhenAll(_host.RefreshAsync(signIn.RefreshToken), _host.RefreshAsync(signIn.RefreshToken));

        results.Should().OnlyContain(r => !r.HasErrors);
        results[0].Data.RefreshToken.Should().Be(results[1].Data.RefreshToken);
        (await _host.SessionStateAsync(SessionOf(signIn.Token.AccessToken))).Should().Be(SessionState.Active);
    }

    [Fact]
    public async Task Session_Should_Expire_When_IdleTimePassesWithoutRefresh()
    {
        await _host.CreateUserAsync("it.s.idle", roles: [RoleCodes.SystemAdministrator]);
        var signIn = (await _host.LoginAsync("it.s.idle", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);

        _host.Clock.Advance(TimeSpan.FromMinutes(29));
        (await _host.SessionStateAsync(sessionId)).Should().Be(SessionState.Active);
        _host.Clock.Advance(TimeSpan.FromMinutes(2));

        (await _host.SessionStateAsync(sessionId)).Should().Be(SessionState.Expired, "30 minutes without a refresh ends an administrator session");
        (await _host.RefreshAsync(signIn.RefreshToken)).ErrorMessages.Should().Equal(ISvcAuthenticator.SessionExpired);
    }

    [Fact]
    public async Task Session_Should_EndAfter12Hours_When_RefreshedThroughout()
    {
        await _host.CreateUserAsync("it.s.absolute", roles: [RoleCodes.BorderShiftSupervisor]);
        var signIn = (await _host.LoginAsync("it.s.absolute", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);
        var token = signIn.RefreshToken;

        for (var hour = 3; hour < 12; hour += 3)
        {
            _host.Clock.Advance(TimeSpan.FromHours(3));
            var refreshed = await _host.RefreshAsync(token);
            refreshed.HasErrors.Should().BeFalse($"refresh keeps an operational session alive at hour {hour}");
            refreshed.Data.RefreshLifetime.Should().Be(TimeSpan.FromHours(12 - hour), "the cookie lives exactly as long as the session can");
            token = refreshed.Data.RefreshToken;
        }

        _host.Clock.Advance(TimeSpan.FromHours(3));

        (await _host.SessionStateAsync(sessionId)).Should().Be(SessionState.Expired);
        (await _host.RefreshAsync(token)).HasErrors.Should().BeTrue("12 hours is the absolute limit for every role");
    }

    [Fact]
    public async Task Logout_Should_RevokeTheSessionAtOnce_When_Called()
    {
        var userId = await _host.CreateUserAsync("it.s.logout");
        var signIn = (await _host.LoginAsync("it.s.logout", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);
        (await _host.SessionStateAsync(sessionId)).Should().Be(SessionState.Active, "the check is now cached");

        await _host.LogoutAsync(userId, sessionId);

        (await _host.SessionStateAsync(sessionId)).Should().Be(SessionState.Revoked, "logout evicts the cached session");
        (await _host.RefreshAsync(signIn.RefreshToken)).HasErrors.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_Should_EndTheOtherSessions_When_PasswordChanges()
    {
        var userId = await _host.CreateUserAsync("it.s.password");
        var other = (await _host.LoginAsync("it.s.password", Password)).Data;
        var current = (await _host.LoginAsync("it.s.password", Password)).Data;

        (await _host.ChangeAsync(userId, SessionOf(current.Token.AccessToken), Password, "amber kiosk river 5520")).HasErrors.Should().BeFalse();

        (await _host.SessionStateAsync(SessionOf(other.Token.AccessToken))).Should().Be(SessionState.Revoked);
        (await _host.SessionStateAsync(SessionOf(current.Token.AccessToken))).Should().Be(SessionState.Active);
    }

    [Fact]
    public async Task Disable_Should_EndEverySessionOfTheUser_When_AdministratorDisables()
    {
        var administratorId = await _host.CreateUserAsync("it.s.admin", roles: [RoleCodes.SystemAdministrator]);
        await _host.CreateUserAsync("it.s.victim", roles: [RoleCodes.TerminalDutyManager]);
        var victimId = await _host.ReadAsync<Guid>("SELECT id FROM \"user\" WHERE user_name = 'it.s.victim'");
        var first = (await _host.LoginAsync("it.s.victim", Password)).Data;
        var second = (await _host.LoginAsync("it.s.victim", Password)).Data;

        (await _host.DisableAsync(administratorId, administratorId)).ErrorMessages.Should().Equal(ISvcAuthenticator.CannotChangeOwnAccount);
        (await _host.DisableAsync(administratorId, victimId)).HasErrors.Should().BeFalse();

        (await _host.SessionStateAsync(SessionOf(first.Token.AccessToken))).Should().Be(SessionState.Revoked);
        (await _host.SessionStateAsync(SessionOf(second.Token.AccessToken))).Should().Be(SessionState.Revoked);
        (await _host.RefreshAsync(second.RefreshToken)).HasErrors.Should().BeTrue();
        (await _host.LoginAsync("it.s.victim", Password)).HasErrors.Should().BeTrue();
    }

    [Fact]
    public async Task SessionCheck_Should_BeUnknown_When_SessionDoesNotExist()
    {
        await _host.CreateUserAsync("it.s.unknown");

        (await _host.SessionStateAsync(Guid.CreateVersion7())).Should().Be(SessionState.Unknown);
        (await _host.RefreshAsync("not-a-refresh-token")).ErrorMessages.Should().Equal(ISvcAuthenticator.SessionExpired);
    }
}
