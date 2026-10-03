using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Microsoft.AspNetCore.DataProtection;

namespace Ariva.Infra.Integration;

/// <summary>
/// Outbound endpoints' settings (ARV-045, <c>Integration:Outbound</c>). Lab hosts may be called over plain HTTP and
/// loopback may be dialled only where <see cref="LabEnvironments"/> allows it (a local machine or a test run, never a
/// cluster); <see cref="MaxResponseBytes"/> caps every answer.
/// </summary>
public sealed class OutboundSettings
{
    public const string SectionName = "Integration:Outbound";
    public static readonly IReadOnlySet<string> LabEnvironments = new HashSet<string>(["vm-local"], StringComparer.Ordinal);

    public IReadOnlyList<string> LabHosts { get; set; } = [];
    public bool AllowLoopback { get; set; }
    public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

    public IReadOnlySet<string> LabHostSet => new HashSet<string>(LabHosts ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why these settings are not allowed, or null. Lab settings need both the host's environment (DOTNET_ENVIRONMENT) and
    /// Application:Environment to be a lab one: Application:Environment alone defaults to vm-local in the base settings,
    /// so a cluster pod missing its environment file would otherwise pass.
    /// </summary>
    public string Problem(string hostEnvironment, string applicationEnvironment) =>
        (AllowLoopback || (LabHosts?.Count ?? 0) > 0) &&
        (!LabEnvironments.Contains(hostEnvironment ?? string.Empty) || !LabEnvironments.Contains(applicationEnvironment ?? string.Empty))
            ? $"Integration:Outbound:AllowLoopback and LabHosts are only allowed in vm-local; this host runs as '{hostEnvironment}' with Application:Environment '{applicationEnvironment}'."
            : MaxResponseBytes is < 1024 or > 64 * 1024 * 1024 ? "Integration:Outbound:MaxResponseBytes is 1 KB to 64 MB." : null;
}

/// <summary>An outbound endpoint's secret material, unprotected: only in memory, only in the host that calls the endpoint.</summary>
public sealed record OutboundSecret(string ClientSecret, string TotpSeed, string ApiKey, string HmacKey, string CertificatePfxBase64, string CertificatePassword)
{
    /// <summary>Never printed (records print their members).</summary>
    public override string ToString() => nameof(OutboundSecret);

    public X509Certificate2 Certificate() =>
        CertificatePfxBase64 is null ? null : X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(CertificatePfxBase64), CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
}

/// <summary>
/// Protects and reads outbound secret material with the Data Protection key ring (purpose <c>Ariva.Outbound.v1</c>),
/// shared by Ariva.Api.Main (which stores it) and Ariva.Api.Integration (which calls the endpoints).
/// </summary>
public sealed class OutboundSecrets(IDataProtectionProvider dataProtection)
{
    public const string Purpose = "Ariva.Outbound.v1";
    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    /// <summary>The protected secret and whether it carries a client certificate, or why it cannot be taken (a certificate that does not load or has no private key).</summary>
    public (string Protected, bool HasCertificate, string Error) Protect(OutboundSecretRequest request, OutboundAuthKind kind)
    {
        ArgumentNullException.ThrowIfNull(request);
        var secret = new OutboundSecret(request.ClientSecret, request.TotpSeed?.TrimEnd('='), request.ApiKey, request.HmacKey, request.CertificatePfxBase64,
            request.CertificatePassword);
        if (kind == OutboundAuthKind.MutualTls)
        {
            try
            {
                using var certificate = secret.Certificate();
                if (certificate is null || !certificate.HasPrivateKey)
                    return (null, false, "The client certificate has no private key.");
                if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
                    return (null, false, "The client certificate has expired.");
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                return (null, false, "The client certificate does not load with this password (PKCS#12 in base64).");
            }
        }

        return (_protector.Protect(JsonSerializer.Serialize(secret)), kind == OutboundAuthKind.MutualTls, null);
    }

    /// <summary>True when the text is one PEM certificate that loads (a pinned CA).</summary>
    public static bool IsCertificatePem(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public OutboundSecret Read(string protectedSecret) =>
        JsonSerializer.Deserialize<OutboundSecret>(_protector.Unprotect(protectedSecret)) ?? throw new CryptographicException("Empty outbound secret.");
}
