using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Ariva.Infra.Notifications;

/// <summary>
/// Email settings (<c>Email</c>, in the base settings so every host agrees on <see cref="Enabled"/> and the limits).
/// How to reach the mail relay, with its credential, is <see cref="SmtpSettings"/>, read only by the sending host.
/// </summary>
public sealed record EmailSettings
{
    public const string SectionName = "Email";

    /// <summary>Alert emails are written (every host) and sent (Ariva.Api.Integration). Off: no email is written at all.</summary>
    public bool Enabled { get; init; }

    public string FromAddress { get; init; }
    public string FromName { get; init; } = "Ariva";

    /// <summary>The most alert emails one address gets in an hour; more are held back (Suppressed), not queued.</summary>
    public int MaxPerRecipientPerHour { get; init; } = 20;

    /// <summary>The most emails sent in a minute by all of Ariva.Api.Integration's replicas together.</summary>
    public int MaxPerMinute { get; init; } = 60;

    public int PollSeconds { get; init; } = 10;
    public int MaxAttempts { get; init; } = 8;

    public IEnumerable<string> Problems()
    {
        if (!Enabled)
            yield break;
        if (!EmailAddresses.IsValid(FromAddress))
            yield return "Email:FromAddress must be a single valid address.";
        if (FromName is null || FromName.Length > 100 || !Ariva.Core.Domain.Components.DisplayText.IsClean(FromName))
            yield return "Email:FromName is at most 100 characters without control characters.";
        if (MaxPerRecipientPerHour is < 1 or > 1000 || MaxPerMinute is < 1 or > 10_000)
            yield return "Email:MaxPerRecipientPerHour is 1 to 1000 and Email:MaxPerMinute 1 to 10,000.";
        if (PollSeconds is < 1 or > 600 || MaxAttempts is < 1 or > 100)
            yield return "Email:PollSeconds is 1 to 600 and MaxAttempts 1 to 100.";
    }
}

/// <summary>
/// The mail relay (<c>Email:Smtp</c>), read only by Ariva.Api.Integration, the host that sends: give it, and its
/// password, to Integration alone. Never printed with the password (<see cref="PrintMembers"/>).
/// </summary>
public sealed record SmtpSettings
{
    public const string SectionName = "Email:Smtp";

    public string Host { get; init; }
    public int Port { get; init; } = 587;

    /// <summary><c>StartTls</c> (default), <c>SslOnConnect</c>, or <c>None</c> (only with <see cref="AllowInsecure"/>, for smtp4dev).</summary>
    public string Security { get; init; } = "StartTls";

    public bool AllowInsecure { get; init; }
    public string Username { get; init; }
    public string Password { get; init; }
    public int TimeoutSeconds { get; init; } = 30;

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Host))
            yield return "Email:Smtp:Host is required when Email:Enabled.";
        if (Port is < 1 or > 65535)
            yield return "Email:Smtp:Port is 1 to 65535.";
        if (Security is not ("StartTls" or "SslOnConnect" or "None"))
            yield return "Email:Smtp:Security is StartTls, SslOnConnect or None.";
        if (Security == "None" && !AllowInsecure)
            yield return "Email:Smtp:Security None sends in clear text; it needs Email:Smtp:AllowInsecure (development and test only).";
        if (TimeoutSeconds is < 1 or > 300)
            yield return "Email:Smtp:TimeoutSeconds is 1 to 300.";
    }

    // The compiler's ToString would print the password; this one never does.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Host = {Host}, Port = {Port}, Security = {Security}, AllowInsecure = {AllowInsecure}, ");
        builder.Append(CultureInfo.InvariantCulture, $"Username = {(string.IsNullOrEmpty(Username) ? "(none)" : "(set)")}, Password = {(string.IsNullOrEmpty(Password) ? "(none)" : "(redacted)")}");
        return true;
    }
}

/// <summary>
/// Email addresses Ariva sends to (ARV-040): one plain address, at most 254 characters, no display name, comments,
/// quotes, whitespace or control characters (so nothing can be smuggled into a header, CWE-93), and one MimeKit also
/// reads as exactly that address.
/// </summary>
public static partial class EmailAddresses
{
    public static bool IsValid(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > EmailMessage.MaxRecipientLength || !Rule().IsMatch(address) || address.IndexOf('@', StringComparison.Ordinal) > 64)
            return false;
        return MailboxAddress.TryParse(ParserOptions.Default, address, out var parsed) && string.Equals(parsed.Address, address, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(parsed.Name);
    }

