using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.DataProtection;

/// <summary>How a key ring element's signer stands with this host (ARV-211).</summary>
public enum SignerStanding
{
    /// <summary>Not a signer this host accepts: the element is refused.</summary>
    Refused,

    /// <summary>The current signing certificate: the element is admitted as it is.</summary>
    Current,

    /// <summary>
    /// A previous signing certificate, or a Data Protection certificate's own key inside the upgrade window: the element is
    /// admitted as an expired copy, so it unprotects what it protected and never protects anything new.
    /// </summary>
    Deactivated
}

/// <summary>
/// The key ring's own signing key (ARV-211, NIST SP 800-57 Part 1 section 5.2: one purpose per key). Before it, the Data
/// Protection certificate's RSA key both wrapped each key ring element (key transport) and signed it, so a previous
/// certificate kept for recovery could still sign an element the hosts admitted. Now a certificate with an ECDSA P-256 key,
/// apart from the wrapping certificates, signs every element (ES256), and the wrapping certificates only wrap. Since the
/// review (finding M1) it also signs every row the hosts store, over its friendly name and exact text (<see cref="SignRow"/>,
/// script 0073), so a key's id, dates and descriptor and every revocation are a host's.
/// <list type="bullet">
/// <item>Clusters: PEM files from the ariva-dataprotection-signing Kubernetes secret (type kubernetes.io/tls), mounted
/// read-only; <c>DataProtection:Signing:CertificatePath</c> and <c>KeyPath</c>. Its key usage must be digital signature
/// alone, so the certificate cannot pass for an encryption certificate.</item>
/// <item>Rotation: the new certificate becomes current and the old one's certificate alone (no key: a previous signer only
/// verifies; a file that holds a private key is refused) goes under <c>DataProtection:Signing:Previous:n:CertificatePath</c>,
/// in a cluster from the chart value <c>dataProtectionSigningPreviousSecretName</c>. Elements the previous signer signed are
/// admitted only as expired copies (<see cref="SignerStanding.Deactivated"/>), so the hosts make a new default key signed by
/// the current signer at once, and the re-protection job moves every stored value to it; then the previous entry goes, and
/// an element signed by that retired signer is refused from then on.</item>
/// <item>Upgrade from the shared key: an element written before ARV-211 carries no signer and is signed by the certificate
/// that wraps it. It is admitted, as an expired copy, only while <c>DataProtection:Signing:CertificateSignedUntil</c> is in
/// the future, so a running installation keeps reading its keys while the re-protection job moves its values; after that,
/// a previous certificate's private key can no longer make an element the hosts admit. The window is at most
/// <see cref="MaxCertificateSignedDays"/> days ahead, and <see cref="KeyRingUpgradeCheck"/> stops the migration job and the
/// hosts when the stored values need such keys, none is under a signed key yet and the window is not open (an upgrade or a
/// restore without it); leftovers after the upgrade are measured, never refused.</item>
/// <item>A retired signer's certificate lost for good (the ARV-211 review's finding L-1): the values under the keys it signed cannot
/// be read again, and when none is under a configured signer's key the hosts refuse to start. <c>DataProtection:Signing:RetiredSignerAcceptedUntil</c>,
/// at most <see cref="MaxRetiredSignerAcceptedDays"/> days ahead, accepts that loss: the hosts start, those values stay unreadable,
/// and an administrator has each set again (wiki 10 section 4.13).</item>
/// <item>vm-local (<c>DataProtection:UseDevelopmentCertificate</c>): a self-signed P-256 certificate created once beside the
/// development Data Protection certificate and reused.</item>
/// </list>
/// Anything else fails startup.
/// </summary>
public sealed class KeyRingSigning
{
    #region Constants

    /// <summary>The signature algorithm label written into every element this signs (ECDSA P-256 with SHA-256).</summary>
    public const string Algorithm = "ES256";

    /// <summary>The configuration section, under DataProtection.</summary>
    public const string SectionName = "DataProtection:Signing";

    /// <summary>
    /// The latest <c>DataProtection:Signing:CertificateSignedUntil</c> a host accepts, in days after it starts (the review's
    /// finding L1): the upgrade window is a short migration step, never a standing setting.
    /// </summary>
    public const int MaxCertificateSignedDays = 90;

