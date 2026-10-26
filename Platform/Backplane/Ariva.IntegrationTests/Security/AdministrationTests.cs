using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Ariva.IntegrationTests.Security.AccountsHost;
using static Ariva.IntegrationTests.Security.AuthenticatorTests;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-011 against PostgreSQL: user creation with a one-time temporary password, the grant rules (no self-grant,
/// nothing above the granter, the last administrator keeps the role), resets that end sessions, and an audit entry
/// for every change in an append-only table.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdministrationTests(PostgresFixture fixture) : IAsyncDisposable
{
    // Its own database: these tests disable administrators and use the one break-glass account.
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<T> As<T>(Guid caller, Func<IServiceProvider, Task<T>> work) => _host.AsCallerAsync(caller, work);

    private static ISvcUsers Users(IServiceProvider services) => services.GetRequiredService<ISvcUsers>();

    private static ISvcRoleAssignment Roles(IServiceProvider services) => services.GetRequiredService<ISvcRoleAssignment>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_Should_IssueAOneTimeTemporaryPasswordAndAudit_When_AdministratorCreatesAUser()
    {
        var admin = await _host.CreateUserAsync("it.adm.creator", roles: [RoleCodes.SystemAdministrator], allSites: true);

        var created = await As(admin, s => Users(s).CreateAsync(new CreateUserRequest("IT.Adm.New", "New Officer", "new@example.org", [RoleCodes.BorderShiftSupervisor]), Ct));
        var duplicate = await As(admin, s => Users(s).CreateAsync(new CreateUserRequest("it.adm.new"), Ct));
        var invalid = await As(admin, s => Users(s).CreateAsync(new CreateUserRequest("a b"), Ct));

        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.User.UserName.Should().Be("it.adm.new");
        created.Data.User.Roles.Should().Equal(RoleCodes.BorderShiftSupervisor);
        created.Data.User.MustChangePassword.Should().BeTrue();
        duplicate.ErrorMessages.Should().Equal(AdministrationErrors.UserNameTaken);
        invalid.ErrorMessages.Should().Equal(AdministrationErrors.InvalidUserName);

        var signIn = await _host.LoginAsync("it.adm.new", created.Data.TemporaryPassword);
        signIn.Data.Token.Scope.Should().Be("pending", "a temporary password must be changed first");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'User.Created' AND actor_id IS NOT NULL", created.Data.User.Id)).Should().Be(1);
        var summary = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id", created.Data.User.Id);
        summary.Contains(created.Data.TemporaryPassword, StringComparison.Ordinal).Should().BeFalse("the audit trail never holds the temporary password");
        summary.Should().Contain("roles=BorderShiftSupervisor");
        (await _host.ReadAsync<Guid?>("SELECT granted_by_id FROM user_role WHERE user_id = @id", created.Data.User.Id)).Should().Be(admin);
    }

    [Fact]
    public async Task Grant_Should_RefuseSelfAndAboveOwnRank_When_RulesAreBroken()
    {
        var admin = await _host.CreateUserAsync("it.adm.granter", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var manager = await _host.CreateUserAsync("it.adm.manager", roles: [RoleCodes.TerminalDutyManager], allSites: true);
        var target = await _host.CreateUserAsync("it.adm.target");

        (await As(admin, s => Roles(s).GrantAsync(admin, RoleCodes.BorderShiftSupervisor, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.OwnAccount);
        (await As(manager, s => Roles(s).GrantAsync(target, RoleCodes.SystemAdministrator, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.AboveOwnRole);
        (await As(admin, s => Roles(s).GrantAsync(target, "Superuser", Ct))).ErrorMessages.Should().Equal(AdministrationErrors.UnknownRole);
        (await As(admin, s => Roles(s).GrantAsync(Guid.NewGuid(), RoleCodes.BorderShiftSupervisor, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);

        var granted = await As(admin, s => Roles(s).GrantAsync(target, RoleCodes.SystemAdministrator, Ct));
        granted.Data.Roles.Should().Equal(RoleCodes.SystemAdministrator);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'User.RoleGranted'", target)).Should().Be(1);
    }

    [Fact]
    public async Task Revoke_Should_EndTheUsersSessionsAndAudit_When_RoleIsRemoved()
    {
        var admin = await _host.CreateUserAsync("it.adm.revoker", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var officer = await _host.CreateUserAsync("it.adm.officer", roles: [RoleCodes.BorderShiftSupervisor]);
        var session = SessionOf((await _host.LoginAsync("it.adm.officer", Password)).Data.Token.AccessToken);

        var revoked = await As(admin, s => Roles(s).RevokeAsync(officer, RoleCodes.BorderShiftSupervisor, Ct));
        var again = await As(admin, s => Roles(s).RevokeAsync(officer, RoleCodes.BorderShiftSupervisor, Ct));

        revoked.Data.Roles.Should().BeEmpty();
        again.HasErrors.Should().BeFalse("revoking a role the user does not hold changes nothing");
        (await _host.SessionStateAsync(session)).Should().Be(SessionState.Revoked);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'User.RoleRevoked'", officer)).Should().Be(1, "only the real change is audited");
    }

    [Fact]
    public async Task Revoke_Should_KeepOneRegularAdministrator_When_BreakGlassRemovesTheLast()
    {
        // Only the break-glass account can be the caller here: any other caller is itself an active administrator.
        var credential = await _host.IssueBreakGlassAsync(rotate: (await _host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE is_break_glass")) > 0);
        var breakGlass = await _host.ReadAsync<Guid>("SELECT id FROM \"user\" WHERE is_break_glass");
        credential.UserName.Should().NotBeNullOrEmpty();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "UPDATE \"user\" SET is_disabled = true WHERE NOT is_break_glass AND id IN (SELECT user_id FROM user_role WHERE role_code = 'SystemAdministrator')", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var last = await _host.CreateUserAsync("it.adm.last", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await As(breakGlass, s => Roles(s).RevokeAsync(last, RoleCodes.SystemAdministrator, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.LastAdministrator);
        (await As(breakGlass, s => Users(s).GetAsync(breakGlass, Ct))).ErrorMessages.Should().Equal(new[] { AdministrationErrors.NotFound }, "the break-glass account is not administered through the API");

        await _host.CreateUserAsync("it.adm.next", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await As(breakGlass, s => Roles(s).RevokeAsync(last, RoleCodes.SystemAdministrator, Ct))).HasErrors.Should().BeFalse("another active administrator remains");
    }

    [Fact]
    public async Task Resets_Should_EndSessionsClearTotpAndAudit_When_AdministratorResets()
    {
        var admin = await _host.CreateUserAsync("it.adm.resetter", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var officer = await _host.CreateUserAsync("it.adm.reset", roles: [RoleCodes.TerminalDutyManager]);
        var session = SessionOf((await _host.LoginAsync("it.adm.reset", Password)).Data.Token.AccessToken);
        var secret = Base32.Decode((await _host.EnrolAsync(officer, session)).Data.Secret);
        (await _host.ConfirmAsync(officer, session, Totp.Code(secret, Totp.StepAt(_host.Clock.GetUtcNow())))).HasErrors.Should().BeFalse();

        var self = await As(admin, s => Users(s).ResetTotpAsync(admin, Ct));
        var totp = await As(admin, s => Users(s).ResetTotpAsync(officer, Ct));

        self.ErrorMessages.Should().Equal(AdministrationErrors.OwnAccount);
        totp.HasErrors.Should().BeFalse();
        (await _host.SessionStateAsync(session)).Should().Be(SessionState.Revoked);
        (await _host.ReadAsync<bool>("SELECT totp_enrolled FROM \"user\" WHERE id = @id", officer)).Should().BeFalse();
        (await _host.ReadAsync<string>("SELECT totp_secret_protected FROM \"user\" WHERE id = @id", officer)).Should().BeNull();
        (await _host.ReadAsync<long>("SELECT count(*) FROM recovery_code WHERE user_id = @id AND used_on IS NULL", officer)).Should().Be(0);

        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        var password = await As(admin, s => Users(s).ResetPasswordAsync(officer, Ct));
        (await _host.LoginAsync("it.adm.reset", Password)).HasErrors.Should().BeTrue("the old password stops working");
        (await _host.LoginAsync("it.adm.reset", password.Data.TemporaryPassword)).Data.Token.Scope.Should().Be("pending");

        var actions = await As(admin, s => s.GetRequiredService<ISvcAuditEntries>().SearchAsync(new AuditEntryCriteria { TargetId = officer }, Ct));
        actions.Data.Data.Select(e => e.Action).Should().Equal(AuditActions.PasswordReset, AuditActions.TotpReset);
        actions.Data.Data.Should().OnlyContain(e => e.ActorId == admin && e.ActorName == "it-admin" && e.IpAddress == "10.1.2.3");
    }

    [Fact]
    public async Task Search_Should_FilterSortPageAndHideBreakGlass_When_Queried()
    {
        var admin = await _host.CreateUserAsync("it.adm.searcher", roles: [RoleCodes.SystemAdministrator], allSites: true);
        await _host.CreateUserAsync("it.adm.find.b", roles: [RoleCodes.HandlerStationManager]);
        await _host.CreateUserAsync("it.adm.find.a", roles: [RoleCodes.HandlerStationManager]);
        await _host.CreateUserAsync("it.adm.find.c", disabled: true);
        await _host.IssueBreakGlassAsync(rotate: (await _host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE is_break_glass")) > 0);

        var page = await As(admin, s => Users(s).SearchAsync(new UserCriteria { Text = "IT.ADM.FIND", SortBy = "userName", PageSize = 2 }, Ct));
        var role = await As(admin, s => Users(s).SearchAsync(new UserCriteria { Text = "it.adm.find", Role = RoleCodes.HandlerStationManager }, Ct));
        var disabled = await As(admin, s => Users(s).SearchAsync(new UserCriteria { Text = "it.adm.find", IsDisabled = true }, Ct));
        var badSort = await As(admin, s => Users(s).SearchAsync(new UserCriteria { SortBy = "password_hash" }, Ct));
        var all = await As(admin, s => Users(s).SearchAsync(new UserCriteria { PageSize = 500 }, Ct));

        page.Data.TotalCount.Should().Be(3);
        page.Data.Data.Select(u => u.UserName).Should().Equal("it.adm.find.a", "it.adm.find.b");
        role.Data.Data.Should().OnlyContain(u => u.Roles.Contains(RoleCodes.HandlerStationManager)).And.HaveCount(2);
        disabled.Data.Data.Select(u => u.UserName).Should().Equal("it.adm.find.c");
        badSort.ErrorMessages.Should().Equal(AdministrationErrors.InvalidCriteria);
        all.Data.Data.Should().NotContain(u => u.UserName == "break-glass");
    }

    [Fact]
    public async Task Names_Should_BeCleanTextAndQuotedInTheAudit_When_AnAdministratorSetsThem()
    {
        var admin = await _host.CreateUserAsync("it.adm.namer", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var target = await _host.CreateUserAsync("it.adm.named");

        foreach (var name in new[] { "Line\nbreak", "Right\u202Eto left", "Bell\u0007", new string('x', 201) })
        {
            (await As(admin, s => Users(s).UpdateAsync(target, new UpdateUserRequest(name), Ct))).ErrorMessages.Should()
                .Equal(new[] { AdministrationErrors.InvalidDisplayName }, "a name is one line of visible text");
            (await As(admin, s => Users(s).CreateAsync(new CreateUserRequest($"it.adm.bad{name.Length}", name), Ct))).ErrorMessages.Should()
                .Equal(new[] { AdministrationErrors.InvalidDisplayName }, "the same rule when creating");
        }

        // A name that looks like more fields stays one quoted value in the trail.
        var forged = "Officer; roles=SystemAdministrator; sites=*";
        (await As(admin, s => Users(s).UpdateAsync(target, new UpdateUserRequest(forged, "named@example.org"), Ct))).HasErrors.Should().BeFalse();
        var after = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'User.Updated'", target);
        after.Should().Contain("displayName=\"Officer; roles=SystemAdministrator; sites=*\"; email=\"named@example.org\"; roles=;")
            .And.Contain("sites=;", "the real fields follow the quoted name");
        (await As(admin, s => Users(s).UpdateAsync(target, new UpdateUserRequest("المسؤول أحمد"), Ct))).Data.DisplayName.Should().Be("المسؤول أحمد", "any script is a name");
    }

    [Fact]
    public async Task AuditEntry_Should_BeAppendOnly_When_TheRuntimeRoleTriesToChangeIt()
    {
        var admin = await _host.CreateUserAsync("it.adm.auditor", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var target = await _host.CreateUserAsync("it.adm.audited");
        await As(admin, s => Users(s).UpdateAsync(target, new UpdateUserRequest("Audited"), Ct));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        foreach (var sql in new[] { "UPDATE audit_entry SET action = 'x'", "DELETE FROM audit_entry", "TRUNCATE audit_entry" })
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
                await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the array above
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            var change = () => command.ExecuteNonQueryAsync(Ct);
            (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("42501", "permission denied");
            await transaction.RollbackAsync(Ct);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'User.Updated'", target)).Should().Be(1);
    }

    /// <summary>
    /// ARV-094 (docs/security/data-classification.md section 6, item 5; L3 and L4 at rest and deletion): connected as a runtime
    /// login, as ariva_ensure_runtime_login creates one for the hosts, nothing deletes or truncates an account, a session, a
    /// refresh token or a recovery code, and nothing updates, deletes or truncates the Data Protection key ring (scripts 0002
    /// to 0005). Accounts are disabled and sessions revoked, never deleted.
    /// </summary>
    [Fact]
    public async Task RuntimeLogin_Should_NeverDeleteAccountsSessionsOrRecoveryCodesNorChangeTheKeyRing_When_ItTries()
    {
        // A row in every table: an account with a session, its refresh token, its recovery codes, and a key of the ring.
        var officer = await _host.CreateUserAsync("it.adm.kept", roles: [RoleCodes.TerminalDutyManager]);
        var session = SessionOf((await _host.LoginAsync("it.adm.kept", Password)).Data.Token.AccessToken);
        var secret = Base32.Decode((await _host.EnrolAsync(officer, session)).Data.Secret);
        (await _host.ConfirmAsync(officer, session, Totp.Code(secret, Totp.StepAt(_host.Clock.GetUtcNow())))).HasErrors.Should().BeFalse();
        var database = await _host.DatabaseAsync();
        const string login = "it_adm_runtime";
        var password = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using (var key = new NpgsqlCommand("INSERT INTO data_protection_key (friendly_name, xml) VALUES (@name, '<key id=\"it-adm\"/>')", admin))
            {
                key.Parameters.AddWithValue("name", "key-it-adm-" + Guid.NewGuid().ToString("N"));
                await key.ExecuteNonQueryAsync(Ct);
            }

            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", login);
            ensure.Parameters.AddWithValue("password", password);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        string[] tables = ["\"user\"", "user_session", "refresh_token", "recovery_code", "data_protection_key"];
        var before = await CountsAsync(database, tables);
        before.Should().OnlyContain(count => count.Value > 0, "every table holds a row the attempts could remove");
        (await _host.ReadAsync<bool>("SELECT pg_has_role('it_adm_runtime', 'ariva_runtime', 'MEMBER') AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = 'it_adm_runtime'"))
            .Should().BeTrue("the login is a runtime login, as the hosts use");

        string[] refused =
        [
            "DELETE FROM \"user\"", "TRUNCATE \"user\"",
            "DELETE FROM user_session", "TRUNCATE user_session",
            "DELETE FROM refresh_token", "TRUNCATE refresh_token",
            "DELETE FROM recovery_code", "TRUNCATE recovery_code",
            "UPDATE data_protection_key SET xml = xml", "DELETE FROM data_protection_key", "TRUNCATE data_protection_key"
        ];
        await using (var runtime = new NpgsqlConnection(fixture.ConnectionString(database, login, password)))
        {
            await runtime.OpenAsync(Ct);
            foreach (var sql in refused)
            {
                await using var transaction = await runtime.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // literal statements from the array above
                await using var command = new NpgsqlCommand(sql, runtime, transaction);
#pragma warning restore CA2100
                var change = () => command.ExecuteNonQueryAsync(Ct);
                (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("42501", $"permission denied: {sql}");
                await transaction.RollbackAsync(Ct);
            }
        }

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            foreach (var table in tables)
            {
                string[] privileges = table == "data_protection_key" ? ["DELETE", "TRUNCATE", "UPDATE"] : ["DELETE", "TRUNCATE"];
                foreach (var privilege in privileges)
                {
                    foreach (var role in new[] { "ariva_runtime", login })
                    {
                        await using var check = new NpgsqlCommand("SELECT has_table_privilege(@role::name, @table::text, @privilege::text)", admin);
                        check.Parameters.AddWithValue("role", role);
                        check.Parameters.AddWithValue("table", table);
                        check.Parameters.AddWithValue("privilege", privilege);
                        ((bool)(await check.ExecuteScalarAsync(Ct))!).Should().BeFalse($"{role} holds no {privilege} on {table}");
                    }
                }
            }
        }

        (await CountsAsync(database, tables)).Should().Equal(before, "no attempt removed or changed a row");
    }

    /// <summary>The row count of each table, read by the migration login.</summary>
    private async Task<Dictionary<string, long>> CountsAsync(string database, IEnumerable<string> tables)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        foreach (var table in tables)
        {
            var sql = table switch
            {
                "\"user\"" => "SELECT count(*) FROM \"user\"",
                "user_session" => "SELECT count(*) FROM user_session",
                "refresh_token" => "SELECT count(*) FROM refresh_token",
                "recovery_code" => "SELECT count(*) FROM recovery_code",
                "data_protection_key" => "SELECT count(*) FROM data_protection_key",
                _ => throw new ArgumentOutOfRangeException(nameof(tables), table, "not a table of this test")
            };
#pragma warning disable CA2100 // one of the literal statements above
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            counts[table] = (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        return counts;
    }
}
