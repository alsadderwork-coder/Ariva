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
/// carries an RSA-PSS-SHA256 signature made with the certificate's private key over everything else. A database writer
/// can therefore neither alter or relabel an existing key nor add a key of their own: the public certificate alone can
/// encrypt but cannot sign, and <see cref="OaepGcmXmlDecryptor"/> refuses an element whose signature does not verify.
/// </summary>
/// <remarks>
/// Stored format: every key records the assembly-qualified name of <see cref="OaepGcmXmlDecryptor"/>. Renaming or moving
/// that type, or renaming its assembly, makes the stored key ring unreadable (pinned by KeyRingEncryptionTests).
/// </remarks>
public sealed class OaepGcmXmlEncryptor(DataProtectionCertificates certificates) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        return new EncryptedXmlInfo(OaepGcmXml.Encrypt(plaintextElement, certificates.Current), typeof(OaepGcmXmlDecryptor));
    }
}

/// <summary>
/// Decrypts what <see cref="OaepGcmXmlEncryptor"/> wrote, with the certificate the element names, from the current or a
/// previous Data Protection certificate. Data Protection creates it by type with the application's services.
/// </summary>
public sealed class OaepGcmXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        return OaepGcmXml.Decrypt(encryptedElement, services.GetRequiredService<DataProtectionCertificates>().All);
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

    public static XElement Encrypt(XElement plaintextElement, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        ArgumentNullException.ThrowIfNull(certificate);
        using var rsa = certificate.GetRSAPublicKey()
                        ?? throw new InvalidOperationException("The Data Protection certificate has no RSA key.");
        using var signer = certificate.GetRSAPrivateKey()
                           ?? throw new InvalidOperationException("The Data Protection certificate has no RSA private key to sign with.");

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
                new XElement(Namespace + "signature", Convert.ToBase64String(
                    signer.SignData(Signed(thumbprint, wrappedKey, nonce, tag, ciphertext), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static XElement Decrypt(XElement encryptedElement, IReadOnlyList<X509Certificate2> certificates)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        ArgumentNullException.ThrowIfNull(certificates);

        if (encryptedElement.Name != Namespace + "encryptedKey" || (string)encryptedElement.Attribute("algorithm") != Algorithm)
            throw new CryptographicException($"The key ring element is not {Algorithm}.");

        var thumbprint = (string)encryptedElement.Attribute("certificate") ?? string.Empty;
        var certificate = certificates.FirstOrDefault(c =>
                              string.Equals(c.GetCertHashString(HashAlgorithmName.SHA256), thumbprint, StringComparison.OrdinalIgnoreCase))
                          ?? throw new CryptographicException($"No Data Protection certificate with SHA-256 thumbprint {thumbprint} is configured.");
        using var rsa = certificate.GetRSAPrivateKey()
                        ?? throw new CryptographicException("The Data Protection certificate has no RSA private key.");
        using var verifier = certificate.GetRSAPublicKey()
                             ?? throw new CryptographicException("The Data Protection certificate has no RSA key.");

        var nonce = Bytes(encryptedElement, "nonce", NonceBytes);
        var tag = Bytes(encryptedElement, "tag", TagBytes);
        var ciphertext = Bytes(encryptedElement, "ciphertext", expectedLength: null);
        var wrappedKey = Bytes(encryptedElement, "wrappedKey", expectedLength: null);
        // Before anything is decrypted: only the holder of the certificate's private key wrote this element.
        if (!verifier.VerifyData(Signed(thumbprint, wrappedKey, nonce, tag, ciphertext), Bytes(encryptedElement, "signature", expectedLength: null),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The key ring element's signature does not verify.");
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

    /// <summary>Binds the ciphertext to its algorithm label and certificate.</summary>
    private static byte[] AssociatedData(string thumbprint) =>
        Encoding.UTF8.GetBytes($"{Algorithm}|{thumbprint.ToUpperInvariant()}");

    /// <summary>What the signature covers: the associated data and every encrypted part, each prefixed with its length.</summary>
    private static byte[] Signed(string thumbprint, params byte[][] parts)
    {
        using var buffer = new MemoryStream();
        foreach (var part in (byte[][])[AssociatedData(thumbprint), .. parts])
        {
            Span<byte> length = stackalloc byte[4];
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
