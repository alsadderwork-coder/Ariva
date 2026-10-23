using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Ariva.AppHost;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-066: Ariva.AppHost's application model, built in process (the testing builder also starts DCP in the background,
/// see <see cref="VariablesAsync"/>). Every host runs in vm-local
/// with URL query redaction kept on, gets its dependencies as the configuration parts it reads, and waits for its
/// readiness probe; the simulator holds only its key's digest; the E2E switches and the host variables file apply.
/// ARV-139c: the development-only site is off for api-main in every run and cannot be turned on by the host variables file
/// or the AppHost's own Seed settings; only the AppHost's switch turns it on, with the separate database volume and the
/// demo accounts' sign-in variables only, and never together with the E2E or scripted-demo settings.
/// ARV-092b: the pinned, offline ClamAV container with Ariva's clamd.conf read-only, ready only once clamd answers PONG, and
/// api-main, the only host that takes uploads, the only one given it.
/// ARV-164: these tests need no container runtime. Aspire only logs that it is missing (<see cref="BuildAsync"/>), and every
/// read of a resource's variables is bounded (<see cref="VariablesAsync"/>), so nothing here can hang the unit run.
/// </summary>
public sealed class AppHostModelTests
{
    private const string SimulatorKey = "simulator-key-for-the-model-test";

    private static readonly string[] Projects = ["api-main", "api-ingest", "api-stream", "api-cronz", "api-integration", "simulation"];

    private static async Task<DistributedApplication> BuildAsync(params string[] extra)
    {
        string[] args =
        [
            "--Parameters:database-owner-password=owner-password-for-tests",
            "--Parameters:database-runtime-password=runtime-password-for-tests",
            "--Parameters:database-readonly-password=readonly-password-for-tests",
            "--Parameters:database-validation-reader-password=validation-reader-password-for-tests",
            "--Parameters:redis-password=redis-password-for-tests",
            $"--Parameters:simulation-operator-key={SimulatorKey}",
            "--AppHost:Web=false",
            // ARV-164: no wait for a container runtime that does not answer, so Aspire logs it instead of failing the start
            // before DCP allocates the endpoints (VariablesAsync). A runtime that answers is used as before.
            "--DcpPublisher:ContainerRuntimeInitializationTimeout=00:00:00",
            .. extra
        ];
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Ariva_AppHost>(args, TestContext.Current.CancellationToken);
        return await builder.BuildAsync(TestContext.Current.CancellationToken);
    }

    private static IEnumerable<IResource> Resources(DistributedApplication app) =>
        app.Services.GetRequiredService<DistributedApplicationModel>().Resources;

    #region Reading a resource's variables (ARV-164)

    // The testing builder lets the AppHost's own Run go ahead once the model is built, so DCP starts in the background, and
    // a variable built from an endpoint (Database__Host, Kafka__BootstrapServers, Redis__ConnectionString, Email__Smtp__Host,
    // MalwareScan__Host) resolves only once DCP has allocated that endpoint. DCP allocates endpoints without a container
    // runtime, but before it runs Aspire waits DcpPublisher:ContainerRuntimeInitializationTimeout (30 s under the testing
    // builder) for one that does not answer and then fails the start; the read, to which GetEnvironmentVariableValuesAsync
    // passes no cancellation token, then waited for ever. BuildAsync turns that wait off. Should DCP still allocate nothing,
    // the read is bounded, and ends with the start's own error when the start fails first. Aspire leaves out a variable that
    // fails to resolve (cancelled by the bound, or a failed endpoint) and reports it only in the result's Exception, so a
    // read with one is refused: an incomplete set would let NotContain and NotContainKey pass on nothing.

    private static readonly TimeSpan AllocationBound = TimeSpan.FromSeconds(60);

    // A start can also fail after DCP has allocated the endpoints; the read then completes, and gets this long to do so.
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(2);

