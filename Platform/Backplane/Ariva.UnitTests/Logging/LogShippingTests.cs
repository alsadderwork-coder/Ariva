using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Logging;
using Ariva.UnitTests.Logging.Fixtures;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-091a (ASVS 5.0 V16.4.2, V16.4.3; CWE-319, CWE-778): in k8s-prd every Backplane host ships its logs to a collector over
/// TLS, besides the console: Loki at an https <c>Loki:Uri</c>, or the OTLP collector at an https endpoint. It also hashes desk
/// codes and track ids with the deployment's pseudonym key from the environment. A host with neither does not start, and the
/// message names the settings, never a value; elsewhere both stay optional. The one-shot commands (the migration and topics
/// Jobs, the stored name check, Stream's replay) never build the host's logger, so they run in k8s-prd without either. Loki
/// sender credentials go with every batch, outside vm-local over https only and never from a file; the OTLP sink reads its
/// endpoint and headers from the host's configuration, the values the guards judged.
/// </summary>
[Collection(HostCollection.Name)]
public sealed partial class LogShippingTests : IDisposable
{
    #region Fields

    private const string Production = ArivaEnvironment.K8sPrd;

    /// <summary>Sent as the Loki password and the OTLP header value; never in a message.</summary>
    private const string Canary = "canary-log-sender-5e1b";

    private const string Sender = "ariva-probe-sender";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-log-shipping-").FullName;

    #endregion

    #region Lifetime

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    #endregion

    #region A collector in production

