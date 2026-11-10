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

    [Theory]
    [InlineData(63, false)]
    [InlineData(64, true)]
    [InlineData(1_024, true)]
    [InlineData(102_400, true)]
    [InlineData(102_401, false)]
    [InlineData(0, false)]
    public void TempFileLimit_Should_BeRefusedOutsideItsBounds_When_Configured(int megabytes, bool valid)
    {
        // ARV-290: the runtime login's cap on temporary files, set on the login by the migration job; 1,024 MB by default.
        var check = new DatabaseSettings { TempFileLimitMegabytes = megabytes }.EnsureValidLimits;

        if (valid)
            check.Should().NotThrow();
        else
            check.Should().Throw<InvalidOperationException>().WithMessage("Database:TempFileLimitMegabytes is a whole number of megabytes from 64 to 102,400 (ARV-290).");
        new DatabaseSettings().TempFileLimitMegabytes.Should().Be(1_024);
    }

    [Fact]
    public void AddArivaPersistence_Should_RefuseToStart_When_TheTempFileLimitIsOutOfRange()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Database:TempFileLimitMegabytes"] = "32" }).Build();

        var add = () => new ServiceCollection().AddArivaPersistence(configuration);

        add.Should().Throw<InvalidOperationException>().WithMessage("*Database:TempFileLimitMegabytes is a whole number of megabytes from 64*");
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(7_200, true)]
    [InlineData(7_201, false)]
    [InlineData(0, false)]
    public void IdleInTransactionTimeout_Should_BeRefusedOutsideItsBounds_When_Configured(int seconds, bool valid)
    {
        // ARV-292: the runtime login's limit on a session idle in a transaction, set on the login by the migration job; ARV-294: 300 s by
        // default (960 s before), the lowest the bounds allow.
        var check = new DatabaseSettings { IdleInTransactionTimeoutSeconds = seconds }.EnsureValidLimits;

        if (valid)
            check.Should().NotThrow();
        else
            check.Should().Throw<InvalidOperationException>().WithMessage("Database:IdleInTransactionTimeoutSeconds is a whole number of seconds from 300 to 7,200 (ARV-292).");
        new DatabaseSettings().IdleInTransactionTimeoutSeconds.Should().Be(300).And.Be(DatabaseSettings.MinIdleInTransactionTimeoutSeconds);
    }

    /// <summary>Every setting a wait of <see cref="IdleTransactionBudget"/> reads, at the greatest value its bounds allow, or the transfers' and the relay's, which no wait reads any more.</summary>
    private static IConfiguration Maxima(string limit = null)
    {
        var values = new Dictionary<string, string>
        {
            ["Database:CommandTimeoutSeconds"] = DatabaseSettings.MaxCommandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:ConnectTimeoutSeconds"] = DatabaseSettings.MaxConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Kafka:Outbox:PassSeconds"] = Ariva.Infra.Messaging.OutboxRelaySettings.MaxPassSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Kafka:Consumers:HandlerTimeoutSeconds"] = Ariva.Infra.Messaging.ConsumerSettings.MaxHandlerTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:Smtp:TimeoutSeconds"] = "300",
            ["Security:RequestTimeouts:TransferSeconds"] = "3600"
        };
        if (limit is not null)
            values["Database:IdleInTransactionTimeoutSeconds"] = limit;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void IdleTransactionBudget_Should_StayBelowTheDefaultLimit_When_EveryWaitTakesItsDefault()
    {
        // ARV-294: the inventory's waits at their settings' defaults: a report's send within its budget (180 s), a staffing pass's inputs
        // within theirs and one connection (120 + 15), an alert evaluation tick (120 + 15 + 6), an outbox pass (60 + 4), a consumer (70 + 15),
        // a key ring load that creates a key (three calls of 15 + 30) and a display stale sweep (60 + 6); the default limit (300 s) is
        // above every one.
        var budget = IdleTransactionBudget.FromConfiguration(new ConfigurationBuilder().Build(), new DatabaseSettings());

        budget.Waits.Select(wait => wait.Seconds).Should().Equal(180, 135, 141, 64, 85, 135, 66);
        budget.Longest.Seconds.Should().Be(180);
        budget.Longest.What.Should().Be("a scheduled report's send, its delivery row and the report slot held");
        budget.ProblemWith(new DatabaseSettings().IdleInTransactionTimeoutSeconds).Should().BeNull();
    }

    [Fact]
    public void IdleTransactionBudget_Should_KeepEveryWaitWithin240Seconds_When_EverySettingIsAtItsMaximum()
    {
        // ARV-294: recomputed at every setting's maximum (command 300 s, connection 60 s, outbox pass 120 s, consumer 120 s, the relay's
        // 300 s and the transfers' 3,600 s, which no wait reads now): the report's 180 s, the staffing pass's 120 + 60, the alert
        // evaluation's 120 + 60 + 6, the outbox's 120 + 4, the consumer's 120 + 60, a key ring load's three calls of 50 + 30 (the
        // security review's R4: each connection opens within 50 s) and the sweep's 66. Before ARV-294 the same settings gave 3,600 s (a transfer), 2,400 s (an email round),
        // 4,500 s (a report), 2,460 s (staffing) and 1,080 s (a key ring load, three calls of 60 + 300).
        var configuration = Maxima();
        var budget = IdleTransactionBudget.FromConfiguration(configuration, DatabaseSettings.FromConfiguration(configuration));

        budget.Waits.Select(wait => wait.Seconds).Should().Equal(180, 180, 186, 124, 180, 240, 66);
        budget.Waits.Should().OnlyContain(wait => wait.Seconds <= IdleTransactionBudget.MaxWaitSeconds);
        budget.Longest.Seconds.Should().Be(240).And.BeLessThan(DatabaseSettings.MinIdleInTransactionTimeoutSeconds);
        budget.Longest.What.Should().Be("a Data Protection key ring load inside a transaction (read, store a new key, read again)");
        IdleTransactionBudget.MaxWaitSeconds.Should().BeLessThan(DatabaseSettings.MinIdleInTransactionTimeoutSeconds);
        budget.ProblemWith(DatabaseSettings.MinIdleInTransactionTimeoutSeconds).Should().BeNull("the lowest limit covers every value the settings allow");
    }

    [Fact]
    public void IdleTransactionBudget_Should_HoldEachBoundToTheCodeThatSetsIt_When_Computed()
    {
        // ARV-294: the fixed parts of the waits are the code's own bounds, each at most the 240 s with the longest connection open.
        Ariva.Infra.Services.Reports.ReportSendBudget.DefaultSeconds.Should().Be(180).And.BeLessThanOrEqualTo(IdleTransactionBudget.MaxWaitSeconds);
        Ariva.Infra.Services.Reports.ReportSendBudget.Default.Total.Should().Be(TimeSpan.FromSeconds(180));
        (Ariva.Infra.Staffing.StaffingRecommendations.InputBudgetSeconds + DatabaseSettings.MaxConnectTimeoutSeconds).Should().BeLessThanOrEqualTo(IdleTransactionBudget.MaxWaitSeconds);
        IdleTransactionBudget.RedisOperationSeconds.Should().Be((int)Ariva.Infra.Live.RedisLiveSnapshots.OperationTimeout.TotalSeconds);
        Ariva.Infra.Services.Displays.DisplayStaleMonitor.DefaultRunTimeout.Should().Be(TimeSpan.FromMinutes(1));
        Ariva.Infra.Alerting.AlertEvaluationWorker.DefaultRunTimeout.Should().Be(TimeSpan.FromMinutes(2));
        Ariva.Infra.DataProtection.PostgresXmlRepository.KeyRingCommandSeconds.Should().Be(30);
        Ariva.Infra.DataProtection.PostgresXmlRepository.KeyRingConnectSeconds.Should().Be(50);
        Ariva.Infra.DataProtection.PostgresXmlRepository.CommandTimeoutSeconds(new DatabaseSettings { CommandTimeoutSeconds = 300 }).Should().Be(30);
        Ariva.Infra.DataProtection.PostgresXmlRepository.CommandTimeoutSeconds(new DatabaseSettings()).Should().Be(30, "the shipped default, unchanged");
        Ariva.Infra.DataProtection.PostgresXmlRepository.CommandTimeoutSeconds(new DatabaseSettings { CommandTimeoutSeconds = 10 }).Should().Be(10, "a shorter command timeout stays as it is");
        Ariva.Infra.DataProtection.PostgresXmlRepository.ConnectTimeoutSeconds(new DatabaseSettings { ConnectTimeoutSeconds = 60 }).Should().Be(50);
        Ariva.Infra.DataProtection.PostgresXmlRepository.ConnectTimeoutSeconds(new DatabaseSettings()).Should().Be(15, "the shipped default, unchanged");
        new DatabaseSettings().ConnectTimeoutSeconds.Should().BeLessThan(Ariva.Infra.DataProtection.PostgresXmlRepository.KeyRingConnectSeconds);
        (IdleTransactionBudget.KeyRingLoadCalls * (Ariva.Infra.DataProtection.PostgresXmlRepository.KeyRingConnectSeconds + Ariva.Infra.DataProtection.PostgresXmlRepository.KeyRingCommandSeconds))
            .Should().BeLessThanOrEqualTo(IdleTransactionBudget.MaxWaitSeconds, "a key ring load that creates a key: read, store, read again");
    }

    [Theory]
    [InlineData("Email:Smtp:TimeoutSeconds", "300")]
    [InlineData("Security:RequestTimeouts:TransferSeconds", "3600")]
    [InlineData("Database:CommandTimeoutSeconds", "300")]
    [InlineData("Database:ConnectTimeoutSeconds", "60")]
    [InlineData("Kafka:Outbox:PassSeconds", "120")]
    [InlineData("Kafka:Consumers:HandlerTimeoutSeconds", "120")]
    public void AddArivaPersistence_Should_StartAtTheDefaultLimit_When_ASettingIsAtItsMaximum(string key, string value)
    {
        // ARV-294: before it, the email round at an SMTP timeout of 60 s, a transfer of 1,200 s or a command timeout of 119 s refused to
        // start under the 960 s limit; now no value of any of them makes a wait pass 240 s, so every host starts at 300 s.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { [key] = value }).Build();

        var add = () => new ServiceCollection().AddArivaPersistence(configuration);

        add.Should().NotThrow();
    }

    [Fact]
    public void AddArivaPersistence_Should_StartAndRegisterTheBudgetAndTheGate_When_EverySettingIsAtItsMaximum()
    {
        var services = new ServiceCollection();

        services.AddArivaPersistence(Maxima(limit: "300"));

        services.Should().ContainSingle(d => d.ServiceType == typeof(IdleTransactionBudget)).Which.ImplementationInstance.Should().BeOfType<IdleTransactionBudget>()
            .Which.Longest.Seconds.Should().Be(240, "the host's own budget, from its settings");
        services.Should().ContainSingle(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(Ariva.Infra.Timescale.IdleTransactionLimitGate));
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    [InlineData(60, true)]
    public void KeyRingConnectionString_Should_BeTheHostsOwnUnlessItsConnectTimeoutPassesTheBound_When_Built(int connectSeconds, bool bounded)
    {
        // ARV-294 (the sign-in freeze): at the shipped connection timeout (15 s) and up to 50 s the key ring uses the hosts' connection
        // string itself, so the same pool and nothing changes; above it only the connection timeout is bounded, to 50 s.
        var settings = new DatabaseSettings { ConnectTimeoutSeconds = connectSeconds };

        var keyRing = Ariva.Infra.DataProtection.PostgresXmlRepository.KeyRingConnectionString(settings);

        var built = new NpgsqlConnectionStringBuilder(keyRing);
        built.Timeout.Should().Be(Math.Min(connectSeconds, 50));
        if (bounded)
        {
            built.Remove("Timeout");
            var hosts = new NpgsqlConnectionStringBuilder(settings.BuildConnectionString());
            hosts.Remove("Timeout");
            built.ConnectionString.Should().Be(hosts.ConnectionString, "only the connection timeout differs");
        }
        else
        {
            keyRing.Should().Be(settings.BuildConnectionString(), "the hosts' own connection string, so the same pool");
        }
    }

    [Fact]
    public void ProblemWith_Should_NameTheLongestWaitAndItsSetting_When_TheLimitIsNotAboveIt()
    {
        // The check AddArivaPersistence makes, should a wait come to pass a limit the bounds allow: it names the wait and its setting.
        var configuration = Maxima();
        var budget = IdleTransactionBudget.FromConfiguration(configuration, DatabaseSettings.FromConfiguration(configuration));

        budget.ProblemWith(240).Should().Be("Database:IdleInTransactionTimeoutSeconds (240 s) must be above the longest a transaction of this host may wait on other work: a Data Protection key ring load inside a transaction (read, store a new key, read again), 240 s from Database:ConnectTimeoutSeconds (ARV-292). Raise the limit or lower that setting.");
        budget.ProblemWith(241).Should().BeNull();
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
    public void SqlEntryPoints_Should_RequireConstantSql_When_DeclaredOnStorageProvider()
    {
        // ARV-228b: the typed reader is the second SQL entry point; each takes its text as a constant, and none a FormattableString.
        var methods = typeof(IStorageProvider).GetMethods()
            .Where(m => m.GetParameters().Any(p => p.Name == "sql" || p.ParameterType == typeof(FormattableString)))
            .ToList();

        methods.Select(m => m.Name).Should().BeEquivalentTo([nameof(IStorageProvider.ExecuteSqlAsync), nameof(IStorageProvider.ReadSqlAsync)],
            "the SQL entry points are ExecuteSqlAsync and ReadSqlAsync, one overload each");
        foreach (var method in methods)
        {
            method.GetParameters().Should().NotContain(p => p.ParameterType == typeof(FormattableString), $"{method.Name} takes no FormattableString");
            var sql = method.GetParameters()[0];
            sql.Name.Should().Be("sql");
            sql.ParameterType.Should().Be<string>();
            sql.GetCustomAttribute<ConstantExpectedAttribute>().Should().NotBeNull($"CA1857 then rejects interpolated SQL given to {method.Name} at compile time");
        }
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