    [GeneratedRegex(@"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+\z",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Rule();
}

/// <summary>A rendered email: a subject of one line and a plain-text body.</summary>
public sealed record RenderedEmail(string Subject, string Body);

/// <summary>
/// The email templates of Ariva.Resources (<c>Templates/Email/{kind}.txt</c>: a <c>Subject:</c> line, a blank line, the
/// body), with <c>{{Name}}</c> placeholders. Every value is made one line of printable text before it is put in (control,
/// line-break and invisible characters removed, at most 200 characters), so no value can add a header line or a line
/// that looks like part of the message (CWE-93, CWE-117); the subject is one line of at most 200 characters.
/// </summary>
public sealed partial class EmailTemplates
{
    private readonly Dictionary<EmailKind, (string Subject, string Body)> _templates = [];

    public EmailTemplates()
    {
        var assembly = typeof(Ariva.Resources._IAssemblyMark).Assembly;
        foreach (var kind in Enum.GetValues<EmailKind>())
        {
            using var stream = assembly.GetManifestResourceStream($"Ariva.Resources.Templates.Email.{kind}.txt")
                               ?? throw new InvalidOperationException($"The email template {kind} is missing from Ariva.Resources.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (!text.StartsWith("Subject: ", StringComparison.Ordinal) || split < 0)
                throw new InvalidOperationException($"The email template {kind} must start with a Subject line and a blank line.");
            _templates[kind] = (text["Subject: ".Length..split], text[(split + 2)..]);
        }
    }

    /// <summary>The placeholders a template names.</summary>
    public IReadOnlySet<string> Placeholders(EmailKind kind) =>
        Placeholder().Matches(_templates[kind].Subject + _templates[kind].Body).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    public RenderedEmail Render(EmailKind kind, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var (subject, body) = _templates[kind];
        string Fill(string text) => Placeholder().Replace(text, m => Clean(values.GetValueOrDefault(m.Groups[1].Value)));
        var renderedSubject = Clean(Fill(subject), EmailMessage.MaxSubjectLength);
        var renderedBody = Fill(body);
        if (renderedBody.Length > EmailMessage.MaxBodyLength)
            renderedBody = renderedBody[..EmailMessage.MaxBodyLength];
        return new RenderedEmail(renderedSubject, renderedBody);
    }

    /// <summary>One line of printable text: control, line-break, and Unicode format characters removed; clipped.</summary>
    public static string Clean(string value, int max = 200)
    {
        if (string.IsNullOrEmpty(value))
            return "-";
        var builder = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (builder.Length >= max)
                break;
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ||
                char.IsSurrogate(c))
                continue;
            builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "-" : cleaned;
    }

    [GeneratedRegex(@"\{\{([A-Za-z]+)\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Placeholder();
}

/// <summary>An email ready for the wire.</summary>
public sealed record OutgoingEmail(Guid Id, string Recipient, string Subject, string Body)
{
    /// <summary>Files sent with the email (the daily report's CSV, ARV-060); none for alert emails.</summary>
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// A file sent with an email. The name is the server's own (letters, digits, dot, dash, underscore), never a user's text,
/// so it cannot inject header parameters (CWE-93).
/// </summary>
public sealed record EmailAttachment
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public EmailAttachment(string fileName, string mediaType, byte[] content)
    {
        if (fileName is null || !System.Text.RegularExpressions.Regex.IsMatch(fileName, "^[A-Za-z0-9._-]{1,100}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("An attachment's name is 1 to 100 letters, digits, dots, dashes or underscores.", nameof(fileName));
        if (mediaType is not ("text/csv" or "text/plain"))
            throw new ArgumentException("Only CSV and plain text are attached.", nameof(mediaType));
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaxBytes)
            throw new ArgumentException("An attachment is at most 5 MB.", nameof(content));
        FileName = fileName;
        MediaType = mediaType;
        Content = content;
    }

    public string FileName { get; }
    public string MediaType { get; }
    public byte[] Content { get; }
}

/// <summary>Where emails go (an SMTP server through MailKit; a recorder in tests).</summary>
public interface IEmailTransport
{
    /// <summary>Sends each email; a failure of one is reported for it, the others still go.</summary>
    Task<IReadOnlyDictionary<Guid, string>> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct);
}

/// <summary>
/// SMTP through MailKit (ARV-040): one connection per batch, TLS as configured (clear text only with
/// <see cref="SmtpSettings.AllowInsecure"/>), plain-text messages from the configured sender. The recipient is checked
/// again and parsed by MimeKit, the subject cleaned again, and MimeKit encodes every header, so a value can never add a
/// header (CWE-93).
/// </summary>
public sealed class MailKitTransport(EmailSettings settings, SmtpSettings smtp) : IEmailTransport
{
    public async Task<IReadOnlyDictionary<Guid, string>> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var failures = new Dictionary<Guid, string>();
        if (emails.Count == 0)
            return failures;
        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        var security = smtp.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" when smtp.AllowInsecure => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls
        };
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
            if (!string.IsNullOrEmpty(smtp.Username))
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            foreach (var email in emails)
                failures[email.Id] = "SMTP connection failed: " + e.GetType().Name;
            return failures;
        }

        foreach (var email in emails)
        {
            if (!EmailAddresses.IsValid(email.Recipient))
            {
                failures[email.Id] = "Recipient address is not valid.";
                continue;
            }

            // A broken connection fails the rest of the batch for a later round, never the emails already sent.
            if (!client.IsConnected)
            {
                failures[email.Id] = "SMTP connection lost.";
                continue;
            }

            using var message = Build(settings, email);
            try
            {
                await client.SendAsync(message, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures[email.Id] = "SMTP refused the message: " + e.GetType().Name;
            }
        }

        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Every email has its outcome; a failed QUIT changes none of them.
        }

        return failures;
    }

    /// <summary>The message as it goes on the wire: one recipient, a cleaned one-line subject, a plain-text body.</summary>
    public static MimeMessage Build(EmailSettings settings, OutgoingEmail email)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(email);
        if (!EmailAddresses.IsValid(email.Recipient))
            throw new ArgumentException("The recipient address is not valid.", nameof(email));
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.Recipient));
        message.Subject = EmailTemplates.Clean(email.Subject, EmailMessage.MaxSubjectLength);
        message.Headers.Add("Auto-Submitted", "auto-generated");
        var text = new TextPart("plain") { Text = email.Body };
        if (email.Attachments is not { Count: > 0 })
        {
            message.Body = text;
            return message;
        }

        var mixed = new Multipart("mixed") { text };
        foreach (var file in email.Attachments)
        {
            var type = file.MediaType.Split('/');
            var part = new MimePart(type[0], type[1])
            {
                Content = new MimeContent(new MemoryStream(file.Content)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = file.FileName
            };
            part.ContentType.Charset = "utf-8";
            mixed.Add(part);
        }

        message.Body = mixed;
        return message;
    }
}

