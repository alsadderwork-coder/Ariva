using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// One email waiting to be sent, sent, or held back (ARV-040): written in the transaction of the change it tells of
/// (an alert raised or escalated), so it is sent exactly when that change committed, and sent by Ariva.Api.Integration.
/// Its recipient, subject and body are fixed when it is written; one email per alert, kind and recipient.
/// </summary>
public class EmailMessage : EntityBase<EmailMessage>
{
    public const int MaxRecipientLength = 254;
    public const int MaxSubjectLength = 200;
    public const int MaxBodyLength = 8000;
    public const int MaxErrorLength = 500;

    protected EmailMessage()
    {
    }

    public EmailMessage(EmailKind kind, Guid alertId, string siteCode, string recipient, string subject, string body, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (recipient.Length > MaxRecipientLength || subject.Length > MaxSubjectLength || body.Length > MaxBodyLength || utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Recipient, subject and body within their lengths, at a UTC time.");
        Id = NewId();
        Kind = kind;
        AlertId = alertId;
        SiteCode = siteCode;
        Recipient = recipient;
        Subject = subject;
        Body = body;
        Status = EmailStatus.Pending;
        CreatedUtc = utcNow;
        NextAttemptUtc = utcNow;
    }

    public virtual EmailKind Kind { get; protected set; }
    public virtual Guid AlertId { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual string Recipient { get; protected set; }
    public virtual string Subject { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(MaxBodyLength)]
    public virtual string Body { get; protected set; }

    public virtual EmailStatus Status { get; protected set; }
    public virtual int Attempts { get; protected set; }
    public virtual DateTime CreatedUtc { get; protected set; }
    public virtual DateTime NextAttemptUtc { get; protected set; }
    public virtual DateTime? SentUtc { get; protected set; }

    /// <summary>Why it was held back (rate limit) or why the last attempt failed; never the message itself.</summary>
    public virtual string Reason { get; protected set; }

    /// <summary>Held back before sending (a rate limit); never sent.</summary>
    public virtual void Suppress(string reason)
    {
        EnsurePending();
        Status = EmailStatus.Suppressed;
        Reason = Clip(reason);
    }

    public virtual void Sent(DateTime utcNow)
    {
        EnsurePending();
        Status = EmailStatus.Sent;
        SentUtc = utcNow;
        Attempts++;
        Reason = null;
    }

    /// <summary>
    /// ARV-294: the reason a pending email carries while a sending round holds its claim (<see cref="Claim"/>), so every round counts it
    /// against the per-minute limit until it is marked or its claim runs out. Replaced by the outcome when the round marks it.
    /// </summary>
    public const string ClaimedReason = "Claimed by a sending round.";

    /// <summary>
    /// ARV-294: claimed by a sending round until <paramref name="claimedUntilUtc"/>, the claim committed before anything is sent: not due
    /// before then, so no other round takes it, and marked by that round only while it still holds it (<see cref="IsClaimedUntil"/>).
    /// A claim that runs out (the round's host gone between its send and its mark) leaves the email due again: sent again, once.
    /// </summary>
    public virtual void Claim(DateTime claimedUntilUtc)
    {
        EnsurePending();
        if (claimedUntilUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A claim ends at a UTC time.", nameof(claimedUntilUtc));
        NextAttemptUtc = claimedUntilUtc;
        Reason = ClaimedReason;
    }

    /// <summary>ARV-294: whether the claim that ends at <paramref name="claimedUntilUtc"/> still holds this email (pending, not marked).</summary>
    public virtual bool IsClaimedUntil(DateTime claimedUntilUtc) =>
        Status == EmailStatus.Pending && NextAttemptUtc == claimedUntilUtc && string.Equals(Reason, ClaimedReason, StringComparison.Ordinal);

    /// <summary>
    /// ARV-294: a claimed email its round did not try (stopped between messages): due again as it was before its claim, with the reason it
    /// had, and no attempt counted.
    /// </summary>
    public virtual void Release(DateTime nextAttemptUtc, string reason)
    {
        EnsurePending();
        if (nextAttemptUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("An attempt is due at a UTC time.", nameof(nextAttemptUtc));
        NextAttemptUtc = nextAttemptUtc;
        Reason = Clip(reason);
    }

    /// <summary>A failed attempt: tried again after a growing delay, given up after <paramref name="maxAttempts"/>.</summary>
    public virtual void Failed(string reason, DateTime utcNow, int maxAttempts)
    {
        EnsurePending();
        Attempts++;
        Reason = Clip(reason);
        if (Attempts >= maxAttempts)
        {
            Status = EmailStatus.Failed;
            return;
        }

        // 1, 2, 4, 8 ... minutes, at most an hour.
        NextAttemptUtc = utcNow.AddMinutes(Math.Min(60, 1 << Math.Min(6, Attempts - 1)));
    }

    // Sent, Suppressed and Failed are final (the database refuses to move them, script 0023).
    private void EnsurePending()
    {
        if (Status != EmailStatus.Pending)
            throw new InvalidOperationException($"An email that was {Status} stays so.");
    }

    private static string Clip(string reason) => reason is null ? null : reason.Length > MaxErrorLength ? reason[..MaxErrorLength] : reason;
}
