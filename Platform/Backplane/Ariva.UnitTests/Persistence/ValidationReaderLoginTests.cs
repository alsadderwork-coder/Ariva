using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ariva.Di.Extensions;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-104g1 (CWE-269, CWE-863, CWE-287): the validation reader login, the only login that reads the shadow nowcast. Its own
/// settings section, refused when it is the runtime or the migration login (or is otherwise unusable), a connection built only
/// by the validation service, the start-up guard every host runs, and no committed file that configures it. The database side
/// (the login's grants, the runtime login still refused, the guard's membership check) is in Ariva.IntegrationTests
/// (ValidationReaderLoginTests).
/// </summary>
public sealed class ValidationReaderLoginTests
{
    #region Fixtures

    private const string ReaderPassword = "reader-password-0123456789";

    private static DatabaseSettings Database(string username = "ariva_app", string migration = "ariva") => new()
    {
        Host = "db.example",
        Port = 6543,
        Name = "ariva_site",
        Username = username,
        Password = "runtime-password-0123456789",
        UseEncryption = true,
        Migration = new MigrationLoginSettings { Username = migration, Password = "owner-password-0123456789" }
    };

    private static ValidationReaderSettings Reader(string username = "ariva_validation", string password = ReaderPassword) =>
        new() { Username = username, Password = password };

    #endregion

    #region Settings

