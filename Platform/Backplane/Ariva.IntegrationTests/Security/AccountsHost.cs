using System.Data;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Security;
using Ariva.Di.Extensions;
using Ariva.Infra.Security;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using User = Ariva.Core.Domain.Entities.User;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// The production account registration (persistence, cache, token issuing) against one database with the shipped
/// scripts, shared by the account and session tests, with a manual clock and a current user the test sets (the hosts
/// read it from the access token). Each test uses its own usernames.
/// </summary>
public sealed class AccountsHost : IAsyncDisposable
{
    public const string Password = "violet tram ladder 9031";
    public const string WrongPassword = "not the password 0000";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static readonly Dictionary<TestDatabase, string> Databases = [];

    private readonly PostgresFixture _fixture;
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-keys", Guid.NewGuid().ToString("N"));
    private ServiceProvider _provider;

    private readonly TestDatabase _databaseKind;

    public AccountsHost(PostgresFixture fixture, Dictionary<string, string> settings = null, TestDatabase database = TestDatabase.Accounts)
    {
        _fixture = fixture;
        Settings = settings ?? [];
        _databaseKind = database;
    }

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

    public Dictionary<string, string> Settings { get; }

    public ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Call CreateUserAsync first.");

    #region Setup

    public async Task<string> DatabaseAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!Databases.TryGetValue(_databaseKind, out var database))
            {
                database = await _fixture.CreateDatabaseAsync(_databaseKind);
                await new SqlScriptRunner(_fixture.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
                Databases[_databaseKind] = database;
            }

            return database;
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
        var values = new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            ["Application:SiteCode"] = "AMM",
            ["Database:Host"] = _fixture.Hostname,
            ["Database:Port"] = _fixture.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = _fixture.AdminUsername,
            ["Database:Password"] = _fixture.AdminPassword,
            ["Redis:Enabled"] = "false",
            ["Auth:Tokens:UseDevelopmentKeys"] = "true",
            ["Auth:Tokens:DevelopmentKeyDirectory"] = _keyDirectory,
            // Account and session tests use accounts without TOTP; TotpTests turns the requirement on.
            ["Auth:TotpRequired"] = "false"
        };
        foreach (var (key, value) in Settings)
            values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<ICurrentUser, TestCurrentUser>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddArivaPersistence(configuration);
        services.AddArivaCaching(configuration);
        services.AddArivaTokenIssuing(configuration);
        services.AddScoped<BreakGlassAccounts>();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public async Task<Guid> CreateUserAsync(string userName, bool temporary = false, bool disabled = false, int iterations = 600_000, string[] roles = null)
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

    #endregion

    #region Service calls (one unit of work each, as one request)

    /// <summary>Runs <paramref name="work"/> in a fresh scope as the given caller and ends the unit of work.</summary>
    public async Task<T> AsAsync<T>(Guid? userId, Guid? sessionId, Func<ISvcAuthenticator, Task<T>> work)
    {
        await using var scope = Provider.CreateAsyncScope();
        var caller = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        caller.Id = userId;
        caller.SessionId = sessionId;
        var result = await work(scope.ServiceProvider.GetRequiredService<ISvcAuthenticator>());
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(TestContext.Current.CancellationToken);
        return result;
    }

    /// <summary>Runs <paramref name="work"/> in a fresh scope as the given caller (any service) and ends the unit of work.</summary>
    public async Task<T> AsCallerAsync<T>(Guid? userId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Provider.CreateAsyncScope();
        var caller = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        caller.Id = userId;
        caller.UserName = "it-admin";
        var result = await work(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(TestContext.Current.CancellationToken);
        return result;
    }

    public Task<Fluentx.Result<SignInResult>> LoginAsync(string userName, string password, string presentedRefreshToken = null, string code = null, string recoveryCode = null) =>
        AsAsync(null, null, service => service.LoginAsync(new LoginRequest(userName, password, code, recoveryCode), Context(presentedRefreshToken), TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<TotpEnrolmentViewModel>> EnrolAsync(Guid userId, Guid sessionId) =>
        AsAsync(userId, sessionId, service => service.EnrolTotpAsync(TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<TotpConfirmedViewModel>> ConfirmAsync(Guid userId, Guid sessionId, string code) =>
        AsAsync(userId, sessionId, service => service.ConfirmTotpAsync(new TotpCodeRequest(code), TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<RecoveryCodesViewModel>> RegenerateAsync(Guid userId, Guid sessionId, string code) =>
        AsAsync(userId, sessionId, service => service.RegenerateRecoveryCodesAsync(new TotpCodeRequest(code), TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<TokenViewModel>> StepUpAsync(Guid userId, Guid sessionId, string code = null, string recoveryCode = null) =>
        AsAsync(userId, sessionId, service => service.StepUpAsync(new StepUpRequest(code, recoveryCode), TestContext.Current.CancellationToken));

    public async Task<BreakGlassCredential> IssueBreakGlassAsync(bool rotate)
    {
        await EnsureProviderAsync();
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BreakGlassAccounts>().IssueAsync(rotate, TestContext.Current.CancellationToken);
    }

    public Task<Fluentx.Result<SignInResult>> RefreshAsync(string refreshToken) =>
        AsAsync(null, null, service => service.RefreshAsync(Context(refreshToken), TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<bool>> LogoutAsync(Guid userId, Guid sessionId) =>
        AsAsync(userId, sessionId, service => service.LogoutAsync(Context(null), TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<TokenViewModel>> ChangeAsync(Guid userId, Guid sessionId, string current, string next) =>
        AsAsync(userId, sessionId, service => service.ChangePasswordAsync(new ChangePasswordRequest(current, next), TestContext.Current.CancellationToken));

    public Task<Fluentx.Result<bool>> DisableAsync(Guid administratorId, Guid userId) =>
        AsAsync(administratorId, null, service => service.DisableAsync(userId, TestContext.Current.CancellationToken));

    public async Task<SessionState> SessionStateAsync(Guid sessionId)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionValidator>().CheckAsync(sessionId, TestContext.Current.CancellationToken);
    }

    private static SignInContext Context(string refreshToken) => new("10.1.2.3", "Ariva integration tests", refreshToken);

    #endregion

    #region Database reads

    public async Task<T> ReadAsync<T>([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, Guid? id = null)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString(await DatabaseAsync()));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        if (id is { } value)
            command.Parameters.AddWithValue("id", value);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is DBNull or null ? default : (T)result;
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (Directory.Exists(_keyDirectory))
            Directory.Delete(_keyDirectory, recursive: true);
    }
}

/// <summary>A <see cref="TimeProvider"/> that moves only when a test advances it.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The caller as Ariva.Api.Main would read it from the access token (sub, sid).</summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? Id { get; set; }
    public string UserName { get; set; } = "it-caller";
    public Guid? SessionId { get; set; }
    public IReadOnlyCollection<string> Roles => [];
    public DateTime? AuthenticatedAt => null;
    public IReadOnlyCollection<string> AuthenticationMethods => [];
    public bool IsAuthenticated => Id is not null;
    public string GetCallerIpAddress() => "10.1.2.3";

    public void SetSystemUser(Guid id, string userName)
    {
        Id = id;
        UserName = userName;
    }
}
