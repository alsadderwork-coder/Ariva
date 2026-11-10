using System.Reflection;
using System.Text;
using Ariva.Core;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-040: alert emails. Templates come from Ariva.Resources and name only values an alert gives; every value is one
/// line of printable text, so no value can add a header or a line; addresses are single plain addresses; the message
/// on the wire has one recipient and a one-line subject; settings refuse clear text outside development; a failed
/// send is retried after a growing delay and given up after its attempts.
/// </summary>
public sealed class EmailTests
{
    private static readonly EmailTemplates Templates = new();

    private static Alert NewAlert(string name = "Nowcast above 15 min", string contact = null)
    {
        var rule = new AlertRule("DMO", 1, new AlertRuleValues(name, ["A-VIS"], AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, 10, 12, 1, 1,
            AlertSeverity.Critical, RoleCodes.BorderShiftSupervisor, 10, contact is null ? RoleCodes.TerminalDutyManager : null, contact, true, true)) { Id = Guid.NewGuid() };
        var alert = new Alert(rule, "A-VIS", null, new AlertTransition(AlertTransitionKind.Raised, new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 16.3, null, null));
        alert.Id = Guid.NewGuid();
        return alert;
    }

    [Fact]
    public void Templates_Should_NameOnlyValuesAnAlertGives_When_LoadedFromResources()
    {
        var given = AlertEmailValues.Of(NewAlert()).Keys.ToHashSet();
        foreach (var kind in Enum.GetValues<EmailKind>())
        {
            Templates.Placeholders(kind).Should().NotBeEmpty().And.BeSubsetOf(given, $"{kind} renders every placeholder from the alert");
            var rendered = Templates.Render(kind, AlertEmailValues.Of(NewAlert()));
            rendered.Subject.Should().StartWith("[Ariva]").And.Contain("R-001").And.NotContain("{{");
            rendered.Body.Should().Contain("A-VIS").And.Contain("2026-09-28 18:05").And.NotContain("{{");
        }
    }

    [Fact]
    public void Render_Should_KeepEveryValueOnOneLine_When_AValueCarriesLineBreaksOrInvisibleCharacters()
    {
        var values = new Dictionary<string, string>(AlertEmailValues.Of(NewAlert()))
        {
            ["RuleName"] = "Queue\r\nBcc: attacker@example.com\r\n\r\nInjected body",
            ["ZoneName"] = "A-VIS" + (char)0x2028 + "Reply-To: x@example.com" + (char)0x202E,
            ["SiteCode"] = new string('S', 500)
        };

        var rendered = Templates.Render(EmailKind.AlertRaised, values);

        rendered.Subject.Should().NotContainAny("\r", "\n", ((char)0x2028).ToString(), ((char)0x202E).ToString());
        rendered.Subject.Length.Should().BeLessThanOrEqualTo(EmailMessage.MaxSubjectLength);
        rendered.Body.Split('\n').Should().NotContain(line => line.StartsWith("Bcc:", StringComparison.Ordinal) || line.StartsWith("Reply-To:", StringComparison.Ordinal),
            "a value never starts a line of its own");
        rendered.Body.Should().Contain("QueueBcc: attacker@example.comInjected body").And.NotContain(new string('S', 201));
        EmailTemplates.Clean(new string([(char)0, (char)0x200B, (char)7])).Should().Be("-");
    }

