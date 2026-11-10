using Ariva.Di.Extensions;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-294 (CWE-400): each host reads the idle transaction limit the server applies to its runtime login (SHOW) at start and refuses
/// to start when it is not above the host's longest wait. The value as PostgreSQL shows it, the decision, and that a host that does not
/// reach the database at start reads nothing; the integration test <c>DatabaseSessionLimitTests</c> runs it against the server.
/// </summary>
public sealed class IdleTransactionLimitGateTests
{
    #region Helpers

    private static IdleTransactionBudget Defaults() => IdleTransactionBudget.FromConfiguration(new ConfigurationBuilder().Build(), new DatabaseSettings());

    #endregion

    #region Reading the value

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("5min", 300_000L)]
    [InlineData("16min", 960_000L)]
    [InlineData("3s", 3_000L)]
    [InlineData("181s", 181_000L)]
    [InlineData("1500ms", 1_500L)]
    [InlineData("1h", 3_600_000L)]
    [InlineData("2h", 7_200_000L)]
    [InlineData("1d", 86_400_000L)]
    [InlineData("250", 250L)]
    [InlineData("2147483647ms", 2_147_483_647L)]
    [InlineData("9999999999d", 863_999_999_913_600_000L)]
    public void Milliseconds_Should_ReadPostgresUnits_When_TheServerShowsALimit(string shown, long expected)
    {
        IdleTransactionLimitGate.Milliseconds(shown).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("min")]
    [InlineData("5 min")]
    [InlineData("-5s")]
    [InlineData("5m")]
    [InlineData("5MIN")]
    [InlineData("1y")]
    [InlineData("1234567890123s")]
    [InlineData("12345678901ms")]
    [InlineData("99999999999d")]
    [InlineData("5min;")]
    public void Milliseconds_Should_BeNull_When_TheValueIsNotOneLimit(string shown)
    {
        // A value that is not a whole number with one of PostgreSQL's units is not guessed at; more than ten digits are not read (the
        // security review's A2: eleven digits of days would overflow a long), and ten digits of days still fit.
        IdleTransactionLimitGate.MaxDigits.Should().Be(10);
        IdleTransactionLimitGate.Milliseconds(shown).Should().BeNull();
    }

    #endregion

    #region The decision

    [Theory]
    [InlineData("5min")]
    [InlineData("181s")]
    [InlineData("180001ms")]
    [InlineData("16min")]
    [InlineData("2h")]
    [InlineData("0")]
    public void Problem_Should_BeNone_When_TheServersLimitIsAboveTheLongestWaitOrThereIsNone(string shown)
    {
        // The longest wait at the defaults is a report's send within its budget, 180 s; no limit (0) ends no transaction.
        IdleTransactionLimitGate.Problem(shown, Defaults(), 300).Should().BeNull();
    }

    [Theory]
    [InlineData("3min", "180")]
    [InlineData("180s", "180")]
    [InlineData("1min", "60")]
    [InlineData("1500ms", "1.5")]
    public void Problem_Should_NameTheLongestWait_When_TheServersLimitIsNotAboveIt(string shown, string seconds)
    {
        // A limit the migration job set from another copy of the settings, or one set on the server by hand, below what this host needs.
        IdleTransactionLimitGate.Problem(shown, Defaults(), 300).Should().Be(
            $"The runtime login's idle_in_transaction_session_timeout on the server ({seconds} s) must be above the longest a transaction of this host may wait on other work: "
            + "a scheduled report's send, its delivery row and the report slot held, 180 s (ARV-294). Run the migration job with Database:IdleInTransactionTimeoutSeconds (300 s), "
            + "or set the login's limit above that wait.");
    }

    [Fact]
    public void Problem_Should_RefuseToStart_When_TheValueCannotBeRead()
    {
        var problem = IdleTransactionLimitGate.Problem("soon", Defaults(), 300);

        problem.Should().Be("The runtime login's idle_in_transaction_session_timeout on the server could not be read as a time (ARV-294); the host does not start without knowing it.");
        problem.Should().NotContain("soon", "the value the server gave is not repeated");
    }

    [Fact]
    public void Problem_Should_JudgeTheHostsOwnBudget_When_ItsSettingsAreAtTheirMaxima()
    {
        // At the settings' maxima the longest is a key ring load that creates a key (three calls of 50 + 30 s): a server limit of 4 min is
        // too short for that host.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:CommandTimeoutSeconds"] = "300",
            ["Database:ConnectTimeoutSeconds"] = "60"
        }).Build();
        var budget = IdleTransactionBudget.FromConfiguration(configuration, DatabaseSettings.FromConfiguration(configuration));

        IdleTransactionLimitGate.Problem("4min", budget, 300).Should().Contain("a Data Protection key ring load inside a transaction (read, store a new key, read again), 240 s");
        IdleTransactionLimitGate.Problem("241s", budget, 300).Should().BeNull();
    }

    #endregion

    #region Start

    [Fact]
    public async Task StartAsync_Should_ReadNothing_When_TheHostDoesNotReachTheDatabaseAtStart()
    {
        // As ValidationReaderGuard: a host that neither verifies nor migrates the schema at start starts without the database (no
        // server listens on this port, so a read would fail).
        var gate = new IdleTransactionLimitGate(new DatabaseSettings { Host = "127.0.0.1", Port = 1, VerifySchemaOnStartup = false, AllowSchemaUpdate = false }, Defaults(),
            NullLogger<IdleTransactionLimitGate>.Instance);

        var start = () => gate.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AddArivaPersistence_Should_RegisterTheGateBeforeTheValidationReaderGuard_When_AnyHostComposes(bool allowSchemaUpdate, bool verifySchema)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:AllowSchemaUpdate"] = allowSchemaUpdate.ToString(),
            ["Database:VerifySchemaOnStartup"] = verifySchema.ToString()
        }).Build();
        var services = new ServiceCollection();

        services.AddArivaPersistence(configuration);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        hosted.Should().ContainSingle(t => t == typeof(IdleTransactionLimitGate));
        hosted.IndexOf(typeof(IdleTransactionLimitGate)).Should().Be(hosted.Count - 2, "after the development migration and the schema check, which set and verify the login, and before the last guard");
    }

    #endregion
}