    /// <summary>
    /// The latest <c>DataProtection:Signing:RetiredSignerAcceptedUntil</c> a host accepts, in days after it starts (the ARV-211
    /// review's finding L-1): the acceptance is a recovery step after a signing certificate was lost for good, never a standing setting.
    /// </summary>
    public const int MaxRetiredSignerAcceptedDays = 7;

    /// <summary>What every row signature covers first (ARV-211, script 0073), so it can never pass for another signature.</summary>
    public const string RowLabel = "ariva-keyring-row-v1";

    private const string NistP256 = "1.2.840.10045.3.1.7";

    /// <summary>A row signature: the algorithm, the signer's thumbprint and the 64-byte signature in base64 (script 0073's shape).</summary>
    private const int RowSignatureBytes = 64;

    #endregion

    #region Fields

    private readonly Dictionary<string, X509Certificate2> _previous;

    #endregion

    #region Constructors

    private KeyRingSigning(X509Certificate2 current, IReadOnlyList<X509Certificate2> previous, DateTimeOffset? certificateSignedUntil,
        DateTimeOffset? retiredSignerAcceptedUntil)
    {
        Current = current;
        Previous = previous;
        CertificateSignedUntil = certificateSignedUntil;
        RetiredSignerAcceptedUntil = retiredSignerAcceptedUntil;
        Signer = SignerOf(current);
        _previous = previous.ToDictionary(SignerOf, c => c, StringComparer.Ordinal);
    }

    #endregion

    #region Properties

    /// <summary>Signs every new element; its private key is the only signing key a host holds.</summary>
    public X509Certificate2 Current { get; }

    /// <summary>The current signer's name in an element: the SHA-256 thumbprint of its certificate, upper-case hex.</summary>
    public string Signer { get; }

    /// <summary>Previous signing certificates, their public halves only: they verify, never sign.</summary>
    public IReadOnlyList<X509Certificate2> Previous { get; }

    /// <summary>Until when elements signed with a Data Protection certificate's own key (before ARV-211) are read; null: refused.</summary>
    public DateTimeOffset? CertificateSignedUntil { get; }

    /// <summary>
    /// Until when the hosts start although every stored value needs a key a signer no host has configured signed (the ARV-211
    /// review's finding L-1, <see cref="RetiredSignerAcceptedUntilOf"/>); null: such a start is refused. It admits no key.
    /// </summary>
    public DateTimeOffset? RetiredSignerAcceptedUntil { get; }

    #endregion

    #region Methods

    /// <summary>The name an element gives its signer: the certificate's SHA-256 thumbprint.</summary>
    public static string SignerOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>How the signer an element names stands here, and its certificate when it is current or previous.</summary>
    public SignerStanding Standing(string signer, out X509Certificate2 certificate)
    {
        certificate = null;
        if (string.IsNullOrEmpty(signer))
            return SignerStanding.Refused;
        if (string.Equals(signer, Signer, StringComparison.Ordinal))
        {
            certificate = Current;
            return SignerStanding.Current;
        }

        return _previous.TryGetValue(signer, out certificate) ? SignerStanding.Deactivated : SignerStanding.Refused;
    }

    /// <summary>Whether an element signed with a Data Protection certificate's own key (the format before ARV-211) is still read.</summary>
    public bool ReadsCertificateSigned(DateTimeOffset now) => CertificateSignedUntil is { } until && now < until;

    /// <summary>Whether <see cref="RetiredSignerAcceptedUntil"/> still holds: the loss of a retired signer's keys is accepted, they stay unreadable.</summary>
    public bool AcceptsRetiredSignerLoss(DateTimeOffset now) => RetiredSignerAcceptedUntil is { } until && now < until;

    /// <summary>
    /// The signature a host writes beside a key ring row it stores (ARV-211, script 0073, the review's finding M1): ES256 by the
    /// current signing certificate over the row's friendly name and its exact text, so a key id, a date, a descriptor or a
    /// revocation cannot be changed, copied under another name or added by a database writer.
    /// </summary>
    public string SignRow(string friendlyName, string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        using var key = Current.GetECDsaPrivateKey()
                        ?? throw new InvalidOperationException("The key ring signing certificate has no ECDSA private key to sign with.");
        var signature = key.SignData(RowSigned(Signer, friendlyName, xml), HashAlgorithmName.SHA256);
        return $"{Algorithm}:{Signer}:{Convert.ToBase64String(signature)}";
    }