    [Theory]
    [InlineData("border.sup@ariva.test", true)]
    [InlineData("first.last+alerts@sub.example.com", true)]
    [InlineData("a@localhost", false)]
    [InlineData("Border Sup <border@ariva.test>", false)]
    [InlineData("border@ariva.test\r\nBcc: x@evil.test", false)]
    [InlineData("border@ariva.test, other@ariva.test", false)]
    [InlineData("\"quoted\"@ariva.test", false)]
    [InlineData("border sup@ariva.test", false)]
    [InlineData("border@ariva.test (comment)", false)]
    [InlineData("bord\u00E9r@ariva.test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Address_Should_BeOnePlainAddress_When_Checked(string address, bool valid)
    {
        EmailAddresses.IsValid(address).Should().Be(valid);
        EmailAddresses.IsValid(new string('a', 65) + "@ariva.test").Should().BeFalse("a local part is at most 64 characters");
        EmailAddresses.IsValid("a@" + string.Join('.', Enumerable.Repeat(new string('d', 60), 5)) + ".test").Should().BeFalse("at most 254 characters");
    }

    [Fact]
    public void Message_Should_HaveOneRecipientAndAOneLineSubject_When_BuiltForTheWire()
    {
        var settings = new EmailSettings { Enabled = true, FromAddress = "no-reply@ariva.test" };
        using var message = MailKitTransport.Build(settings, new OutgoingEmail(Guid.NewGuid(), "border@ariva.test", "Subject\r\nBcc: evil@example.com", "Body\r\nwith lines"));
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var wire = Encoding.UTF8.GetString(stream.ToArray());

        message.To.Mailboxes.Select(m => m.Address).Should().Equal("border@ariva.test");
        message.Bcc.Count.Should().Be(0);
        wire.Split("\r\n").Should().NotContain(line => line.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase), "the subject cannot add a header");
        message.Headers["Auto-Submitted"].Should().Be("auto-generated");
        var injected = () => MailKitTransport.Build(settings, new OutgoingEmail(Guid.NewGuid(), "a@ariva.test\r\nBcc: b@evil.test", "s", "b"));
        injected.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Message_Should_CarryTheReportFilesAsAttachments_When_TheEmailHasThem()
    {
        var settings = new EmailSettings { Enabled = true, FromAddress = "no-reply@ariva.test" };
        var csv = Encoding.UTF8.GetBytes("site,zone\r\nDMO,'=cmd\r\n");
        using var message = MailKitTransport.Build(settings, new OutgoingEmail(Guid.NewGuid(), "border@ariva.test", "Ariva daily report DMO 2026-09-30", "Summary")
        {
            Attachments = [new EmailAttachment("ariva-DMO-2026-09-30-hours.csv", "text/csv", csv)]
        });

        message.TextBody.Should().Be("Summary");
        var attachment = message.Attachments.OfType<MimeKit.MimePart>().Single();
        attachment.FileName.Should().Be("ariva-DMO-2026-09-30-hours.csv");
        attachment.ContentType.MimeType.Should().Be("text/csv");
        using var content = new MemoryStream();
        attachment.Content.DecodeTo(content, TestContext.Current.CancellationToken);
        content.ToArray().Should().Equal(csv);
    }

    [Theory]
    [InlineData("report.csv\r\nX: y", "text/csv")]
    [InlineData("../report.csv", "text/csv")]
    [InlineData("\"; filename=evil.exe", "text/csv")]
    [InlineData("report.html", "text/html")]
    public void Attachment_Should_BeRefused_When_ItsNameOrTypeIsNotTheServers(string name, string type)
    {
        var attach = () => new EmailAttachment(name, type, [1]);
        attach.Should().Throw<ArgumentException>();
        var large = () => new EmailAttachment("big.csv", "text/csv", new byte[EmailAttachment.MaxBytes + 1]);
        large.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Settings_Should_RefuseClearTextAndMissingParts_When_EmailIsOn()
    {
        new EmailSettings().Problems().Should().BeEmpty("off needs nothing");
        var on = new EmailSettings { Enabled = true, FromAddress = "no-reply@ariva.test" };
        on.Problems().Should().BeEmpty();
        (on with { FromAddress = "Ariva <no-reply@ariva.test>" }).Problems().Should().NotBeEmpty();
        (on with { FromName = "Ariva\r\nBcc: x@evil.test" }).Problems().Should().NotBeEmpty();
        (on with { MaxPerMinute = 0 }).Problems().Should().NotBeEmpty();

        var relay = new SmtpSettings { Host = "smtp.ariva.test" };
        relay.Problems().Should().BeEmpty();
        (relay with { Host = null }).Problems().Should().NotBeEmpty();
        (relay with { Security = "None" }).Problems().Should().Contain(p => p.Contains("AllowInsecure", StringComparison.Ordinal));
        (relay with { Security = "None", AllowInsecure = true }).Problems().Should().BeEmpty("smtp4dev in development and tests");
        (relay with { Security = "Opportunistic" }).Problems().Should().NotBeEmpty();
    }

    [Fact]
    public void SmtpSettings_Should_NeverPrintThePassword_When_Formatted()
    {
        var relay = new SmtpSettings { Host = "smtp.ariva.test", Username = "relay-user", Password = "s3cr3t-relay-password" };

        relay.ToString().Should().NotContain("s3cr3t-relay-password").And.NotContain("relay-user").And.Contain("smtp.ariva.test").And.Contain("(redacted)");
        $"{relay}".Should().NotContain("s3cr3t");
    }

    private static IConfiguration Configuration(string environment, bool insecure = true, string password = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            ["Email:Enabled"] = "true",
            ["Email:FromAddress"] = "no-reply@ariva.test",
            ["Email:Smtp:Host"] = "smtp4dev",
            ["Email:Smtp:Security"] = insecure ? "None" : "StartTls",
            ["Email:Smtp:AllowInsecure"] = insecure ? "true" : "false",
            ["Email:Smtp:Password"] = password
        }).Build();

    [Theory]
    [InlineData("vm-local", true)]
    [InlineData("k8s-dev", true)]
    [InlineData("k8s-demo", false)]
    [InlineData("k8s-prd", false)]
    [InlineData(null, false)]
    public void ClearText_Should_BeAllowedOnlyInDevelopment_When_TheSendingHostStarts(string environment, bool allowed)
    {
        var register = () => Ariva.Di.Extensions.EmailExtensions.AddArivaEmailSending(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), Configuration(environment));

        if (allowed)
            register.Should().NotThrow();
        else
            register.Should().Throw<InvalidOperationException>().WithMessage("*AllowInsecure*");
        var tls = () => Ariva.Di.Extensions.EmailExtensions.AddArivaEmailSending(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), Configuration(environment, insecure: false));
        tls.Should().NotThrow();
    }

