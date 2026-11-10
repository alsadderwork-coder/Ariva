using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Di.Extensions;
using Ariva.Infra.Availability;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-083a (CWE-269, CWE-285, CWE-287; ASVS 5.0 V13.2.2): one database login per host and one for the availability ledger.
/// The settings the migration job alone reads (Database:HostLogins, all five in a cluster, none or all on a vm-local), each a
/// login of its own; the ledger's (Database:Availability), which a cluster's Cronz host requires; the job stopping before it
/// connects when they are wrong; the declared grants of each role (HostDatabaseAccess) and script 0074 granting exactly them;
/// and no committed file or runtime type that carries the logins. The database side (the grants a migrated database holds,
/// each login's refusals) is in Ariva.IntegrationTests (HostDatabaseAccessTests).
/// </summary>
public sealed partial class HostDatabaseLoginTests
{
    #region Fixtures

    private static readonly string[] Hosts = ["Main", "Ingest", "Stream", "Cronz", "Integration"];

    private static DatabaseSettings Database(string username = "ariva_app_main", string migration = "ariva") => new()
    {
        Host = "db.example",
        Port = 6543,
        Name = "ariva_site",
        Username = username,
        Password = "runtime-password-0123456789",
        UseEncryption = true,
        Migration = new MigrationLoginSettings { Username = migration, Password = "owner-password-0123456789" }
    };

