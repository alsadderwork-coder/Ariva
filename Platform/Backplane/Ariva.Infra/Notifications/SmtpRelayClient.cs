using System.Net.Security;
using Ariva.Infra.Settings;
using MailKit.Net.Smtp;

namespace Ariva.Infra.Notifications;

/// <summary>
/// ARV-226 (CWE-918): MailKit's SMTP client whose TLS handshake, TLS on connect and STARTTLS alike, builds the relay's chain without
/// downloads and checks no revocation (<see cref="CertificateDownloads"/>). MailKit checks revocation online by default and gives
/// SslStream no chain policy, so the platform's default would ask the addresses the relay's certificate names, before the relay is
/// authenticated. MailKit's own check of the certificate (an error refuses it) is kept.
/// </summary>
internal sealed class SmtpRelayClient : SmtpClient
{
    protected override SslClientAuthenticationOptions GetSslClientAuthenticationOptions(string host, RemoteCertificateValidationCallback remoteCertificateValidationCallback)
    {
        var options = base.GetSslClientAuthenticationOptions(host, remoteCertificateValidationCallback);
        CertificateDownloads.Refuse(options);
        return options;
    }
}