    [Fact]
    public void Outbox_Should_HoldNoRelayOrPassword_When_AHostOnlyWritesEmails()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Ariva.Di.Extensions.EmailExtensions.AddArivaEmailOutbox(services, Configuration("k8s-prd", insecure: true, password: "s3cr3t"));

        services.Should().NotContain(d => d.ServiceType == typeof(SmtpSettings) || d.ServiceType == typeof(IEmailTransport), "only the sending host reads the relay and its credential");
    }

    [Fact]
    public void Failure_Should_BeRetriedLaterAndGivenUp_When_SendingKeepsFailing()
    {
        var now = new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc);
        var email = new EmailMessage(EmailKind.AlertRaised, Guid.NewGuid(), "DMO", "border@ariva.test", "s", "b", now);

        email.Failed("SMTP connection failed: SocketException", now, maxAttempts: 3);
        email.Should().Match<EmailMessage>(e => e.Status == EmailStatus.Pending && e.NextAttemptUtc == now.AddMinutes(1) && e.Attempts == 1);
        email.Failed("x", now, 3);
        email.NextAttemptUtc.Should().Be(now.AddMinutes(2));
        email.Failed("x", now, 3);
        email.Status.Should().Be(EmailStatus.Failed);
        var again = () => email.Failed("x", now, 3);
        again.Should().Throw<InvalidOperationException>("given up is final");
        var sent = () => email.Sent(now);
        sent.Should().Throw<InvalidOperationException>();

        var other = new EmailMessage(EmailKind.AlertRaised, Guid.NewGuid(), "DMO", "border@ariva.test", "s", "b", now);
        other.Failed(new string('r', 900), now, 3);
        other.Reason.Should().HaveLength(EmailMessage.MaxErrorLength);
        other.Sent(now.AddMinutes(1));
        var suppress = () => other.Suppress("late");
        suppress.Should().Throw<InvalidOperationException>("sent is final");
    }

    [Fact]
    public void Claim_Should_HoldTheEmailUntilItsEndAndLetOnlyThatClaimMarkIt_When_ARoundClaimsIt()
    {
        // ARV-294: a claim moves the email's next attempt to the claim's end (so no other round finds it due) and marks it with the
        // claimed reason; only the claim that ends at that time still holds it, and the outcome replaces both.
        var now = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        var until = now.AddMinutes(9);
        var email = new EmailMessage(EmailKind.AlertRaised, Guid.NewGuid(), "DMO", "border@ariva.test", "s", "b", now);

        email.Claim(until);

        email.Should().Match<EmailMessage>(e => e.Status == EmailStatus.Pending && e.NextAttemptUtc == until && e.Reason == EmailMessage.ClaimedReason && e.Attempts == 0);
        email.IsClaimedUntil(until).Should().BeTrue();
        email.IsClaimedUntil(until.AddSeconds(1)).Should().BeFalse("another claim's end");
        email.Sent(now.AddMinutes(1));
        email.Should().Match<EmailMessage>(e => e.Status == EmailStatus.Sent && e.Reason == null && e.Attempts == 1);
        email.IsClaimedUntil(until).Should().BeFalse("a sent email is held by no claim");
        var again = () => email.Claim(until.AddMinutes(9));
        again.Should().Throw<InvalidOperationException>("a sent email stays sent");

        var local = new EmailMessage(EmailKind.AlertRaised, Guid.NewGuid(), "DMO", "border@ariva.test", "s", "b", now);
        var notUtc = () => local.Claim(DateTime.SpecifyKind(until, DateTimeKind.Unspecified));
        notUtc.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Release_Should_PutTheEmailBackAsItWasWithoutAnAttempt_When_ItsRoundDidNotTryIt()
    {
        var now = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        var email = new EmailMessage(EmailKind.AlertRaised, Guid.NewGuid(), "DMO", "border@ariva.test", "s", "b", now);
        email.Failed("SMTP connection lost.", now, 8);
        var due = email.NextAttemptUtc;

        email.Claim(now.AddMinutes(10));
        email.Release(due, "SMTP connection lost.");

        email.Should().Match<EmailMessage>(e => e.Status == EmailStatus.Pending && e.NextAttemptUtc == due && e.Reason == "SMTP connection lost." && e.Attempts == 1);
        email.IsClaimedUntil(now.AddMinutes(10)).Should().BeFalse();
        email.Claim(now.AddMinutes(20));
        email.Failed("SMTP refused the message: SmtpCommandException", now.AddMinutes(2), 8);
        email.Should().Match<EmailMessage>(e => e.Attempts == 2 && e.NextAttemptUtc == now.AddMinutes(4) && e.Reason == "SMTP refused the message: SmtpCommandException");
    }

    [Fact]
    public void ClaimLength_Should_OutlastTheRoundsLastMessage_When_TheRelayIsAtItsSlowest()
    {
        // ARV-294 (CWE-362): a round begins no message after its send window, and the message begun last takes at most six exchanges at
        // the SMTP timeout, so its claim (with a minute for the replicas' clocks) runs out only after it: no other round takes it before.
        EmailSender.ClaimLength(new SmtpSettings()).Should().Be(TimeSpan.FromMinutes(9), "5 min + 6 x 30 s + 1 min");
        EmailSender.ClaimLength(new SmtpSettings { TimeoutSeconds = 300 }).Should().Be(TimeSpan.FromMinutes(36), "5 min + 6 x 300 s + 1 min");
        EmailSender.ClaimLength(new SmtpSettings { TimeoutSeconds = 1 })
            .Should().BeGreaterThan(EmailSender.SendWindow + TimeSpan.FromSeconds(MailKitTransport.ExchangesPerMessage));
        EmailSender.SendWindow.Should().BeLessThan(EmailSenderWorker.DefaultRunTimeout, "the run's own timeout never cuts the window short by default");
    }

    [Fact]
    public void Transports_Should_EachTakeTheSendOptionsThemselves_When_AHostShipsThem()
    {
        // ARV-294 (the security review's A5): the interface's default options overload drops the early outcome and the budget, so every
        // transport a host ships implements it itself; one that did not would leave a report's send without its total budget.
        Assembly[] shipped =
        [
            typeof(Ariva.Infra._IAssemblyMark).Assembly, typeof(Ariva.Di.Extensions.EmailExtensions).Assembly, typeof(Ariva.Api.Common.Hubs.HubSessionSweeper).Assembly,
            typeof(Ariva.Api.Main._IAssemblyMark).Assembly, typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly, typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly, typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
        ];
        var withOptions = typeof(IEmailTransport).GetMethod(nameof(IEmailTransport.SendAsync), [typeof(IReadOnlyList<OutgoingEmail>), typeof(EmailSendOptions), typeof(CancellationToken)]);
        withOptions.Should().NotBeNull();

        var transports = shipped.SelectMany(a => a.GetTypes()).Where(t => t.IsClass && !t.IsAbstract && typeof(IEmailTransport).IsAssignableFrom(t)).ToList();

        transports.Should().Contain(typeof(MailKitTransport));
        foreach (var transport in transports)
        {
            var map = transport.GetInterfaceMap(typeof(IEmailTransport));
            map.TargetMethods[Array.IndexOf(map.InterfaceMethods, withOptions)].DeclaringType.Should().Be(transport, $"{transport.Name} takes the outcome and the budget itself");
        }
    }
}
