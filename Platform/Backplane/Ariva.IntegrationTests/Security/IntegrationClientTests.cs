using System.Net;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Integration;
using Ariva.Di.Extensions;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-042 against TimescaleDB with script 0025: administrators register integration clients only within their sites and
/// see the secret and seed once (stored as a PBKDF2 hash and a protected seed); the token exchange accepts the right
/// secret and a fresh TOTP code once, refuses everything else with the same invalid_client, limits attempts per client,
/// locks a client after ten failures (audited) and refuses disabled clients and foreign addresses; a call is accepted
/// only with a token issued after the client's last change and, when its policy says so, a fresh code; calls are
/// recorded and the runtime role cannot rewrite the record or delete a client.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IntegrationClientTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (Guid All, Guid Dmo)? _admins;
    private static readonly IPAddress Inside = IPAddress.Parse("10.20.0.7");

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.IntegrationClients, configure: services =>
    {
        services.AddArivaIntegrationClients();
        services.AddArivaIntegrationAuth();
        services.AddSingleton(provider =>
        {
            var settings = IntegrationTokenSettings.From(new ConfigurationBuilder().Build(), provider.GetRequiredService<AuthSettings>().Tokens);
            var ring = new TokenSettings
            {
                Issuer = settings.Issuer, Audience = settings.Audience, LifetimeMinutes = settings.LifetimeMinutes, ClockSkewSeconds = settings.ClockSkewSeconds,
                UseDevelopmentKeys = true, DevelopmentKeyDirectory = provider.GetRequiredService<AuthSettings>().Tokens.DevelopmentKeyDirectory, DevelopmentKeyFile = settings.DevelopmentKeyFile
            };
            return new IntegrationTokenKeys(TokenKeys.Load(ring, requireSigningKey: true), ring);
        });
        services.AddSingleton<IntegrationTokenIssuer>();
    });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task<(Guid All, Guid Dmo)> AdminsAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_admins is { } known)
            {
                await _host.CreateUserAsync("it.ic.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var all = await _host.CreateUserAsync("it.ic.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", all);
            foreach (var (code, name) in new[] { ("DMO", "Demo airport"), ("ALX", "Other airport") })
                await _host.AsCallerAsync(all, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(code, name), Ct));
            var dmo = await _host.CreateUserAsync("it.ic.dmoadmin", roles: [RoleCodes.SystemAdministrator]);
            await _host.AsCallerAsync(all, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(dmo, new SiteAccessRequest(false, ["DMO"]), Ct));
            _admins = (all, dmo);
            return _admins.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Task<Fluentx.Result<IntegrationClientCredentialsViewModel>> CreateAsync(Guid admin, string[] sites, string[] networks = null, bool? totpPerRequest = null,
        string kind = "Aodb") =>
        _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcIntegrationClients>().CreateAsync(
            new CreateIntegrationClientRequest("AODB " + Guid.NewGuid().ToString("N")[..6], kind, ["flights:write", "allocations:write"], sites, networks ?? ["10.20.0.0/24"],
                totpPerRequest), Ct));

    private string Code(string seed, int offsetSteps = 0) => Totp.Code(Base32.Decode(seed), Totp.StepAt(_host.Clock.GetUtcNow()) + offsetSteps);

    private Task<Fluentx.Result<IntegrationTokenViewModel>> ExchangeAsync(string clientId, string secret, string code, IPAddress remote = null) =>
        _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationAuth>().ExchangeAsync(new IntegrationTokenRequest(clientId, secret, code), remote ?? Inside, Ct));

    private async Task<IntegrationCallerViewModel> CheckAsync(string clientId, int version, string code = null, IPAddress remote = null) =>
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationAuth>().CheckCallAsync(clientId, version, remote ?? Inside, code, Ct))).Caller;

    private Task<int> VersionAsync(string clientId) => _host.ReadAsync<int>("SELECT token_version FROM integration_client WHERE client_id = @secret", secret: clientId);

    [Fact]
    public async Task Client_Should_ShowItsCredentialsOnceAndStoreThemProtected_When_Registered()
    {
        var p = await AdminsAsync();
        var created = await CreateAsync(p.All, ["DMO"]);

        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        var view = created.Data;
        view.Client.Should().Match<IntegrationClientViewModel>(c => c.Kind == "Aodb" && c.Status == "Active" && !c.RequireTotpPerRequest && c.SiteCodes.Single() == "DMO");
        view.ClientSecret.Should().StartWith("ics_");
        view.TotpUri.Should().StartWith("otpauth://totp/").And.Contain(view.Client.ClientId).And.Contain("secret=" + view.TotpSecret);
        (await _host.ReadAsync<int>("SELECT secret_iterations FROM integration_client WHERE id = @id", view.Client.Id)).Should().Be(600_000);
        var stored = await _host.ReadAsync<string>("SELECT totp_secret_protected || '|' || secret_hash FROM integration_client WHERE id = @id", view.Client.Id);
        stored.Should().NotContain(view.TotpSecret).And.NotContain(view.ClientSecret);
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'IntegrationClient.Created'", view.Client.Id))
            .Should().Contain("flights:write").And.NotContain(view.ClientSecret).And.NotContain(view.TotpSecret);

        var get = await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().GetAsync(view.Client.Id, Ct));
        get.Data.Should().BeEquivalentTo(view.Client with { Locked = false });
        (await CreateAsync(p.All, ["DMO"], kind: "Immigration")).Data.Client.RequireTotpPerRequest.Should().BeTrue("immigration clients need a code on every call by default");
    }

    [Fact]
    public async Task Administrator_Should_BindAndSeeOnlyClientsWithinTheirSites_When_Limited()
    {
        var p = await AdminsAsync();
        (await CreateAsync(p.Dmo, ["DMO", "ALX"])).ErrorMessages.Should().Equal(IntegrationErrors.BeyondOwnSites);
        (await CreateAsync(p.Dmo, ["ZZ9"])).ErrorMessages.Should().Equal([IntegrationErrors.BeyondOwnSites], "an unknown site outside your own answers the same");
        (await CreateAsync(p.All, ["ZZ9"])).ErrorMessages.Should().Equal(IntegrationErrors.UnknownSite);

        var both = (await CreateAsync(p.All, ["DMO", "ALX"])).Data.Client;
        var mine = (await CreateAsync(p.Dmo, ["DMO"])).Data.Client;
        var listed = (await _host.AsCallerAsync(p.Dmo, s => s.GetRequiredService<ISvcIntegrationClients>().ListAsync(null, Ct))).Data.Select(c => c.Id).ToList();
        listed.Should().Contain(mine.Id).And.NotContain(both.Id);
        foreach (var action in new Func<ISvcIntegrationClients, Task<bool>>[]
                 {
                     async c => (await c.GetAsync(both.Id, Ct)).ErrorMessages.SequenceEqual([IntegrationErrors.NotFound]),
                     async c => (await c.RotateSecretAsync(both.Id, Ct)).ErrorMessages.SequenceEqual([IntegrationErrors.NotFound]),
                     async c => (await c.DisableAsync(both.Id, Ct)).ErrorMessages.SequenceEqual([IntegrationErrors.NotFound]),
                     async c => (await c.UpdateAsync(both.Id, new UpdateIntegrationClientRequest("x", ["flights:write"], ["DMO"], [], false), Ct)).ErrorMessages
                         .SequenceEqual([IntegrationErrors.NotFound])
                 })
        {
            (await _host.AsCallerAsync(p.Dmo, s => action(s.GetRequiredService<ISvcIntegrationClients>()))).Should().BeTrue("a client beyond your sites does not exist for you");
        }
    }

    [Fact]
    public async Task Exchange_Should_IssueATokenOnceForACodeAndRefuseEverythingElseAlike_When_Called()
    {
        var p = await AdminsAsync();
        var c = (await CreateAsync(p.All, ["DMO"])).Data;
        var id = c.Client.ClientId;

        var issued = await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret));
        issued.HasErrors.Should().BeFalse();
        issued.Data.ExpiresAt.Should().Be(Now.AddMinutes(15));
        (await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret))).ErrorMessages.Should().Equal([IntegrationErrors.InvalidClient], "the same code twice (replay)");
        (await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret, -1))).ErrorMessages.Should().Equal([IntegrationErrors.InvalidClient], "an older step than the one accepted");

        // A new minute for the attempt limit; the three failures below that name this client count in it.
        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        foreach (var (why, clientId, secret, code, remote) in new[]
                 {
                     ("wrong secret", id, c.ClientSecret[..^1] + (c.ClientSecret[^1] == 'A' ? "B" : "A"), Code(c.TotpSecret), Inside),
                     ("wrong code", id, c.ClientSecret, Code(c.TotpSecret) == "000000" ? "000001" : "000000", Inside),
                     ("unknown client", "ic_" + new string('a', 26), c.ClientSecret, Code(c.TotpSecret), Inside),
                     ("malformed client", "' OR 1=1 --", c.ClientSecret, Code(c.TotpSecret), Inside),
                     ("outside the networks", id, c.ClientSecret, Code(c.TotpSecret), IPAddress.Parse("192.0.2.5"))
                 })
        {
            (await ExchangeAsync(clientId, secret, code, remote)).ErrorMessages.Should().Equal([IntegrationErrors.InvalidClient], why);
        }

        (await _host.ReadAsync<int>("SELECT failed_attempts FROM integration_client WHERE client_id = @secret", secret: id)).Should().Be(4,
            "the replay, the older step, a wrong secret and a wrong code count; an unknown or malformed id and an outside address count nothing");
        (await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret), IPAddress.Parse("::ffff:10.20.0.9"))).HasErrors.Should().BeFalse("an IPv4-mapped address inside the network");
        (await _host.ReadAsync<int>("SELECT failed_attempts FROM integration_client WHERE client_id = @secret", secret: id)).Should().Be(0, "a success clears the failures");
    }

    [Fact]
    public async Task Client_Should_BeLockedAfterTenFailuresAndUnlocked_When_TheAdministratorUnlocksIt()
    {
        var p = await AdminsAsync();
        var c = (await CreateAsync(p.All, ["DMO"])).Data;
        var id = c.Client.ClientId;
        for (var i = 0; i < 10; i++)
        {
            if (i % 4 == 0)
                _host.Clock.Advance(TimeSpan.FromMinutes(1)); // under the 5 attempts a minute per client
            (await ExchangeAsync(id, "ics_" + new string('x', 43), Code(c.TotpSecret))).HasErrors.Should().BeTrue();
        }

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret))).ErrorMessages.Should().Equal([IntegrationErrors.InvalidClient], "locked, even with the right credentials");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'IntegrationClient.LockedOut'", c.Client.Id)).Should().Be(1);
        var view = await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().GetAsync(c.Client.Id, Ct));
        view.Data.Should().Match<IntegrationClientViewModel>(v => v.Locked && v.FailedAttempts == 0, "the count starts again at a lock");

        // The lock expires: one more failure does not lock again, and the next ten lock it again with a second alarm.
        _host.Clock.Advance(TimeSpan.FromMinutes(16));
        (await ExchangeAsync(id, "ics_" + new string('x', 43), Code(c.TotpSecret))).HasErrors.Should().BeTrue();
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().GetAsync(c.Client.Id, Ct))).Data.Locked.Should().BeFalse();
        for (var i = 0; i < 9; i++)
        {
            if (i % 4 == 0)
                _host.Clock.Advance(TimeSpan.FromMinutes(1));
            await ExchangeAsync(id, "ics_" + new string('x', 43), Code(c.TotpSecret));
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'IntegrationClient.LockedOut'", c.Client.Id)).Should().Be(2);

        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().UnlockAsync(c.Client.Id, Ct))).Data.Locked.Should().BeFalse();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret))).HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Exchange_Should_RefuseTheSixthAttemptOfAMinuteWithoutCountingItAsAFailure_When_AClientIsHammered()
    {
        var p = await AdminsAsync();
        var c = (await CreateAsync(p.All, ["DMO"])).Data;
        for (var i = 0; i < 5; i++)
            await ExchangeAsync(c.Client.ClientId, "ics_" + new string('y', 43), "000000");
        (await ExchangeAsync(c.Client.ClientId, c.ClientSecret, Code(c.TotpSecret))).HasErrors.Should().BeTrue("the sixth attempt of the minute, even a right one");
        (await _host.ReadAsync<int>("SELECT failed_attempts FROM integration_client WHERE client_id = @secret", secret: c.Client.ClientId)).Should().Be(5);

        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        (await ExchangeAsync(c.Client.ClientId, c.ClientSecret, Code(c.TotpSecret))).HasErrors.Should().BeFalse("a new minute");
    }

    [Fact]
    public async Task Call_Should_NeedATokenOfTheClientsCurrentVersionAndItsCode_When_Checked()
    {
        var p = await AdminsAsync();
        var c = (await CreateAsync(p.All, ["DMO"])).Data;
        var id = c.Client.ClientId;
        var version = await VersionAsync(id);
        (await CheckAsync(id, version)).Should().Match<IntegrationCallerViewModel>(v => v.ClientId == id && v.SiteCodes.Single() == "DMO");
        (await CheckAsync(id, version, remote: IPAddress.Parse("192.0.2.5"))).Should().BeNull("outside the networks");

        // In the same second, too: the version, not the time, decides.
        await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().RotateSecretAsync(c.Client.Id, Ct));
        (await CheckAsync(id, version)).Should().BeNull("a token from before the rotation");
        version = await VersionAsync(id);
        (await CheckAsync(id, version)).Should().NotBeNull();

        var update = new UpdateIntegrationClientRequest(c.Client.Name, ["flights:write"], ["DMO"], ["10.20.0.0/24"], true);
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().UpdateAsync(c.Client.Id, update, Ct))).Data.RequireTotpPerRequest.Should().BeTrue();
        (await CheckAsync(id, version, Code(c.TotpSecret))).Should().BeNull("a token from before the change");
        version = await VersionAsync(id);
        (await CheckAsync(id, version)).Should().BeNull("a code on every call now");
        (await CheckAsync(id, version, Code(c.TotpSecret))).Should().Match<IntegrationCallerViewModel>(v => v.Scopes.SequenceEqual(new[] { "flights:write" }),
            "the scopes the record has now, for the scope check");
        (await CheckAsync(id, version, "123")).Should().BeNull();

        await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcIntegrationClients>().DisableAsync(c.Client.Id, Ct));
        (await CheckAsync(id, await VersionAsync(id), Code(c.TotpSecret))).Should().BeNull("disabled");
        (await ExchangeAsync(id, c.ClientSecret, Code(c.TotpSecret))).HasErrors.Should().BeTrue("disabled");
        (await CheckAsync("ic_" + new string('b', 26), 0)).Should().BeNull();
    }

    [Fact]
    public async Task Changes_Should_EachMoveTheTokenVersion_When_AdministratorsChangeAClientAtOnce()
    {
        var p = await AdminsAsync();
        var c = (await CreateAsync(p.All, ["DMO"])).Data;
        var before = await VersionAsync(c.Client.ClientId);

        // Eight changes at once, each in its own unit of work: the row lock makes them run in turn, so none writes a
        // version another already wrote (a token issued between two of them could otherwise survive the second).
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _host.AsCallerAsync(p.All, s => i % 2 == 0
            ? s.GetRequiredService<ISvcIntegrationClients>().RotateSecretAsync(c.Client.Id, Ct)
            : s.GetRequiredService<ISvcIntegrationClients>().ResetTotpAsync(c.Client.Id, Ct))));

        (await VersionAsync(c.Client.ClientId)).Should().Be(before + 8);
    }

    [Fact]
    public async Task OutsideCallers_Should_CountNothing_When_TheyKnowOnlyTheClientId()
    {
        var p = await AdminsAsync();
        var c = (await CreateAsync(p.All, ["DMO"])).Data;
        for (var i = 0; i < 25; i++)
            (await ExchangeAsync(c.Client.ClientId, "ics_" + new string('z', 43), "000000", IPAddress.Parse("192.0.2.77"))).HasErrors.Should().BeTrue();

        (await _host.ReadAsync<int>("SELECT failed_attempts + attempts_in_window FROM integration_client WHERE client_id = @secret", secret: c.Client.ClientId)).Should().Be(0);
        (await ExchangeAsync(c.Client.ClientId, c.ClientSecret, Code(c.TotpSecret))).HasErrors.Should().BeFalse("neither worn down nor locked from outside");
        (await CreateAsync(p.All, ["DMO"], networks: [])).ErrorMessages.Should().Contain(e => e.Contains("allowed source networks", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DELETE FROM integration_client", "42501")]
    [InlineData("TRUNCATE integration_client", "42501")]
    [InlineData("DELETE FROM integration_call", "42501")]
    [InlineData("UPDATE integration_call SET status = 200", "42501")]
    [InlineData("UPDATE integration_client SET secret_iterations = 1", "23514")]
    [InlineData("UPDATE integration_client SET scope_names = 'admin:all'", "23514")]
    [InlineData("UPDATE integration_client SET site_codes = 'dmo'", "23514")]
    [InlineData("UPDATE integration_client SET client_id = 'x'", "23514")]
    public async Task Database_Should_RefuseRewritingTheRecord_When_TheRuntimeRoleTries(string sql, string state)
    {
        var p = await AdminsAsync();
        if (await _host.ReadAsync<long>("SELECT count(*) FROM integration_call") == 0)
        {
            var c = (await CreateAsync(p.All, ["DMO"])).Data;
            await _host.AsCallerAsync(null, async s =>
            {
                await s.GetRequiredService<ISvcIntegrationAuth>().RecordCallAsync(new IntegrationCallRecord(c.Client.ClientId, Guid.NewGuid(), "flights:write", "GET",
                    "api/v1/integration/sites/{siteCode}/flights/check", "DMO", 200, null, 0, Inside, Now), Ct);
                return true;
            });
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(state);
        await transaction.RollbackAsync(Ct);
    }
}
