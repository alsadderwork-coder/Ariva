using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.NHibernate.Mapping;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Npgsql;

namespace Ariva.UnitTests.Persistence;

/// <summary>ARV-005: schema update gate, connection string safety, naming conventions and the SQL API shape.</summary>
public sealed class PersistenceSettingsTests
{
    public static TheoryData<string> EnvironmentFiles =>
    [
        "appsettings.base.json",
        "appsettings.base.k8s-dev.json",
        "appsettings.base.k8s-demo.json",
        "appsettings.base.k8s-prd.json"
    ];

    [Theory]
    [MemberData(nameof(EnvironmentFiles))]
    public void AllowSchemaUpdate_Should_BeFalseOrAbsent_When_EnvironmentIsNotVmLocal(string file)
    {
        var value = ReadAllowSchemaUpdate(file);

        value.Should().NotBe(true, $"{file} must get its schema from the versioned scripts, never from SchemaUpdate");
    }

    [Fact]
    public void AllowSchemaUpdate_Should_BeTrue_When_EnvironmentIsVmLocal()
    {
        ReadAllowSchemaUpdate("appsettings.base.vm-local.json").Should().BeTrue();
    }

    public static TheoryData<string> ClusterFiles =>
    [
        "appsettings.base.k8s-dev.json",
        "appsettings.base.k8s-demo.json",
        "appsettings.base.k8s-prd.json"
    ];

    [Theory]
    [MemberData(nameof(ClusterFiles))]
    public void DatabaseSettings_Should_SeparateRuntimeAndMigrationLogins_When_EnvironmentIsCluster(string file)
    {
        var settings = Load(file);

        settings.UsesSeparateRuntimeLogin.Should().BeTrue($"{file}: hosts must not connect with the migration login (CWE-269)");
        settings.VerifySchemaOnStartup.Should().BeTrue($"{file}: hosts must stop on a schema they were not built for");
        settings.AllowSchemaUpdate.Should().BeFalse();
    }

    [Fact]
    public void DatabaseSettings_Should_MigrateAtStartup_When_EnvironmentIsVmLocal()
    {
        var settings = Load("appsettings.base.vm-local.json");

        settings.AllowSchemaUpdate.Should().BeTrue();
        settings.VerifySchemaOnStartup.Should().BeFalse("a vm-local host migrates the development database itself");
        settings.UsesSeparateRuntimeLogin.Should().BeTrue();
    }

    [Fact]
    public void BuildMigrationConnectionString_Should_UseMigrationLogin_When_Configured()
    {
        var settings = new DatabaseSettings
        {
            Username = "ariva_app",
            Password = "runtime-secret-value",
            Migration = new MigrationLoginSettings { Username = "ariva", Password = "owner-secret-value" }
        };

        new NpgsqlConnectionStringBuilder(settings.BuildMigrationConnectionString()).Username.Should().Be("ariva");
        new NpgsqlConnectionStringBuilder(settings.BuildConnectionString()).Username.Should().Be("ariva_app");
    }

    [Fact]
    public void BuildConnectionString_Should_KeepPasswordAsOneValue_When_PasswordContainsSeparators()
    {
        var settings = new DatabaseSettings { Password = "p;Include Error Detail=true;x=" };

        var parsed = new NpgsqlConnectionStringBuilder(settings.BuildConnectionString());

        parsed.Password.Should().Be("p;Include Error Detail=true;x=");
        parsed.IncludeErrorDetail.Should().BeFalse("a password must not be able to add connection options");
    }

    [Fact]
    public void BuildConnectionString_Should_SetTheCommandTimeout_When_BuildingEitherLogin()
    {
        // ARV-096d: every command on these connections (NHibernate's, ExecuteSqlAsync's) waits this long unless it sets its own.
        var settings = new DatabaseSettings { CommandTimeoutSeconds = 45, Migration = new MigrationLoginSettings { Username = "ariva", Password = "x" } };

        new NpgsqlConnectionStringBuilder(settings.BuildConnectionString()).CommandTimeout.Should().Be(45);
        new NpgsqlConnectionStringBuilder(settings.BuildMigrationConnectionString()).CommandTimeout.Should().Be(45);
        new NpgsqlConnectionStringBuilder(new DatabaseSettings().BuildConnectionString()).CommandTimeout.Should().Be(30, "Npgsql's own default, now said");
    }

