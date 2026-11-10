using System.Globalization;
using Ariva.Infra.Alerting;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Messaging;
using Ariva.Infra.Services.Displays;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Staffing;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>
/// ARV-292, ARV-294 (CWE-400): the longest each transaction of a host may stay idle on the server while the host waits on other work,
/// from that host's own settings, and the check that the runtime login's limit (<see cref="DatabaseSettings.IdleInTransactionTimeoutSeconds"/>,
/// idle_in_transaction_session_timeout) is above the longest, so the server never ends a transaction a host is still working through.
/// Since ARV-294 no wait passes <see cref="MaxWaitSeconds"/> at any value its settings allow: the alert email round sends outside its
/// claim, the uploads and the site configuration transfer do their file work outside their transactions, the CSV imports and the site
/// configuration import write their own audit entries after their transactions end, the availability ledger reads Redis before its
/// lock, and a report's send, a staffing pass's inputs and a Data Protection key ring load each have a bound of their own. The waits
/// are the inventory in docs/security/costly-operations.md section 16: every other transaction a host holds is idle between statements
/// for milliseconds. Each host checks its configured limit against these at composition (Ariva.Di) and the server's own at start
/// (<see cref="Timescale.IdleTransactionLimitGate"/>).
/// </summary>
public sealed record IdleTransactionBudget(IReadOnlyList<IdleTransactionWait> Waits)
{
    #region Constants

    /// <summary>
    /// ARV-294: the most any wait below may take at any value its settings allow (the unit tests compute each at the settings' maxima),
    /// a minute below the lowest limit a host may set (<see cref="DatabaseSettings.MinIdleInTransactionTimeoutSeconds"/>).
    /// </summary>
    public const int MaxWaitSeconds = 240;

    /// <summary>The statements a staffing pass reads on its second connection while its site lock's transaction waits.</summary>
    public const int StaffingInputStatements = 8;

    /// <summary>One Redis call (<c>RedisLiveSnapshots.OperationTimeout</c>): a live notice after a rule's commit, a snapshot read.</summary>
    public const int RedisOperationSeconds = 6;

    /// <summary>The outbox relay's broker probe within its pass.</summary>
    public const int OutboxProbeSeconds = 4;

    /// <summary>
    /// The key ring repository calls of one Data Protection key ring load that creates a key (the security review's R4): Data
    /// Protection's key ring provider reads every element, stores the new key, then reads every element again; each call opens its own
    /// connection (<c>PostgresXmlRepository</c>, within at most 50 s) and runs one statement (within at most 30 s).
    /// </summary>
    public const int KeyRingLoadCalls = 3;

    /// <summary>What names a wait no setting moves.</summary>
    public const string Fixed = "a fixed bound";

    #endregion

    #region Methods

    /// <summary>The waits as this host's configuration sets them (each setting's own default where it is not set).</summary>
    public static IdleTransactionBudget FromConfiguration(IConfiguration configuration, DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(database);
        var kafka = configuration.GetSection(KafkaSettings.SectionName).Get<KafkaSettings>() ?? new KafkaSettings();
        var connect = database.ConnectTimeoutSeconds;
        var connectKey = $"{DatabaseSettings.SectionName}:{nameof(DatabaseSettings.ConnectTimeoutSeconds)}";
        return new IdleTransactionBudget(
        [
            // The whole send within its budget (connection, authentication, message), whatever Email:Smtp:TimeoutSeconds says.
            new("a scheduled report's send, its delivery row and the report slot held", ReportSendBudget.DefaultSeconds, Fixed),
            // Eight statements at the command timeout, cut at the inputs' budget, and one connection opened (not cut by a token).
            new("a staffing pass reading its inputs on a second connection",
                Math.Min(StaffingInputStatements * database.CommandTimeoutSeconds, StaffingRecommendations.InputBudgetSeconds) + connect, connectKey),
            new("an alert evaluation tick in its lock unit", (int)AlertEvaluationWorker.DefaultRunTimeout.TotalSeconds + connect + RedisOperationSeconds, connectKey),
            new("an outbox relay pass", kafka.Outbox.PassSeconds + OutboxProbeSeconds, $"{KafkaSettings.SectionName}:Outbox:{nameof(OutboxRelaySettings.PassSeconds)}"),
            // The attempt's token ends the consumer's statements; a second connection being opened then ends at the connection timeout.
            new("a consumer inside its inbox transaction", kafka.Consumers.HandlerTimeoutSeconds + connect,
                $"{KafkaSettings.SectionName}:Consumers:{nameof(ConsumerSettings.HandlerTimeoutSeconds)}"),
            // Three calls (read, store, read again), each a connection opened and one statement within the key ring's own bounds.
            new("a Data Protection key ring load inside a transaction (read, store a new key, read again)",
                KeyRingLoadCalls * (PostgresXmlRepository.ConnectTimeoutSeconds(database) + PostgresXmlRepository.CommandTimeoutSeconds(database)), connectKey),
            new("a display stale sweep reading the live snapshots under its leader lock", (int)DisplayStaleMonitor.DefaultRunTimeout.TotalSeconds + RedisOperationSeconds, Fixed)
        ]);
    }

    /// <summary>The longest of <see cref="Waits"/>.</summary>
    public IdleTransactionWait Longest => Enumerable.MaxBy(Waits, wait => wait.Seconds);

    /// <summary>Why <paramref name="limitSeconds"/> is too short for this host, or null when it is above every wait.</summary>
    public string ProblemWith(int limitSeconds)
    {
        var longest = Longest;
        return longest.Seconds < limitSeconds ? null : string.Create(CultureInfo.InvariantCulture,
            $"{DatabaseSettings.SectionName}:{nameof(DatabaseSettings.IdleInTransactionTimeoutSeconds)} ({limitSeconds:N0} s) must be above the longest a transaction of this host may wait on other work: {longest.What}, {longest.Seconds:N0} s from {longest.Setting} (ARV-292). Raise the limit or lower that setting.");
    }

    #endregion
}

/// <summary>One transaction a host holds open while it waits on other work, the longest that wait may take, and the setting that bounds it.</summary>
public sealed record IdleTransactionWait(string What, int Seconds, string Setting);