    [Fact]
    public void Problems_Should_BeEmpty_When_TheLoginIsNotConfigured()
    {
        new ValidationReaderSettings().Problems(Database()).Should().BeEmpty();
        new ValidationReaderSettings().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Problems_Should_BeEmpty_When_TheReaderIsALoginOfItsOwn()
    {
        Reader().Problems(Database()).Should().BeEmpty();
        Reader().IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("Ariva_Validation")]
    [InlineData("ariva validation")]
    [InlineData("ariva-validation")]
    [InlineData("1reader")]
    [InlineData("x; DROP ROLE ariva")]
    [InlineData("pg_reader")]
    [InlineData("reader\u200b")]
    [InlineData("reader\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Problems_Should_RefuseTheName_When_ItIsNotAPlainLowerCaseIdentifier(string username)
    {
        Reader(username).Problems(Database()).Should().ContainSingle(p => p.Contains("plain lower-case PostgreSQL identifier", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ariva_runtime")]
    [InlineData("ariva_migration")]
    [InlineData("ariva_validation_reader")]
    public void Problems_Should_RefuseTheName_When_ItIsOneOfArivasRoles(string username)
    {
        Reader(username).Problems(Database()).Should().Contain(p => p.Contains("one of Ariva's database roles", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ariva_app", "ariva_app")]
    [InlineData("ARIVA_APP", "ariva_app")]
    [InlineData("ariva_app", "Ariva_App")]
    public void Problems_Should_RefuseTheRuntimeLogin_When_TheNamesMatchInAnyCase(string runtime, string reader)
    {
        Reader(reader).Problems(Database(runtime)).Should().Contain(p => p.Contains("is the runtime login", StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_Should_RefuseTheMigrationLogin_When_TheNamesMatch()
    {
        Reader("ariva_owner").Problems(Database(migration: "ariva_owner")).Should().Contain(p => p.Contains("is the migration login", StringComparison.Ordinal));
        // Without a migration login of its own, the migration job uses the runtime login: that one is the migration login too.
        Reader("ariva_app").Problems(Database(migration: string.Empty)).Should().Contain(p => p.Contains("is the migration login", StringComparison.Ordinal))
            .And.Contain(p => p.Contains("is the runtime login", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("fifteen-chars-1", true)]
    [InlineData("sixteen-chars-12", false)]
    public void Problems_Should_RequireSixteenCharacters_When_ThePasswordIsSet(string password, bool refused)
    {
        var problems = Reader(password: password).Problems(Database());

        if (refused)
            problems.Should().ContainSingle(p => p.Contains("at least 16 characters", StringComparison.Ordinal));
        else
            problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pässword-0123456789")]
    [InlineData("password\t0123456789")]
    [InlineData("password-0123456789\n")]
    [InlineData("password-0123456789\r\n")]
    [InlineData("password-0123456789\u00a0")]
    [InlineData("password-\u202e0123456789")]
    public void Problems_Should_RefuseThePassword_When_ItIsNotPrintableAscii(string password)
    {
        // The migration job derives the SCRAM-SHA-256 verifier itself; without SASLprep that is exact for printable ASCII only.
        Reader(password: password).Problems(Database()).Should().ContainSingle(p => p.Contains("printable ASCII", StringComparison.Ordinal))
            .Which.Should().Contain("trailing newline from a password file", "the operator is told what to look for (ARV-104g1 re-check)").And.NotContain(password.Trim());
    }

    [Fact]
    public void Problems_Should_RefuseAReusedPassword_When_ItIsTheRuntimeOrTheMigrationLoginsPassword()
    {
        Reader(password: "runtime-password-0123456789").Problems(Database()).Should().ContainSingle(p => p.Contains("must not be the runtime or the migration", StringComparison.Ordinal));
        Reader(password: "owner-password-0123456789").Problems(Database()).Should().ContainSingle(p => p.Contains("must not be the runtime or the migration", StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_Should_RefuseAPassword_When_NoNameIsSet()
    {
        new ValidationReaderSettings { Password = ReaderPassword }.Problems(Database()).Should().ContainSingle(p => p.Contains("without", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureValid_Should_ListEveryProblemWithoutAnySecret_When_TheReaderIsTheRuntimeLogin()
    {
        var database = Database();
        var reader = Reader("ariva_app", database.Password);

        var act = () => reader.EnsureValid(database);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("is the runtime login").And.Contain("must not be the runtime or the migration");
        message.Should().NotContain(database.Password).And.NotContain(database.Migration.Password).And.NotContain(ReaderPassword);
    }

    [Fact]
    public void FromConfiguration_Should_ReadDatabaseValidationReader_When_TheSectionIsGiven()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Username"] = "ariva_app",
            ["Database:ValidationReader:Username"] = "ariva_validation",
            ["Database:ValidationReader:Password"] = ReaderPassword
        }).Build();

        var reader = ValidationReaderSettings.FromConfiguration(configuration);

        reader.Username.Should().Be("ariva_validation");
        reader.Password.Should().Be(ReaderPassword);
        ValidationReaderSettings.SectionName.Should().Be("Database:ValidationReader");
        DatabaseSettings.FromConfiguration(configuration).Username.Should().Be("ariva_app", "the runtime settings are unchanged by the reader's section");
    }

    #endregion

    #region Connection

    [Fact]
    public void BuildReaderConnectionString_Should_UseTheReaderLoginWithItsOwnNameAndPool_When_Configured()
    {
        var parsed = new NpgsqlConnectionStringBuilder(Reader(password: "reader;Include Error Detail=true;x=").BuildReaderConnectionString(Database()));

        parsed.Username.Should().Be("ariva_validation");
        parsed.Password.Should().Be("reader;Include Error Detail=true;x=", "a password must not be able to add connection options");
        parsed.IncludeErrorDetail.Should().BeFalse();
        parsed.Host.Should().Be("db.example");
        parsed.Port.Should().Be(6543);
        parsed.Database.Should().Be("ariva_site");
        parsed.SslMode.Should().Be(SslMode.VerifyFull, "the reader follows the deployment's TLS setting");
        parsed.ApplicationName.Should().Be("ariva-validation-reader");
        parsed.MaxPoolSize.Should().Be(ValidationReaderSettings.MaxPoolSize);
    }

    [Fact]
    public void BuildReaderConnectionString_Should_Refuse_When_TheReaderIsNotConfiguredOrNotItsOwnLogin()
    {
        var notConfigured = () => new ValidationReaderSettings().BuildReaderConnectionString(Database());
        var runtime = () => Reader("ariva_app").BuildReaderConnectionString(Database());

        notConfigured.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
        runtime.Should().Throw<InvalidOperationException>().WithMessage("*runtime login*");
    }

    /// <summary>The validation service with only what its shadow read uses (ARV-104g2 gave it the campaign reads and the computation).</summary>
    private static Ariva.Infra.Services.Validation.SvcValidationResults Results(DatabaseSettings database, ValidationReaderSettings reader) =>
        new(null, null, TimeProvider.System, null, null, null, null, new Ariva.Infra.Settings.ValidationResultsSettings(), null, database, reader);

    [Fact]
    public async Task ReadShadow_Should_RefuseWithoutConnecting_When_TheLoginIsAbsentOrNotItsOwn()
    {
        // The database host does not exist: an answer proves nothing was opened (CWE-269: never with a runtime login).
        var absent = await Results(Database(), new ValidationReaderSettings())
            .ReadShadowAsync("DMO", ["Q1"], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), TestContext.Current.CancellationToken);
        var runtime = await Results(Database(), Reader("ariva_app"))
            .ReadShadowAsync("DMO", ["Q1"], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), TestContext.Current.CancellationToken);

        absent.HasErrors.Should().BeTrue();
        absent.ErrorMessages.Should().ContainSingle().Which.Should().Contain("not configured");
        runtime.ErrorMessages.Should().ContainSingle().Which.Should().Contain("misconfigured").And.NotContain(ReaderPassword);
    }

    public static TheoryData<string, string[], DateTime, DateTime, string> OutOfBoundsReads()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        return new TheoryData<string, string[], DateTime, DateTime, string>
        {
            { null, ["Q1"], from, to, "site" },
            { "dmo", ["Q1"], from, to, "site" },
            { "DMO/Q1", ["Q1"], from, to, "site" },
            { "DMO", null, from, to, "queue zone" },
            { "DMO", [], from, to, "queue zone" },
            { "DMO", [.. Enumerable.Range(0, 51).Select(i => $"Q{i}")], from, to, "queue zone" },
            { "DMO", ["Q1", "Q1"], from, to, "queue zone" },
            { "DMO", ["Q1", ""], from, to, "queue zone" },
            { "DMO", [new string('Q', 200)], from, to, "queue zone" },
            { "DMO", ["Q1"], DateTime.SpecifyKind(from, DateTimeKind.Local), to, "window" },
            { "DMO", ["Q1"], from, DateTime.SpecifyKind(to, DateTimeKind.Unspecified), "window" },
            { "DMO", ["Q1"], from, from, "window" },
            { "DMO", ["Q1"], to, from, "window" },
            { "DMO", ["Q1"], from, from.AddDays(33).AddTicks(1), "window" }
        };
    }

    [Theory]
    [MemberData(nameof(OutOfBoundsReads))]
    public async Task ReadShadow_Should_RefuseWithoutConnecting_When_TheReadIsOutOfBounds(string site, string[] zones, DateTime from, DateTime to, string what)
    {
        // CWE-120, CWE-863: one valid site, 1 to 50 distinct zone names whose keys fit, a UTC window of at most 33 days.
        var result = await Results(Database(), Reader()).ReadShadowAsync(site, zones, from, to, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeTrue();
        result.ErrorMessages.Should().ContainSingle().Which.Should().Contain(what);
    }

    [Fact]
    public void ScramVerifier_Should_MatchTheRfc7677Exchange_When_GivenItsPasswordAndSalt()
    {
        // RFC 7677, section 3: user "user", password "pencil", salt W22ZaJ0SNY7soEsUEjb6gQ==, 4096 iterations. The verifier's keys
        // must reproduce the exchange: the server signature v=, and the client proof p= must recover a ClientKey whose SHA-256
        // is the StoredKey.
        var verifier = ScramVerifier.For("pencil", Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ=="), 4096);
        var match = Regex.Match(verifier, @"^SCRAM-SHA-256\$4096:W22ZaJ0SNY7soEsUEjb6gQ==\$([A-Za-z0-9+/]{43}=):([A-Za-z0-9+/]{43}=)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        match.Success.Should().BeTrue(verifier);
        var storedKey = Convert.FromBase64String(match.Groups[1].Value);
        var serverKey = Convert.FromBase64String(match.Groups[2].Value);
        var authMessage = System.Text.Encoding.UTF8.GetBytes(
            "n=user,r=rOprNGfwEbeRWgbNEkqO," +
            "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096," +
            "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0");

        Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(serverKey, authMessage)).Should().Be("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=");
        var proof = Convert.FromBase64String("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=");
        var clientSignature = System.Security.Cryptography.HMACSHA256.HashData(storedKey, authMessage);
        var clientKey = proof.Zip(clientSignature, (a, b) => (byte)(a ^ b)).ToArray();
        System.Security.Cryptography.SHA256.HashData(clientKey).Should().Equal(storedKey);
    }

    [Fact]
    public void ScramVerifier_Should_DrawANewSaltAndRefuseFewIterations_When_Computed()
    {
        var first = ScramVerifier.For(ReaderPassword);
        var second = ScramVerifier.For(ReaderPassword);

        first.Should().NotBe(second, "every verifier has its own random salt").And.StartWith("SCRAM-SHA-256$4096:").And.NotContain(ReaderPassword);
        Regex.IsMatch(first, @"^SCRAM-SHA-256\$[0-9]{4,7}:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Should().BeTrue("the shape script 0049 accepts");
        var few = () => ScramVerifier.For(ReaderPassword, new byte[16], 4095);
        few.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Start-up guard and registration

    [Theory]
    [InlineData("ariva_app")]
    [InlineData("ariva")]
    public async Task Guard_Should_RefuseToStart_When_TheReaderIsTheRuntimeOrTheMigrationLogin(string username)
    {
        // No database check is due (neither the schema check nor the development migration), so only the names decide.
        var guard = new ValidationReaderGuard(Database(), Reader(username));

        var act = () => guard.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CWE-269").And.NotContain(ReaderPassword);
    }

    [Fact]
    public async Task Guard_Should_Start_When_TheReaderIsAbsentOrItsOwnAndNoDatabaseCheckIsDue()
    {
        await new ValidationReaderGuard(Database(), new ValidationReaderSettings()).StartAsync(TestContext.Current.CancellationToken);
        await new ValidationReaderGuard(Database(), Reader()).StartAsync(TestContext.Current.CancellationToken);

        // What the database check asks (M2 of the review): membership of the reader role or pg_read_all_data (MEMBER, so any grant
        // and every superuser), or SELECT on any value column of the table or its chunks (grants, PUBLIC, ownership).
        ShadowReadAccess.Query.Should().Contain("'ariva_validation_reader', 'pg_read_all_data'").And.Contain("pg_has_role(l.oid, g.oid, 'MEMBER')")
            .And.Contain("to_regclass('public.queue_minute_shadow')").And.Contain("pg_inherits").And.Contain("has_column_privilege(l.oid, r.oid, a.attnum, 'SELECT')")
            .And.Contain("NOT a.attisdropped").And.Contain("NOT IN ('zone_key', 'minute_utc')").And.Contain("rolname = @login");
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Ariva.Api.Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Theory]
    [InlineData("k8s-dev", false, true)]
    [InlineData("k8s-prd", false, true)]
    [InlineData("k8s-prd", true, false)]
    [InlineData("vm-local", false, false)]
    public async Task Guard_Should_RefuseTheReader_When_TheHostDoesNotRunTheValidationServiceOutsideVmLocal(string environment, bool validationHost, bool refused)
    {
        // L6 of the review (CWE-863): outside vm-local only Ariva.Api.Main (which registers ValidationReaderHost) may hold the login.
        var guard = new ValidationReaderGuard(Database(), Reader(), new FakeEnvironment(environment), validationHost ? new ValidationReaderHost() : null);

        var act = () => guard.StartAsync(TestContext.Current.CancellationToken);

        if (refused)
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("does not run the validation service").And.NotContain(ReaderPassword);
        else
            await act.Should().NotThrowAsync();
        await new ValidationReaderGuard(Database(), new ValidationReaderSettings(), new FakeEnvironment(environment)).StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_StartWithTheReaderLoginOnlyWhenItIsMain_When_RunInACluster(string host)
    {
        // The real composition: Ariva.Api.Main declares itself the validation service's host; every other host refuses the login.
        await using var app = ArivaHosts.Create(host, "k8s-dev", builder => builder
            .UseSetting("Database:ValidationReader:Username", "ariva_validation")
            .UseSetting("Database:ValidationReader:Password", ReaderPassword));

        var start = () => app.Services;

        if (host == ArivaHosts.Main)
            start.Should().NotThrow();
        else
            start.Should().Throw<InvalidOperationException>().WithMessage("*does not run the validation service*");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AddArivaPersistence_Should_RegisterTheGuardAfterTheSchemaServices_When_AnyHostComposes(bool allowSchemaUpdate, bool verifySchema)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:AllowSchemaUpdate"] = allowSchemaUpdate.ToString(),
            ["Database:VerifySchemaOnStartup"] = verifySchema.ToString(),
            ["Database:ValidationReader:Username"] = "ariva_validation"
        }).Build();
        var services = new ServiceCollection();

        services.AddArivaPersistence(configuration);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        hosted.Should().EndWith(typeof(ValidationReaderGuard), "the guard runs after the development migration or the schema check, which it relies on");
        hosted.Should().ContainSingle(t => t == typeof(ValidationReaderGuard));
        services.Should().ContainSingle(d => d.ServiceType == typeof(ValidationReaderSettings))
            .Which.ImplementationInstance.Should().BeOfType<ValidationReaderSettings>().Which.Username.Should().Be("ariva_validation");
    }

    #endregion

    #region Where the login may go

    [Fact]
    public void CommittedSettings_Should_NeverConfigureTheReader_When_TheyAreInTheRepository()
    {
        // The base files reach every host and the service files are committed: the reader's name and password come only from
        // the secret ariva-validation-reader (api-main and the migration job) or, on a developer machine, from git-ignored files
        // and the AppHost's user secrets.
        var configured = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Backplane"), "appsettings*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Simulation"), "appsettings*.json", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.EndsWith("appsettings.local.json", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ValidationReader", StringComparison.OrdinalIgnoreCase))
            .ToList();

        configured.Should().BeEmpty();
    }

    [Fact]
    public void ReaderSettings_Should_BeHeldOnlyByTheValidationServiceTheMigrationAndTheGuard_When_TheTypeGraphIsReflected()
    {
        // Who can reach the reader's name and password: the validation service (its shadow read opens the connection), the
        // migrator (it passes them to ariva_ensure_validation_reader_login and never connects as the reader), the vm-local
        // migration that runs the migrator at start-up, and the guard (names only). No runtime type holds them.
        HoldersOf(typeof(ValidationReaderSettings)).Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal).Should().Equal(
            "Ariva.Infra.Services.Validation.SvcValidationResults", "Ariva.Infra.Settings.ValidationReaderSettings", "Ariva.Infra.Timescale.DatabaseMigrator",
            "Ariva.Infra.Timescale.DevelopmentMigrationService", "Ariva.Infra.Timescale.ValidationReaderGuard");

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        typeof(DatabaseSettings).GetMembers(all).Concat(typeof(MigrationLoginSettings).GetMembers(all))
            .Where(m => m.Name.Contains("Reader", StringComparison.OrdinalIgnoreCase) || (m is PropertyInfo p && p.PropertyType == typeof(ValidationReaderSettings)))
            .Should().BeEmpty("the runtime settings never carry the reader login, so no runtime connection can be built from it");
    }

    [Fact]
    public void ReaderSettings_Should_BeNamedOnlyByTheFiveFilesThatNeedThem_When_SourcesAreScanned()
    {
        // L5 of the review (CWE-863): the settings type is internal, and only the settings, the validation service's shadow read,
        // the migrator, the start-up guard and the composition root name it, its section or its environment variables.
        typeof(ValidationReaderSettings).IsPublic.Should().BeFalse("only Ariva.Infra and Ariva.Di (InternalsVisibleTo) reach it");
        var names = new Regex(@"\bValidationReaderSettings\b|Database:ValidationReader|ValidationReader__", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        ProductionSources(names).Should().Equal(
            "Backplane/Ariva.Di/Extensions/PersistenceExtensions.cs", "Backplane/Ariva.Infra/Services/Validation/SvcValidationResults.Shadow.cs",
            "Backplane/Ariva.Infra/Settings/ValidationReaderSettings.cs", "Backplane/Ariva.Infra/Timescale/DatabaseMigrator.cs",
            "Backplane/Ariva.Infra/Timescale/ValidationReaderGuard.cs");
    }

    private static List<string> ProductionSources(Regex pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Backplane"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains("Ariva.UnitTests", StringComparison.Ordinal) && !f.Contains("Ariva.IntegrationTests", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void ReaderConnection_Should_BeBuiltOnlyByTheValidationService_When_SourcesAreScanned()
    {
        var builder = new Regex(@"\bBuildReaderConnectionString\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        var callers = ProductionSources(builder);

        callers.Should().Equal("Backplane/Ariva.Infra/Services/Validation/SvcValidationResults.Shadow.cs", "Backplane/Ariva.Infra/Settings/ValidationReaderSettings.cs");
    }

    [Fact]
    public void Script0049_Should_GrantTheLoginTheReaderRoleAndConnectOnly_When_Parsed()
    {
        // What the validation reads through the login need, and nothing else (the service's other reads use the runtime login).
        var script = File.ReadAllText(Path.Combine(RepositoryPaths.Platform, "Backplane", "Ariva.Infra", "Timescale", "Scripts", "0049_validation_reader_login.sql"));
        var code = Regex.Replace(script, "--[^\n]*", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        Regex.Matches(code, @"\bGRANT\b[^;']*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(m => m.Value.Trim())
            .Should().Equal("GRANT ariva_validation_reader TO %I", "GRANT CONNECT ON DATABASE %I TO %I");
        code.Should().Contain("REVOKE ALL ON FUNCTION ariva_ensure_validation_reader_login(text, text) FROM PUBLIC");
        code.Should().Contain("SET search_path = pg_catalog, pg_temp", "L8 of the review: no other schema can stand in for a catalog")
            .And.Contain("FROM pg_shdepend d").And.Contain("aclexplode(db.datacl)").And.Contain("login_verifier !~ '^SCRAM-SHA-256");
        code.Should().NotContainAny("SECURITY DEFINER", "ariva_runtime TO", "ariva_migration TO");
    }

    #endregion

    #region Type graph

    private static readonly Assembly[] Assemblies =
    [
        typeof(Ariva.Core._IAssemblyMark).Assembly,
        typeof(Ariva.Infra._IAssemblyMark).Assembly,
        typeof(Ariva.Di._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Common._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
    ];

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<Type> Mentioned(Type type)
    {
        if (type.IsArray || type.IsByRef || type.IsPointer)
            return Mentioned(type.GetElementType()!);
        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Mentioned).Prepend(type) : [type];
    }

    private static Type Owner(Type type)
    {
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            type = type.DeclaringType;
        return type;
    }

    /// <summary>Every Ariva type whose fields can hold <paramref name="held"/>, directly or through other types.</summary>
    private static HashSet<Type> HoldersOf(Type held)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var fields = Assemblies.SelectMany(TypesOf).Distinct().ToDictionary(t => t, t =>
        {
            var mentioned = new HashSet<Type>();
            for (var current = t; current is not null && current != typeof(object); current = current.BaseType)
                foreach (var field in current.GetFields(all))
                    foreach (var m in Mentioned(field.FieldType))
                        mentioned.Add(m.IsGenericType ? m.GetGenericTypeDefinition() : m);
            return mentioned;
        });
        var holders = new HashSet<Type> { held };
        bool grown;
        do
        {
            grown = false;
            foreach (var (type, mentioned) in fields)
            {
                if (!holders.Contains(type) && mentioned.Any(holders.Contains))
                    grown = holders.Add(type) || grown;
            }
        }
        while (grown);

        return holders;
    }

    #endregion
}
