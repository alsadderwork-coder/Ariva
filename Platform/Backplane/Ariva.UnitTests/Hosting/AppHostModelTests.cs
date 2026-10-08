using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Ariva.AppHost;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-066: Ariva.AppHost's application model, built in process without starting anything. Every host runs in vm-local
/// with URL query redaction kept on, gets its dependencies as the configuration parts it reads, and waits for its
/// readiness probe; the simulator holds only its key's digest; the E2E switches and the host variables file apply.
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
            .. extra
        ];
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Ariva_AppHost>(args, TestContext.Current.CancellationToken);
        return await builder.BuildAsync(TestContext.Current.CancellationToken);
    }

    private static IEnumerable<IResource> Resources(DistributedApplication app) =>
        app.Services.GetRequiredService<DistributedApplicationModel>().Resources;

    private static async Task<Dictionary<string, string>> VariablesAsync(IResource resource) =>
        await ((IResourceWithEnvironment)resource).GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);

    [Fact]
    public async Task Hosts_Should_RunInVmLocal_WithRedactionOn_AndTheirDependencies()
    {
        await using var app = await BuildAsync();
        var projects = Resources(app).OfType<ProjectResource>().ToList();
        projects.Select(p => p.Name).Should().BeEquivalentTo(Projects);

        foreach (var project in projects)
        {
            var variables = await VariablesAsync(project);
            variables["DOTNET_ENVIRONMENT"].Should().Be("vm-local", project.Name);
            variables["ASPNETCORE_ENVIRONMENT"].Should().Be("vm-local", project.Name);
            variables["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"].Should().Be("false", "access_token never reaches a span ({0})", project.Name);
            variables["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"].Should().Be("false", project.Name);
            project.Annotations.OfType<HealthCheckAnnotation>().Should().NotBeEmpty("{0} is ready only when its readiness probe says so", project.Name);
            // dotnet run would take --environment as its own option: the environment comes from the variables only.
            (await ArgumentsAsync(project)).Should().NotContain("--environment", project.Name);
        }

        var main = await VariablesAsync(projects.Single(p => p.Name == "api-main"));
        main["Database__Name"].Should().Be("ariva");
        main["Database__Username"].Should().Be("ariva_app");
        main["Database__Password"].Should().Be("runtime-password-for-tests");
        main["Database__Migration__Username"].Should().Be("ariva");
        main["Database__Migration__Password"].Should().Be("owner-password-for-tests");
        main["Kafka__Enabled"].Should().Be("true");
        main["Redis__Enabled"].Should().Be("true");
        main["Redis__ConnectionString"].Should().Contain("password=redis-password-for-tests");
        main["Email__Smtp__Security"].Should().Be("None");

        // ARV-104g1: the validation reader login reaches api-main (the validation service) and no other host.
        main["Database__ValidationReader__Username"].Should().Be("ariva_validation");
        main["Database__ValidationReader__Password"].Should().Be("validation-reader-password-for-tests");
        foreach (var project in projects.Where(p => p.Name != "api-main"))
            (await VariablesAsync(project)).Keys.Should().NotContain(k => k.StartsWith("Database__ValidationReader__", StringComparison.Ordinal), project.Name);
    }

    [Fact]
    public async Task Simulator_Should_HoldOnlyItsKeysDigest()
    {
        await using var app = await BuildAsync();
        var variables = await VariablesAsync(Resources(app).Single(r => r.Name == "simulation"));

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

        $"{image.Image}:{image.Tag}".Should().Be("timescale/timescaledb-ha:pg17-ts2.30", "the image of docker-compose.dev.yml and the integration tests");
        timescale.Annotations.OfType<ContainerMountAnnotation>().Should().Contain(m => m.Source == "ariva-apphost-timescaledb" && m.Target == "/home/postgres/pgdata");
        timescale.Annotations.OfType<ContainerLifetimeAnnotation>().Single().Lifetime.Should().Be(ContainerLifetime.Persistent);
    }

    [Fact]
    public async Task E2eSwitches_Should_DropStream_AndKeepNoData()
    {
        await using var app = await BuildAsync("--AppHost:Stream=false", "--AppHost:Persistent=false", "--AppHost:DatabaseVolume=");
        Resources(app).Select(r => r.Name).Should().NotContain("api-stream").And.NotContain("web");
        var timescale = Resources(app).OfType<ContainerResource>().Single(r => r.Name == "timescaledb");
        timescale.Annotations.OfType<ContainerMountAnnotation>().Should().NotContain(m => m.Target == "/home/postgres/pgdata");
        timescale.Annotations.OfType<ContainerLifetimeAnnotation>().Single().Lifetime.Should().Be(ContainerLifetime.Session);
    }

    [Fact]
    public async Task HostEnvironmentFile_Should_ApplyLast_To_TheNamedResource()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ariva-apphost-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """{"api-cronz": {"Cronz__Dashboard__Enabled": "true", "Email__Smtp__Port": "25251"}}""", TestContext.Current.CancellationToken);
        try
        {
            await using var app = await BuildAsync($"--AppHost:HostEnvironmentFile={file}");
            var cronz = await VariablesAsync(Resources(app).Single(r => r.Name == "api-cronz"));
            cronz["Cronz__Dashboard__Enabled"].Should().Be("true");
            cronz["Email__Smtp__Port"].Should().Be("25251", "the file wins over the AppHost's own wiring");
            (await VariablesAsync(Resources(app).Single(r => r.Name == "api-main"))).Should().NotContainKey("Cronz__Dashboard__Enabled");
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
