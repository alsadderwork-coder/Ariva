using Ariva.Api.Common.Hosting;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-083b (CWE-522, CWE-269; ASVS 5.0 V13.3.1, V13.3.2): in a cluster the shared base settings, which every pod mounts from
/// base-appsettings-secret, carry no credential. A host or job whose <c>appsettings.base.*.json</c> gives a password, a secret
/// or a Redis connection string with a user or password does not start, and says which setting of which file, never its value;
/// the same credential from the environment (the host's own secret) or in vm-local is accepted, and so are the committed base
/// files. Every host checks it right after its go-live settings, before any command runs.
/// </summary>
public sealed class SharedSettingsTests : IDisposable
{
    #region Fields

    private const string Secret = "shared-secret-value-0123";
    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-shared-settings-").FullName;

    #endregion

    #region Cases

    public static TheoryData<string, string> Credentials => new()
    {
        { "Database:Password", Secret },
        { "Database:Migration:Password", Secret },
        { "Database:HostLogins:Main:Password", Secret },
        { "Database:Availability:Password", Secret },
        { "Database:ValidationReader:Password", Secret },
        { "Kafka:SaslPassword", Secret },
        { "Redis:Password", Secret },
        { "Email:Smtp:Password", Secret },
        { "Integration:Outbound:ClientSecret", Secret },
        { "Redis:ConnectionString", "redis:6379,ssl=true,password=" + Secret },
        { "Redis:ConnectionString", "redis:6379,ssl=true,user=ariva" },
        // The ARV-083b review: every name the logs omit, and the other credential names, not only Password and Secret.
        { "Logging:PseudonymKey", Secret },
        { "Otlp:Headers", "Authorization=Basic%20" + Secret },
        { "Integration:Partner:ApiKey", Secret },
        { "Integration:Partner:AccessToken", Secret },
        { "Integration:Partner:Token", Secret },
        { "Security:Signing:HmacKey", Secret },
        { "Auth:Seed:TotpSeed", Secret },
        { "Database:Pwd", Secret },
        { "Integration:Outbound:CertificatePfxBase64", Secret },
        { "Email:Smtp:Credential", Secret },
        { "Integration:Partner:KeyPassphrase", Secret },
        // A credential by its shape inside any other value: a connection string's Password pair, a bearer token.
        { "Reports:Store:ConnectionString", "Host=db;Port=5432;Username=reports;Password=" + Secret },
        { "Integration:Partner:Header", "Bearer " + Secret }
    };

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(Credentials))]
    public void EnsureNoCredentials_Should_Refuse_When_AClustersBaseFileCarriesACredential(string key, string value)
    {
        foreach (var environment in new[] { ArivaEnvironment.K8sDev, ArivaEnvironment.K8sDemo, ArivaEnvironment.K8sPrd })
        {
            var configuration = Configuration($"appsettings.base.{environment}.json", key, value);

            var start = () => SharedSettings.EnsureNoCredentials(configuration, environment);

            start.Should().Throw<InvalidOperationException>()
                .WithMessage($"base-appsettings-secret is mounted in every pod*appsettings.base.{environment}.json gives {key}*")
                .Which.Message.Contains(Secret, StringComparison.Ordinal).Should().BeFalse("the message names the setting, never its value");
        }

        var neutral = () => SharedSettings.EnsureNoCredentials(Configuration("appsettings.base.json", key, value), ArivaEnvironment.K8sPrd);
        neutral.Should().Throw<InvalidOperationException>("the environment-neutral base file is mounted from the image in every pod too");
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public void EnsureNoCredentials_Should_Accept_When_TheCredentialComesFromTheHostsOwnSource(string key, string value)
    {
        var fromEnvironment = new ConfigurationBuilder()
            .AddJsonFile(Write("appsettings.base.k8s-prd.json", "Application:Environment", "k8s-prd"))
            .AddInMemoryCollection([new KeyValuePair<string, string>(key, value)])
            .Build();
        var fromServiceFile = Configuration("appsettings.service.k8s-prd.json", key, value);
        var developer = Configuration("appsettings.base.vm-local.json", key, value);

        FluentActions.Invoking(() => SharedSettings.EnsureNoCredentials(fromEnvironment, ArivaEnvironment.K8sPrd)).Should().NotThrow("the host's own secret gives it");
        FluentActions.Invoking(() => SharedSettings.EnsureNoCredentials(fromServiceFile, ArivaEnvironment.K8sPrd)).Should().NotThrow("the host's own settings secret");
        FluentActions.Invoking(() => SharedSettings.EnsureNoCredentials(developer, ArivaEnvironment.VmLocal)).Should().NotThrow("a developer machine's shared logins");
    }

    [Fact]
    public void EnsureNoCredentials_Should_Accept_When_TheValueIsEmptyOrNotACredential()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Write("appsettings.base.k8s-prd.json", "Database:Password", ""))
            .AddJsonFile(Write("appsettings.base.k8s-dev.json", "Redis:ConnectionString", "redis:6379,ssl=true"))
            .AddJsonFile(Write("appsettings.base.k8s-demo.json", "DataProtection:KeyPath", "/app/secrets/dataprotection/tls.key"))
            .AddJsonFile(Write("appsettings.base.json", "Security:Cors:AllowCredentials", "true"))
            .Build();

        SharedSettings.CredentialProblems(configuration, ArivaEnvironment.K8sPrd).Should().BeEmpty("an empty password, a connection string without credentials and a path are no credential");
    }

    [Theory]
    [InlineData(ArivaEnvironment.K8sDev)]
    [InlineData(ArivaEnvironment.K8sDemo)]
    [InlineData(ArivaEnvironment.K8sPrd)]
    public void CommittedBaseFiles_Should_CarryNoCredential_When_ReadAsACluster(string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, $"appsettings.base.{environment}.json"))
            .Build();

        SharedSettings.CredentialProblems(configuration, environment).Should().BeEmpty();
        configuration["Kafka:SaslUsername"].Should().BeNullOrEmpty("each host's principal comes from its own secret, not the shared file");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Host_Should_CheckTheSharedSettings_When_ItsGoLiveSettingsAreChecked(string project)
    {
        var program = File.ReadAllLines(RepositoryPaths.Resolve($"Platform/Backplane/{project}/Program.cs"));
        var goLive = Array.FindIndex(program, line => line.Trim() == "GoLiveSettings.EnsureConfirmed(builder.Configuration, environment);");
        var shared = Array.FindIndex(program, line => line.Trim() == "SharedSettings.EnsureNoCredentials(builder.Configuration, environment);");
        var firstReturn = Array.FindIndex(program, line => line.TrimStart().StartsWith("return", StringComparison.Ordinal));

        goLive.Should().BeGreaterThan(0);
        shared.Should().BeGreaterThan(goLive, "the check follows the go-live check");
        program.Skip(goLive + 1).Take(shared - goLive - 1).Should().OnlyContain(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal),
            "nothing reads a setting between the two checks");
        if (firstReturn >= 0)
            shared.Should().BeLessThan(firstReturn, "no command (a migration, the topics, a replay, a break-glass) runs before the check");
        program.Count(line => line.Contains("SharedSettings.EnsureNoCredentials", StringComparison.Ordinal)).Should().Be(1);
    }

    public static TheoryData<string> Hosts => new() { "Ariva.Api.Main", "Ariva.Api.Ingest", "Ariva.Api.Stream", "Ariva.Api.Cronz", "Ariva.Api.Integration" };

    #endregion

    #region Helpers

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private IConfiguration Configuration(string file, string key, string value) =>
        new ConfigurationBuilder().AddJsonFile(Write(file, key, value)).Build();

    /// <summary>A settings file of that name holding one setting (a nested JSON object per part of the key).</summary>
    private string Write(string file, string key, string value)
    {
        var parts = key.Split(':');
        var json = string.Concat(parts.Take(parts.Length - 1).Select(p => $"{{\"{p}\":")) + $"{{\"{parts[^1]}\":{System.Text.Json.JsonSerializer.Serialize(value)}}}" +
                   new string('}', parts.Length - 1);
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N"), file);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, json);
        return path;
    }

    #endregion
}
