using Ariva.Api.Common.Settings;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Security;
using Ariva.Di.Extensions;
using Ariva.Infra.Settings;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010a: the committed settings match ADR-0026 (15 minute tokens, lockout 10 for 15 minutes, 10 sign-ins a minute
/// per address), development keys and accounts exist only in vm-local, and in the clusters every host reads the
/// public keys while only Ariva.Api.Main reads the signing key. Plus the account rules on the User entity.
/// </summary>
public sealed class AuthSettingsTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> ClusterEnvironments => ["k8s-dev", "k8s-demo", "k8s-prd"];

    public static TheoryData<string, string> ServiceFiles
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var host in new[] { "Main", "Ingest", "Stream", "Cronz", "Integration" })
            {
                foreach (var environment in new[] { "k8s-dev", "k8s-demo", "k8s-prd" })
                    data.Add(host, environment);
            }

            return data;
        }
    }

    [Theory]
    [InlineData("vm-local", "vm-local", true)]
    [InlineData("vm-local", null, false)]
    [InlineData("vm-local", "", false)]
    [InlineData("vm-local", "k8s-prd", false)]
    [InlineData("vm-local", "k8s-demo", false)]
    [InlineData("k8s-dev", "vm-local", false)]
    [InlineData("k8s-prd", null, false)]
    [InlineData(null, null, false)]
    public void LocalOnly_Should_AllowDevelopmentAccountsAndNoTotp_When_BothEnvironmentsAreVmLocal(string application, string host, bool allowed)
    {
        // ARV-064: a cluster whose settings say vm-local (refused at startup since ARV-098) is still refused by its
        // host environment, which Helm sets; an Auth__TotpRequired=false variable on a cluster refuses to start. A host
        // that does not say which environment it runs in is refused too.
        IConfiguration Settings(params (string Key, string Value)[] extra) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Application:Environment"] = application }
                .Concat(extra.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)))).Build();
        var noTotp = Settings(("Auth:TotpRequired", "false"));
        var accounts = Settings(("Auth:DevelopmentUsers:0:UserName", "demo.border"), ("Auth:DevelopmentUsers:0:Password", "x"));

        (Ariva.Di.Extensions.AccountExtensions.LocalOnlyProblem(AuthSettings.From(noTotp), noTotp, host) is null).Should().Be(allowed);
        (Ariva.Di.Extensions.AccountExtensions.LocalOnlyProblem(AuthSettings.From(accounts), accounts, host) is null).Should().Be(allowed);
        var plain = Settings();
        Ariva.Di.Extensions.AccountExtensions.LocalOnlyProblem(AuthSettings.From(plain), plain, host).Should().BeNull("TOTP required and no development accounts are allowed anywhere");
        if (!allowed)
            Ariva.Di.Extensions.AccountExtensions.LocalOnlyProblem(AuthSettings.From(noTotp), noTotp, host).Should().Contain("Auth:TotpRequired false only allowed in vm-local");
    }

    [Theory]
    [InlineData("k8s-dev", false)]
    [InlineData("k8s-prd", false)]
    [InlineData("vm-local", true)]
    public void AddArivaAccounts_Should_RefuseSignInWithoutTotp_When_AnyHostRunsOutsideVmLocal(string host, bool starts)
    {
        // ARV-064 review: every host checks, not only Main's token issuing (Cronz reads Auth:TotpRequired for report
        // recipients), and so does the break-glass command, which also calls AddArivaAccounts.
        var configuration = new ConfigurationBuilder().AddConfiguration(Base(null)).AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            ["Auth:TotpRequired"] = "false"
        }).Build();

        var add = () => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddArivaAccounts(configuration, host);

        if (starts)
            add.Should().NotThrow();
        else
            add.Should().Throw<InvalidOperationException>().WithMessage($"*Auth:TotpRequired false only allowed in vm-local; this host runs as '{host}'*");
    }

    [Fact]
    public void LocalOnly_Should_IgnoreTheEnvironmentConfigurationKey_When_TheHostRunsElsewhere()
    {
        // ARV-064 review: an unprefixed ENVIRONMENT variable sets the "environment" configuration key, never the host's
        // own environment name, so it cannot make a cluster host look like vm-local.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            [Microsoft.Extensions.Hosting.HostDefaults.EnvironmentKey] = "vm-local",
            ["Auth:TotpRequired"] = "false"
        }).Build();

        Ariva.Di.Extensions.AccountExtensions.LocalOnlyProblem(AuthSettings.From(configuration), configuration, "k8s-prd")
            .Should().Contain("this host runs as 'k8s-prd'");
    }

    [Fact]
    public void Defaults_Should_MatchAdr0026_When_BaseFileIsRead()
    {
        var auth = AuthSettings.From(Base(null));
        var limits = Base(null).GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>();

        auth.Tokens.Issuer.Should().Be("ariva");
        auth.Tokens.Audience.Should().Be("ariva-users");
        auth.Tokens.LifetimeMinutes.Should().Be(15);
        auth.Tokens.ClockSkewSeconds.Should().Be(30);
        auth.Lockout.Threshold.Should().Be(10);
        auth.Lockout.DurationSeconds.Should().Be(900);
        auth.TotpRequired.Should().BeTrue("every human account needs TOTP (ARV-010c)");
        auth.Totp.SkewSteps.Should().Be(1, "one step either side, RFC 6238 guidance");
        auth.Sessions.AbsoluteSeconds.Should().Be(12 * 3600, "12 hours for every role");
        auth.Sessions.IdleSecondsAdministrator.Should().Be(30 * 60);
        auth.Sessions.IdleSecondsOperational.Should().Be(4 * 3600);
        auth.Sessions.RefreshGraceSeconds.Should().Be(30);
        auth.Sessions.CacheSeconds.Should().BeLessThan(5, "revocation must reach every node within 5 seconds, also without the Redis backplane");
        auth.DevelopmentUsers.Should().BeEmpty();
        limits.Auth.PermitLimit.Should().Be(10);
        limits.Auth.WindowSeconds.Should().Be(60);
        new RateLimitingSettings().Auth.PermitLimit.Should().Be(10, "the code default matches the file");
    }

    [Theory]
    [MemberData(nameof(ClusterEnvironments))]
    public void ClusterFiles_Should_ReadMountedPublicKeysAndNeverUseDevelopmentKeys_When_EnvironmentIsCluster(string environment)
    {
        var tokens = AuthSettings.From(Base(environment)).Tokens;

        tokens.UseDevelopmentKeys.Should().BeFalse($"{environment} must use the mounted keys");
        AuthSettings.From(Base(environment)).TotpRequired.Should().BeTrue($"{environment}: every human account needs TOTP (ARV-010c)");
        tokens.PublicKeyPaths.Should().Equal("/app/secrets/token-public/public.pem", "/app/secrets/token-public/previous.pem");
        tokens.SigningKeyPath.Should().BeNullOrEmpty("only Ariva.Api.Main's service file names the signing key");
    }

    [Fact]
    public void VmLocal_Should_UseDevelopmentKeys_When_FileIsRead()
    {
        AuthSettings.From(Base("vm-local")).Tokens.UseDevelopmentKeys.Should().BeTrue();
        AuthSettings.From(Base("vm-local")).TotpRequired.Should().BeTrue("developers sign in like operators; the E2E run alone turns it off");
    }

    [Theory]
    [MemberData(nameof(ServiceFiles))]
    public void ServiceFiles_Should_GiveTheSigningKeyOnlyToMain_When_EnvironmentIsCluster(string host, string environment)
    {
        var file = RepositoryPaths.Resolve($"Platform/Backplane/Ariva.Api.{host}/appsettings.service.{environment}.json");
        var tokens = AuthSettings.From(new ConfigurationBuilder().AddJsonFile(file).Build()).Tokens;

        if (host == "Main")
            tokens.SigningKeyPath.Should().Be("/app/secrets/token-signing/signing.key");
        else
            tokens.SigningKeyPath.Should().BeNullOrEmpty($"Ariva.Api.{host} validates tokens and must not be able to sign them");
        AuthSettings.From(new ConfigurationBuilder().AddJsonFile(file).Build()).DevelopmentUsers.Should().BeEmpty();
    }

    #region User entity

    [Fact]
    public void User_Should_StoreANormalisedUserName_When_Created()
    {
        new User("  Officer.ONE ", "Officer One", " officer@example.org ").UserName.Should().Be("officer.one");
    }

    [Fact]
    public void IsLocked_Should_HoldUntilTheLockEnds_When_AccountIsLocked()
    {
        var user = new User("officer.one", "Officer One", null);
        typeof(User).GetProperty(nameof(User.LockedUntil))!.SetValue(user, Now.AddMinutes(15));

        user.IsLocked(Now).Should().BeTrue();
        user.IsLocked(Now.AddMinutes(15)).Should().BeFalse("the lock ends on its own");

        user.Unlock();
        user.IsLocked(Now).Should().BeFalse();
        user.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public void RecordSuccessfulLogin_Should_ClearFailuresAndLock_When_Called()
    {
        var user = new User("officer.one", "Officer One", null);
        typeof(User).GetProperty(nameof(User.FailedLoginCount))!.SetValue(user, 7);

        user.RecordSuccessfulLogin(Now);

        user.FailedLoginCount.Should().Be(0);
        user.LockedUntil.Should().BeNull();
        user.LastLoginOn.Should().Be(Now);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    public void IsPending_Should_FollowTemporaryPasswordAndTotp_When_Asked(bool temporary, bool totpEnrolled, bool totpRequired, bool expected)
    {
        var user = new User("officer.one", "Officer One", null);
        user.SetPassword(new PasswordHashValue("pbkdf2-sha256", 600_000, "c2FsdA==", "aGFzaA=="), temporary);
        if (totpEnrolled)
        {
            user.BeginTotpEnrolment("protected-secret");
            user.ConfirmTotp(1);
        }

        user.IsPending(totpRequired).Should().Be(expected);
    }

    [Fact]
    public void Grant_Should_RefuseUnknownRoleCodes_When_Called()
    {
        var user = new User("officer.one", "Officer One", null);

        user.Grant(RoleCodes.SystemAdministrator);
        user.Grant(RoleCodes.SystemAdministrator);
        var unknown = () => user.Grant("Superuser");

        user.Roles.Should().ContainSingle().Which.RoleCode.Should().Be(RoleCodes.SystemAdministrator);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    private static IConfiguration Base(string environment)
    {
        var builder = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"));
        if (environment is not null)
            builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, $"appsettings.base.{environment}.json"));
        return builder.Build();
    }
}
