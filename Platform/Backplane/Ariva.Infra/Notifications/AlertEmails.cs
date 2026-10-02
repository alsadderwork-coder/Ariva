using System.Data;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NHibernate.Linq;

namespace Ariva.Infra.Notifications;

/// <summary>
/// Writes the emails an alert change calls for (ARV-040), in the transaction of that change: when an alert is raised,
/// to the people of its owner role (every operational role when it has none); when it is escalated, to the people of
/// its escalation role. Only if email is on and the alert's rule notifies by email. A person is someone enabled, not a
/// break-glass account, holding the role and the alert's site, with a valid address (others are skipped). One email per
/// alert, kind and address; an address that already had <see cref="EmailSettings.MaxPerRecipientPerHour"/> alert emails
/// in the last hour gets the next ones held back (Suppressed), not queued.
/// </summary>
public sealed class AlertEmails(IUnitOfWork unitOfWork, EmailSettings settings, EmailTemplates templates, ILogger<AlertEmails> logger)
{
    /// <summary>The most people one alert email goes to.</summary>
    public const int MaxRecipients = 200;

    private static readonly string[] OperationalRoles = [RoleCodes.BorderShiftSupervisor, RoleCodes.TerminalDutyManager, RoleCodes.HandlerStationManager];

    // Queued by this scope (one unit of work), per address, not yet visible to the database's count.
    private readonly Dictionary<string, int> _queuedHere = new(StringComparer.OrdinalIgnoreCase);

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    /// <summary>Writes the emails; returns how many were queued (held-back ones are written but not counted).</summary>
    public async Task<int> EnqueueAsync(Alert alert, EmailKind kind, DateTime now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (!settings.Enabled)
            return 0;
        var rule = await Storage.GetAsync<AlertRule>(alert.RuleId, ct);
        if (rule is null || !rule.NotifyByEmail)
            return 0;
        string[] roles = kind == EmailKind.AlertRaised
            ? alert.OwnerRole is { } owner ? [owner] : OperationalRoles
            : alert.EscalateToRole is { } to ? [to] : [];
        if (roles.Length == 0)
            return 0;

        var people = await Storage.ExecuteSqlAsync<AddressRow>("""
            SELECT DISTINCT u.email AS "Address" FROM "user" u
            JOIN user_role r ON r.user_id = u.id
            LEFT JOIN user_site s ON s.user_id = u.id AND s.site_code = :site
            WHERE NOT u.is_disabled AND NOT u.is_break_glass AND u.email IS NOT NULL AND r.role_code IN (:roles) AND (u.all_sites OR s.site_code IS NOT NULL)
            ORDER BY 1 LIMIT 200
            """, new Dictionary<string, object> { ["site"] = alert.SiteCode, ["roles"] = roles }, ct);
        var addresses = people.Select(p => p.Address?.Trim()).Where(EmailAddresses.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count < people.Count)
            logger.LogWarning("Alert email for {Alert}: {Skipped} recipients skipped for an address that is not valid", alert.Id, people.Count - addresses.Count);
        if (addresses.Count == 0)
            return 0;

        var already = (await Storage.Query<EmailMessage>().Where(m => m.AlertId == alert.Id.Value && m.Kind == kind).Select(m => m.Recipient).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rendered = templates.Render(kind, AlertEmailValues.Of(alert));

        // What each address had in the last hour: committed rows from the database (one query for all of them), plus
        // what this unit of work wrote before (not flushed yet, so the database does not see it): one tick can raise
        // or escalate many alerts in one transaction, and the limit holds across them.
        var since = now.AddHours(-1);
        var counted = await Storage.ExecuteSqlAsync<CountRow>("""
            SELECT lower(recipient) AS "Address", CAST(count(*) AS integer) AS "Count" FROM email_message
            WHERE lower(recipient) IN (:addresses) AND created_utc > :since AND created_utc <= :now AND status IN ('Pending', 'Sent')
            GROUP BY lower(recipient)
            """, new Dictionary<string, object> { ["addresses"] = addresses.Select(a => a.ToLowerInvariant()).Distinct().ToList(), ["since"] = since, ["now"] = now }, ct);
        var recent = counted.ToDictionary(c => c.Address, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var queued = 0;
        foreach (var address in addresses.Where(a => !already.Contains(a)))
        {
            var count = recent.GetValueOrDefault(address) + _queuedHere.GetValueOrDefault(address);
            var email = new EmailMessage(kind, alert.Id.Value, alert.SiteCode, address, rendered.Subject, rendered.Body, now);
            if (count >= settings.MaxPerRecipientPerHour)
            {
                email.Suppress($"More than {settings.MaxPerRecipientPerHour} alert emails to this address in an hour.");
            }
            else
            {
                queued++;
                _queuedHere[address] = _queuedHere.GetValueOrDefault(address) + 1;
            }

            await Storage.SaveAsync(email, ct);
        }

        return queued;
    }

    private sealed class AddressRow
    {
        public string Address { get; set; }
    }

    private sealed class CountRow
    {
        public string Address { get; set; }
        public int Count { get; set; }
    }
}

/// <summary>What one sending round did: sent, given up, to be retried, and held back because the recipient may no longer be told.</summary>
public sealed record EmailRound(int Sent, int Failed, int Retrying, int Withheld = 0);

/// <summary>
/// Sends the emails that are due (ARV-040), in one transaction, one replica at a time (a transaction advisory lock; a
/// replica that does not get it skips the round), so <see cref="EmailSettings.MaxPerMinute"/> holds over all of them:
/// claims at most what that limit leaves with <c>FOR UPDATE SKIP LOCKED</c>, sends, and marks each sent, to retry after
/// a growing delay, or given up after <see cref="EmailSettings.MaxAttempts"/>. A row is data from the database, not an
/// instruction (CWE-501): it is sent only while its recipient is still an enabled person (not break-glass) holding the
/// alert's site and the role the email is for; otherwise it is held back. The reason kept for a failure names the
/// error type, never the message or the server's reply. Delivery is at least once.
/// </summary>
public sealed class EmailSender(IUnitOfWork unitOfWork, IEmailTransport transport, EmailSettings settings)
{
    /// <summary>The transaction advisory lock key of the sending round (any constant no other lock uses).</summary>
    public const long RoundLock = 0x4152_5641_0040;

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    public async Task<EmailRound> SendDueAsync(DateTime now, CancellationToken ct)
    {
        if (!Storage.IsTransactionActive())
            Storage.BeginTransaction(IsolationLevel.ReadCommitted);
        var locked = await Storage.ExecuteSqlAsync<LockRow>("""SELECT pg_try_advisory_xact_lock(:key) AS "Taken" """,
            new Dictionary<string, object> { ["key"] = RoundLock }, ct);
        if (!locked.Single().Taken)
            return new EmailRound(0, 0, 0);
        var minuteAgo = now.AddMinutes(-1);
        var sentLastMinute = await Storage.Query<EmailMessage>().CountAsync(m => m.Status == EmailStatus.Sent && m.SentUtc > minuteAgo && m.SentUtc <= now, ct);
        var budget = Math.Min(100, settings.MaxPerMinute - sentLastMinute);
        if (budget <= 0)
            return new EmailRound(0, 0, 0);

        // Claimed with whether the recipient may still be told: the same people the email was written for.
        var due = await Storage.ExecuteSqlAsync<DueRow>("""
            SELECT m.id AS "Id", EXISTS (
                SELECT 1 FROM "user" u
                JOIN user_role r ON r.user_id = u.id
                LEFT JOIN user_site s ON s.user_id = u.id AND s.site_code = a.site_code
                WHERE lower(u.email) = lower(m.recipient) AND NOT u.is_disabled AND NOT u.is_break_glass AND (u.all_sites OR s.site_code IS NOT NULL)
                  AND ((m.kind = 'AlertRaised' AND (r.role_code = a.owner_role
                        OR (a.owner_role IS NULL AND r.role_code IN ('BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager'))))
                    OR (m.kind = 'AlertEscalated' AND r.role_code = a.escalate_to_role))) AS "Eligible"
            FROM email_message m JOIN alert a ON a.id = m.alert_id
            WHERE m.status = 'Pending' AND m.next_attempt_utc <= :now
            ORDER BY m.created_utc LIMIT :limit FOR UPDATE OF m SKIP LOCKED
            """, new Dictionary<string, object> { ["now"] = now, ["limit"] = budget }, ct);
        if (due.Count == 0)
            return new EmailRound(0, 0, 0);
        var emails = new List<EmailMessage>();
        var withheld = 0;
        foreach (var row in due)
        {
            if (await Storage.GetAsync<EmailMessage>(row.Id, ct) is not { Status: EmailStatus.Pending } email)
                continue;
            if (row.Eligible)
            {
                emails.Add(email);
            }
            else
            {
                email.Suppress("The recipient no longer holds the role, the site or an enabled account with this address.");
                withheld++;
            }
        }

        var failures = emails.Count == 0
            ? new Dictionary<Guid, string>()
            : await transport.SendAsync([.. emails.Select(e => new OutgoingEmail(e.Id!.Value, e.Recipient, e.Subject, e.Body))], ct);
        int sent = 0, failed = 0, retrying = 0;
        foreach (var email in emails)
        {
            if (failures.TryGetValue(email.Id!.Value, out var reason))
            {
                email.Failed(reason, now, settings.MaxAttempts);
                if (email.Status == EmailStatus.Failed)
                    failed++;
                else
                    retrying++;
            }
            else
            {
                email.Sent(now);
                sent++;
            }
        }

        await Storage.FlushAsync(ct);
        unitOfWork.PromiseToCommit();
        return new EmailRound(sent, failed, retrying, withheld);
    }

    private sealed class LockRow
    {
        public bool Taken { get; set; }
    }

    private sealed class DueRow
    {
        public Guid Id { get; set; }
        public bool Eligible { get; set; }
    }
}

/// <summary>Sends due emails every <see cref="EmailSettings.PollSeconds"/> in Ariva.Api.Integration.</summary>
public sealed class EmailSenderWorker(IServiceScopeFactory scopes, EmailSettings settings, TimeProvider timeProvider, ILogger<EmailSenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollSeconds), timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                try
                {
                    var round = await scope.ServiceProvider.GetRequiredService<EmailSender>().SendDueAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                    await unitOfWork.EndAsync(stoppingToken);
                    if (round.Failed + round.Retrying + round.Withheld > 0)
                        logger.LogWarning("Email round: {Sent} sent, {Retrying} to retry, {Failed} given up, {Withheld} held back", round.Sent, round.Retrying, round.Failed,
                            round.Withheld);
                }
                catch
                {
                    await unitOfWork.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed round is logged; the next one tries again
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Email round failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
