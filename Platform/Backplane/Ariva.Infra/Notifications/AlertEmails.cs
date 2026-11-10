using System.Data;
using System.Diagnostics;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Infra.Resilience;
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
/// in the last hour gets the next ones held back (Suppressed), not queued. An alert on a border-only metric
/// (<see cref="BorderAlerts"/>, ARV-181b2 and ARV-187) is written to the people of border roles only, whatever its owner and escalation
/// roles say: none when neither is a border role.
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
        // ARV-181b2, ARV-187: an alert on a border-only metric is written to the people of border roles only, whatever its owner and
        // escalation roles say (the sender checks it again before each send).
        roles = [.. BorderAlerts.Audience(alert.Metric, roles)];
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

/// <summary>
/// What one sending round did: sent, given up, to be retried, and held back because the recipient may no longer be told. ARV-294:
/// <paramref name="Unmarked"/> emails had an outcome the round could not record (the database did not answer, or the claim had run
/// out); each stays claimed until its claim runs out and is then sent again, once.
/// </summary>
public sealed record EmailRound(int Sent, int Failed, int Retrying, int Withheld = 0, int Unmarked = 0);

/// <summary>
/// Sends the emails that are due (ARV-040) in three steps, so no transaction waits on the relay (ARV-294, CWE-400): the claim, one
/// replica at a time (a transaction advisory lock; a replica that does not get it skips the round), takes at most what
/// <see cref="EmailSettings.MaxPerMinute"/> leaves (the emails sent in the last minute and those other rounds hold count) with
/// <c>FOR UPDATE SKIP LOCKED</c>, marks each claimed until <see cref="ClaimLength"/> from now and commits; the batch is then sent
/// outside any transaction; and each email is marked, as soon as the relay has answered for it, in a short transaction of its own:
/// sent, to retry after a growing delay, given up after <see cref="EmailSettings.MaxAttempts"/>, or, when the round stopped before
/// it, released as it was. A mark lands only while the round's own claim still holds the email (CWE-362): a claim is never taken by
/// another round before it runs out, and a round begins no message once <see cref="SendWindow"/> from its start has passed (the
/// claim's own time counted in it; a claim that commits after the window sends nothing and releases its emails), so its last message
/// ends within its claim. A row is data from the database, not an instruction (CWE-501): it is sent only while its recipient is still an
/// enabled person (not break-glass) holding the alert's site and the role the email is for (for an alert on a border-only metric, a
/// border role: <see cref="BorderAlerts"/>, ARV-181b2, so a row written before that story, by hand or for an alert changed in the
/// database is held back too); otherwise it is held back. The reason kept for a failure names the error type, never the message or
/// the server's reply. Delivery is at least once (docs/product/decisions.md, ARV-294): an email the relay accepted is marked sent, or,
/// when its host is gone between the send and the mark (or the mark cannot be written), sent once more after its claim runs out.
/// </summary>
public sealed class EmailSender(IUnitOfWork unitOfWork, IServiceScopeFactory scopes, IEmailTransport transport, EmailSettings settings, SmtpSettings smtp,
    ILogger<EmailSender> logger)
{
    #region Constants

    /// <summary>The transaction advisory lock key of the sending round's claim (any constant no other lock uses).</summary>
    public const long RoundLock = 0x4152_5641_0040;

    /// <summary>The most emails one round claims.</summary>
    public const int MaxPerRound = 100;

    /// <summary>
    /// ARV-294: how long after its start a round may begin messages, the time its claim takes included (the security review's R1: a
    /// claim that waited for its connection or its statements leaves that much less); the rest are released for the next round. Its
    /// run timeout (or the host's stop) ends it sooner.
    /// </summary>
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ARV-294: what a claim keeps beyond its round's last message: for the replicas' clocks to differ, for the moment between the
    /// round's clock reading and its start, and for the last email's mark (its own connection and short transaction).
    /// </summary>
    public static readonly TimeSpan ClockMargin = TimeSpan.FromMinutes(1);

    #endregion

    #region Fields

    private TimeSpan _window = SendWindow;

    #endregion

    #region Properties

    /// <summary>The send window of this round's sender: <see cref="SendWindow"/>, or shorter when a test sets it (a longer value is clamped to it).</summary>
    internal TimeSpan Window
    {
        get => _window;
        set => _window = value < SendWindow ? value : SendWindow;
    }

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    #endregion

    #region Methods

    /// <summary>
    /// ARV-294: how long a round's claim holds its emails: the send window from the round's start (the claim's own time within it), then
    /// the longest the message begun last may take (<see cref="MailKitTransport.ExchangesPerMessage"/> exchanges at
    /// <c>Email:Smtp:TimeoutSeconds</c>), then the margin for the clocks, the round's start and the last mark (<see cref="ClockMargin"/>).
    /// Nine minutes at the defaults, 36 at an SMTP timeout of 300 s: an email whose round's host was lost waits this long to be sent again.
    /// </summary>
    public static TimeSpan ClaimLength(SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        return SendWindow + TimeSpan.FromSeconds(MailKitTransport.ExchangesPerMessage * smtp.TimeoutSeconds) + ClockMargin;
    }

    public async Task<EmailRound> SendDueAsync(DateTime now, CancellationToken ct)
    {
        // The window runs from here, before the claim: the time the claim takes (a connection from the pool, its statements) comes out
        // of the window, never out of the claim's margin (CWE-362, the security review's R1).
        var started = Stopwatch.GetTimestamp();
        var claim = await ClaimAsync(now, ct);
        if (claim.Emails.Count == 0)
            return new EmailRound(0, 0, 0, claim.Withheld);

        var tally = new Tally();
        var left = Window - Stopwatch.GetElapsedTime(started);
        if (left <= TimeSpan.Zero)
        {
            // The claim committed after the window: no message is begun, each email released as it was, for the next round.
            foreach (var email in claim.Emails)
                await ReleaseAsync(email, tally);
            logger.LogWarning("Email round: its claim committed after the send window; {Released} emails released for the next round", claim.Emails.Count);
            return new EmailRound(0, 0, 0, claim.Withheld, tally.Unmarked);
        }

        // Outside any transaction: the claim is committed, so the relay's pace holds no row, no lock and no connection.
        var decided = new HashSet<Guid>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(left);
        var outgoing = claim.Emails.Select(e => new OutgoingEmail(e.Id, e.Recipient, e.Subject, e.Body)).ToList();
        // Each email is marked as soon as the relay has answered for it, so a host lost during the batch sends again only the one in
        // flight. A transport that throws (the host stopping in the middle) leaves the rest claimed until their claim runs out.
        var batch = await transport.SendAsync(outgoing, new EmailSendOptions
        {
            Outcome = async (id, failure) =>
            {
                if (decided.Add(id))
                    await MarkAsync(claim.Of(id), failure, now, tally);
            }
        }, window.Token);

        foreach (var email in claim.Emails.Where(e => !decided.Contains(e.Id)))
        {
            // ARV-096e: a round cut before this email stopped between messages; it is released as it was, no attempt counted.
            if (batch.NotTried.Contains(email.Id))
                await ReleaseAsync(email, tally);
            else
                await MarkAsync(email, batch.Failures.GetValueOrDefault(email.Id), now, tally);
        }

        if (tally.Unmarked > 0)
            logger.LogWarning("Email round: {Unmarked} emails the relay answered for could not be marked; each is sent again once its claim runs out", tally.Unmarked);
        return new EmailRound(tally.Sent, tally.Failed, tally.Retrying, claim.Withheld, tally.Unmarked);
    }

    /// <summary>
    /// The claim, in one short transaction under the round lock: the per-minute room, the due emails (held back when their recipient
    /// may no longer be told), each claimed, committed before anything is sent. Statements only: nothing inside it waits on other work.
    /// </summary>
    private async Task<Claimed> ClaimAsync(DateTime now, CancellationToken ct)
    {
        if (!Storage.IsTransactionActive())
            await Storage.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var locked = await Storage.ExecuteSqlAsync<LockRow>("""SELECT pg_try_advisory_xact_lock(:key) AS "Taken" """,
                new Dictionary<string, object> { ["key"] = RoundLock }, ct);
            if (!locked.Single().Taken)
                return await NothingAsync();
            var minuteAgo = now.AddMinutes(-1);
            var length = ClaimLength(smtp);
            var latest = now + length;
            var sentLastMinute = await Storage.Query<EmailMessage>().CountAsync(m => m.Status == EmailStatus.Sent && m.SentUtc > minuteAgo && m.SentUtc <= now, ct);
            // Held by rounds still sending (another replica's, or this one's before a restart): they count until marked or run out. A
            // claim further ahead than any round takes is not counted, so a planted row cannot hold the limit down (CWE-501).
            var inFlight = await Storage.Query<EmailMessage>().CountAsync(m => m.Status == EmailStatus.Pending && m.Reason == EmailMessage.ClaimedReason &&
                m.NextAttemptUtc > now && m.NextAttemptUtc <= latest, ct);
            var budget = Math.Min(MaxPerRound, settings.MaxPerMinute - sentLastMinute - inFlight);
            if (budget <= 0)
                return await NothingAsync();

            // Claimed with whether the recipient may still be told: the same people the email was written for.
            var due = await Storage.ExecuteSqlAsync<DueRow>("""
                SELECT m.id AS "Id", EXISTS (
                    SELECT 1 FROM "user" u
                    JOIN user_role r ON r.user_id = u.id
                    LEFT JOIN user_site s ON s.user_id = u.id AND s.site_code = a.site_code
                    WHERE lower(u.email) = lower(m.recipient) AND NOT u.is_disabled AND NOT u.is_break_glass AND (u.all_sites OR s.site_code IS NOT NULL)
                      AND ((m.kind = 'AlertRaised' AND (r.role_code = a.owner_role
                            OR (a.owner_role IS NULL AND r.role_code IN ('BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager'))))
                        OR (m.kind = 'AlertEscalated' AND r.role_code = a.escalate_to_role))
                      AND (a.metric NOT IN (:borderMetrics) OR r.role_code IN (:borderRoles))) AS "Eligible"
                FROM email_message m JOIN alert a ON a.id = m.alert_id
                WHERE m.status = 'Pending' AND m.next_attempt_utc <= :now
                ORDER BY m.created_utc LIMIT :limit FOR UPDATE OF m SKIP LOCKED
                """, new Dictionary<string, object>
            {
                ["now"] = now,
                ["limit"] = budget,
                ["borderMetrics"] = BorderAlerts.MetricNames,
                ["borderRoles"] = BorderAlerts.Roles
            }, ct);
            if (due.Count == 0)
                return await NothingAsync();

            // Whole seconds, so the claim reads back from the database exactly as it was written (it names the claim in each mark).
            var claimedUntil = new DateTime(latest.Ticks - (latest.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            var emails = new List<ClaimedEmail>();
            var withheld = 0;
            foreach (var row in due)
            {
                if (await Storage.GetAsync<EmailMessage>(row.Id, ct) is not { Status: EmailStatus.Pending } email)
                    continue;
                if (row.Eligible)
                {
                    // A claim that ran out (its round's host lost) left its own reason: nothing to give back on a release.
                    var reason = string.Equals(email.Reason, EmailMessage.ClaimedReason, StringComparison.Ordinal) ? null : email.Reason;
                    emails.Add(new ClaimedEmail(email.Id.Value, email.Recipient, email.Subject, email.Body, claimedUntil, email.NextAttemptUtc, reason));
                    email.Claim(claimedUntil);
                }
                else
                {
                    email.Suppress("The recipient no longer holds the role, the site or an enabled account with this address.");
                    withheld++;
                }
            }

            unitOfWork.PromiseToCommit();
            await unitOfWork.EndAsync(CancellationToken.None);
            return new Claimed(emails, withheld);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Claimed> NothingAsync()
    {
        await unitOfWork.RollbackAsync(CancellationToken.None);
        return new Claimed([], 0);
    }

    /// <summary>The outcome of one email, in a scope and transaction of its own, while its round's claim still holds it.</summary>
    private Task MarkAsync(ClaimedEmail email, string failure, DateTime now, Tally tally) =>
        WithinClaimAsync(email, message =>
        {
            if (failure is null)
            {
                message.Sent(now);
                tally.Sent++;
                return;
            }

            message.Failed(failure, now, settings.MaxAttempts);
            if (message.Status == EmailStatus.Failed)
                tally.Failed++;
            else
                tally.Retrying++;
        }, tally);

    /// <summary>An email the round did not try: due again as before its claim, its reason as it was, no attempt counted.</summary>
    private Task ReleaseAsync(ClaimedEmail email, Tally tally) =>
        WithinClaimAsync(email, message => message.Release(email.DueUtc, email.Reason), tally);

    /// <summary>
    /// One short transaction: the row locked while it is still pending and held by this claim, the change, the commit. Whatever the
    /// round's token says (the relay has answered), bounded by the command timeout. A claim that no longer holds the row, or a
    /// transaction that fails, leaves the email as it is and counts it unmarked.
    /// </summary>
    private async Task WithinClaimAsync(ClaimedEmail email, Action<EmailMessage> change, Tally tally)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                var storage = work.StorageProvider;
                await storage.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
                var held = await storage.ExecuteSqlAsync<IdRow>("""
                    SELECT id AS "Id" FROM email_message WHERE id = :id AND status = 'Pending' AND next_attempt_utc = :until AND reason = :claimed FOR UPDATE
                    """, new Dictionary<string, object> { ["id"] = email.Id, ["until"] = email.ClaimedUntil, ["claimed"] = EmailMessage.ClaimedReason },
                    CancellationToken.None);
                var message = held.Count == 1 ? await storage.GetAsync<EmailMessage>(email.Id, CancellationToken.None) : null;
                if (message is null || !message.IsClaimedUntil(email.ClaimedUntil))
                {
                    await work.RollbackAsync(CancellationToken.None);
                    logger.LogWarning("Email {EmailId}: its claim no longer holds it, so its outcome is not recorded", email.Id);
                    tally.Unmarked++;
                    return;
                }

                change(message);
                work.PromiseToCommit();
                await work.EndAsync(CancellationToken.None);
            }
            catch
            {
                await work.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
#pragma warning disable CA1031 // a mark that fails is logged; the email stays claimed and is sent again once its claim runs out
        catch (Exception e)
#pragma warning restore CA1031
        {
            logger.LogError(e, "Email {EmailId}: its outcome could not be recorded; it is sent again once its claim runs out", email.Id);
            tally.Unmarked++;
        }
    }

    #endregion

    /// <summary>What the claim took: the emails to send, each with its claim and what it was before, and how many it held back.</summary>
    private sealed record Claimed(IReadOnlyList<ClaimedEmail> Emails, int Withheld)
    {
        public ClaimedEmail Of(Guid id) => Emails.First(e => e.Id == id);
    }

    /// <summary>A claimed email: what goes on the wire, its claim, and when it was due and why before it (to release it as it was).</summary>
    private sealed record ClaimedEmail(Guid Id, string Recipient, string Subject, string Body, DateTime ClaimedUntil, DateTime DueUtc, string Reason);

    private sealed class Tally
    {
        public int Sent { get; set; }
        public int Failed { get; set; }
        public int Retrying { get; set; }
        public int Unmarked { get; set; }
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

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }
}

/// <summary>
/// Sends due emails every <see cref="EmailSettings.PollSeconds"/> in Ariva.Api.Integration. A round ends at
/// <see cref="RunTimeout"/> (ARV-096e) between messages: the message the relay is taking is finished, each email it accepted is
/// marked sent in a transaction of its own (ARV-294) and the rest are released for the next round, so a cut never sends an email twice.
/// </summary>
public sealed class EmailSenderWorker(IServiceScopeFactory scopes, EmailSettings settings, TimeProvider timeProvider, ILogger<EmailSenderWorker> logger)
    : BackgroundService, IBoundedRuns
{
    /// <summary>Ten minutes: a slow relay's round is cut between messages and goes on in the next round from what is left.</summary>
    public TimeSpan RunTimeout { get; init; } = DefaultRunTimeout;

    /// <summary>The run timeout a host runs with. Since ARV-294 no transaction of the round is open while it sends (<see cref="EmailSender"/>).</summary>
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>One round under the run timeout (tests).</summary>
    internal Task<EmailRound> RunOnceAsync(CancellationToken stoppingToken) => RunBound.RunAsync("Email round", RunTimeout, timeProvider, async ct =>
    {
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            // ARV-294: the round commits its claim and each mark itself, each mark whatever the run's token says (ARV-096e); this ends
            // what is left of the scope's unit of work (nothing to commit).
            var round = await scope.ServiceProvider.GetRequiredService<EmailSender>().SendDueAsync(timeProvider.GetUtcNow().UtcDateTime, ct);
            await unitOfWork.EndAsync(CancellationToken.None);
            return round;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }, stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollSeconds), timeProvider);
        do
        {
            try
            {
                var round = await RunOnceAsync(stoppingToken);
                if (round.Failed + round.Retrying + round.Withheld + round.Unmarked > 0)
                    logger.LogWarning("Email round: {Sent} sent, {Retrying} to retry, {Failed} given up, {Withheld} held back, {Unmarked} not marked", round.Sent,
                        round.Retrying, round.Failed, round.Withheld, round.Unmarked);
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