    private static async Task<Dictionary<string, string>> VariablesAsync(DistributedApplication app, IResource resource)
    {
        string Unallocated() => $"DCP allocated no endpoint for the variables of {resource.Name} within {AllocationBound.TotalSeconds:0} s, and the AppHost's start had not failed.";

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(AllocationBound);
        var read = ExecutionConfigurationBuilder.Create(resource).WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance, bound.Token);
        // Under the testing builder this starts nothing of its own: it waits for the start the AppHost's Run began, and
        // fails with that start's error.
        var started = app.StartAsync(bound.Token);
        try
        {
            await Task.WhenAny(read, started);
            if (started.IsFaulted && await Task.WhenAny(read, Task.Delay(ReadGrace, bound.Token)) != read)
                Assert.Fail($"The AppHost did not start, so DCP allocated no endpoint for the variables of {resource.Name}: {started.Exception.GetBaseException().Message}");

            var result = await read;
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (bound.IsCancellationRequested)
                Assert.Fail(Unallocated());
            if (result.Exception is not null)
                Assert.Fail($"A variable of {resource.Name} did not resolve: {result.Exception.GetBaseException().Message}");
            return result.EnvironmentVariables.ToDictionary();
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail(Unallocated());
            throw;
        }
        finally
        {
            await bound.CancelAsync();
            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await started.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    #endregion

    [Fact]
    public async Task Hosts_Should_RunInVmLocal_WithRedactionOn_AndTheirDependencies()
    {
        await using var app = await BuildAsync();
        var projects = Resources(app).OfType<ProjectResource>().ToList();
        projects.Select(p => p.Name).Should().BeEquivalentTo(Projects);

        foreach (var project in projects)
        {
            var variables = await VariablesAsync(app, project);
            variables["DOTNET_ENVIRONMENT"].Should().Be("vm-local", project.Name);
            variables["ASPNETCORE_ENVIRONMENT"].Should().Be("vm-local", project.Name);
            variables["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"].Should().Be("false", "access_token never reaches a span ({0})", project.Name);
            variables["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"].Should().Be("false", project.Name);
            project.Annotations.OfType<HealthCheckAnnotation>().Should().NotBeEmpty("{0} is ready only when its readiness probe says so", project.Name);
            // dotnet run would take --environment as its own option: the environment comes from the variables only.
            (await ArgumentsAsync(project)).Should().NotContain("--environment", project.Name);
        }

        var main = await VariablesAsync(app, projects.Single(p => p.Name == "api-main"));
        main["Database__Name"].Should().Be("ariva");
        main["Database__Username"].Should().Be("ariva_app");
        main["Database__Password"].Should().Be("runtime-password-for-tests");
        main["Database__Migration__Username"].Should().Be("ariva");
        main["Database__Migration__Password"].Should().Be("owner-password-for-tests");
        main["Kafka__Enabled"].Should().Be("true");
        main["Redis__Enabled"].Should().Be("true");
        main["Redis__ConnectionString"].Should().Contain("password=redis-password-for-tests");
        main["Email__Smtp__Security"].Should().Be("None");
        main["Seed__NbjSite"].Should().Be("false", "the development-only NBJ-BC1 site is never seeded under the AppHost (ARV-139c)");

        // ARV-104g1: the validation reader login reaches api-main (the validation service) and no other host.
        main["Database__ValidationReader__Username"].Should().Be("ariva_validation");
        main["Database__ValidationReader__Password"].Should().Be("validation-reader-password-for-tests");
        foreach (var project in projects.Where(p => p.Name != "api-main"))
            (await VariablesAsync(app, project)).Keys.Should().NotContain(k => k.StartsWith("Database__ValidationReader__", StringComparison.Ordinal), project.Name);

        // ARV-092b: api-main takes the uploads, so it alone is given the malware scanner, and starts once clamd answers.
        main["MalwareScan__Host"].Should().NotBeNullOrWhiteSpace();
        main["MalwareScan__Port"].Should().NotBeNullOrWhiteSpace();
        projects.Single(p => p.Name == "api-main").Annotations.OfType<WaitAnnotation>().Should().Contain(w => w.Resource.Name == "clamav");
        foreach (var project in projects.Where(p => p.Name != "api-main"))
            (await VariablesAsync(app, project)).Keys.Should().NotContain(k => k.StartsWith("MalwareScan__", StringComparison.Ordinal), project.Name);
    }

    [Fact]
    public async Task Clamav_Should_RunThePinnedImage_Offline_WithTheRepositorysConfReadOnly()
    {
        await using var app = await BuildAsync();
        var clamav = Resources(app).OfType<ContainerResource>().Single(r => r.Name == "clamav");
        var image = clamav.Annotations.OfType<ContainerImageAnnotation>().Single();

        $"{image.Registry}/{image.Image}@sha256:{image.SHA256}".Should().Be(
            "docker.io/clamav/clamav@sha256:3bba6dd5ac6663d55b80f86a3126e146612d93b593cc2617188d13f637d91260",
            "the digest of docker-compose.dev.yml, CI and the integration tests (scripts/base-images.mjs)");
        (await VariablesAsync(app, clamav))["CLAMAV_NO_FRESHCLAMD"].Should().Be("true", "no mirror traffic, the same verdicts in every run");
        var conf = clamav.Annotations.OfType<ContainerMountAnnotation>().Should().ContainSingle(m => m.Target == "/etc/clamav/clamd.conf").Which;
        conf.Type.Should().Be(ContainerMountType.BindMount);
        conf.IsReadOnly.Should().BeTrue("Ariva's limits, which the container cannot change");
        conf.Source.Should().EndWith(Path.Combine("Platform", "Cloud", "clamav", "clamd.conf"));
        File.Exists(conf.Source).Should().BeTrue(conf.Source);
        var endpoint = clamav.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "clamd");
        endpoint.TargetPort.Should().Be(3310);
        endpoint.Port.Should().Be(13310, "docker-compose.dev.yml's port");
        clamav.Annotations.OfType<HealthCheckAnnotation>().Select(h => h.Key).Should().Contain(ClamdPing.HealthCheckKey, "api-main waits until clamd answers, not until the container runs");
    }

