using System.Data;
using System.Security.Claims;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Security;
using Ariva.Di.Extensions;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using User = Ariva.Core.Domain.Entities.User;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-010a against PostgreSQL with the shipped scripts and the Ariva.Core mapping: sign-in, the identical failure
/// for every reason, lockout (counted atomically, also under parallel attempts), the lock ending on its own, hash
/// upgrade, password change and the pending scope, and permissions resolved from stored grants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthenticatorTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Password = "violet tram ladder 9031";
    private const string WrongPassword = "not the password 0000";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static string _database;

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-keys", Guid.NewGuid().ToString("N"));
    private ServiceProvider _provider;

    #region Sign-in

    [Fact]
    public async Task Login_Should_IssueAFullToken_When_PasswordIsCorrect()
    {
        var userId = await CreateUserAsync("it.success", roles: [RoleCodes.SystemAdministrator]);

        var result = await LoginAsync("  IT.Success ", Password);

        result.HasErrors.Should().BeFalse();
        result.Data.Scope.Should().BeNull();
        result.Data.ExpiresIn.Should().Be(900);
        var token = new JsonWebToken(result.Data.AccessToken);
        token.Subject.Should().Be(userId.ToString());
        token.Alg.Should().Be("ES256");
        (await ReadAsync<DateTime?>("SELECT last_login_on FROM \"user\" WHERE user_name = 'it.success'")).Should().Be(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Login_Should_FailTheSameWay_When_PasswordIsWrongUserIsUnknownOrDisabled()
    {
        await CreateUserAsync("it.same");
        await CreateUserAsync("it.disabled", disabled: true);

        var wrong = await LoginAsync("it.same", WrongPassword);
        var unknown = await LoginAsync("it.nobody", WrongPassword);
        var malformed = await LoginAsync("x", WrongPassword);
        var disabled = await LoginAsync("it.disabled", Password);

        foreach (var result in new[] { wrong, unknown, malformed, disabled })
        {
            result.HasErrors.Should().BeTrue();
            result.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials);
        }

        (await ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE user_name = 'it.same'")).Should().Be(1);
    }

    [Fact]
    public async Task Login_Should_IssueThePendingScope_When_PasswordIsTemporary()
    {
        await CreateUserAsync("it.temporary", temporary: true);

        var result = await LoginAsync("it.temporary", Password);

        result.Data.Scope.Should().Be("pending");
        new JsonWebToken(result.Data.AccessToken).GetClaim("scope").Value.Should().Be("pending");
    }

    [Fact]
    public async Task Login_Should_UpgradeTheHash_When_StoredHashIsWeaker()
    {
        await CreateUserAsync("it.rehash", iterations: 1_000);

        (await LoginAsync("it.rehash", Password)).HasErrors.Should().BeFalse();

        (await ReadAsync<int>("SELECT password_iterations FROM \"user\" WHERE user_name = 'it.rehash'")).Should().Be(600_000);
        (await LoginAsync("it.rehash", Password)).HasErrors.Should().BeFalse("the upgraded hash verifies");
    }

    #endregion

    #region Lockout

    [Fact]
    public async Task Login_Should_LockAtTheTenthFailureAndUnlockOnItsOwn_When_FailuresAreConsecutive()
    {
        await CreateUserAsync("it.lockout");

        for (var attempt = 1; attempt < 10; attempt++)
            await LoginAsync("it.lockout", WrongPassword);
        (await LockedUntilAsync("it.lockout")).Should().BeNull("nine failures do not lock");

        await LoginAsync("it.lockout", WrongPassword);
        (await LockedUntilAsync("it.lockout")).Should().Be(_clock.GetUtcNow().UtcDateTime.AddMinutes(15));

        (await LoginAsync("it.lockout", Password)).HasErrors.Should().BeTrue("a locked account refuses the correct password");
        _clock.Advance(TimeSpan.FromMinutes(15));
        (await LoginAsync("it.lockout", Password)).HasErrors.Should().BeFalse("the lock ends on its own");
        (await ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE user_name = 'it.lockout'")).Should().Be(0);
    }

    [Fact]
    public async Task Login_Should_LockAfterExactlyTenFailures_When_AttemptsArriveInParallel()
    {
        await CreateUserAsync("it.parallel");

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => LoginAsync("it.parallel", WrongPassword)));

        (await LockedUntilAsync("it.parallel")).Should().NotBeNull("parallel attempts cannot lose counts");
        (await ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE user_name = 'it.parallel'")).Should().Be(0,
            "the tenth failure locked the account and reset the count; failures while locked are not counted");
    }

    [Fact]
    public async Task Unlock_Should_ClearTheLock_When_AdministratorUnlocks()
    {
        var userId = await CreateUserAsync("it.unlock");
        for (var attempt = 0; attempt < 10; attempt++)
            await LoginAsync("it.unlock", WrongPassword);

        Fluentx.Result<bool> result;
        await using (var scope = Provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(Guid.CreateVersion7(), "it.admin");
            result = await scope.ServiceProvider.GetRequiredService<ISvcAuthenticator>().UnlockAsync(userId, TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(TestContext.Current.CancellationToken);
        }

        result.HasErrors.Should().BeFalse();
        (await LoginAsync("it.unlock", Password)).HasErrors.Should().BeFalse();
    }

    #endregion

    #region Password change

    [Fact]
    public async Task ChangePassword_Should_RefuseWeakReusedOrUnverifiedPasswords_When_Asked()
    {
        var userId = await CreateUserAsync("it.change", temporary: true);

        (await ChangeAsync(userId, Password, "password1234")).ErrorMessages.Should().Contain(e => e.Contains("breached"));
        (await ChangeAsync(userId, Password, "my it.change key 2026")).ErrorMessages.Should().Contain(e => e.Contains("username"));
        (await ChangeAsync(userId, Password, Password)).ErrorMessages.Should().Contain(e => e.Contains("different"));
        (await ChangeAsync(userId, WrongPassword, "amber kiosk river 5520")).ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials);

        var changed = await ChangeAsync(userId, Password, "amber kiosk river 5520");

        changed.HasErrors.Should().BeFalse();
        changed.Data.Scope.Should().BeNull("the temporary password is gone, so the account leaves the pending scope");
        (await LoginAsync("it.change", Password)).HasErrors.Should().BeTrue();
        (await LoginAsync("it.change", "amber kiosk river 5520")).Data.Scope.Should().BeNull();
    }

    #endregion

    #region Permissions

    [Fact]
    public async Task GetPermissions_Should_ComeFromStoredGrants_When_TokenHasASubject()
    {
        var userId = await CreateUserAsync("it.grants", roles: [RoleCodes.HandlerStationManager]);
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", userId.ToString()), new Claim(ClaimTypes.Role, RoleCodes.SystemAdministrator)], "Bearer"));

        await using var scope = Provider.CreateAsyncScope();
        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionResolver>().GetPermissionsAsync(claims, TestContext.Current.CancellationToken);

        permissions.Should().BeEquivalentTo(RolePermissions.HandlerStationManager, "a role claim in a token is ignored; the stored grant decides");
    }

    [Fact]
    public async Task GetPermissions_Should_BeEmpty_When_TokenIsPendingOrAccountIsDisabled()
    {
        var disabledId = await CreateUserAsync("it.grants.off", roles: [RoleCodes.SystemAdministrator], disabled: true);
        var activeId = await CreateUserAsync("it.grants.pending", roles: [RoleCodes.SystemAdministrator]);
        var disabled = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", disabledId.ToString())], "Bearer"));
        var pending = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", activeId.ToString()), new Claim("scope", "pending")], "Bearer"));

        await using var scope = Provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        (await resolver.GetPermissionsAsync(disabled, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await resolver.GetPermissionsAsync(pending, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Call CreateUserAsync first.");

    private async Task<string> DatabaseAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (_database is null)
            {
                var name = await fixture.CreateDatabaseAsync(TestDatabase.Accounts);
                await new SqlScriptRunner(fixture.ConnectionString(name), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
                _database = name;
            }

            return _database;
        }
        finally
        {
            DatabaseGate.Release();
        }
    }

    private async Task EnsureProviderAsync()
    {
        if (_provider is not null)
            return;

        var database = await DatabaseAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            ["Application:SiteCode"] = "AMM",
            ["Database:Host"] = fixture.Hostname,
            ["Database:Port"] = fixture.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = fixture.AdminUsername,
            ["Database:Password"] = fixture.AdminPassword,
            ["Redis:Enabled"] = "false",
            ["Auth:Tokens:UseDevelopmentKeys"] = "true",
            ["Auth:Tokens:DevelopmentKeyDirectory"] = _keyDirectory
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddArivaPersistence(configuration);
        services.AddArivaCaching(configuration);
        services.AddArivaTokenIssuing(configuration);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task<Guid> CreateUserAsync(string userName, bool temporary = false, bool disabled = false, int iterations = 600_000, string[] roles = null)
    {
        await EnsureProviderAsync();
        await using var scope = Provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(Guid.Empty, "it-seed");
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var storage = unitOfWork.StorageProvider;
        storage.BeginTransaction(IsolationLevel.ReadCommitted);

        var user = new User(userName, userName, null);
        user.SetPassword(iterations == PasswordHasher.Iterations ? PasswordHasher.Hash(Password) : WeakHash(iterations), temporary);
        if (disabled)
            user.Disable();
        await storage.SaveAsync(user, TestContext.Current.CancellationToken);
        foreach (var role in roles ?? [])
        {
            user.Grant(role);
            await storage.SaveAsync(user.Roles.Last(), TestContext.Current.CancellationToken);
        }

        unitOfWork.PromiseToCommit();
        await unitOfWork.EndAsync(TestContext.Current.CancellationToken);
        return user.Id.Value;
    }

    private static PasswordHashValue WeakHash(int iterations)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(Password), salt, iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return new PasswordHashValue(PasswordHasher.Algorithm, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private async Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.TokenViewModel>> LoginAsync(string userName, string password)
    {
        await using var scope = Provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISvcAuthenticator>().LoginAsync(new LoginRequest(userName, password), TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private async Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.TokenViewModel>> ChangeAsync(Guid userId, string current, string next)
    {
        await using var scope = Provider.CreateAsyncScope();
        // Outside a request the current user is the identity set here; in Ariva.Api.Main it is the token's subject.
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(userId, "it.change");
        var result = await scope.ServiceProvider.GetRequiredService<ISvcAuthenticator>().ChangePasswordAsync(new ChangePasswordRequest(current, next), TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private Task<DateTime?> LockedUntilAsync(string userName) =>
        userName switch
        {
            "it.lockout" => ReadAsync<DateTime?>("SELECT locked_until FROM \"user\" WHERE user_name = 'it.lockout'"),
            "it.parallel" => ReadAsync<DateTime?>("SELECT locked_until FROM \"user\" WHERE user_name = 'it.parallel'"),
            _ => throw new ArgumentOutOfRangeException(nameof(userName))
        };

    private async Task<T> ReadAsync<T>([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await DatabaseAsync()));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DBNull or null ? default : (T)value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (Directory.Exists(_keyDirectory))
            Directory.Delete(_keyDirectory, recursive: true);
    }

    #endregion
}

/// <summary>A <see cref="TimeProvider"/> that moves only when a test advances it.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