    [Theory]
    [InlineData(30, 360, true)]
    [InlineData(5, 301, true)]
    [InlineData(300, 3600, true)]
    [InlineData(4, 360, false)]
    [InlineData(301, 360, false)]
    [InlineData(30, 300, false)]
    [InlineData(30, 3601, false)]
    [InlineData(0, 0, false)]
    public void Timeouts_Should_BeRefusedOutsideTheirBounds_When_Configured(int command, int statement, bool valid)
    {
        var settings = new DatabaseSettings { CommandTimeoutSeconds = command, StatementTimeoutSeconds = statement };

        var check = settings.EnsureValidTimeouts;

        if (valid)
            check.Should().NotThrow();
        else
            check.Should().Throw<InvalidOperationException>().WithMessage("*Database:*TimeoutSeconds is a whole number of seconds from*");
    }

    [Theory]
    [InlineData("CommandTimeoutSeconds", "1")]
    [InlineData("StatementTimeoutSeconds", "60")]
    public void AddArivaPersistence_Should_RefuseToStart_When_ATimeoutIsOutOfRange(string name, string value)
    {
        // Every host and the migration job build their persistence through this, so each stops at start.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Database:" + name] = value }).Build();

        var add = () => new ServiceCollection().AddArivaPersistence(configuration);

        add.Should().Throw<InvalidOperationException>().WithMessage($"*Database:{name} is a whole number of seconds from*");
    }

    [Fact]
    public void StatementTimeout_Should_StayAboveEveryClientTimeoutOfTheRuntimeLogin_When_Bounded()
    {
        // The server limit is a backstop: it never ends a statement a host still waits for.
        DatabaseSettings.MinStatementTimeoutSeconds.Should().BeGreaterThan(DatabaseSettings.LongestRuntimeCommandSeconds);
        DatabaseSettings.MinStatementTimeoutSeconds.Should().BeGreaterThan(DatabaseSettings.MaxCommandTimeoutSeconds);
        new DatabaseSettings().StatementTimeoutSeconds.Should().BeInRange(DatabaseSettings.MinStatementTimeoutSeconds, DatabaseSettings.MaxStatementTimeoutSeconds);
    }

    [Theory]
    [InlineData(true, SslMode.VerifyFull)]
    [InlineData(false, SslMode.Disable)]
    public void BuildConnectionString_Should_VerifyServerCertificate_When_EncryptionIsOn(bool useEncryption, SslMode expected)
    {
        var settings = new DatabaseSettings { UseEncryption = useEncryption };

        new NpgsqlConnectionStringBuilder(settings.BuildConnectionString()).SslMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("CreatedById", "created_by_id")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("ZoneProfile", "zone_profile")]
    [InlineData("Level2Name", "level2_name")]
    [InlineData("Id", "id")]
    public void ToSnakeCase_Should_SeparateWords_When_NameIsPascalCase(string name, string expected)
    {
        NHibernateMappingRules.ToSnakeCase(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("user", "`user`")]
    [InlineData("order", "`order`")]
    [InlineData("zone", "zone")]
    public void Quote_Should_QuoteOnlyReservedWords_When_NameIsGiven(string identifier, string expected)
    {
        NHibernateMappingRules.Quote(identifier).Should().Be(expected);
    }

    [Fact]
    public void ExecuteSqlAsync_Should_RequireConstantSql_When_DeclaredOnStorageProvider()
    {
        var methods = typeof(IStorageProvider).GetMethods().Where(m => m.Name == nameof(IStorageProvider.ExecuteSqlAsync)).ToList();

        methods.Should().ContainSingle("there is one SQL entry point and no FormattableString overload");
        var sql = methods[0].GetParameters()[0];
        sql.ParameterType.Should().Be<string>();
        sql.GetCustomAttribute<ConstantExpectedAttribute>().Should().NotBeNull("CA1857 then rejects interpolated SQL at compile time");
    }

    /// <summary>The base file, then the environment file on top, as the hosts layer them.</summary>
    private static DatabaseSettings Load(string file) =>
        DatabaseSettings.FromConfiguration(new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, file))
            .Build());

    private static bool? ReadAllowSchemaUpdate(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)));
        return document.RootElement.TryGetProperty("Database", out var database) &&
               database.TryGetProperty("AllowSchemaUpdate", out var allow)
            ? allow.GetBoolean()
            : null;
    }
}