    /// <summary>
    /// How a row's signature stands here, checked over its exact text before anything parses it: <see cref="SignerStanding.Current"/>
    /// or <see cref="SignerStanding.Deactivated"/> (a previous signer) when it verifies, otherwise <see cref="SignerStanding.Refused"/>
    /// with the reason, a fixed text (a signer's name only in a thumbprint's shape). Never throws for a malformed signature.
    /// </summary>
    public SignerStanding CheckRow(KeyRingRow row, out string reason)
    {
        ArgumentNullException.ThrowIfNull(row);
        var parts = (row.Signature ?? string.Empty).Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            reason = $"its row signature is not {Algorithm}";
            return SignerStanding.Refused;
        }

        var standing = Standing(parts[1], out var certificate);
        if (standing == SignerStanding.Refused)
        {
            reason = $"its row is signed by {ThumbprintOrNot(parts[1])}, which is neither the current nor a previous key ring signing certificate (a retired signing key)";
            return SignerStanding.Refused;
        }

        var signature = new byte[RowSignatureBytes];
        if (!Convert.TryFromBase64String(parts[2], signature, out var written) || written != RowSignatureBytes)
        {
            reason = "its row signature is not a 64-byte ES256 signature";
            return SignerStanding.Refused;
        }

        using var verifier = certificate.GetECDsaPublicKey();
        if (verifier is null || row.FriendlyName is null || row.Xml is null ||
            !verifier.VerifyData(RowSigned(parts[1], row.FriendlyName, row.Xml), signature, HashAlgorithmName.SHA256))
        {
            reason = "its row signature does not verify";
            return SignerStanding.Refused;
        }

