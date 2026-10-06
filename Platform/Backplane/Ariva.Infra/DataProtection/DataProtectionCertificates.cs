using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// The certificates that protect the Data Protection key ring at rest (ARV-008).
/// <list type="bullet">
/// <item>Clusters: PEM files from the ariva-dataprotection Kubernetes secret (type kubernetes.io/tls), mounted read-only;
/// DataProtection:CertificatePath and DataProtection:KeyPath. During a rotation, DataProtection:Previous:n:CertificatePath
/// and KeyPath list the old certificates so existing keys stay readable.</item>
/// <item>vm-local (DataProtection:UseDevelopmentCertificate): a self-signed certificate created once in the user's
/// profile and reused, so keys written yesterday still decrypt today.</item>
/// </list>
/// Anything else fails startup: an unprotected key ring would put every cookie and encrypted secret at the mercy of a
/// database dump.
/// </summary>
public sealed class DataProtectionCertificates
{
    private DataProtectionCertificates(X509Certificate2 current, IReadOnlyList<X509Certificate2> all)
    {
        Current = current;
        All = all;
    }

    /// <summary>Encrypts new keys.</summary>
    public X509Certificate2 Current { get; }

    /// <summary>Current plus previous certificates; any of them may decrypt existing keys.</summary>
    public IReadOnlyList<X509Certificate2> All { get; }

    public static DataProtectionCertificates Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("DataProtection");

        X509Certificate2 current;
        var certificatePath = section["CertificatePath"];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            current = FromPem(certificatePath, section["KeyPath"]);
        }
        else if (bool.TryParse(section["UseDevelopmentCertificate"], out var development) && development)
        {
            current = DevelopmentCertificate(section["DevelopmentCertificateDirectory"]);
        }
        else
        {
            throw new InvalidOperationException(
                "Data Protection needs a key-protection certificate: set DataProtection:CertificatePath and DataProtection:KeyPath " +
                "(mounted from the ariva-dataprotection secret), or DataProtection:UseDevelopmentCertificate on a developer machine.");
        }

        var all = new List<X509Certificate2> { current };
        foreach (var previous in section.GetSection("Previous").GetChildren())
            all.Add(FromPem(previous["CertificatePath"], previous["KeyPath"]));

        return new DataProtectionCertificates(current, all);
    }

    public static DataProtectionCertificates From(X509Certificate2 current, params X509Certificate2[] previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        RequireStrong(current);
        foreach (var certificate in previous)
            RequireStrong(certificate);
        return new(current, [current, .. previous]);
    }

    /// <summary>The smallest RSA key that may protect the key ring (ASVS V11.2.3: at least 128 bits of security).</summary>
    public const int MinimumRsaKeyBits = 3072;

    /// <summary>
    /// ARV-080: the key ring is wrapped with RSA-OAEP (<see cref="OaepGcmXmlEncryptor"/>), so the certificate must hold an
    /// RSA key of at least <see cref="MinimumRsaKeyBits"/> bits. An EC or other key cannot wrap with OAEP and is refused
    /// too, whatever its size. Checked for previous certificates as well: a weak one would still unlock every old key.
    /// </summary>
    public static void RequireStrong(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null)
            throw new InvalidOperationException(
                $"The Data Protection certificate {certificate.Subject} has no RSA key; the key ring needs RSA {MinimumRsaKeyBits} or larger.");
        if (rsa.KeySize < MinimumRsaKeyBits)
            throw new InvalidOperationException(
                $"The Data Protection certificate {certificate.Subject} has an RSA {rsa.KeySize} key; the key ring needs RSA {MinimumRsaKeyBits} or larger.");
    }

    private static X509Certificate2 FromPem(string certificatePath, string keyPath)
    {
        if (string.IsNullOrWhiteSpace(certificatePath) || string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("A Data Protection certificate needs both CertificatePath and KeyPath.");

        var certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException($"The Data Protection certificate {certificate.Subject} has no private key.");
        RequireStrong(certificate);
        return certificate;
    }

    /// <summary>Self-signed RSA 3072 certificate, ten years, stored as PEM readable by the current user only.</summary>
    private static X509Certificate2 DevelopmentCertificate(string directory)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ariva")
            : directory;
        var certificatePath = Path.Combine(directory, "dataprotection-dev.crt");
        var keyPath = Path.Combine(directory, "dataprotection-dev.key");

        if (!File.Exists(certificatePath) || !File.Exists(keyPath))
        {
            Directory.CreateDirectory(directory);
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=Ariva Data Protection (development)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            WritePrivate(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            WritePrivate(certificatePath, created.ExportCertificatePem());
        }

        return FromPem(certificatePath, keyPath);
    }

    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