    [Theory]
    [InlineData("no setting at all")]
    [InlineData("the chart's empty OTLP endpoint", "OTEL_EXPORTER_OTLP_ENDPOINT=")]
    [InlineData("a Loki that is off", "Loki:Enabled=false", "Loki:Uri=" + ArivaHosts.ProductionCollector)]
    [InlineData("an OTLP endpoint without the export", "Otlp:Endpoint=" + ArivaHosts.ProductionCollector)]
    [InlineData("an OTLP export that is off", "Otlp:Enabled=false", "Otlp:Endpoint=" + ArivaHosts.ProductionCollector)]
    [InlineData("only the logs variable, which the OTLP sink does not read", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=" + ArivaHosts.ProductionCollector)]
    public void Configure_Should_RefuseToBuild_When_ProductionShipsTheLogsNowhere(string reason, params string[] settings)
    {
        var configuration = Settings(Production, [.. settings, $"{LogProtection.KeySetting}={ArivaHosts.ProductionPseudonymKey}"]);

        var configure = () => Build(configuration, Production);

        configure.Should().Throw<InvalidOperationException>(reason).WithMessage(ArivaLogging.NoCollector)
            .Which.Message.Should().NotContain("127.0.0.1", "the message names the settings, never a value");
    }

    [Theory]
    [InlineData("Loki over https", "Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector)]
    [InlineData("Loki over https beside the chart's empty OTLP endpoint", "Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector,
        "OTEL_EXPORTER_OTLP_ENDPOINT=")]
    [InlineData("the OTLP collector by the chart's variable", "OTEL_EXPORTER_OTLP_ENDPOINT=" + ArivaHosts.ProductionCollector)]
    [InlineData("the OTLP collector by Ariva's settings", "Otlp:Enabled=true", "Otlp:Endpoint=" + ArivaHosts.ProductionCollector)]
    [InlineData("both", "Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector, "OTEL_EXPORTER_OTLP_ENDPOINT=" + ArivaHosts.ProductionCollector)]
    public void Configure_Should_Build_When_ProductionShipsTheLogsOverTls(string reason, params string[] settings)
    {
        var configuration = Settings(Production, [.. settings, $"{LogProtection.KeySetting}={ArivaHosts.ProductionPseudonymKey}"]);

        var configure = () => Build(configuration, Production);

        configure.Should().NotThrow(reason);
    }

    [Theory]
    [InlineData(ArivaEnvironment.VmLocal)]
    [InlineData(ArivaEnvironment.K8sDev)]
    [InlineData(ArivaEnvironment.K8sDemo)]
    public void Configure_Should_AskForNoCollectorAndNoKey_When_TheEnvironmentIsNotProduction(string environment)
    {
        var configure = () => Build(Settings(environment), environment);

        configure.Should().NotThrow("outside k8s-prd a collector is the environment's choice, and each process may draw its own key");
    }

    [Fact]
    public void Configure_Should_ApplyProductionsRules_When_OnlyTheSettingsNameK8sPrd()
    {
        var withoutCollector = () => Build(Settings(Production), environment: null);
        var withoutKey = () => Build(Settings(Production, "Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector), environment: null);

        withoutCollector.Should().Throw<InvalidOperationException>().WithMessage(ArivaLogging.NoCollector);
        withoutKey.Should().Throw<InvalidOperationException>().WithMessage($"{LogProtection.KeySetting} is not set*");
    }

    #endregion

    #region The pseudonym key in production

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Configure_Should_RefuseToBuild_When_ProductionHasNoPseudonymKey(string key)
    {
        string[] settings = key is null
            ? ["Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector]
            : ["Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector, $"{LogProtection.KeySetting}={key}"];
        var configuration = Settings(Production, settings);

        var configure = () => Build(configuration, Production);

        configure.Should().Throw<InvalidOperationException>()
            .WithMessage($"{LogProtection.KeySetting} is not set: in k8s-prd*Logging__PseudonymKey from a Kubernetes Secret*");
    }

    #endregion

    #region Loki sender credentials

    [Fact]
    public async Task Configure_Should_SendTheLokiCredentialsWithTheBatch_When_UsernameAndPasswordAreSet()
    {
        await using var loki = new LoopbackCollector();
        var configuration = Settings(ArivaEnvironment.VmLocal, "Loki:Enabled=true", $"Loki:Uri={loki.Address}", $"{ArivaLogging.LokiUsernameSetting}={Sender}",
            $"{ArivaLogging.LokiPasswordSetting}={Canary}");

        using (var logger = ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal).CreateLogger())
            logger.Information("Probe line for Loki");

        var request = await loki.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        request.RequestLine.Should().StartWith("POST /loki/api/v1/push ");
        request.Headers.Should().ContainKey("Authorization")
            .WhoseValue.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Sender}:{Canary}")), "HTTP basic authentication with the configured pair");
    }

    [Fact]
    public async Task Configure_Should_SendNoCredentials_When_NoneAreSet()
    {
        await using var loki = new LoopbackCollector();
        var configuration = Settings(ArivaEnvironment.VmLocal, "Loki:Enabled=true", $"Loki:Uri={loki.Address}");

        using (var logger = ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal).CreateLogger())
            logger.Information("Probe line for Loki");

        var request = await loki.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        request.RequestLine.Should().StartWith("POST /loki/api/v1/push ");
        request.Headers.Should().NotContainKey("Authorization");
    }

    [Theory]
    [InlineData(Production, ArivaHosts.ProductionCollector, Sender, null, "Loki:Username and Loki:Password are set together, or neither is set.")]
    [InlineData(Production, ArivaHosts.ProductionCollector, null, Canary, "Loki:Username and Loki:Password are set together, or neither is set.")]
    [InlineData(ArivaEnvironment.VmLocal, "http://127.0.0.1:9", "ariva:" + Canary, Canary, "Loki:Username must not contain a colon*")]
    [InlineData(ArivaEnvironment.K8sDev, "http://127.0.0.1:9", Sender, Canary, "*are sent over https only*")]
    [InlineData(ArivaEnvironment.K8sDemo, "http://127.0.0.1:9", Sender, Canary, "*are sent over https only*")]
    public void Configure_Should_RefuseTheLokiCredentials_When_TheyAreIncompleteMalformedOrWouldGoInClear(string environment, string uri, string username,
        string password, string message)
    {
        var configuration = Settings(environment, "Loki:Enabled=true", $"Loki:Uri={uri}", $"{ArivaLogging.LokiUsernameSetting}={username}",
            $"{ArivaLogging.LokiPasswordSetting}={password}", $"{LogProtection.KeySetting}={ArivaHosts.ProductionPseudonymKey}");

        var configure = () => Build(configuration, environment);

        configure.Should().Throw<InvalidOperationException>().WithMessage(message)
            .Which.Message.Should().NotContain(Canary, "the message names the settings, never a value").And.NotContain(Sender);
    }

    [Theory]
    [InlineData(ArivaEnvironment.VmLocal, "http://127.0.0.1:9")]
    [InlineData(ArivaEnvironment.K8sDev, ArivaHosts.ProductionCollector)]
    [InlineData(Production, ArivaHosts.ProductionCollector)]
    public void Configure_Should_AcceptTheLokiCredentials_When_TheEnvironmentGivesThemForHttps(string environment, string uri)
    {
        var configuration = Settings(environment, "Loki:Enabled=true", $"Loki:Uri={uri}", $"{ArivaLogging.LokiUsernameSetting}={Sender}",
            $"{ArivaLogging.LokiPasswordSetting}={Canary}", $"{LogProtection.KeySetting}={ArivaHosts.ProductionPseudonymKey}");

        var configure = () => Build(configuration, environment);

        configure.Should().NotThrow(environment == ArivaEnvironment.VmLocal ? "a developer's Loki may use http" : "https, from the environment");
    }

    [Fact]
    public void Configure_Should_RefuseTheLokiCredentials_When_TheyComeFromAFileOutsideVmLocal()
    {
        var file = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(file, $$"""{ "Loki": { "Username": "{{Sender}}", "Password": "{{Canary}}" } }""");
        IConfiguration FromFile(string environment, params string[] more) => new ConfigurationBuilder()
            .AddJsonFile(file)
            .AddInMemoryCollection(Pairs(environment, ["Loki:Enabled=true", "Loki:Uri=" + ArivaHosts.ProductionCollector,
                $"{LogProtection.KeySetting}={ArivaHosts.ProductionPseudonymKey}", .. more]))
            .Build();

        var production = () => Build(FromFile(Production), Production);
        var local = () => Build(FromFile(ArivaEnvironment.VmLocal), ArivaEnvironment.VmLocal);
        var overridden = () => Build(FromFile(Production, $"{ArivaLogging.LokiUsernameSetting}={Sender}", $"{ArivaLogging.LokiPasswordSetting}={Canary}"), Production);

        production.Should().Throw<InvalidOperationException>().WithMessage("*come from a configuration file*")
            .Which.Message.Should().NotContain(Canary);
        local.Should().NotThrow("a developer machine may keep them in a file");
        overridden.Should().NotThrow("the values the environment gives win, and they are not a file's");
    }

    #endregion

    #region The OTLP sink

    [Fact]
    public async Task Configure_Should_ShipOtlpLogsWhereTheHostsConfigurationSays_When_TheStandardVariablesAreSettings()
    {
        await using var collector = new LoopbackCollector();
        // Ariva's own endpoint is a closed port; the standard variables exist only in the configuration, not in the process
        // environment, so the batch reaches the collector only if the sink reads them from the configuration the guards judged.
        var configuration = Settings(ArivaEnvironment.VmLocal, "Otlp:Enabled=true", "Otlp:Endpoint=http://127.0.0.1:9",
            $"OTEL_EXPORTER_OTLP_ENDPOINT={collector.Address}", "OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf", $"OTEL_EXPORTER_OTLP_HEADERS=x-collector-key={Canary}");

        using (var logger = ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal).CreateLogger())
            logger.Information("Probe line for the OTLP collector");

        var request = await collector.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        request.RequestLine.Should().StartWith("POST /v1/logs ");
        request.Headers.Should().ContainKey("x-collector-key").WhoseValue.Should().Be(Canary, "OTEL_EXPORTER_OTLP_HEADERS carries a collector's sender credential");
    }

    #endregion

    #region Every host in production

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_ProductionShipsTheLogsNowhere(string host)
    {
        await using var app = ArivaHosts.Create(host, Production, builder => builder.UseSetting(ArivaLogging.LokiEnabledSetting, "false"));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage(ArivaLogging.NoCollector);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_Start_When_ProductionShipsTheLogsToLokiOverHttps(string host)
    {
        await using var app = ArivaHosts.Create(host, Production);

        Action start = () => app.CreateClient().Dispose();

        start.Should().NotThrow("the test hosts' k8s-prd settings give Loki over https and a key from outside any file");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_Start_When_ProductionShipsTheLogsToTheOtlpCollectorOverHttps(string host)
    {
        await using var app = ArivaHosts.Create(host, Production, builder => builder
            .UseSetting(ArivaLogging.LokiEnabledSetting, "false")
            .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", ArivaHosts.ProductionCollector));

        Action start = () => app.CreateClient().Dispose();

        start.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_ProductionSendsTheLogsToLokiInClear(string host)
    {
        await using var app = ArivaHosts.Create(host, Production, builder => builder.UseSetting(ArivaLogging.LokiUriSetting, "http://127.0.0.1:9"));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("Loki:Uri must be https in k8s-prd*");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_ProductionHasNoPseudonymKey(string host)
    {
        await using var app = ArivaHosts.Create(host, Production, builder => builder.UseSetting(LogProtection.KeySetting, string.Empty));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage($"{LogProtection.KeySetting} is not set*");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_ProductionsPseudonymKeyIsMalformed(string host)
    {
        await using var app = ArivaHosts.Create(host, Production, builder => builder.UseSetting(LogProtection.KeySetting, "AAECAwQFBgcICQoLDA0ODw=="));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage($"{LogProtection.KeySetting} must be base64 of at least 32 random bytes*")
            .Which.Message.Should().NotContain("AAECAwQFBgcICQoLDA0ODw==");
    }

    [Fact]
    public async Task Host_Should_Start_When_ProductionsLokiAuthenticatesTheSender()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, Production, builder => builder
            .UseSetting(ArivaLogging.LokiUsernameSetting, Sender)
            .UseSetting(ArivaLogging.LokiPasswordSetting, Canary));

        Action start = () => app.CreateClient().Dispose();

        start.Should().NotThrow("the pair comes from outside any file and goes over https");
    }

    #endregion

    #region The one-shot commands

    [Fact]
    public async Task MigrationJob_Should_ReachTheDatabase_When_ProductionHasNoCollectorAndNoKey()
    {
        using var database = new FakeDatabase();

        var exit = await RunAsync<Ariva.Api.Main._IAssemblyMark>(["--migrate", .. ProductionCommandLine(database)]);

        exit.Should().Be(1, "the stand-in database closes the connection, so the migration fails as a migration does");
        (await database.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeTrue("the migration started and connected: it asks for neither a collector nor a key");
    }

    [Fact]
    public async Task StoredNameCheck_Should_ReachTheDatabase_When_ProductionHasNoCollectorAndNoKey()
    {
        using var database = new FakeDatabase();

        var exit = await RunAsync<Ariva.Api.Main._IAssemblyMark>(["--check-stored-names", .. ProductionCommandLine(database)]);

        exit.Should().Be(1, "the stand-in database closes the connection, so the check fails as it would without its database");
        (await database.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeTrue("the check started and connected: it asks for neither a collector nor a key");
    }

    [Fact]
    public async Task TopicsJob_Should_Succeed_When_ProductionHasNoCollectorAndNoKey()
    {
        var exit = await RunAsync<Ariva.Api.Main._IAssemblyMark>(["--provision-topics", $"--environment={Production}", "--Kafka:Enabled=false"]);

        exit.Should().Be(0, "with Kafka off the job's only path ends in success, with no collector and no key in the settings");
    }

    [Fact]
    public async Task ReplayCommand_Should_ReachItsOwnArgumentCheck_When_ProductionHasNoCollectorAndNoKey()
    {
        var exit = await RunAsync<Ariva.Api.Stream._IAssemblyMark>(["--replay", $"--environment={Production}", $"--Database:RootCertificate={DatabaseCa()}"]);

        exit.Should().Be(2, "the replay refuses its missing arguments itself (exit 2), so it started without a collector or a key");
    }

    [Theory]
    [InlineData("Main", "DatabaseMigration.RunAsync", "TopicProvisioning.RunAsync", "BreakGlassCommand.RunAsync", "StoredNameCheckCommand.RunAsync")]
    [InlineData("Ingest")]
    [InlineData("Stream", "ReplayCommand.RunAsync")]
    [InlineData("Cronz")]
    [InlineData("Integration")]
    public void Program_Should_RunEveryOneShotCommandBeforeTheHostsLogger_When_ItStarts(string host, params string[] commands)
    {
        var lines = File.ReadAllLines(RepositoryPaths.Resolve($"Platform/Backplane/Ariva.Api.{host}/Program.cs"));
        var logging = Array.FindIndex(lines, line => line.Trim() == "builder.AddAppLogging();");
        var started = lines.Select((line, index) => (Line: line.Trim(), Index: index)).Where(l => OneShotReturn().IsMatch(l.Line)).ToList();

        logging.Should().BePositive("every Backplane host builds its logger with AddAppLogging");
        started.Select(s => OneShotReturn().Match(s.Line).Groups["command"].Value).Should().BeEquivalentTo(commands);
        started.Where(s => s.Index > logging).Should().BeEmpty(
            "a one-shot command returns before the host's logger is built, so the collector and key guards never stop the Jobs (log inventory, section 2.3)");
    }

    #endregion

    #region Helpers

    private static void Build(IConfiguration configuration, string environment) =>
        ArivaLogging.Configure(new LoggerConfiguration(), configuration, environment).CreateLogger().Dispose();

    /// <summary>Settings as written in the theories, <c>key=value</c> (split at the first '='), with Application:Environment.</summary>
    private static IConfiguration Settings(string environment, params string[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(Pairs(environment, settings)).Build();

    private static Dictionary<string, string> Pairs(string environment, string[] settings)
    {
        var pairs = new Dictionary<string, string> { ["Application:Environment"] = environment };
        foreach (var setting in settings)
        {
            var equals = setting.IndexOf('=', StringComparison.Ordinal);
            pairs[setting[..equals]] = setting[(equals + 1)..];
        }

        return pairs;
    }

    /// <summary>A one-shot command's start in k8s-prd: its database is the stand-in, pinned to a CA there is a file for; no collector, no key.</summary>
    private string[] ProductionCommandLine(FakeDatabase database) =>
    [
        $"--environment={Production}",
        "--Database:Host=127.0.0.1",
        $"--Database:Port={database.Port}",
        $"--Database:RootCertificate={DatabaseCa()}",
        "--Kafka:Enabled=false"
    ];

    /// <summary>A CA certificate (PEM) in the test's folder, for the pinned database CA the k8s-prd commands check before they connect.</summary>
    private string DatabaseCa()
    {
        var path = Path.Combine(_folder, "database-ca.crt");
        if (File.Exists(path))
            return path;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ariva test database CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }

    /// <summary>
    /// Runs a host's entry point as its Job or an operator starts it, in this process, and returns its exit code. A command
    /// that went on to start the web host instead would not return, so the run is bounded.
    /// </summary>
    private static async Task<int> RunAsync<TEntryPoint>(string[] args)
    {
        var entry = typeof(TEntryPoint).Assembly.EntryPoint ?? throw new InvalidOperationException("The host assembly has no entry point.");
        try
        {
            var result = await Task.Run(() => entry.Invoke(null, [args]), Ct).WaitAsync(TimeSpan.FromMinutes(1), Ct);
            return result switch
            {
                int code => code,
                Task<int> task => await task,
                _ => throw new InvalidOperationException("The entry point returned no exit code.")
            };
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw new InvalidOperationException($"The command threw instead of returning an exit code: {e.InnerException.Message}", e.InnerException);
        }
    }

    /// <summary>A line of Program.cs that returns a one-shot command's exit code, for example <c>return await DatabaseMigration.RunAsync(builder.Configuration);</c>.</summary>
    [GeneratedRegex(@"^return\s+await\s+(?<command>\w+\.RunAsync)\(", RegexOptions.CultureInvariant)]
    private static partial Regex OneShotReturn();

    #endregion

    #region Types

    /// <summary>
    /// Stands in for the database: accepts the first connection and closes it at once, so a command that reaches it fails
    /// straight away (no TLS answer) and the test knows it got that far.
    /// </summary>
    private sealed class FakeDatabase : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<bool> _reached;

        public FakeDatabase()
        {
            _listener.Start();
            _reached = AcceptOnceAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>True once a connection came in; false when the listener stopped first.</summary>
        public Task<bool> Reached => _reached;

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task<bool> AcceptOnceAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                return true;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return false;
            }
        }
    }

    #endregion
}