        reason = null;
        return standing;
    }

    /// <summary>A signer's name as it may reach a log line: as written only in the shape of a SHA-256 thumbprint (CWE-117).</summary>
    public static string ThumbprintOrNot(string name) =>
        name is { Length: 64 } && name.All(Uri.IsHexDigit) ? name : "a name that is not a SHA-256 thumbprint";

    public static KeyRingSigning Load(IConfiguration configuration, DataProtectionCertificates wrapping)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(wrapping);
        var section = configuration.GetSection(SectionName);

        X509Certificate2 current;
        var certificatePath = section["CertificatePath"];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            current = FromPem(certificatePath, section["KeyPath"]);
        }
        else if (bool.TryParse(configuration["DataProtection:UseDevelopmentCertificate"], out var development) && development)
        {
            current = DevelopmentCertificate(configuration["DataProtection:DevelopmentCertificateDirectory"]);
        }
        else
        {
            throw new InvalidOperationException(
                "The Data Protection key ring needs a signing certificate of its own (ARV-211): set DataProtection:Signing:CertificatePath and KeyPath " +
                "(mounted from the ariva-dataprotection-signing secret), or DataProtection:UseDevelopmentCertificate on a developer machine.");
        }

        var previous = new List<X509Certificate2>();
        foreach (var entry in section.GetSection("Previous").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entry["KeyPath"]))
                throw new InvalidOperationException(
                    "A previous key ring signing certificate is given by its certificate alone: remove DataProtection:Signing:Previous:n:KeyPath (a previous signer only verifies, ARV-211).");
            var path = entry["CertificatePath"];
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("A previous key ring signing certificate needs its CertificatePath.");
            var pem = File.ReadAllText(path);
            if (HoldsPrivateKey(pem))
                throw new InvalidOperationException(
                    "The previous key ring signing certificate's file holds a private key; mount its certificate alone (tls.crt), a previous signer only verifies (ARV-211).");
            previous.Add(X509Certificate2.CreateFromPem(pem));
        }

        var now = DateTimeOffset.UtcNow;
        return From(current, previous, CertificateSignedUntilOf(configuration, now), wrapping, RetiredSignerAcceptedUntilOf(configuration, now));
    }

    /// <summary>Whether PEM text holds a private key block of any kind (PKCS#8, encrypted PKCS#8, RSA or EC), found by its label.</summary>
    internal static bool HoldsPrivateKey(string pem)
    {
        var rest = (pem ?? string.Empty).AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            if (rest[fields.Label].EndsWith("PRIVATE KEY", StringComparison.Ordinal))
                return true;
            rest = rest[fields.Location.End..];
        }

        return false;
    }

    /// <summary>The signing certificates as given, each checked; <paramref name="wrapping"/> must not hold the same key.</summary>
    public static KeyRingSigning From(X509Certificate2 current, IReadOnlyList<X509Certificate2> previous = null, DateTimeOffset? certificateSignedUntil = null,
        DataProtectionCertificates wrapping = null, DateTimeOffset? retiredSignerAcceptedUntil = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        RequireSigningCertificate(current);
        if (!current.HasPrivateKey)
            throw new InvalidOperationException($"The key ring signing certificate {current.Subject} has no private key.");
        var verifiers = new List<X509Certificate2>();
        foreach (var certificate in previous ?? [])
        {
            RequireSigningCertificate(certificate);
            if (certificate.HasPrivateKey)
                throw new InvalidOperationException($"The previous key ring signing certificate {certificate.Subject} is given with its private key; a previous signer only verifies (ARV-211).");
            if (!string.Equals(SignerOf(certificate), SignerOf(current), StringComparison.Ordinal))
                verifiers.Add(certificate);
        }

        foreach (var certificate in wrapping?.All ?? [])
        {
            if (certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(current.PublicKey.ExportSubjectPublicKeyInfo()))
                throw new InvalidOperationException("The key ring signing certificate holds the same key as a Data Protection certificate; give it a key of its own (ARV-211).");
        }

        return new KeyRingSigning(current, verifiers, certificateSignedUntil, retiredSignerAcceptedUntil);
    }

    /// <summary>
    /// DataProtection:Signing:CertificateSignedUntil (ARV-211): until when elements written before the key ring had a signing key
    /// of its own are read, so stored secrets can be re-protected after the upgrade; unset, they are refused. An ISO 8601 date or
    /// time, UTC when no offset is given.
    /// </summary>
    /// <remarks>
    /// The review's finding L1: a window more than <see cref="MaxCertificateSignedDays"/> days after <paramref name="now"/> is
    /// refused, so it cannot be left open as a standing setting; one that has passed is kept as it is (closed).
    /// </remarks>
    public static DateTimeOffset? CertificateSignedUntilOf(IConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[$"{SectionName}:CertificateSignedUntil"];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var until = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new InvalidOperationException("DataProtection:Signing:CertificateSignedUntil is not a date (use YYYY-MM-DD).");
        if (until > now.AddDays(MaxCertificateSignedDays))
            throw new InvalidOperationException(
                $"DataProtection:Signing:CertificateSignedUntil {until.UtcDateTime:yyyy-MM-dd} is more than {MaxCertificateSignedDays} days ahead; the upgrade window is a short " +
                "migration step: set a date 7 to 90 days ahead and remove it after the upgrade (wiki 04 section 6.2).");
        return until;
    }

    /// <summary>
    /// DataProtection:Signing:RetiredSignerAcceptedUntil (the ARV-211 review's finding L-1): until when the hosts start although no
    /// stored value is under a key a configured signer signed and some need a key a signer no host has configured signed, because
    /// that signer's certificate is lost for good (wiki 10 section 4.13). It admits no key: those values stay unreadable, and the
    /// hosts start only so that an administrator can have each set again. It never excuses keys from before ARV-211 outside
    /// <c>CertificateSignedUntil</c>. A date (YYYY-MM-DD, read as UTC) or a time in UTC; unset, such a start is refused.
    /// </summary>
    /// <remarks>
    /// Read as <see cref="CertificateSignedUntilOf"/> reads its window, and bounded harder: a date more than
    /// <see cref="MaxRetiredSignerAcceptedDays"/> days after <paramref name="now"/>, or a time with an offset other than UTC, is
    /// refused, so the acceptance cannot stand; one that has passed is kept as it is (it accepts nothing).
    /// </remarks>
    public static DateTimeOffset? RetiredSignerAcceptedUntilOf(IConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[$"{SectionName}:RetiredSignerAcceptedUntil"];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var until = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new InvalidOperationException("DataProtection:Signing:RetiredSignerAcceptedUntil is not a date (use YYYY-MM-DD, UTC).");
        if (until.Offset != TimeSpan.Zero)
            throw new InvalidOperationException(
                "DataProtection:Signing:RetiredSignerAcceptedUntil is not in UTC: give a date (YYYY-MM-DD) or a time that ends in Z (wiki 10 section 4.13).");
        if (until > now.AddDays(MaxRetiredSignerAcceptedDays))
            throw new InvalidOperationException(
                $"DataProtection:Signing:RetiredSignerAcceptedUntil {until.UtcDateTime:yyyy-MM-dd} is more than {MaxRetiredSignerAcceptedDays} days ahead; it is a recovery " +
                "step for a key ring signing certificate lost for good: set a date at most 7 days ahead and remove it once the values are set again (wiki 10 section 4.13).");
        return until;
    }

    /// <summary>
    /// ARV-211: a key ring signing certificate holds an ECDSA P-256 key (128 bits of security, ASVS V11.2.3) and says it is for
    /// digital signatures and nothing else: a key usage extension with digitalSignature and without key or data encipherment or
    /// key agreement. An RSA key is refused, so the signing key can never be a wrapping certificate's key.
    /// </summary>
    public static void RequireSigningCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is null || !IsP256(ecdsa))
            throw new InvalidOperationException($"The key ring signing certificate {certificate.Subject} has no ECDSA P-256 key; the key ring is signed with ES256 (ARV-211).");
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        const X509KeyUsageFlags others = X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyAgreement |
                                         X509KeyUsageFlags.EncipherOnly | X509KeyUsageFlags.DecipherOnly;
        if (usage is null || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) || (usage.KeyUsages & others) != 0)
            throw new InvalidOperationException(
                $"The key ring signing certificate {certificate.Subject} must carry a key usage of digitalSignature without key or data encipherment or key agreement (ARV-211).");
    }

    private static bool IsP256(ECDsa ecdsa)
    {
        var curve = ecdsa.ExportParameters(false).Curve;
        return ecdsa.KeySize == 256 && curve.IsNamed &&
               (curve.Oid?.Value == NistP256 || curve.Oid?.FriendlyName is "nistP256" or "ECDSA_P256");
    }

    private static X509Certificate2 FromPem(string certificatePath, string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("The key ring signing certificate needs both DataProtection:Signing:CertificatePath and KeyPath.");
        return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
    }

    /// <summary>
    /// Self-signed P-256 certificate, ten years, digital signature only, stored with its key as one PEM file readable by the current
    /// user only. The hosts of a developer machine start together: each that finds no file writes its own under a name of its own
    /// and moves it into place, which fails when another got there first, so every host reads the one file that won, never a
    /// certificate from one host and a key from another.
    /// </summary>
    private static X509Certificate2 DevelopmentCertificate(string directory)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ariva")
            : directory;
        var path = Path.Combine(directory, "dataprotection-signing-dev.pem");

        if (!File.Exists(path))
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Ariva Data Protection key ring signing (development)", ecdsa, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            var own = Path.Combine(directory, $"dataprotection-signing-dev.{Guid.CreateVersion7():N}.tmp");
            WritePrivate(own, created.ExportCertificatePem() + "\n" + ecdsa.ExportPkcs8PrivateKeyPem() + "\n");
            try
            {
                File.Move(own, path, overwrite: false);
            }
            catch (IOException)
            {
                // Another host's file is in place (use it), or the move failed: either way this host's own file, which holds a
                // private key, does not stay behind (the review's finding L6). With no file in place the failure stands.
                File.Delete(own);
                if (!File.Exists(path))
                    throw;
            }
        }

        return X509Certificate2.CreateFromPemFile(path);
    }

    /// <summary>What a row signature covers: <see cref="RowLabel"/>, the algorithm and signer, the friendly name and the text, each prefixed with its length.</summary>
    private static byte[] RowSigned(string signer, string friendlyName, string xml)
    {
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var part in (string[])[RowLabel, $"{Algorithm}|{signer}", friendlyName, xml])
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }

        return buffer.ToArray();
    }

    private static void WritePrivate(string path, string content)
    {
        using var stream = DataProtectionCertificates.CreatePrivate(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    #endregion
}
