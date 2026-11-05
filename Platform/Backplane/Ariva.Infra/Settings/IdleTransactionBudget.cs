using System.Globalization;
using Ariva.Infra.Alerting;
using Ariva.Infra.Messaging;
using Ariva.Infra.Notifications;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>
/// ARV-292 (CWE-400): the longest a transaction of a host may stay idle on the server while the host waits on other work, from
/// that host's own settings, and the check that the runtime login's limit (<see cref="DatabaseSettings.IdleInTransactionTimeoutSeconds"/>,
/// idle_in_transaction_session_timeout) is above it, so the server never ends a transaction a host is still working through. The
/// waits are the inventory in docs/security/costly-operations.md (the database timeouts row): every other transaction a host
/// holds is idle between statements for milliseconds.
/// </summary>
public sealed record IdleTransactionBudget(IReadOnlyList<IdleTransactionWait> Waits)
{
    #region Constants

    /// <summary>
    /// The SMTP exchanges one message can take inside a transaction: MailKit bounds each network read or write by
    /// <c>Email:Smtp:TimeoutSeconds</c>, not a whole send (the envelope, the data, its reply, then QUIT).
    /// </summary>
    public const int SmtpExchangesPerMessage = 6;

    /// <summary>
    /// The SMTP network reads of a scheduled report's send, each bounded by the SMTP timeout (MailKit bounds an operation, not a
    /// send): connect, greeting, EHLO, STARTTLS, two for the TLS handshake, EHLO again, authentication, MAIL, RCPT, DATA, the final
    /// reply and QUIT, with one to spare for the attachment's writes.
    /// </summary>
    public const int SmtpExchangesPerReport = 15;

    /// <summary>The statements a staffing pass reads on its second connection while its site lock's transaction waits.</summary>
    public const int StaffingInputStatements = 8;

    /// <summary>One live notice publish to Redis after a rule's commit, inside the alert evaluation's lock unit.</summary>
    public const int RedisPublishSeconds = 6;

    /// <summary>The outbox relay's broker probe within its pass.</summary>
    public const int OutboxProbeSeconds = 4;

    /// <summary>The section and default of the transfer endpoints' request timeout (Ariva.Api.Common's RequestTimeoutsSettings).</summary>
    public const string TransferSecondsKey = "Security:RequestTimeouts:TransferSeconds";

    /// <inheritdoc cref="TransferSecondsKey"/>
    public const int DefaultTransferSeconds = 900;

    #endregion

    #region Methods

    /// <summary>The waits as this host's configuration sets them (each setting's own default where it is not set).</summary>
    public static IdleTransactionBudget FromConfiguration(IConfiguration configuration, DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(database);
        var smtp = configuration.GetSection(SmtpSettings.SectionName).Get<SmtpSettings>() ?? new SmtpSettings();
        var kafka = configuration.GetSection(KafkaSettings.SectionName).Get<KafkaSettings>() ?? new KafkaSettings();
        var transfer = configuration.GetValue<int?>(TransferSecondsKey) ?? DefaultTransferSeconds;
        return new IdleTransactionBudget(
        [
            new("a request on a transfer endpoint (an upload, a site configuration import or export)", transfer, TransferSecondsKey),
            new("an alert email round, its batch sent inside its claim", (int)EmailSenderWorker.DefaultRunTimeout.TotalSeconds + SmtpExchangesPerMessage * smtp.TimeoutSeconds,
                $"{SmtpSettings.SectionName}:{nameof(SmtpSettings.TimeoutSeconds)}"),
            new("a scheduled report's send, its delivery row and the report slot held", SmtpExchangesPerReport * smtp.TimeoutSeconds,
                $"{SmtpSettings.SectionName}:{nameof(SmtpSettings.TimeoutSeconds)}"),
            new("a staffing pass reading its inputs on a second connection", StaffingInputStatements * database.CommandTimeoutSeconds + database.ConnectTimeoutSeconds,
                $"{DatabaseSettings.SectionName}:{nameof(DatabaseSettings.CommandTimeoutSeconds)}"),
            new("an alert evaluation tick in its lock unit", (int)AlertEvaluationWorker.DefaultRunTimeout.TotalSeconds + database.ConnectTimeoutSeconds + RedisPublishSeconds,
                $"{DatabaseSettings.SectionName}:{nameof(DatabaseSettings.ConnectTimeoutSeconds)}"),
            new("an outbox relay pass", kafka.Outbox.PassSeconds + OutboxProbeSeconds, $"{KafkaSettings.SectionName}:Outbox:{nameof(OutboxRelaySettings.PassSeconds)}"),
            new("a consumer inside its inbox transaction", kafka.Consumers.HandlerTimeoutSeconds, $"{KafkaSettings.SectionName}:Consumers:{nameof(ConsumerSettings.HandlerTimeoutSeconds)}")
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
