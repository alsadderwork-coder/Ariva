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

        string.Equals(parsed.Password, "p;Include Error Detail=true;x=", StringComparison.Ordinal).Should().BeTrue("the password stays one value");
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

    [Fact]
    public void BuildConnectionString_Should_SetTheConnectTimeout_When_BuildingEitherLogin()
    {
        // ARV-096e: a run timeout cannot cut a connection the server never answers, so this bounds that wait.
        var settings = new DatabaseSettings { ConnectTimeoutSeconds = 7, Migration = new MigrationLoginSettings { Username = "ariva", Password = "x" } };

        new NpgsqlConnectionStringBuilder(settings.BuildConnectionString()).Timeout.Should().Be(7);
        new NpgsqlConnectionStringBuilder(settings.BuildMigrationConnectionString()).Timeout.Should().Be(7);
        new NpgsqlConnectionStringBuilder(new DatabaseSettings().BuildConnectionString()).Timeout.Should().Be(15, "Npgsql's own default, now said");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void ConnectTimeout_Should_BeRefusedOutsideItsBounds_When_Configured(int seconds, bool valid)
    {
        var check = new DatabaseSettings { ConnectTimeoutSeconds = seconds }.EnsureValidLimits;

        if (valid)
            check.Should().NotThrow();
        else
            check.Should().Throw<InvalidOperationException>().WithMessage("Database:ConnectTimeoutSeconds is a whole number of seconds from 2 to 60 (ARV-096e).");
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

        var check = settings.EnsureValidLimits;

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

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void MaxPoolSize_Should_BeRefusedOutsideItsBounds_When_Configured(int connections, bool valid)
    {
        // ARV-232: a host keeps at most this many connections; Npgsql's own 100 is the upper bound.
        var check = new DatabaseSettings { MaxPoolSize = connections }.EnsureValidLimits;

        if (valid)
            check.Should().NotThrow();
        else
            check.Should().Throw<InvalidOperationException>().WithMessage("Database:MaxPoolSize is a whole number of connections from 2 to 100 (ARV-232).");
    }

    [Fact]
    public void AddArivaPersistence_Should_RefuseToStart_When_ThePoolIsOutOfRange()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Database:MaxPoolSize"] = "500" }).Build();

        var add = () => new ServiceCollection().AddArivaPersistence(configuration);

        add.Should().Throw<InvalidOperationException>().WithMessage("Database:MaxPoolSize is a whole number of connections from 2 to 100*");
    }

    [Fact]
    public void BuildConnectionString_Should_BoundThePoolAndNameTheHost_When_BuildingTheRuntimeLogin()
    {
        // ARV-232: pg_stat_activity tells the hosts apart, and each host's pool has a ceiling of its own.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:ShortName"] = "stream",
            ["Database:MaxPoolSize"] = "12"
        }).Build();

        var runtime = new NpgsqlConnectionStringBuilder(DatabaseSettings.FromConfiguration(configuration).BuildConnectionString());

        runtime.ApplicationName.Should().Be("ariva-stream");
        runtime.MaxPoolSize.Should().Be(12);
        runtime.Pooling.Should().BeTrue();
        new NpgsqlConnectionStringBuilder(new DatabaseSettings().BuildConnectionString()).MaxPoolSize.Should().Be(20, "the default when a host names none");
    }

    [Fact]
    public void DeskTermConnectionString_Should_BeAPoolOfTwoWithShortTimeouts_When_StreamReadsDeskTerms()
    {
        // ARV-232: the desk term reads' own pool (their timeouts make it one), held to DeskTermPoolSize; the chart counts it on
        // api-stream's replicas only.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:ShortName"] = "stream",
            ["Database:MaxPoolSize"] = "20"
        }).Build();

        var desks = new NpgsqlConnectionStringBuilder(Ariva.Infra.Streaming.DeskTermSource.ConnectionString(DatabaseSettings.FromConfiguration(configuration)));

        desks.MaxPoolSize.Should().Be(DatabaseSettings.DeskTermPoolSize).And.Be(2);
        desks.Timeout.Should().Be(5);
        desks.CommandTimeout.Should().Be(10);
        desks.ApplicationName.Should().Be("ariva-stream");
        desks.Pooling.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "ariva")]
    [InlineData("", "ariva")]
    [InlineData("base", "ariva")]
    [InlineData("main", "ariva-main")]
    [InlineData("integration", "ariva-integration")]
    public void ApplicationName_Should_FollowTheHostsShortName_When_Configured(string shortName, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Application:ShortName"] = shortName }).Build();

        DatabaseSettings.FromConfiguration(configuration).ApplicationName.Should().Be(expected);
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("main;Pooling=false")]
    [InlineData("main name")]
    [InlineData("-main")]
    [InlineData("main\n")]
    public void ApplicationName_Should_RefuseAShortName_When_ItIsNotLowerCaseLettersDigitsAndHyphens(string shortName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Application:ShortName"] = shortName }).Build();

        var load = () => DatabaseSettings.FromConfiguration(configuration);

        load.Should().Throw<InvalidOperationException>().WithMessage("Application:ShortName must be lower case letters*");
    }

    [Fact]
    public void ApplicationName_Should_NotBeConfigured_When_TheDatabaseSectionNamesOne()
    {
        // Derived only: a Database:ApplicationName in a settings file or the environment cannot pass for another host.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:ShortName"] = "cronz",
            ["Database:ApplicationName"] = "ariva-main"
        }).Build();

        DatabaseSettings.FromConfiguration(configuration).ApplicationName.Should().Be("ariva-cronz");
    }

    [Fact]
    public void BuildMigrationConnectionString_Should_NotPool_When_BuildingTheMigrationLogin()
    {
        // ARV-232: a host that migrates or checks the schema at start closes those connections for good.
        var settings = new DatabaseSettings { Migration = new MigrationLoginSettings { Username = "ariva", Password = "x" } };

        var migration = new NpgsqlConnectionStringBuilder(settings.BuildMigrationConnectionString());

        migration.Pooling.Should().BeFalse();
        migration.ApplicationName.Should().Be("ariva-migration");
        new NpgsqlConnectionStringBuilder(new DatabaseSettings().BuildMigrationConnectionString()).Pooling.Should().BeFalse("without a migration login too");
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
