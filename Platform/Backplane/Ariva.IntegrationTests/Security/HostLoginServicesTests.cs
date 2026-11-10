using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Integration;
using Ariva.Core.Services.Security;
using Ariva.Di;
using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Integration;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Inbox;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static Ariva.IntegrationTests.Security.AccountsHost;
using static Ariva.IntegrationTests.Security.AuthenticatorTests;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-083a review (CWE-863, CWE-269): the authentication paths and the shared plumbing run as the production services, each
/// under the narrowed login of the host that runs it, on a database a cluster's migration job migrated (k8s-dev, every
/// Database:HostLogins and Database:Availability). A grant an authentication path needs and its host role lacks would fail
/// here, not in production: the other suites run their services as the fixture's superuser, and the E2E suites on vm-local's
/// development login, which holds every role.
/// <list type="bullet">
/// <item>Ariva.Api.Main's login: sign-in with the password and a TOTP code (the replay guard's update), a recovery code once,
/// refresh rotation with the grace path (the row lock) and the family revoked on reuse, logout, and the session ends of a
/// password change and of disabling an account (RevokeAllAsync).</item>
/// <item>Every API host's login: the per-request session check, the stored permissions and the site access, which every host
/// runs (UseSessionValidation), reading an account through the base's columns only (no credential).</item>
/// <item>Ariva.Api.Integration's login: the token exchange, its failure count and lockout (audited), the per-call check and
/// the call record.</item>
/// <item>Every API host's login: the outbox write, the relay's claim and mark, its housekeeping (sent rows and the inbox), and
/// the inbox claim on the hosts that consume (Main, Stream, Integration; Ingest and Cronz are refused it).</item>
/// <item>Every API host's login: the Data Protection key ring's store and read as the hosts do them since ARV-211, through the
/// trusted filter (TrustedKeyRingRepository) with the row signature of script 0073, and a key read back and used.</item>
/// <item>The key ring upgrade check at start (KeyRingUpgradeGate) under Ariva.Api.Main's and Ariva.Api.Cronz's logins, which read
/// the three columns that hold Data Protection payloads, and refused under Ingest's, Stream's and Integration's, which do not
/// register it (the review's R2-2).</item>
/// <item>Ariva.Api.Cronz's login: the key lifecycle round (ARV-207), with script 0072's purge and the dates of the credentials in
/// use; every other host's login is refused the purge.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HostLoginServicesTests(PostgresFixture fixture)
{
    #region Fields

    private const string Prefix = "it_hls_";

    private const string NewPassword = "amber kiosk river 5520";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    private static string _database;

    private static Dictionary<string, string> _logins;

    private static (Guid UserId, Guid Active, Guid Revoked)? _signedIn;

    /// <summary>One key ring for every host here, as the hosts share theirs: Main protects a client's TOTP seed, Integration reads it.</summary>
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static readonly IPAddress Inside = IPAddress.Parse("10.20.0.7");

    /// <summary>The key ring's wrapping certificate and signer for every host here, as the hosts share theirs (ARV-211).</summary>
    private static readonly X509Certificate2 WrappingCertificate = Wrapping();

    private static readonly KeyRingSigning Signing = KeyRingSigning.From(Signer());

    #endregion

    #region Properties

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Ariva.Api.Main's sign-in

    [Fact]
    public async Task MainLogin_Should_SignInWithThePasswordAndATotpCodeOrARecoveryCodeOnce_When_TheAccountIsEnrolled()
    {
        await using var main = await HostAsync("Main", new Dictionary<string, string> { ["Auth:TotpRequired"] = "true" });
        const string userName = "it.hls.totp";
        var userId = await main.CreateUserAsync(userName);
        var pending = (await main.LoginAsync(userName, Password)).Data;
        pending.Token.Scope.Should().Be("pending", "no TOTP yet");
        var sessionId = SessionOf(pending.Token.AccessToken);
        var secret = Base32.Decode((await main.EnrolAsync(userId, sessionId)).Data.Secret);
        var confirmed = await main.ConfirmAsync(userId, sessionId, Code(main, secret));
        confirmed.HasErrors.Should().BeFalse(string.Join(", ", confirmed.ErrorMessages ?? []));
        main.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        var code = Code(main, secret);
        var accepted = await main.LoginAsync(userName, Password, code: code);
        accepted.HasErrors.Should().BeFalse(string.Join(", ", accepted.ErrorMessages ?? []));
        accepted.Data.Token.Scope.Should().BeNull("a confirmed account signs in with the full scope");
        (await main.LoginAsync(userName, Password, code: code)).ErrorMessages.Should().Equal(new[] { ISvcAuthenticator.InvalidCredentials },
            "CWE-294: the replay guard's update holds under Main's login");

        (await main.LoginAsync(userName, WrongPassword, code: Code(main, secret, 1))).ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials);
        (await main.ReadAsync<int>("SELECT failed_login_count FROM \"user\" WHERE id = @id", userId)).Should().Be(2, "the replayed code and the wrong password are each counted on the account");

        var recovered = await main.LoginAsync(userName, Password, recoveryCode: confirmed.Data.RecoveryCodes[0]);
        recovered.HasErrors.Should().BeFalse(string.Join(", ", recovered.ErrorMessages ?? []));
        recovered.Data.Token.RecoveryCodesRemaining.Should().Be(9);
        (await main.LoginAsync(userName, Password, recoveryCode: confirmed.Data.RecoveryCodes[0])).HasErrors.Should().BeTrue("a recovery code is single use");
        (await main.ReadAsync<long>("SELECT count(*) FROM recovery_code WHERE user_id = @id AND used_on IS NOT NULL", userId)).Should().Be(1);
    }

    [Fact]
    public async Task MainLogin_Should_RotateTheRefreshTokenGiveTheGraceSuccessorAndRevokeAReusedFamily_When_Refreshing()
    {
        await using var main = await HostAsync("Main", new Dictionary<string, string> { ["Auth:Sessions:RefreshGraceSeconds"] = "3" });
        await main.CreateUserAsync("it.hls.refresh");
        var signIn = (await main.LoginAsync("it.hls.refresh", Password)).Data;
        var sessionId = SessionOf(signIn.Token.AccessToken);

        var first = await main.RefreshAsync(signIn.RefreshToken);
        var second = await main.RefreshAsync(signIn.RefreshToken);
        var third = await main.RefreshAsync(signIn.RefreshToken);

        first.HasErrors.Should().BeFalse(string.Join(", ", first.ErrorMessages ?? []));
        (first.Data.RefreshToken != signIn.RefreshToken).Should().BeTrue("a refresh rotates the token");
        SessionOf(first.Data.Token.AccessToken).Should().Be(sessionId);
        second.HasErrors.Should().BeFalse("the grace path, under the row lock, gives the successor again");
        (second.Data.RefreshToken == first.Data.RefreshToken).Should().BeTrue("the same successor within the grace window");
        third.ErrorMessages.Should().Equal(ISvcAuthenticator.SessionExpired);
        (await main.SessionStateAsync(sessionId)).Should().Be(SessionState.Revoked, "a further reuse revokes the family");
        (await main.RefreshAsync(first.Data.RefreshToken)).HasErrors.Should().BeTrue("the successor dies with its family");
        (await main.ReadAsync<string>("SELECT revoked_reason FROM user_session WHERE id = @id", sessionId)).Should().Be("refresh-token-reused");

        // Two tabs at once: the second waits on the first one's row lock and gets the same successor.
        await main.CreateUserAsync("it.hls.tabs");
        var tabs = (await main.LoginAsync("it.hls.tabs", Password)).Data;
        var results = await Task.WhenAll(main.RefreshAsync(tabs.RefreshToken), main.RefreshAsync(tabs.RefreshToken));
        results.Should().OnlyContain(r => !r.HasErrors);
        (results[0].Data.RefreshToken == results[1].Data.RefreshToken).Should().BeTrue("both tabs get the same successor");
    }

    [Fact]
    public async Task MainLogin_Should_EndTheSessions_When_TheUserLogsOutChangesThePasswordOrIsDisabled()
    {
        await using var main = await HostAsync("Main");
        var administratorId = await main.CreateUserAsync("it.hls.admin", roles: [RoleCodes.SystemAdministrator], allSites: true);
        var userId = await main.CreateUserAsync("it.hls.ends", roles: [RoleCodes.TerminalDutyManager]);
        var other = (await main.LoginAsync("it.hls.ends", Password)).Data;
        var current = (await main.LoginAsync("it.hls.ends", Password)).Data;

        (await main.ChangeAsync(userId, SessionOf(current.Token.AccessToken), Password, NewPassword)).HasErrors.Should().BeFalse();
        (await main.SessionStateAsync(SessionOf(other.Token.AccessToken))).Should().Be(SessionState.Revoked, "RevokeAllAsync ends the other sessions");
        (await main.SessionStateAsync(SessionOf(current.Token.AccessToken))).Should().Be(SessionState.Active, "and keeps the one that changed it");

        await main.LogoutAsync(userId, SessionOf(current.Token.AccessToken));
        (await main.SessionStateAsync(SessionOf(current.Token.AccessToken))).Should().Be(SessionState.Revoked, "logout revokes the session at once");
        (await main.RefreshAsync(current.RefreshToken)).HasErrors.Should().BeTrue();

        var again = (await main.LoginAsync("it.hls.ends", NewPassword)).Data;
        (await main.DisableAsync(administratorId, userId)).HasErrors.Should().BeFalse();
        (await main.SessionStateAsync(SessionOf(again.Token.AccessToken))).Should().Be(SessionState.Revoked, "disabling ends every session (RevokeAllAsync)");
        (await main.LoginAsync("it.hls.ends", NewPassword)).HasErrors.Should().BeTrue();
        (await main.ReadAsync<long>("SELECT count(*) FROM user_session WHERE user_id = @id AND revoked_reason IN ('password-changed', 'disabled')", userId))
            .Should().Be(2);
    }

    #endregion

    #region Every host's request authentication

    [Theory]
    [InlineData("Main")]
    [InlineData("Ingest")]
    [InlineData("Stream")]
    [InlineData("Cronz")]
    [InlineData("Integration")]
    public async Task HostLogin_Should_CheckTheSessionThePermissionsAndTheSites_When_ItsHostAuthenticatesARequest(string host)
    {
        var (userId, active, revoked) = await SignedInAsync();
        await using var services = await HostAsync(host);
        await services.StartAsync();
        await using var scope = services.Provider.CreateAsyncScope();

        var sessions = scope.ServiceProvider.GetRequiredService<ISessionValidator>();
        (await sessions.CheckAsync(active, Ct)).Should().Be(SessionState.Active, host);
        (await sessions.CheckAsync(revoked, Ct)).Should().Be(SessionState.Revoked, host);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ArivaClaims.Subject, userId.ToString())], "it-bearer"));
        (await scope.ServiceProvider.GetRequiredService<IPermissionResolver>().GetPermissionsAsync(principal, Ct)).Should().NotBeEmpty($"{host} reads the stored roles");
        var sites = await scope.ServiceProvider.GetRequiredService<SiteAccessResolver>().ForUserAsync(userId, Ct);
        sites.AllSites.Should().BeFalse(host);
        sites.SiteCodes.Should().BeEquivalentTo(new[] { "HLS" }, host);
    }

    #endregion

    #region Ariva.Api.Integration's client authentication

    [Fact]
    public async Task IntegrationLogin_Should_ExchangeCountLockCheckAndRecord_When_AClientAuthenticates()
    {
        await using var main = await HostAsync("Main", configure: IntegrationServices);
        var administratorId = await main.CreateUserAsync("it.hls.icadmin", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await main.AsCallerAsync(administratorId, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("HLI", "Host login integration site"), Ct)))
            .HasErrors.Should().BeFalse();
        var created = await main.AsCallerAsync(administratorId, s => s.GetRequiredService<ISvcIntegrationClients>().CreateAsync(
            new CreateIntegrationClientRequest("AODB host login", "Aodb", ["flights:write"], ["HLI"], ["10.20.0.0/24"], null), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        var client = created.Data;
        var id = client.Client.ClientId;

        await using var integration = await HostAsync("Integration", configure: IntegrationServices);
        await integration.StartAsync();

        var issued = await ExchangeAsync(integration, id, client.ClientSecret, Code(integration, Base32.Decode(client.TotpSecret)));
        issued.HasErrors.Should().BeFalse(string.Join(", ", issued.ErrorMessages ?? []));
        (await ExchangeAsync(integration, id, client.ClientSecret, Code(integration, Base32.Decode(client.TotpSecret)))).ErrorMessages
            .Should().Equal(new[] { IntegrationErrors.InvalidClient }, "the same code twice");
        (await main.ReadAsync<int>("SELECT failed_attempts FROM integration_client WHERE client_id = @secret", secret: id)).Should().Be(1, "the replay is counted");

        // Nine more failures, at most four a minute under the attempt limit: the tenth in a row locks the client, audited.
        for (var i = 0; i < 9; i++)
        {
            if (i % 4 == 0)
                integration.Clock.Advance(TimeSpan.FromMinutes(1));
            (await ExchangeAsync(integration, id, "ics_" + new string('x', 43), Code(integration, Base32.Decode(client.TotpSecret)))).HasErrors.Should().BeTrue();
        }

        (await main.ReadAsync<bool>("SELECT locked_until_utc IS NOT NULL AND failed_attempts = 0 FROM integration_client WHERE client_id = @secret", secret: id))
            .Should().BeTrue("locked, and the count starts again");
        (await main.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'IntegrationClient.LockedOut'", client.Client.Id)).Should().Be(1);
        (await ExchangeAsync(integration, id, client.ClientSecret, Code(integration, Base32.Decode(client.TotpSecret)))).ErrorMessages
            .Should().Equal(new[] { IntegrationErrors.InvalidClient }, "locked, even with the right credentials");

        // A lock stops new tokens, not the calls of a token the client holds; each call is recorded.
        var version = await main.ReadAsync<int>("SELECT token_version FROM integration_client WHERE client_id = @secret", secret: id);
        var check = await integration.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationAuth>().CheckCallAsync(id, version, Inside, null, Ct));
        check.Caller.Should().NotBeNull(check.Reason);
        (await integration.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationAuth>().CheckCallAsync(id, version + 1, Inside, null, Ct))).Caller
            .Should().BeNull("a token of an older version");
        await integration.AsCallerAsync(null, async s =>
        {
            await s.GetRequiredService<ISvcIntegrationAuth>().RecordCallAsync(new IntegrationCallRecord(id, issued.Data.SessionId, "flights:write", "GET",
                "api/v1/integration/sites/{siteCode}/flights/check", "HLI", 200, null, 0, Inside, integration.Clock.GetUtcNow().UtcDateTime), Ct);
            return true;
        });
        (await main.ReadAsync<long>("SELECT count(*) FROM integration_call WHERE client_id = @secret", secret: id)).Should().Be(1);
    }

    #endregion

    #region Every host's outbox, inbox and key ring

    [Theory]
    [InlineData("Main", true)]
    [InlineData("Ingest", false)]
    [InlineData("Stream", true)]
    [InlineData("Cronz", false)]
    [InlineData("Integration", true)]
    public async Task HostLogin_Should_WriteRelayAndPurgeTheOutboxAndClaimTheInboxItConsumes_When_ItsHostRunsThem(string host, bool consumes)
    {
        await using var services = await HostAsync(host);
        await services.StartAsync();
        var key = "it-hls-" + host.ToLowerInvariant();
        // The rows other tests left are marked sent first (as the migration login), so the pass claims this host's own.
        await services.ReadAsync<object>("UPDATE outbox_message SET sent_on = now() WHERE sent_on IS NULL");

        var probe = new OutboxProbe { AlertId = key, Step = 1, CorrelationId = "corr-" + key };
        await services.AsCallerAsync(null, async s =>
        {
            var unitOfWork = s.GetRequiredService<IUnitOfWork>();
            await unitOfWork.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await new NHibernateDomainEventOutbox(new EventCatalog([typeof(OutboxProbe).Assembly]), TimeProvider.System)
                .WriteAsync(unitOfWork.StorageProvider, [probe], Ct);
            unitOfWork.PromiseToCommit();
            return true;
        });
        // For the relay's hourly housekeeping: a sent row and an inbox entry past their keep times (planted as the migration login).
        await services.ReadAsync<int>("""
            INSERT INTO outbox_message (id, topic, message_key, message_type, payload, created_on, sent_on)
            VALUES (gen_random_uuid(), 'ariva.alert.state-changed.v1', @secret, 'OutboxProbe', '{}', '2026-01-01Z', '2026-01-01Z') RETURNING 1
            """, secret: key + "-old");
        await services.ReadAsync<int>("INSERT INTO processed_event (consumer, event_id, processed_on) VALUES (@secret, gen_random_uuid(), '2026-01-01Z') RETURNING 1", secret: key);

        var transport = new SinkTransport();
        var relay = new OutboxRelay(services.Provider.GetRequiredService<DatabaseSettings>(),
            new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = 100, PassSeconds = 60 } }, transport, TimeProvider.System, NullLogger<OutboxRelay>.Instance);
        var pass = await relay.RunOnceAsync(Ct);

        pass.Leader.Should().BeTrue($"{host} takes the relay's lock");
        transport.Keys.Should().Contain(key, "the relay claimed the host's row and sent it");
        (await services.ReadAsync<DateTime?>("SELECT sent_on FROM outbox_message WHERE id = @id", probe.Id)).Should().NotBeNull("the relay marked it sent");
        (await services.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key = @secret", secret: key + "-old")).Should().Be(0, "housekeeping removed the old sent row");
        (await services.ReadAsync<long>("SELECT count(*) FROM processed_event WHERE consumer = @secret", secret: key)).Should().Be(0, "and the old inbox entry");

        var eventId = Guid.CreateVersion7();
        var claim = () => services.AsCallerAsync(null, async s =>
        {
            var claimed = await new PostgresInbox(s.GetRequiredService<IStorageProvider>(), services.Clock).TryClaimAsync("it-hls." + host, eventId, Ct);
            s.GetRequiredService<IUnitOfWork>().PromiseToCommit();
            return claimed;
        });
        if (consumes)
        {
            (await claim()).Should().BeTrue($"{host} consumes events and claims each once");
            (await claim()).Should().BeFalse("a duplicate is skipped");
            return;
        }

        var thrown = (await claim.Should().ThrowAsync<Exception>($"{host} consumes nothing")).Which;
        var refused = thrown as PostgresException ?? thrown.InnerException as PostgresException;
        refused.Should().NotBeNull(thrown.ToString());
        refused.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"{host} may not write the inbox");
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("Ingest")]
    [InlineData("Stream")]
    [InlineData("Cronz")]
    [InlineData("Integration")]
    public async Task HostLogin_Should_StoreAndReadTheDataProtectionKeyRing_When_ItsHostStarts(string host)
    {
        // ARV-211: the hosts store a key through the trusted filter, with the row signature of script 0073, and read the ring back
        // through it; the base's table grants on data_protection_key cover the signature column (the review's R2-2, item d).
        var (database, logins) = await MigratedAsync();
        var settings = LoginSettings(database, logins, host);
        IKey key;
        string payload;
        await using (var protection = DataProtectionHost(settings))
        {
            key = protection.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            payload = protection.GetRequiredService<IDataProtectionProvider>().CreateProtector("it-hls-keyring").Protect(host);
        }

        var name = $"key-{key.KeyId:D}";
        var store = new PostgresXmlRepository(settings);
        var row = store.GetAllRows().Should().ContainSingle(r => r.FriendlyName == name, $"{host} stored the key").Subject;
        row.Signature.Should().MatchRegex("^ES256:[0-9A-F]{64}:[A-Za-z0-9+/]{86}==$", $"{host}'s login wrote the row signature");
        var trusted = new TrustedKeyRingRepository(store, DataProtectionCertificates.From(WrappingCertificate), Signing, null, TimeProvider.System, NullLogger.Instance);
        trusted.Verdict(row).Should().BeNull("the row's signature verifies under the signer that wrote it");

        // A name is stored once (ON CONFLICT DO NOTHING under the login): another element under it leaves the row as it was.
        trusted.StoreElement(new XElement("key", new XAttribute("id", key.KeyId)), name);
        store.GetAllRows().Single(r => r.FriendlyName == name).Should().Be(row);

        // A host started afresh under the same login reads the key back through the filter and unprotects with the ring.
        await using var again = DataProtectionHost(settings);
        again.GetRequiredService<IKeyManager>().GetAllKeys().Should().Contain(k => k.KeyId == key.KeyId, $"{host} reads the signed row");
        var unprotected = again.GetRequiredService<IDataProtectionProvider>().CreateProtector("it-hls-keyring").Unprotect(payload);
        string.Equals(unprotected, host, StringComparison.Ordinal).Should().BeTrue($"{host}'s login reads the ring that protected the value");
    }

    #endregion

    #region The key ring upgrade check and the key lifecycle job

    [Theory]
    [InlineData("Main", true)]
    [InlineData("Cronz", true)]
    [InlineData("Ingest", false)]
    [InlineData("Stream", false)]
    [InlineData("Integration", false)]
    public async Task HostLogin_Should_RunTheKeyRingUpgradeCheck_When_ItsHostRegistersIt_And_BeRefusedItsRead_When_ItDoesNot(string host, bool registers)
    {
        // The review's R2-2 (a): the check reads "user".totp_secret_protected, integration_client.totp_secret_protected and
        // outbound_endpoint.secret_protected. Main and Cronz may read all three and register the gate; the others may not, so a host
        // that ran it would stop at start on 42501, and they do not register it (DataProtectionExtensions).
        var (database, logins) = await MigratedAsync();
        HostDatabaseAccess.RunsKeyRingUpgradeCheck(host.ToLowerInvariant()).Should().Be(registers);
        // An upgraded installation's stored value: a TOTP seed under a key the hosts signed, protected as Main's login protects one.
        string seed;
        await using (var main = DataProtectionHost(LoginSettings(database, logins, "Main")))
            seed = main.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.DataProtectionPurpose).Protect("JBSWY3DPEHPK3PXP");
        await InsertSeededUserAsync(database, $"it.hls.seed.{host.ToLowerInvariant()}", seed);

        var settings = LoginSettings(database, logins, host);
        using var metrics = new KeyAgeMetrics();
        var gate = new KeyRingUpgradeGate(settings, Signing, TimeProvider.System, NullLogger<KeyRingUpgradeGate>.Instance, metrics);
        if (registers)
        {
            await gate.StartAsync(Ct);
            var finding = await KeyRingUpgradeCheck.FindAsync(settings.BuildConnectionString(), Signing, Ct);
            finding.OnSignedKeys.Should().BeGreaterThanOrEqualTo(1, $"{host}'s login reads the stored values and the signed rows they name");
            finding.Total.Should().Be(0, "no value needs a key the hosts refuse");
            return;
        }

        var start = () => gate.StartAsync(Ct);
        (await start.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
            $"{host}'s login may not read every stored payload, so its host does not run the check");
    }

    [Theory]
    [InlineData("Cronz", true)]
    [InlineData("Main", false)]
    [InlineData("Ingest", false)]
    [InlineData("Stream", false)]
    [InlineData("Integration", false)]
    public async Task HostLogin_Should_RunTheKeyLifecycleRoundAsCronzOnly_When_TheJobRuns(string host, bool runs)
    {
        // The review's R2-2 (b) and (c): the round runs script 0072's purge, which script 0074 grants ariva_cronz alone, and reads the
        // dates, the status and whether a credential is set of integration clients, devices, displays and outbound endpoints.
        var (database, logins) = await MigratedAsync();
        await InsertDatedCredentialsAsync(database);
        var round = new KeyLifecycleRound(LoginSettings(database, logins, host), new KeyLifecycleSettings(), NullLogger<KeyLifecycleRound>.Instance, Signing);
        if (runs)
        {
            var run = await round.RunAsync(Ct);
            run.KeyRing.Deleted.Should().Be(0, "no key here expired");
            run.OldestMadeUtc[KeyAgeMetrics.IntegrationClientSecret].Should().BeOnOrBefore(DateTime.UtcNow.AddDays(-399), "Cronz's login reads a client's secret date");
            run.OldestMadeUtc[KeyAgeMetrics.IntegrationClientSeed].Should().BeOnOrBefore(DateTime.UtcNow.AddDays(-199));
            run.OldestMadeUtc[KeyAgeMetrics.OutboundSecret].Should().BeOnOrBefore(DateTime.UtcNow.AddDays(-49));
            run.Unsigned.Should().NotBeNull("the round counts the stored values with each row's signature checked");
            return;
        }

        var purge = () => round.RunAsync(Ct);
        (await purge.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
            $"{host}'s login may not run the key ring purge");
    }

    #endregion

    #region Helpers

    /// <summary>One database migrated by a cluster's job with every host's login, for the class.</summary>
    private async Task<(string Database, Dictionary<string, string> Logins)> MigratedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_database is null)
            {
                var database = await fixture.CreateDatabaseAsync(TestDatabase.HostLoginServices);
                var logins = HostLogins.ClusterSettings(Prefix);
                var settings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Application:Environment"] = "k8s-dev",
                    ["Database:Host"] = fixture.Hostname,
                    ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
                    ["Database:Name"] = database,
                    ["Database:Migration:Username"] = fixture.AdminUsername,
                    ["Database:Migration:Password"] = fixture.AdminPassword,
                    ["Database:Username"] = logins["Database:HostLogins:Main:Username"],
                    ["Database:Password"] = logins["Database:HostLogins:Main:Password"]
                };
                foreach (var (key, value) in logins)
                    settings[key] = value;
                (await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Ct)).Should().Be(0,
                    "the cluster's job with every host login");
                _logins = logins;
                _database = database;
            }

            return (_database, _logins);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A host's own login on the class's database, as its Database section gives it.</summary>
    private DatabaseSettings LoginSettings(string database, Dictionary<string, string> logins, string host) => new()
    {
        Host = fixture.Hostname,
        Port = fixture.Port,
        Name = database,
        Username = logins[$"Database:HostLogins:{host}:Username"],
        Password = logins[$"Database:HostLogins:{host}:Password"]
    };

    /// <summary>The hosts' Data Protection as the given login, behind the trusted key filter (rows signed, script 0073).</summary>
    private static ServiceProvider DataProtectionHost(DatabaseSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(settings, DataProtectionCertificates.From(WrappingCertificate), Signing);
        return services.BuildServiceProvider();
    }

    /// <summary>An account holding a protected TOTP seed (as the migration login).</summary>
    private async Task InsertSeededUserAsync(string database, string userName, string seed)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations, must_change_password, totp_enrolled,
                is_disabled, failed_login_count, is_break_glass, all_sites, totp_secret_protected)
            VALUES (gen_random_uuid(), @name, 'hash', 'salt', 'pbkdf2-sha256', 600000, false, true, false, 0, false, false, @seed)
            """, connection);
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("seed", seed);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
    }

    /// <summary>
    /// An active integration client whose secret is 400 days old and seed 200, and an active outbound endpoint whose secret is 50
    /// days old, once (as the migration login); their stored values are not payloads, so the upgrade check ignores them.
    /// </summary>
    private async Task InsertDatedCredentialsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO integration_client (id, client_id, name, kind, status, scope_names, site_codes, allowed_networks, require_totp_per_request,
                secret_algorithm, secret_iterations, secret_salt, secret_hash, secret_changed_utc, totp_secret_protected, totp_changed_utc,
                tokens_valid_from_utc, token_version)
            SELECT gen_random_uuid(), 'ic_hostloginkeylifecycleabcde', 'Host login key lifecycle client', 'Other', 'Active', 'queues:read', 'HLS', '10.0.0.0/8', false,
                'pbkdf2-sha256', 600000, 'salt', 'hash', now() - interval '400 days', 'not-a-payload', now() - interval '200 days', now(), 0
             WHERE NOT EXISTS (SELECT 1 FROM integration_client WHERE client_id = 'ic_hostloginkeylifecycleabcde');
            INSERT INTO outbound_endpoint (id, code, name, purpose, status, site_codes, base_url, allowed_networks, auth_kind, header_name,
                totp_per_request, timeout_seconds, retry_count, breaker_failures, break_seconds, poll_seconds, secret_protected,
                has_client_certificate, secret_changed_utc, client_version)
            SELECT gen_random_uuid(), 'hls-key-lifecycle', 'Host login key lifecycle partner', 'Generic', 'Active', 'HLS', 'https://partner.example/', '10.0.0.0/8',
                'ApiKeyHeader', 'X-Api-Key', false, 10, 0, 5, 30, 0, 'not-a-payload', false, now() - interval '50 days', 0
             WHERE NOT EXISTS (SELECT 1 FROM outbound_endpoint WHERE code = 'hls-key-lifecycle');
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static X509Certificate2 Wrapping()
    {
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        using var created = new CertificateRequest("CN=Ariva DP (host logins)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 Signer()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ariva key ring signing (host logins)", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    /// <summary>The production account, persistence and messaging registration of a host, connected as that host's own login.</summary>
    private async Task<AccountsHost> HostAsync(string host, Dictionary<string, string> settings = null, Action<IServiceCollection> configure = null)
    {
        var (database, logins) = await MigratedAsync();
        var values = new Dictionary<string, string>(settings ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["Database:Username"] = logins[$"Database:HostLogins:{host}:Username"],
            ["Database:Password"] = logins[$"Database:HostLogins:{host}:Password"]
        };
        return new AccountsHost(fixture, values, configure: services =>
        {
            services.AddSingleton(Protection);
            configure?.Invoke(services);
        }, databaseName: database);
    }

    /// <summary>An account of a terminal duty manager at site HLS with one active and one revoked session, made by Main's login.</summary>
    private async Task<(Guid UserId, Guid Active, Guid Revoked)> SignedInAsync()
    {
        await SignInGate.WaitAsync(Ct);
        try
        {
            if (_signedIn is { } known)
                return known;

            await using var main = await HostAsync("Main");
            var administratorId = await main.CreateUserAsync("it.hls.siteadmin", roles: [RoleCodes.SystemAdministrator], allSites: true);
            (await main.AsCallerAsync(administratorId, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("HLS", "Host login site"), Ct)))
                .HasErrors.Should().BeFalse();
            var userId = await main.CreateUserAsync("it.hls.officer", roles: [RoleCodes.TerminalDutyManager]);
            (await main.AsCallerAsync(administratorId, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(userId, new SiteAccessRequest(false, ["HLS"]), Ct)))
                .HasErrors.Should().BeFalse();
            var active = SessionOf((await main.LoginAsync("it.hls.officer", Password)).Data.Token.AccessToken);
            var revoked = SessionOf((await main.LoginAsync("it.hls.officer", Password)).Data.Token.AccessToken);
            await main.LogoutAsync(userId, revoked);
            _signedIn = (userId, active, revoked);
            return _signedIn.Value;
        }
        finally
        {
            SignInGate.Release();
        }
    }

    /// <summary>Integration client administration (Main) and authentication (Integration), with the integration token key ring.</summary>
    private static void IntegrationServices(IServiceCollection services)
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
    }

    private static Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.IntegrationTokenViewModel>> ExchangeAsync(AccountsHost host, string clientId, string secret, string code) =>
        host.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationAuth>().ExchangeAsync(new IntegrationTokenRequest(clientId, secret, code), Inside, Ct));

    private static string Code(AccountsHost host, byte[] secret, int offset = 0) => Totp.Code(secret, Totp.StepAt(host.Clock.GetUtcNow()) + offset);

    /// <summary>A broker that takes every row and keeps the keys it was given.</summary>
    private sealed class SinkTransport : IOutboxTransport
    {
        public List<string> Keys { get; } = [];

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
        {
            lock (Keys)
                Keys.Add(message.Key);
            return Task.CompletedTask;
        }
    }

    #endregion
}