/// <summary>What an alert email says, from the alert as it is now.</summary>
public static class AlertEmailValues
{
    public static IReadOnlyDictionary<string, string> Of(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        string Time(DateTime? t) => t is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-";
        // The role only: the free-text escalation contact stays out of emails (no officer data, wiki 14).
        var escalation = alert.EscalateAfterMinutes is { } minutes
            ? $"after {minutes} min to {(alert.EscalateToRole ?? (alert.EscalationContact is null ? "the owner's escalation path" : "the rule's escalation contact"))}"
            : "no";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SiteCode"] = alert.SiteCode,
            ["RuleCode"] = alert.RuleCode,
            ["RuleName"] = alert.RuleName,
            ["ZoneName"] = alert.ZoneName,
            ["DeviceCode"] = alert.DeviceCode,
            ["Metric"] = alert.Metric.ToString(),
            ["Value"] = alert.RaisedValue.ToString("0.#", CultureInfo.InvariantCulture),
            ["Severity"] = alert.Severity.ToString(),
            ["OwnerRole"] = alert.OwnerRole ?? "the zone's owner",
            ["Escalation"] = escalation,
            ["RaisedUtc"] = Time(alert.RaisedUtc),
            ["EscalatedUtc"] = Time(alert.EscalatedUtc)
        };
    }
}
