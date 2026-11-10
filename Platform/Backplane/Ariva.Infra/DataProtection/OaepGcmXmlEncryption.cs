using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// Wraps a Data Protection key at rest (ARV-080, ASVS V11.3.1 to V11.3.3): the key XML is encrypted with AES-256-GCM
/// under a fresh 256-bit key, and that key is wrapped with RSA-OAEP-SHA256 by the current Data Protection certificate.
/// It replaces the framework's certificate encryptor, which uses RSA PKCS#1 v1.5 and AES-256-CBC without a MAC.
/// The element names the algorithm and the certificate (SHA-256 thumbprint), both authenticated as associated data, and
/// since ARV-211 carries an ECDSA P-256 signature (ES256) made with the key ring's own signing key
/// (<see cref="KeyRingSigning"/>), which it names, over everything else in the element. A database writer can therefore
/// neither alter the wrapped master key nor wrap one of their own: the public certificate alone can encrypt but cannot sign,
/// a wrapping certificate's key no longer signs anything, and <see cref="OaepGcmXmlDecryptor"/> refuses an element whose
/// signature does not verify under a signer the host accepts. This signature covers the element alone, not the key around
/// it (its id, dates and descriptor): those are covered by the row signature <see cref="TrustedKeyRingRepository"/> checks
/// before a row is parsed (script 0073), so a genuine element copied under another id, other dates or another descriptor is
/// refused there.
/// </summary>
/// <remarks>
/// Stored format: every key records the assembly-qualified name of <see cref="OaepGcmXmlDecryptor"/>. Renaming or moving
/// that type, or renaming its assembly, makes the stored key ring unreadable (pinned by KeyRingEncryptionTests).
/// </remarks>
public sealed class OaepGcmXmlEncryptor(DataProtectionCertificates certificates, KeyRingSigning signing) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        return new EncryptedXmlInfo(OaepGcmXml.Encrypt(plaintextElement, certificates.Current, signing.Current), typeof(OaepGcmXmlDecryptor));
    }
}

/// <summary>
/// Decrypts what <see cref="OaepGcmXmlEncryptor"/> wrote, with the certificate the element names, from the current or a
/// previous Data Protection certificate, once its signature verifies under a signer the host accepts. Data Protection
/// creates it by type with the application's services.
/// </summary>
public sealed class OaepGcmXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        return OaepGcmXml.Decrypt(encryptedElement, services.GetRequiredService<DataProtectionCertificates>().All,
            services.GetRequiredService<KeyRingSigning>(), now);
    }
}

/// <summary>The element format shared by <see cref="OaepGcmXmlEncryptor"/> and <see cref="OaepGcmXmlDecryptor"/>.</summary>
public static class OaepGcmXml
{
    /// <summary>The algorithm label written into every element; a reader refuses any other.</summary>
    public const string Algorithm = "RSA-OAEP-256+A256GCM";

    public static readonly XNamespace Namespace = "urn:ariva:dataprotection:keyring:v1";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>
    /// ARV-211: wraps the element under <paramref name="certificate"/> (RSA-OAEP, key transport only) and signs it with
    /// <paramref name="signer"/>'s ECDSA P-256 key (ES256), which the element names by its SHA-256 thumbprint.
    /// </summary>
    public static XElement Encrypt(XElement plaintextElement, X509Certificate2 certificate, X509Certificate2 signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        using var signingKey = signer.GetECDsaPrivateKey()
                               ?? throw new InvalidOperationException("The key ring signing certificate has no ECDSA private key to sign with.");
        var signerName = KeyRingSigning.SignerOf(signer);
        var element = Wrap(plaintextElement, certificate, signed => signingKey.SignData(Signed(signed.Thumbprint, SignerLabel(signerName), signed.Parts), HashAlgorithmName.SHA256));
        element.SetAttributeValue("signer", signerName);
        element.SetAttributeValue("signatureAlgorithm", KeyRingSigning.Algorithm);
        return element;
    }