    [Theory]
    [InlineData("PONG\0", HealthStatus.Healthy)]
    [InlineData("UNKNOWN COMMAND\0", HealthStatus.Unhealthy)]
    [InlineData("", HealthStatus.Unhealthy)]
    public async Task ClamdPing_Should_BeHealthy_OnlyWhenClamdAnswersPong(string answer, HealthStatus expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = client.GetStream();
            var request = new byte[6];
            await stream.ReadExactlyAsync(request, TestContext.Current.CancellationToken);
            Encoding.ASCII.GetString(request).Should().Be("zPING\0");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var result = await ClamdPing.CheckAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
        await server;

        result.Status.Should().Be(expected);
    }

    [Fact]
    public async Task ClamdPing_Should_BeUnhealthy_When_NothingListens()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        (await ClamdPing.CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Simulator_Should_HoldOnlyItsKeysDigest()
    {
        await using var app = await BuildAsync();
        var variables = await VariablesAsync(app, Resources(app).Single(r => r.Name == "simulation"));

        variables["Simulation__Control__Keys__0__Sha256"].Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SimulatorKey))));
        variables.Values.Should().NotContain(SimulatorKey);
        variables.Should().ContainKey("Simulation__Aman__Kafka__BootstrapServers", "the AMAN mimic publishes to the AppHost's broker");
        variables.Keys.Should().NotContain(k => k.StartsWith("Database__", StringComparison.Ordinal), "the simulator never touches the database");
    }

    [Fact]
    public async Task Timescale_Should_UseTheComposeImage_WithItsDataVolume()
    {
        await using var app = await BuildAsync();
        var timescale = Resources(app).OfType<ContainerResource>().Single(r => r.Name == "timescaledb");
        var image = timescale.Annotations.OfType<ContainerImageAnnotation>().Single();

        // ARV-096: pinned by the chart's digest, which Aspire runs as image@sha256 (setting the digest clears the tag).
        $"{image.Registry}/{image.Image}@sha256:{image.SHA256}".Should().Be(
            "docker.io/timescale/timescaledb-ha@sha256:2fcc39a5d4c8a65f58691ef92c7819df5773db72397b2dd8493f114659b519c2",
            "the image and digest of the chart, docker-compose.dev.yml and the integration tests");
        image.Tag.Should().BeNull("Aspire runs the digest, not the tag");
        timescale.Annotations.OfType<ContainerMountAnnotation>().Should().Contain(m => m.Source == "ariva-apphost-timescaledb" && m.Target == "/home/postgres/pgdata");
        timescale.Annotations.OfType<ContainerLifetimeAnnotation>().Single().Lifetime.Should().Be(ContainerLifetime.Persistent);
    }

    [Fact]
    public async Task E2eSwitches_Should_DropStream_AndKeepNoData()
    {
        await using var app = await BuildAsync("--AppHost:Stream=false", "--AppHost:Persistent=false", "--AppHost:DatabaseVolume=", "--AppHost:ClamdPort=23310");
        Resources(app).Select(r => r.Name).Should().NotContain("api-stream").And.NotContain("web");
        var timescale = Resources(app).OfType<ContainerResource>().Single(r => r.Name == "timescaledb");
        timescale.Annotations.OfType<ContainerMountAnnotation>().Should().NotContain(m => m.Target == "/home/postgres/pgdata");
        timescale.Annotations.OfType<ContainerLifetimeAnnotation>().Single().Lifetime.Should().Be(ContainerLifetime.Session);
        // ARV-092b: scripts/e2e-apphost.mjs runs its clamd apart from the development stack's.
        Resources(app).OfType<ContainerResource>().Single(r => r.Name == "clamav").Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "clamd")
            .Port.Should().Be(23310);
    }

    [Fact]
    public async Task HostEnvironmentFile_Should_ApplyLast_To_TheNamedResource()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ariva-apphost-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """{"api-cronz": {"Cronz__Dashboard__Enabled": "true", "Email__Smtp__Port": "25251"}}""", TestContext.Current.CancellationToken);
        try
        {
            await using var app = await BuildAsync($"--AppHost:HostEnvironmentFile={file}");
            var cronz = await VariablesAsync(app, Resources(app).Single(r => r.Name == "api-cronz"));
            cronz["Cronz__Dashboard__Enabled"].Should().Be("true");
            cronz["Email__Smtp__Port"].Should().Be("25251", "the file wins over the AppHost's own wiring");
            (await VariablesAsync(app, Resources(app).Single(r => r.Name == "api-main"))).Should().NotContainKey("Cronz__Dashboard__Enabled");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("""{"api-main": {"BAD NAME": "x"}}""", "not an environment variable name")]
    [InlineData("""{"api-main": {"PATH=/tmp;X": "x"}}""", "not an environment variable name")]
    [InlineData("""{"api-main": {"OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION": "true"}}""", "is refused")]
    [InlineData("""{"api-main": {"DOTNET_STARTUP_HOOKS": "/tmp/hook.dll"}}""", "is refused")]
    [InlineData("""{"simulation": {"ASPNETCORE_ENVIRONMENT": "k8s-prd"}}""", "is refused")]
    [InlineData("""{"api-main": {"ld_preload": "/tmp/x.so"}}""", "is refused")]
    public void HostEnvironmentFile_Should_RefuseWhatIsNotAVariableName(string json, string reason)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ariva-apphost-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        try
        {
            var settings = AppHostSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["AppHost:HostEnvironmentFile"] = file }).Build());
            settings.Invoking(s => s.ReadHostEnvironment()).Should().Throw<InvalidOperationException>().WithMessage($"*{reason}*");
        }
        finally
        {
            File.Delete(file);
        }
    }

    #region The development-only site (ARV-139c)

    private static string Volume(DistributedApplication app) =>
        Resources(app).OfType<ContainerResource>().Single(r => r.Name == "timescaledb").Annotations.OfType<ContainerMountAnnotation>()
            .Single(m => m.Target == "/home/postgres/pgdata").Source;

    private static async Task<string> TempFileAsync(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ariva-apphost-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, json, TestContext.Current.CancellationToken);
        return file;
    }

    [Fact]
    public async Task DevelopmentSite_Should_StayOff_When_TheHostVariablesFileOrTheAppHostsSeedSettingsSayOn()
    {
        var file = await TempFileAsync("""{"api-main": {"Seed__NbjSite": "true"}}""");
        try
        {
            await using var app = await BuildAsync($"--AppHost:HostEnvironmentFile={file}", "--Seed:NbjSite=true");
            (await VariablesAsync(app, Resources(app).Single(r => r.Name == "api-main")))["Seed__NbjSite"]
                .Should().Be("false", "the pin comes after the host variables file, and only the AppHost's own switch decides");
            Volume(app).Should().Be("ariva-apphost-timescaledb");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task DevelopmentSite_Should_TurnOnForApiMainOnly_WithTheSeparateVolumeAndTheAccounts_When_TheSwitchIsOn()
    {
        var file = await TempFileAsync("""
            {"api-main": {"Auth__TotpRequired": "false", "Auth__DevelopmentUsers__0__UserName": "demo.admin", "Auth__DevelopmentUsers__0__Password": "pw-for-tests",
                          "Auth__DevelopmentUsers__0__Roles__0": "SystemAdministrator", "Auth__DevelopmentUsers__0__Sites__0": "*", "Auth__DevelopmentUsers__0__TotpSecret": "JBSWY3DPEHPK3PXP"},
             "simulation": {"Simulation__Control__Keys__1__Name": "demo-local"}}
            """);
        try
        {
            await using var app = await BuildAsync("--AppHost:NbjSite=true", $"--AppHost:AccountsFile={file}");
            var projects = Resources(app).OfType<ProjectResource>().ToList();
            var main = await VariablesAsync(app, projects.Single(p => p.Name == "api-main"));

            main["Seed__NbjSite"].Should().Be("true");
            main["Auth__DevelopmentUsers__0__UserName"].Should().Be("demo.admin", "the demo accounts still sign in");
            main["Auth__TotpRequired"].Should().Be("false");
            foreach (var project in projects.Where(p => p.Name != "api-main"))
                (await VariablesAsync(app, project)).Keys.Should().NotContain(k => k.StartsWith("Seed__", StringComparison.Ordinal) || k.StartsWith("Auth__DevelopmentUsers__", StringComparison.Ordinal), project.Name);
            (await VariablesAsync(app, projects.Single(p => p.Name == "simulation"))).Should().NotContainKey("Simulation__Control__Keys__1__Name", "the scripted demo cannot drive this run");
            Volume(app).Should().Be("ariva-apphost-timescaledb-nbj", "the usual volume never holds the site's rows");
            Resources(app).OfType<ContainerResource>().Single(r => r.Name == "timescaledb").Annotations.OfType<ContainerLifetimeAnnotation>().Single().Lifetime
                .Should().Be(ContainerLifetime.Persistent);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task DevelopmentSite_Should_RefuseToBuild_When_CombinedWithTheE2eOrTheScriptedDemoSettings()
    {
        var file = await TempFileAsync("""{"api-main": {"Auth__TotpRequired": "false"}}""");
        try
        {
            foreach (var (args, reason) in new (string[] Args, string Reason)[]
                     {
                         (new[] { "--AppHost:NbjSite=true", $"--AppHost:HostEnvironmentFile={file}" }, "AppHost:HostEnvironmentFile"),
                         (new[] { "--AppHost:NbjSite=true", "--AppHost:Persistent=false", "--AppHost:DatabaseVolume=", "--AppHost:Stream=false" }, "AppHost:Persistent false"),
                         (new[] { $"--AppHost:AccountsFile={file}" }, "only for a run with")
                     })
            {
                var act = async () => { await using var app = await BuildAsync(args); };
                (await act.Should().ThrowAsync<Exception>(reason)).Which.ToString().Should().Contain(reason);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("AppHost:HostEnvironmentFile", "x.json", "AppHost:HostEnvironmentFile")]
    [InlineData("AppHost:Persistent", "false", "AppHost:Persistent false")]
    [InlineData("AppHost:DatabaseVolume", "", "needs a database volume")]
    public void EnsureConsistent_Should_RefuseTheSwitch_When_CombinedWith(string key, string value, string reason)
    {
        var settings = AppHostSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            [AppHostSettings.DevelopmentSiteKey] = "true",
            [key] = value
        }).Build());

        settings.Invoking(s => s.EnsureConsistent()).Should().Throw<InvalidOperationException>().WithMessage($"*{reason}*");
    }

    [Theory]
    [InlineData("""{"api-main": {"Seed__NbjSite": "true"}}""", "Seed__NbjSite for api-main is refused")]
    [InlineData("""{"api-main": {"Database__Host": "elsewhere"}}""", "Database__Host for api-main is refused")]
    [InlineData("""{"api-main": {"Auth__DevelopmentUsers__0__Password__x": "x"}}""", "is refused")]
    [InlineData("""{"simulation": {"Simulation__Control__Keys__1__Name": "demo-local"}}""", "no api-main accounts")]
    [InlineData("""{"api-main": {"AUTH__TOTPREQUIRED": "false"}}""", "AUTH__TOTPREQUIRED for api-main is refused")] // names in another letter case
    [InlineData("""{"api-main": {"auth__DevelopmentUsers__0__UserName": "demo.admin"}}""", "auth__DevelopmentUsers__0__UserName for api-main is refused")]
    [InlineData("""{"api-main": {"Auth__developmentusers__0__password": "x"}}""", "is refused")]
    [InlineData("""{"API-MAIN": {"Auth__TotpRequired": "false"}}""", "no api-main accounts")]
    public void ReadAccounts_Should_RefuseAnythingButSignInVariablesForApiMain(string json, string reason)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ariva-apphost-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        try
        {
            var settings = AppHostSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                [AppHostSettings.DevelopmentSiteKey] = "true",
                ["AppHost:AccountsFile"] = file
            }).Build());

            settings.Invoking(s => s.ReadAccounts()).Should().Throw<InvalidOperationException>().WithMessage($"*{reason}*");
        }
        finally
        {
            File.Delete(file);
        }
    }

    #endregion

    private static async Task<List<string>> ArgumentsAsync(IResource resource)
    {
        var arguments = new List<string>();
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var callbacks))
        {
            var collected = new List<object>();
            var context = new CommandLineArgsCallbackContext(collected, resource);
            foreach (var callback in callbacks)
                await callback.Callback(context);
            arguments.AddRange(collected.Select(a => a?.ToString() ?? string.Empty));
        }

        return arguments;
    }
}