    /// <summary>A cluster's settings for the migration job: every host's login and the ledger's, each with a password of its own.</summary>
    private static Dictionary<string, string> ClusterLogins()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in Hosts)
        {
            settings[$"Database:HostLogins:{host}:Username"] = $"ariva_app_{host.ToLowerInvariant()}";
            settings[$"Database:HostLogins:{host}:Password"] = $"{host.ToLowerInvariant()}-password-0123456789";
        }

        settings["Database:Availability:Username"] = "ariva_app_availability";
        settings["Database:Availability:Password"] = "availability-password-0123456789";
        return settings;
    }

    private static (HostLoginSettings Hosts, AvailabilityLoginSettings Availability) Read(Dictionary<string, string> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (HostLoginSettings.FromConfiguration(configuration), AvailabilityLoginSettings.FromConfiguration(configuration));
    }

    private static IReadOnlyList<string> Problems(Dictionary<string, string> settings, string environment = "k8s-dev", string readerUsername = "", string readerPassword = "")
    {
        var (hosts, availability) = Read(settings);
        return hosts.Problems(Database(), availability, environment, readerUsername, readerPassword);
    }

    #endregion

    #region Host logins

    [Theory]
    [InlineData(null)]
    [InlineData("vm-local")]
    public void Problems_Should_BeEmpty_When_NoLoginIsConfiguredOutsideACluster(string environment)
    {
        var (hosts, _) = Read([]);

        hosts.IsConfigured.Should().BeFalse();
        Problems([], environment).Should().BeEmpty("a developer machine's hosts share Database:Username");
    }

    [Theory]
    [InlineData("k8s-dev")]
    [InlineData("k8s-demo")]
    [InlineData("k8s-prd")]
    [InlineData("vm-local")]
    public void Problems_Should_BeEmpty_When_EveryHostAndTheLedgerHaveLoginsOfTheirOwn(string environment)
    {
        Problems(ClusterLogins(), environment).Should().BeEmpty();
        Read(ClusterLogins()).Hosts.IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("k8s-dev")]
    [InlineData("k8s-prd")]
    public void Problems_Should_NameEveryMissingLogin_When_AClusterHasNone(string environment)
    {
        var problems = Problems([], environment);

        foreach (var host in Hosts)
            problems.Should().Contain(p => p.StartsWith($"Database:HostLogins:{host}:Username is not set: a cluster's migration job ensures every host's login", StringComparison.Ordinal));
        problems.Should().Contain(p => p.StartsWith("Database:Availability:Username is not set", StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_Should_RefuseSomeButNotAll_When_OnlyPartOfTheHostsAreConfigured()
    {
        var settings = ClusterLogins();
        settings.Remove("Database:HostLogins:Ingest:Username");
        settings.Remove("Database:HostLogins:Ingest:Password");

        Problems(settings, "vm-local").Should().ContainSingle()
            .Which.Should().Be("Database:HostLogins:Ingest:Username is not set: configure every host's login or none (ARV-083a).");
    }

    [Theory]
    [InlineData("ariva_app_main")]
    [InlineData("ariva_app_cronz")]
    public void Problems_Should_RefuseOneLoginForTwoHosts_When_TheNamesMatch(string streamLogin)
    {
        var settings = ClusterLogins();
        settings["Database:HostLogins:Stream:Username"] = streamLogin;

        Problems(settings).Should().ContainSingle(p => p.Contains("name one login; each host has a login of its own (CWE-269)", StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_Should_RefuseASharedPassword_When_TwoLoginsHaveOne()
    {
        var settings = ClusterLogins();
        settings["Database:Availability:Password"] = settings["Database:HostLogins:Cronz:Password"];

        var problem = Problems(settings).Should().ContainSingle().Which;

        problem.Should().Be("Database:HostLogins:Cronz:Password and Database:Availability:Password are the same; each login has a password of its own (CWE-287).");
        problem.Contains(settings["Database:Availability:Password"], StringComparison.Ordinal).Should().BeFalse("a problem never quotes a password");
    }

    [Fact]
    public void Problems_Should_RefuseTheMigrationAndTheReaderLogins_When_AHostUsesOne()
    {
        var settings = ClusterLogins();
        settings["Database:HostLogins:Main:Username"] = "ariva_owner";
        settings["Database:HostLogins:Ingest:Username"] = "ariva_validation";
        settings["Database:HostLogins:Integration:Password"] = "owner-password-0123456789";
        settings["Database:HostLogins:Stream:Password"] = "reader-password-0123456789";
        var (hosts, availability) = Read(settings);

        var problems = hosts.Problems(Database(migration: "ariva_owner"), availability, "k8s-prd", "ariva_validation", "reader-password-0123456789");

        problems.Should().BeEquivalentTo(
            "Database:HostLogins:Main:Username is the migration login; a host login is a login of its own (CWE-269).",
            "Database:HostLogins:Ingest:Username is the validation reader's login; a host login is a login of its own (CWE-269).",
            "Database:HostLogins:Stream:Password must not be the migration login's or the validation reader's password (CWE-287).",
            "Database:HostLogins:Integration:Password must not be the migration login's or the validation reader's password (CWE-287).");
    }

    /// <summary>
    /// The review of ARV-083a, round 2 (CWE-269): the committed runtime login name, whose password base-appsettings-secret holds
    /// and every pod mounts, is no host's login and not the ledger's in a cluster, since a host login under that name and the base
    /// file's password would give every pod that host's grants. The problem names the setting, never the value.
    /// </summary>
    [Theory]
    [InlineData("k8s-dev", "Database:HostLogins:Main")]
    [InlineData("k8s-demo", "Database:HostLogins:Ingest")]
    [InlineData("k8s-prd", "Database:HostLogins:Stream")]
    [InlineData("k8s-prd", "Database:HostLogins:Cronz")]
    [InlineData("k8s-dev", "Database:HostLogins:Integration")]
    [InlineData("k8s-prd", "Database:Availability")]
    public void Problems_Should_RefuseTheCommittedRuntimeName_When_AClusterHostOrTheLedgerHasIt(string environment, string section)
    {
        var settings = ClusterLogins();
        settings[$"{section}:Username"] = DatabaseLoginSettings.CommittedRuntimeName;

        var problems = Problems(settings, environment);

        problems.Should().ContainSingle().Which.Should().Be(
            $"{section}:Username is the runtime login name the committed appsettings files give every host (Database:Username), whose password every pod " +
            "mounts from base-appsettings-secret; in a cluster this login has a name of its own (CWE-269).");
        problems.Should().OnlyContain(p => !p.Contains(DatabaseLoginSettings.CommittedRuntimeName, StringComparison.Ordinal), "a problem names the setting, never its value");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("vm-local")]
    public void Problems_Should_AcceptTheCommittedRuntimeName_When_TheProcessIsNotInACluster(string environment)
    {
        // A vm-local machine's hosts share it (the development login), and a job without an environment (the restore tests) may
        // give it to Ariva.Api.Main.
        var settings = ClusterLogins();
        settings["Database:HostLogins:Main:Username"] = DatabaseLoginSettings.CommittedRuntimeName;

        Problems(settings, environment).Should().BeEmpty();
        new DatabaseLoginSettings { Username = DatabaseLoginSettings.CommittedRuntimeName, Password = "runtime-password-0123456789" }.Problems("Database")
            .Should().BeEmpty("the shared development login is checked outside a cluster only");
    }

    [Fact]
    public void CommittedRuntimeName_Should_BeTheCommittedDatabaseUsername_When_TheBaseFilesAreRead()
    {
        // The refusal follows the committed default: renaming Database:Username in the base files moves the constant with it.
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Backplane", "Ariva.Api.Common"), "appsettings.base*.json", SearchOption.TopDirectoryOnly).ToList();

        files.Select(Path.GetFileName).Should().Contain("appsettings.base.json");
        files.Select(f => new ConfigurationBuilder().AddJsonFile(f).Build()["Database:Username"]).Where(u => u is not null)
            .Should().NotBeEmpty().And.OnlyContain(u => u == DatabaseLoginSettings.CommittedRuntimeName);
    }

    [Theory]
    [InlineData("Ariva_App_Main", "plain lower-case PostgreSQL identifier")]
    [InlineData("ariva-app", "plain lower-case PostgreSQL identifier")]
    [InlineData("x; DROP ROLE ariva", "plain lower-case PostgreSQL identifier")]
    [InlineData("pg_main", "plain lower-case PostgreSQL identifier")]
    [InlineData("main\n", "plain lower-case PostgreSQL identifier")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "plain lower-case PostgreSQL identifier")]
    [InlineData("ariva_runtime", "one of Ariva's database roles")]
    [InlineData("ariva_main", "one of Ariva's database roles")]
    [InlineData("ariva_availability", "one of Ariva's database roles")]
    [InlineData("ariva_migration", "one of Ariva's database roles")]
    public void LoginProblems_Should_RefuseTheName_When_ItIsNotAPlainIdentifierOrIsAnArivaRole(string username, string problem)
    {
        new DatabaseLoginSettings { Username = username, Password = "host-password-0123456789" }.Problems("Database:HostLogins:Main")
            .Should().ContainSingle(p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "at least 16 characters")]
    [InlineData("fifteen-chars-1", "at least 16 characters")]
    [InlineData("password\t0123456789", "printable ASCII")]
    [InlineData("password-0123456789\n", "printable ASCII")]
    [InlineData("p\u00e4ssword-0123456789", "printable ASCII")]
    public void LoginProblems_Should_RefuseThePassword_When_ItIsShortOrNotPrintableAscii(string password, string problem)
    {
        // The migration job derives the SCRAM-SHA-256 verifier itself; without SASLprep that is exact for printable ASCII only.
        var found = new DatabaseLoginSettings { Username = "ariva_app_main", Password = password }.Problems("Database:HostLogins:Main")
            .Should().ContainSingle(p => p.Contains(problem, StringComparison.Ordinal)).Which;
        (password.Trim().Length > 0 && found.Contains(password.Trim(), StringComparison.Ordinal)).Should().BeFalse("a problem never quotes a password");
    }

    [Fact]
    public void LoginProblems_Should_RefuseAPassword_When_NoNameIsSet()
    {
        new DatabaseLoginSettings { Password = "host-password-0123456789" }.Problems("Database:Availability")
            .Should().Equal("Database:Availability:Password is set without Database:Availability:Username.");
    }

    [Fact]
    public void EnsureValid_Should_ListEveryProblemWithoutAnySecret_When_Misconfigured()
    {
        var settings = ClusterLogins();
        settings["Database:HostLogins:Stream:Username"] = "ariva_app_main";
        settings["Database:HostLogins:Cronz:Password"] = settings["Database:HostLogins:Integration:Password"];
        var (hosts, availability) = Read(settings);
        var database = Database();

        var act = () => hosts.EnsureValid(database, availability, "k8s-prd", string.Empty, string.Empty);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().StartWith("The host database logins are misconfigured (ARV-083a):").And.Contain("name one login").And.Contain("are the same");
        settings.Where(s => s.Key.EndsWith(":Password", StringComparison.Ordinal)).Select(s => s.Value).Append(database.Migration.Password)
            .Any(secret => message.Contains(secret, StringComparison.Ordinal)).Should().BeFalse("the message names the sections, never a password");
    }

    [Fact]
    public void All_Should_GiveEachHostItsSectionAndItsRole_When_Read()
    {
        var (hosts, _) = Read(ClusterLogins());

        hosts.All.Select(h => $"{h.Section} {h.Role} {h.Login.Username}").Should().Equal(
            "Database:HostLogins:Main ariva_main ariva_app_main",
            "Database:HostLogins:Ingest ariva_ingest ariva_app_ingest",
            "Database:HostLogins:Stream ariva_stream ariva_app_stream",
            "Database:HostLogins:Cronz ariva_cronz ariva_app_cronz",
            "Database:HostLogins:Integration ariva_integration ariva_app_integration");
        hosts.All.Select(h => h.Role).Append(HostDatabaseAccess.AvailabilityRole).Should().Equal(HostDatabaseAccess.HostRoles);
        typeof(HostLoginSettings).IsPublic.Should().BeFalse("only Ariva.Infra and the composition root reach every host's password");
    }

    #endregion

    #region Availability ledger login

    private static AvailabilityLoginSettings Ledger(string username = "ariva_app_availability", string password = "availability-password-0123456789") =>
        new() { Username = username, Password = password };

    [Fact]
    public void AvailabilityProblems_Should_RefuseTheRuntimeOrTheMigrationLogin_When_TheLedgerUsesOne()
    {
        Ledger().Problems(Database()).Should().BeEmpty();
        new AvailabilityLoginSettings().Problems(Database()).Should().BeEmpty("not configured: a vm-local ledger uses the shared login");

        Ledger("ariva_app_main").Problems(Database()).Should().ContainSingle(p => p.Contains("is the host's runtime login", StringComparison.Ordinal));
        Ledger("ARIVA").Problems(Database()).Should().Contain(p => p.Contains("is the migration login", StringComparison.Ordinal));
        Ledger(password: "runtime-password-0123456789").Problems(Database()).Should().ContainSingle(p => p.Contains("must not be the runtime or the migration", StringComparison.Ordinal));
        Ledger(password: "owner-password-0123456789").Problems(Database()).Should().ContainSingle(p => p.Contains("must not be the runtime or the migration", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildConnectionString_Should_UseTheLedgersLoginNameAndPoolWithTheDatabasesTls_When_Configured()
    {
        var parsed = new NpgsqlConnectionStringBuilder(Ledger(password: "ledger;Include Error Detail=true;x=").BuildConnectionString(Database()));

        parsed.Username.Should().Be("ariva_app_availability");
        string.Equals(parsed.Password, "ledger;Include Error Detail=true;x=", StringComparison.Ordinal).Should().BeTrue("a password must not be able to add connection options");
        parsed.IncludeErrorDetail.Should().BeFalse();
        parsed.Host.Should().Be("db.example");
        parsed.Port.Should().Be(6543);
        parsed.Database.Should().Be("ariva_site");
        parsed.SslMode.Should().Be(SslMode.VerifyFull, "the ledger follows the deployment's TLS setting");
        parsed.ApplicationName.Should().Be("ariva-availability");
        parsed.MaxPoolSize.Should().Be(AvailabilityLoginSettings.MaxPoolSize);
        parsed.CommandTimeout.Should().Be(new DatabaseSettings().CommandTimeoutSeconds);
        parsed.Timeout.Should().Be(new DatabaseSettings().ConnectTimeoutSeconds);
    }

    [Fact]
    public void BuildConnectionString_Should_Refuse_When_TheLoginIsNotConfiguredOrNotItsOwn()
    {
        var notConfigured = () => new AvailabilityLoginSettings().BuildConnectionString(Database());
        var runtime = () => Ledger("ariva_app_main").BuildConnectionString(Database());

        notConfigured.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
        runtime.Should().Throw<InvalidOperationException>().WithMessage("*runtime login*").Which.Message.Should().NotContain("availability-password");
    }

    [Theory]
    [InlineData("k8s-dev", null, "*Database:Availability:Username is not set*")]
    [InlineData("k8s-prd", null, "*Database:Availability:Username is not set*")]
    [InlineData("k8s-dev", "ariva_app", "*is the host's runtime login*")]
    [InlineData("k8s-dev", "ariva_app_availability", null)]
    [InlineData("vm-local", null, null)]
    [InlineData(null, null, null)]
    public void AddArivaAvailabilityLedger_Should_RefuseACluster_When_TheLedgerHasNoLoginOfItsOwn(string environment, string login, string refusal)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Application:Environment"] = environment,
            ["Database:Username"] = "ariva_app",
            ["Database:Password"] = "runtime-password-0123456789"
        };
        if (login is not null)
        {
            settings["Database:Availability:Username"] = login;
            settings["Database:Availability:Password"] = "availability-password-0123456789";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var compose = () => new ServiceCollection().AddArivaAvailabilityLedger(configuration);

        if (refusal is null)
            compose.Should().NotThrow().Which.Should().ContainSingle(d => d.ServiceType == typeof(AvailabilityLoginSettings));
        else
            compose.Should().Throw<InvalidOperationException>().WithMessage(refusal).Which.Message.Should().NotContain("availability-password");
    }

    [Theory]
    [InlineData("k8s-dev", true)]
    [InlineData("k8s-prd", true)]
    [InlineData("vm-local", false)]
    [InlineData(null, false)]
    public void AddArivaAvailabilityLedger_Should_RefuseTheCommittedRuntimeName_When_AClustersLedgerHasIt(string environment, bool refused)
    {
        // The Cronz host's own login is another (its chart secret), so only the committed name itself is refused, and only in a cluster.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Application:Environment"] = environment,
            ["Database:Username"] = "ariva_app_cronz",
            ["Database:Password"] = "runtime-password-0123456789",
            ["Database:Availability:Username"] = DatabaseLoginSettings.CommittedRuntimeName,
            ["Database:Availability:Password"] = "availability-password-0123456789"
        }).Build();
        var compose = () => new ServiceCollection().AddArivaAvailabilityLedger(configuration);

        if (refused)
            compose.Should().Throw<InvalidOperationException>()
                .WithMessage("The availability ledger's login is misconfigured (ARV-083a): Database:Availability:Username is the runtime login name the committed appsettings files give every host*")
                .Which.Message.Contains(DatabaseLoginSettings.CommittedRuntimeName, StringComparison.Ordinal).Should().BeFalse("the message names the setting, never its value");
        else
            compose.Should().NotThrow();
    }

    #endregion

    #region Migration job

    private static DatabaseMigrator Migrator(string environment, Dictionary<string, string> logins, string runtimePassword = "runtime-password-0123456789")
    {
        // Nothing listens on the port: a job that tried to connect would fail with a connection error instead.
        var settings = new DatabaseSettings
        {
            Host = "127.0.0.1",
            Port = 1,
            Username = "ariva_app",
            Password = runtimePassword,
            Migration = new MigrationLoginSettings { Username = "ariva", Password = "owner-password-0123456789" }
        };
        var (hosts, availability) = Read(logins);
        return new DatabaseMigrator(settings, new ValidationReaderSettings(), new NHibernateSessionFactoryProvider(settings), NullLoggerFactory.Instance,
            new DatabaseEnvironment(environment), hosts, availability);
    }

    [Theory]
    [InlineData("k8s-dev")]
    [InlineData("k8s-demo")]
    [InlineData("k8s-prd")]
    public async Task MigrateAsync_Should_StopBeforeItConnects_When_AClusterHasNoHostLogins(string environment)
    {
        var migrate = () => Migrator(environment, []).MigrateAsync(TestContext.Current.CancellationToken);

        await migrate.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("The host database logins are misconfigured (ARV-083a): Database:HostLogins:Main:Username is not set*");
    }

    [Fact]
    public async Task MigrateAsync_Should_StopBeforeItConnects_When_AClusterHasNoLedgerLogin()
    {
        var logins = ClusterLogins();
        logins.Remove("Database:Availability:Username");
        logins.Remove("Database:Availability:Password");

        var migrate = () => Migrator("k8s-prd", logins).MigrateAsync(TestContext.Current.CancellationToken);

        await migrate.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("*Database:Availability:Username is not set*");
    }

    [Theory]
    [InlineData("k8s-dev", "Main")]
    [InlineData("k8s-prd", "Integration")]
    public async Task MigrateAsync_Should_StopBeforeItConnects_When_AClusterHostLoginHasTheCommittedRuntimeName(string environment, string host)
    {
        var logins = ClusterLogins();
        logins[$"Database:HostLogins:{host}:Username"] = DatabaseLoginSettings.CommittedRuntimeName;

        var migrate = () => Migrator(environment, logins).MigrateAsync(TestContext.Current.CancellationToken);

        var message = (await migrate.Should().ThrowExactlyAsync<InvalidOperationException>()).Which.Message;
        message.Should().Be($"The host database logins are misconfigured (ARV-083a): Database:HostLogins:{host}:Username is the runtime login name the committed " +
                            "appsettings files give every host (Database:Username), whose password every pod mounts from base-appsettings-secret; in a cluster this " +
                            "login has a name of its own (CWE-269).");
    }

    [Fact]
    public async Task MigrateAsync_Should_StopBeforeItConnects_When_TheSharedDevelopmentLoginsPasswordCannotBeAVerifier()
    {
        // vm-local without host logins: the job derives the shared login's verifier from Database:Password, so it follows their rules.
        var migrate = () => Migrator("vm-local", [], "short").MigrateAsync(TestContext.Current.CancellationToken);

        var message = (await migrate.Should().ThrowExactlyAsync<InvalidOperationException>()).Which.Message;
        message.Should().StartWith("The hosts' shared development login is misconfigured (ARV-083a)").And.Contain("at least 16 characters");
    }

    #endregion

    #region Declared grants

    private static readonly string[] AccountTables = ["user", "user_role", "user_session", "user_site", "refresh_token", "recovery_code"];

    private static readonly string[] Writes = ["INSERT", "UPDATE", "DELETE"];

    /// <summary>The privileges of a manifest entry, each with its columns when it is held on some columns only.</summary>
    private static List<(string Privilege, string Columns)> PrivilegesOf(string entry) =>
        [.. Privilege().Matches(entry[(entry.IndexOf('=', StringComparison.Ordinal) + 1)..]).Select(m => (m.Groups[1].Value, m.Groups[2].Value))];

    private static bool IsWrite(string entry) => PrivilegesOf(entry).Any(p => Writes.Contains(p.Privilege));

    [Fact]
    public void Manifest_Should_BeWellFormedAndInByteOrder_When_Read()
    {
        HostDatabaseAccess.Roles.Should().Equal("ariva_runtime", "ariva_main", "ariva_ingest", "ariva_stream", "ariva_cronz", "ariva_integration", "ariva_availability");
        HostDatabaseAccess.Grants.Keys.Should().BeEquivalentTo(HostDatabaseAccess.Roles);
        foreach (var (role, grants) in HostDatabaseAccess.Grants)
        {
            grants.Should().OnlyContain(g => Entry().IsMatch(g), $"{role}: relation=PRIVILEGE,... with SELECT, INSERT, UPDATE, DELETE only");
            grants.Select(HostDatabaseAccess.RelationOf).Should().BeInAscendingOrder(StringComparer.Ordinal, $"{role}: in byte order, as ariva-db.sh lists them")
                .And.OnlyHaveUniqueItems();
            foreach (var entry in grants)
            {
                var privileges = PrivilegesOf(entry);
                var table = privileges.Where(p => p.Columns.Length == 0).Select(p => Array.IndexOf(Writes.Prepend("SELECT").ToArray(), p.Privilege)).ToList();
                table.Should().BeInAscendingOrder($"{role} {entry}: table privileges in the order SELECT, INSERT, UPDATE, DELETE");
                privileges.Select(p => p.Privilege).Should().OnlyHaveUniqueItems($"{role} {entry}");
                foreach (var (_, columns) in privileges.Where(p => p.Columns.Length > 0))
                    columns.Split(' ').Should().BeInAscendingOrder(StringComparer.Ordinal, $"{role} {entry}: columns in byte order");
            }
        }
    }

    [Fact]
    public void Manifest_Should_NameOnlyRelationsTheScriptsCreate_When_ComparedWithTheScripts()
    {
        // Production schema comes from the scripts only (CLAUDE.md rule 5); schema_version is the script runner's own table.
        var created = SqlScriptCatalog.Embedded().SelectMany(s => CreatedTable().Matches(s.Sql)).Select(m => m.Groups[2].Value).Append("schema_version").ToHashSet(StringComparer.Ordinal);

        HostDatabaseAccess.Grants.Values.SelectMany(g => g).Select(HostDatabaseAccess.RelationOf).Distinct(StringComparer.Ordinal).Should().OnlyContain(r => created.Contains(r));
    }

    [Fact]
    public void Script0074_Should_GrantEachRoleExactlyItsManifest_When_Parsed()
    {
        var script = SqlScriptCatalog.Embedded().Single(s => s.Name == "0074_host_database_roles.sql").Sql;
        var code = Regex.Replace(script, "--[^\n]*", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var granted = new Dictionary<string, Dictionary<string, List<(string Privilege, string Columns)>>>(StringComparer.Ordinal);
        foreach (Match grant in TableGrant().Matches(code))
        {
            var relation = grant.Groups[3].Value.Trim('"');
            var role = granted.TryGetValue(grant.Groups[4].Value, out var held) ? held : granted[grant.Groups[4].Value] = new(StringComparer.Ordinal);
            var privileges = role.TryGetValue(relation, out var list) ? list : role[relation] = [];
            var columns = grant.Groups[2].Success ? string.Join(' ', grant.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)) : string.Empty;
            privileges.AddRange(grant.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Select(p => (p, columns)));
        }

        string[] order = ["SELECT", "INSERT", "UPDATE", "DELETE"];
        foreach (var role in HostDatabaseAccess.Roles)
        {
            var rebuilt = (granted.TryGetValue(role, out var relations) ? relations : new(StringComparer.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => r.Key + "=" + string.Join(',', r.Value.OrderBy(p => p.Columns.Length > 0).ThenBy(p => Array.IndexOf(order, p.Privilege))
                    .Select(p => p.Columns.Length == 0 ? p.Privilege : $"{p.Privilege}({p.Columns})")));
            rebuilt.Should().Equal(HostDatabaseAccess.Grants[role], $"script 0074 grants {role} exactly what HostDatabaseAccess declares");
        }

        granted.Keys.Should().BeSubsetOf(HostDatabaseAccess.Roles, "the script grants no other role a table");
        code.Should().Contain("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM ariva_runtime").And.Contain("REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM ariva_runtime")
            .And.Contain("ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM ariva_runtime")
            .And.Contain("GRANT EXECUTE ON FUNCTION ariva_retention_purge(text, integer) TO ariva_cronz")
            .And.Contain("REVOKE ALL ON FUNCTION ariva_data_protection_key_purge(integer, integer) FROM ariva_runtime")
            .And.Contain("GRANT EXECUTE ON FUNCTION ariva_data_protection_key_purge(integer, integer) TO ariva_cronz")
            .And.Contain("REVOKE ALL ON FUNCTION ariva_ensure_host_login(text, text, text) FROM PUBLIC")
            .And.Contain("SET search_path = pg_catalog, pg_temp").And.Contain("FROM pg_shdepend d").And.Contain("login_verifier !~ '^SCRAM-SHA-256");
        code.Should().NotContainAny("SECURITY DEFINER", "ariva_migration TO", "ariva_validation_reader TO");
    }

    [Fact]
    public void Manifest_Should_LetOnlyTheLedgerWriteItsMinutesAndCursors_When_Read()
    {
        // The ARV-150 review: a host that could record minutes ahead of the ledger, or move a site's cursor past minutes, would
        // decide what the availability report says.
        foreach (var (role, grants) in HostDatabaseAccess.Grants.Where(g => g.Key != HostDatabaseAccess.AvailabilityRole))
        {
            grants.Where(g => HostDatabaseAccess.RelationOf(g) is "availability_minute" or "availability_cursor").Where(IsWrite).Should().BeEmpty(role);
        }

        HostDatabaseAccess.Grants[HostDatabaseAccess.AvailabilityRole].Where(g => HostDatabaseAccess.RelationOf(g).StartsWith("availability_", StringComparison.Ordinal))
            .Should().Equal("availability_cursor=SELECT,INSERT,UPDATE", "availability_minute=SELECT,INSERT");
        HostDatabaseAccess.Grants[HostDatabaseAccess.AvailabilityRole].Where(IsWrite).Select(HostDatabaseAccess.RelationOf)
            .Should().Equal(new[] { "availability_cursor", "availability_minute" }, "the ledger writes nothing else");
    }

    [Fact]
    public void Manifest_Should_GiveIngestReadsOfItsTopologyOnly_When_Read()
    {
        // Ingest maps sensor events to zones and authenticates devices; it publishes to Kafka and writes no table beyond the base.
        HostDatabaseAccess.Grants[HostDatabaseAccess.IngestRole].Should().Equal(
            "airport=SELECT", "device=SELECT", "level=SELECT", "line=SELECT", "terminal=SELECT", "zone=SELECT", "zone_profile=SELECT");
    }

    [Fact]
    public void Manifest_Should_GiveNoHostButMainAnAccountWrite_When_Read()
    {
        // Accounts are Main's (sign-in, sessions, administration). Cronz only re-protects a TOTP secret under a new key.
        foreach (var role in new[] { HostDatabaseAccess.RuntimeRole, HostDatabaseAccess.IngestRole, HostDatabaseAccess.StreamRole, HostDatabaseAccess.IntegrationRole, HostDatabaseAccess.AvailabilityRole })
        {
            HostDatabaseAccess.Grants[role].Where(g => AccountTables.Contains(HostDatabaseAccess.RelationOf(g))).Where(IsWrite).Should().BeEmpty(role);
        }

        HostDatabaseAccess.Grants[HostDatabaseAccess.CronzRole].Where(g => AccountTables.Contains(HostDatabaseAccess.RelationOf(g)))
            .Should().Equal("user=SELECT(totp_secret_protected),UPDATE(totp_secret_protected)");
        HostDatabaseAccess.Grants[HostDatabaseAccess.MainRole].Should().Contain("user=SELECT,INSERT,UPDATE", "Ariva.Api.Main alone loads a whole account");
        HostDatabaseAccess.Grants[HostDatabaseAccess.StreamRole].Where(g => AccountTables.Contains(HostDatabaseAccess.RelationOf(g))).Should().BeEmpty();
    }

    [Fact]
    public void Manifest_Should_KeepTheBaseToWhatEveryHostShares_When_Read()
    {
        HostDatabaseAccess.Grants[HostDatabaseAccess.RuntimeRole].Should().Equal(
            "data_protection_key=SELECT,INSERT", "database_environment=SELECT", "outbox_message=SELECT,INSERT,UPDATE,DELETE", "processed_event=SELECT,DELETE",
            "schema_version=SELECT",
            "user=SELECT(all_sites created_by created_by_id created_on display_name email failed_login_count id is_break_glass is_disabled last_login_on "
            + "locked_until modified_by modified_by_id modified_on must_change_password totp_enrolled totp_last_step user_name)",
            "user_role=SELECT", "user_session=SELECT", "user_site=SELECT");
        HostDatabaseAccess.Functions.Keys.Should().Equal(HostDatabaseAccess.CronzRole);
        HostDatabaseAccess.Functions[HostDatabaseAccess.CronzRole].Should().Equal("ariva_data_protection_key_purge(integer,integer)", "ariva_retention_purge(text,integer)");
        new[] { "main", "ingest", "stream", "cronz", "integration", "web" }.Select(HostDatabaseAccess.RoleOf)
            .Should().Equal("ariva_main", "ariva_ingest", "ariva_stream", "ariva_cronz", "ariva_integration", null);
    }

    [Fact]
    public void Manifest_Should_KeepAccountCredentialsFromEveryHostButMain_When_Read()
    {
        // ARV-083a review (CWE-269, V13.2.2): every pod mounts the Data Protection certificate, so a host that could read the
        // password hashes or the protected TOTP seeds would hold all it needs to attack them. The base reads every column of an
        // account but those; Main reads the whole account and Cronz the seed it re-protects, and no other role reads any of them.
        string[] credentialColumns = ["password_algorithm", "password_hash", "password_iterations", "password_salt", "totp_secret_protected"];
        var baseRead = HostDatabaseAccess.Grants[HostDatabaseAccess.RuntimeRole].Single(g => HostDatabaseAccess.RelationOf(g) == "user");
        var baseColumns = PrivilegesOf(baseRead).Should().ContainSingle().Which;
        baseColumns.Privilege.Should().Be("SELECT");
        baseColumns.Columns.Split(' ').Should().NotIntersectWith(credentialColumns).And.Contain(new[] { "id", "is_disabled", "all_sites", "email", "is_break_glass",
            "must_change_password", "totp_enrolled" }, "the session, permission and site checks, the alert emails and the report deliveries read these");
        foreach (var role in new[] { HostDatabaseAccess.IngestRole, HostDatabaseAccess.StreamRole, HostDatabaseAccess.IntegrationRole, HostDatabaseAccess.AvailabilityRole })
            HostDatabaseAccess.Grants[role].Should().NotContain(g => HostDatabaseAccess.RelationOf(g) == "user", role);
    }

    [Fact]
    public void Manifest_Should_LetOnlyTheKeyRingUpgradeCheckHostsReadEveryStoredSecret_When_Read()
    {
        // ARV-083a review (R2-2): the key ring upgrade check at start (KeyRingUpgradeGate) reads the three columns that hold Data
        // Protection payloads, so a host runs it only when its login, its role with the base, may read all three; a host started
        // with the check and without them would stop on 42501.
        (string Relation, string Column)[] payloads = [("user", "totp_secret_protected"), ("integration_client", "totp_secret_protected"), ("outbound_endpoint", "secret_protected")];
        bool Reads(string role, string relation, string column) =>
            HostDatabaseAccess.Grants[role].Concat(HostDatabaseAccess.Grants[HostDatabaseAccess.RuntimeRole])
                .Where(g => HostDatabaseAccess.RelationOf(g) == relation)
                .SelectMany(PrivilegesOf)
                .Any(p => p.Privilege == "SELECT" && (p.Columns.Length == 0 || p.Columns.Split(' ').Any(c => c == column)));

        HostDatabaseAccess.HostRoles.Where(role => payloads.All(p => Reads(role, p.Relation, p.Column)))
            .Should().Equal(HostDatabaseAccess.KeyRingUpgradeCheckRoles, "the check runs exactly where its read is granted");
        HostDatabaseAccess.KeyRingUpgradeCheckRoles.Should().Equal(HostDatabaseAccess.MainRole, HostDatabaseAccess.CronzRole);
        new[] { "main", "ingest", "stream", "cronz", "integration", "base", null }.Select(HostDatabaseAccess.RunsKeyRingUpgradeCheck)
            .Should().Equal(true, false, false, true, false, false, false);
    }

    [Fact]
    public void Manifest_Should_GiveCronzTheKeyLifecycleReadsAndPurgeOnly_When_Read()
    {
        // ARV-083a review (R2-2): the key lifecycle round (ARV-207) reads the dates, the status and whether a credential is set, and
        // runs script 0072's purge; Cronz is granted those columns, SELECT only, and no other column of the three tables.
        var cronz = HostDatabaseAccess.Grants[HostDatabaseAccess.CronzRole];
        cronz.Should().Contain("display=SELECT(credential_hash credential_issued_on deleted_on)")
            .And.Contain("integration_client=SELECT(id secret_changed_utc status totp_changed_utc totp_secret_protected),UPDATE(totp_secret_protected)")
            .And.Contain("outbound_endpoint=SELECT(id secret_changed_utc secret_protected status),UPDATE(secret_protected)")
            .And.Contain("device=SELECT");
        HostDatabaseAccess.Functions[HostDatabaseAccess.CronzRole].Should().Contain("ariva_data_protection_key_purge(integer,integer)");
        HostDatabaseAccess.Functions.Where(f => f.Key != HostDatabaseAccess.CronzRole).Should().BeEmpty("no other role runs a purge");
    }

    #endregion

    #region Where the logins may go

    [Fact]
    public void CommittedSettings_Should_NeverConfigureAHostLoginOrTheLedgersLogin_When_TheyAreInTheRepository()
    {
        // The logins' names and passwords come from the chart's secrets (the migration job, and each host its own), never from a
        // committed file that every host or every environment layers.
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Backplane"), "appsettings*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Simulation"), "appsettings*.json", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.EndsWith("appsettings.local.json", StringComparison.Ordinal))
            .ToList();

        files.Should().NotBeEmpty();
        files.Where(f =>
            {
                var configuration = new ConfigurationBuilder().AddJsonFile(f).Build();
                return configuration.GetSection(HostLoginSettings.SectionName).Exists() || configuration.GetSection(AvailabilityLoginSettings.SectionName).Exists();
            })
            .Should().BeEmpty();
    }

    [Fact]
    public void LoginSettings_Should_BeTakenOnlyByTheMigratorAndTheLedger_When_ConstructorsAreReflected()
    {
        // Every host's password reaches the migration job alone; the ledger's, the job and the ledger.
        var assemblies = new[]
        {
            typeof(Ariva.Infra._IAssemblyMark).Assembly, typeof(Ariva.Di._IAssemblyMark).Assembly, typeof(Ariva.Api.Common._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Main._IAssemblyMark).Assembly, typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly, typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly, typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
        };
        var types = assemblies.SelectMany(TypesOf).ToList();

        TakersOf(types, typeof(HostLoginSettings)).Should().Equal(typeof(DatabaseMigrator).FullName);
        TakersOf(types, typeof(AvailabilityLoginSettings)).Should().Equal(typeof(AvailabilityLedger).FullName, typeof(DatabaseMigrator).FullName);
    }

    private static List<string> TakersOf(IEnumerable<Type> types, Type settings)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return [.. types.Where(t => t != settings &&
                (t.GetConstructors(all).Any(c => c.GetParameters().Any(p => p.ParameterType == settings)) ||
                 t.GetFields(all).Any(f => f.FieldType == settings) || t.GetProperties(all).Any(p => p.PropertyType == settings)))
            .Select(t => t.FullName).Order(StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null);
        }
    }

    #endregion

    #region Patterns

    [GeneratedRegex(@"^[a-z_]+=(?:SELECT|INSERT|UPDATE|DELETE)(?:\([a-z_]+(?: [a-z_]+)*\))?(?:,(?:SELECT|INSERT|UPDATE|DELETE)(?:\([a-z_]+(?: [a-z_]+)*\))?)*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Entry();

    [GeneratedRegex(@"(SELECT|INSERT|UPDATE|DELETE)(?:\(([a-z_ ]+)\))?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Privilege();

    [GeneratedRegex(@"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?(""?)([a-z_0-9]+)\1", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreatedTable();

    [GeneratedRegex(@"^GRANT ((?:SELECT|INSERT|UPDATE|DELETE)(?:, (?:SELECT|INSERT|UPDATE|DELETE))*)(?: \(([a-z_, ]+)\))? ON (""?[a-z_0-9]+""?) TO ([a-z_]+);", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableGrant();

    #endregion
}
