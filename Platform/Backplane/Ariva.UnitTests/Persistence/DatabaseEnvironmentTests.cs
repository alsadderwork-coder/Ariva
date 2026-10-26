using Ariva.Di.Extensions;
using Ariva.Infra.Timescale;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-180 (CWE-269): a vm-local process, which migrates the database it is given and seeds the development accounts, refuses a
/// database that records another environment; nothing else is refused by the record. The record's name is the script's own
/// rule, and a vm-local host that seeds without migrating gets the start-up guard before every other hosted service.
/// Against PostgreSQL: <c>Ariva.IntegrationTests.Persistence.DatabaseEnvironmentTests</c>.
/// </summary>
public sealed class DatabaseEnvironmentTests
{
    [Theory]
    [InlineData("vm-local", "k8s-dev", true)]
    [InlineData("vm-local", "k8s-prd", true)]
    [InlineData("vm-local", "it", true)]
    [InlineData("vm-local", "vm-local", false)]
    [InlineData("vm-local", null, false)] // nothing recorded yet, or a database without script 0064
    [InlineData("k8s-dev", "k8s-prd", false)] // a cluster's job records itself; only vm-local is refused
    [InlineData("k8s-prd", "vm-local", false)]
    [InlineData(null, "k8s-dev", false)]
    [InlineData("VM-LOCAL", "k8s-dev", false)] // exact, as every other vm-local check
    public void ProblemWith_Should_RefuseOnlyAVmLocalProcess_When_TheDatabaseRecordsAnotherEnvironment(string environment, string recorded, bool refused)
    {
        var problem = new DatabaseEnvironment(environment).ProblemWith(recorded);

        if (refused)
            problem.Should().Contain($"'{recorded}'").And.Contain("vm-local").And.Contain("ARV-180").And.NotContain("Password");
        else
            problem.Should().BeNull();
    }

    [Theory]
    [InlineData("vm-local", true)]
    [InlineData("k8s-prd", true)]
    [InlineData("it", true)]
    [InlineData("a", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)] // 32
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)] // 33
    [InlineData("-vm", false)]
    [InlineData("K8s-prd", false)]
    [InlineData("k8s_prd", false)]
    [InlineData("k8s-prd\n", false)]
    [InlineData("k8s prd", false)]
    [InlineData("", false)]
    public void IsValidName_Should_FollowTheScriptsCheck_When_AnEnvironmentIsRecorded(string name, bool valid)
    {
        DatabaseEnvironment.IsValidName(name).Should().Be(valid);
        var check = () => new DatabaseEnvironment(name).EnsureValid();
        if (valid || name.Length == 0)
            check.Should().NotThrow("an empty name is no environment, and nothing is recorded for it");
        else
            check.Should().Throw<InvalidOperationException>().WithMessage("Application:Environment must be*ARV-180*");
    }

    [Theory]
    [InlineData("vm-local", "false", true, false, true)]   // seeds the development accounts
    [InlineData("vm-local", "false", false, true, true)]   // seeds the demo topology
    [InlineData("vm-local", "true", false, false, true)]   // migrates: the guard before the migration
    [InlineData("vm-local", "false", false, false, false)] // writes nothing at start (the unit tests' own hosts)
    [InlineData("k8s-dev", "false", true, true, false)]    // development accounts are refused outside vm-local anyway
    [InlineData(null, "true", false, false, false)]        // the integration tests' own processes name no environment
    public void AddArivaPersistence_Should_PutTheGuardFirst_When_AVmLocalHostWritesToItsDatabaseAtStart(string environment, string schemaUpdate, bool users,
        bool demo, bool guarded)
    {
        var hosted = new ServiceCollection().AddArivaPersistence(Configuration(environment, schemaUpdate, users, demo))
            .Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();

        if (guarded)
            hosted.First().Should().Be<DevelopmentDatabaseGuard>("it is the first hosted service, and it checks in StartingAsync, before any hosted service starts");
        else
            hosted.Should().NotContain(typeof(DevelopmentDatabaseGuard));
        if (schemaUpdate == "true" && guarded)
            hosted[1].Should().Be<DevelopmentMigrationService>("the migration, which checks the record again before any script, comes next");
    }

    [Fact]
    public void Guard_Should_CheckBeforeAnyHostedServiceStarts_When_TheHostStarts()
    {
        // The review's finding H1: a check in StartAsync stops a seed registered after it only while the services start one
        // after another. A lifecycle service's StartingAsync runs before any hosted service's StartAsync, in either start mode.
        typeof(IHostedLifecycleService).IsAssignableFrom(typeof(DevelopmentDatabaseGuard)).Should().BeTrue();
    }

    [Fact]
    public void Host_Should_StartItsServicesOneAfterAnother_When_TheConfigurationAsksForThemAtOnce()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string> { ["servicesStartConcurrently"] = "true" });
        builder.Services.AddArivaPersistence(Configuration("vm-local", "false", false, false));
        using var host = builder.Build();

        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ServicesStartConcurrently.Should()
            .BeFalse("the start-up guards (this story's, ARV-104g1's ValidationReaderGuard) rely on the services starting in order");
    }

    [Theory]
    [InlineData("k8s-dev")]
    [InlineData("k8s-demo")] // k8s-prd stops first at its transport check (ARV-082a)
    [InlineData("it")]
    public void AddArivaPersistence_Should_RefuseSchemaUpdate_When_TheProcessRunsAsAnotherEnvironmentThanVmLocal(string environment)
    {
        var act = () => new ServiceCollection().AddArivaPersistence(Configuration(environment, "true", false, false));

        act.Should().Throw<InvalidOperationException>().WithMessage($"Database:AllowSchemaUpdate is only allowed in vm-local; this process runs as '{environment}'*");
    }

    private static IConfiguration Configuration(string environment, string schemaUpdate, bool users, bool demo)
    {
        var values = new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            ["Database:AllowSchemaUpdate"] = schemaUpdate,
            ["Database:VerifySchemaOnStartup"] = "false",
            ["Seed:DemoTopology"] = demo ? "true" : "false"
        };
        if (users)
            values["Auth:DevelopmentUsers:0:UserName"] = "dev.admin";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