    /// <summary>
    /// The format before ARV-211, signed with RSA-PSS by the wrapping certificate's own key: what an installation's existing
    /// keys look like, so the upgrade path can be tested. Nothing in the hosts writes it.
    /// </summary>
    internal static XElement EncryptSignedWithCertificate(XElement plaintextElement, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var signingKey = certificate.GetRSAPrivateKey()
                               ?? throw new InvalidOperationException("The Data Protection certificate has no RSA private key to sign with.");
        return Wrap(plaintextElement, certificate, signed => signingKey.SignData(Signed(signed.Thumbprint, null, signed.Parts), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    /// <summary>
    /// Whether the element may be read here, and how (ARV-211), before anything is decrypted:
    /// <see cref="SignerStanding.Current"/> when the current signing certificate signed it; <see cref="SignerStanding.Deactivated"/>
    /// when a previous signing certificate did, or, inside <see cref="KeyRingSigning.CertificateSignedUntil"/>, the configured
    /// Data Protection certificate that wraps it (the format before ARV-211); otherwise <see cref="SignerStanding.Refused"/>
    /// with the reason. Never throws for an element that is malformed: that is refused.
    /// </summary>
    public static SignerStanding Check(XElement encryptedElement, IReadOnlyList<X509Certificate2> certificates, KeyRingSigning signing, DateTimeOffset now,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(signing);
        try
        {
            if (encryptedElement.Name != Namespace + "encryptedKey" || (string)encryptedElement.Attribute("algorithm") != Algorithm)
            {
                reason = $"it is not {Algorithm}";
                return SignerStanding.Refused;
            }

            var thumbprint = (string)encryptedElement.Attribute("certificate") ?? string.Empty;
            var parts = Parts(encryptedElement);
            var signature = Bytes(encryptedElement, "signature", expectedLength: null);
            var signer = (string)encryptedElement.Attribute("signer");
            if (signer is null)
                return CheckCertificateSigned(thumbprint, parts, signature, certificates, signing, now, out reason);

            if ((string)encryptedElement.Attribute("signatureAlgorithm") != KeyRingSigning.Algorithm)
            {
                reason = $"its signature is not {KeyRingSigning.Algorithm}";
                return SignerStanding.Refused;
            }

            var standing = signing.Standing(signer, out var signerCertificate);
            if (standing == SignerStanding.Refused)
            {
                // The name as written only when it has a thumbprint's shape: the row's text never reaches a log line as it is.
                reason = $"it is signed by {KeyRingSigning.ThumbprintOrNot(signer)}, which is neither the current nor a previous key ring signing certificate (a retired signing key)";
                return SignerStanding.Refused;
            }

            using var verifier = signerCertificate.GetECDsaPublicKey()
                                 ?? throw new CryptographicException("The key ring signing certificate has no ECDSA key.");
            if (!verifier.VerifyData(Signed(thumbprint, SignerLabel(signer), parts), signature, HashAlgorithmName.SHA256))
            {
                reason = "its signature does not verify";
                return SignerStanding.Refused;
            }

            reason = null;
            return standing;
        }
        catch (CryptographicException exception)
        {
            reason = exception.Message;
            return SignerStanding.Refused;
        }
    }

    public static XElement Decrypt(XElement encryptedElement, IReadOnlyList<X509Certificate2> certificates, KeyRingSigning signing, DateTimeOffset now)
    {
        // Before anything is decrypted: only the holder of a signing key this host accepts wrote this element.
        if (Check(encryptedElement, certificates, signing, now, out var reason) == SignerStanding.Refused)
            throw new CryptographicException($"The key ring element is refused: {reason}.");

        var thumbprint = (string)encryptedElement.Attribute("certificate") ?? string.Empty;
        var certificate = Certificate(certificates, thumbprint);
        using var rsa = certificate.GetRSAPrivateKey()
                        ?? throw new CryptographicException("The Data Protection certificate has no RSA private key.");

        var nonce = Bytes(encryptedElement, "nonce", NonceBytes);
        var tag = Bytes(encryptedElement, "tag", TagBytes);
        var ciphertext = Bytes(encryptedElement, "ciphertext", expectedLength: null);
        var wrappedKey = Bytes(encryptedElement, "wrappedKey", expectedLength: null);
        var key = rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            if (key.Length != KeyBytes)
                throw new CryptographicException("The wrapped key has the wrong length.");
            using (var aes = new AesGcm(key, TagBytes))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(thumbprint));

            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>The encrypted parts of an element, in the order the signature covers them.</summary>
    private sealed record SignedParts(string Thumbprint, byte[][] Parts);

    /// <summary>Encrypts and wraps, then signs with <paramref name="sign"/> (the parts the signature covers).</summary>
    private static XElement Wrap(XElement plaintextElement, X509Certificate2 certificate, Func<SignedParts, byte[]> sign)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        ArgumentNullException.ThrowIfNull(certificate);
        using var rsa = certificate.GetRSAPublicKey()
                        ?? throw new InvalidOperationException("The Data Protection certificate has no RSA key.");

        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        try
        {
            using (var aes = new AesGcm(key, TagBytes))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(thumbprint));
            var wrappedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);

            return new XElement(Namespace + "encryptedKey",
                new XAttribute("algorithm", Algorithm),
                new XAttribute("certificate", thumbprint),
                new XElement(Namespace + "wrappedKey", Convert.ToBase64String(wrappedKey)),
                new XElement(Namespace + "nonce", Convert.ToBase64String(nonce)),
                new XElement(Namespace + "tag", Convert.ToBase64String(tag)),
                new XElement(Namespace + "ciphertext", Convert.ToBase64String(ciphertext)),
                new XElement(Namespace + "signature", Convert.ToBase64String(sign(new SignedParts(thumbprint, [wrappedKey, nonce, tag, ciphertext])))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// An element without a signer: written before ARV-211 and signed by the certificate that wraps it. Read only inside the
    /// upgrade window, and then only to unprotect (<see cref="SignerStanding.Deactivated"/>).
    /// </summary>
    private static SignerStanding CheckCertificateSigned(string thumbprint, byte[][] parts, byte[] signature, IReadOnlyList<X509Certificate2> certificates,
        KeyRingSigning signing, DateTimeOffset now, out string reason)
    {
        if (!signing.ReadsCertificateSigned(now))
        {
            reason = "it is signed with a Data Protection certificate's own key (before ARV-211) and DataProtection:Signing:CertificateSignedUntil does not admit it";
            return SignerStanding.Refused;
        }

        using var verifier = Certificate(certificates, thumbprint).GetRSAPublicKey()
                             ?? throw new CryptographicException("The Data Protection certificate has no RSA key.");
        if (!verifier.VerifyData(Signed(thumbprint, null, parts), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            reason = "its signature does not verify";
            return SignerStanding.Refused;
        }

        reason = null;
        return SignerStanding.Deactivated;
    }

    private static X509Certificate2 Certificate(IReadOnlyList<X509Certificate2> certificates, string thumbprint) =>
        certificates.FirstOrDefault(c => string.Equals(c.GetCertHashString(HashAlgorithmName.SHA256), thumbprint, StringComparison.OrdinalIgnoreCase))
        ?? throw new CryptographicException($"No Data Protection certificate with SHA-256 thumbprint {KeyRingSigning.ThumbprintOrNot(thumbprint)} is configured.");

    private static byte[][] Parts(XElement element) =>
    [
        Bytes(element, "wrappedKey", expectedLength: null),
        Bytes(element, "nonce", NonceBytes),
        Bytes(element, "tag", TagBytes),
        Bytes(element, "ciphertext", expectedLength: null)
    ];

    /// <summary>Binds the ciphertext to its algorithm label and certificate.</summary>
    private static byte[] AssociatedData(string thumbprint) =>
        Encoding.UTF8.GetBytes($"{Algorithm}|{thumbprint.ToUpperInvariant()}");

    /// <summary>ARV-211: what binds a signature to its algorithm and signer, so a signature cannot be moved to another signer's name.</summary>
    private static byte[] SignerLabel(string signer) => Encoding.UTF8.GetBytes($"{KeyRingSigning.Algorithm}|{signer}");

    /// <summary>
    /// What the signature covers: the associated data, the signer's label (ARV-211; none in the format before it) and every
    /// encrypted part, each prefixed with its length.
    /// </summary>
    private static byte[] Signed(string thumbprint, byte[] signerLabel, byte[][] parts)
    {
        using var buffer = new MemoryStream();
        byte[][] all = signerLabel is null ? [AssociatedData(thumbprint), .. parts] : [AssociatedData(thumbprint), signerLabel, .. parts];
        Span<byte> length = stackalloc byte[4];
        foreach (var part in all)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            buffer.Write(length);
            buffer.Write(part);
        }

        return buffer.ToArray();
    }

    private static byte[] Bytes(XElement element, string name, int? expectedLength)
    {
        byte[] value;
        try
        {
            value = Convert.FromBase64String((string)element.Element(Namespace + name) ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException($"The key ring element's {name} is not base64.", exception);
        }

        if (value.Length == 0 || (expectedLength is { } length && value.Length != length))
            throw new CryptographicException($"The key ring element's {name} has the wrong length.");
        return value;
    }
}
