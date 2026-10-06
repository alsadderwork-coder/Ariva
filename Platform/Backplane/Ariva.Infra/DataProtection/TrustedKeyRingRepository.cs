using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// Admits into the key ring only keys Ariva wrote (ARV-080, CWE-501). The runtime login may INSERT into
/// data_protection_key, and Data Protection would otherwise accept any key element it finds: one whose master key is in
/// plain text, or one in the framework's older EncryptedXml format, which anyone can produce with the public certificate.
/// Such a key with a later activation date would become the default key, and every newly protected value (and every
/// secret the re-protection job moves) would be readable by whoever planted it.
/// <list type="bullet">
/// <item>A key whose master key names <see cref="OaepGcmXmlDecryptor"/> is admitted; that decryptor also verifies the
/// element's RSA-PSS signature, which needs the certificate's private key.</item>
/// <item>A key in the older format (<c>EncryptedXmlDecryptor</c>) is admitted only before
/// <c>DataProtection:LegacyFormatUntil</c>, a migration window set in configuration (never derived from the data).
/// Without the setting the older format is refused.</item>
/// <item>Revocations are admitted (at worst they force a new key). Anything else is skipped and logged.</item>
/// </list>
/// </summary>
public sealed class TrustedKeyRingRepository(IXmlRepository inner, DateTimeOffset? legacyFormatUntil, TimeProvider time, ILogger logger) : IXmlRepository
{
    private static readonly string OurDecryptor = typeof(OaepGcmXmlDecryptor).FullName!;
    private const string LegacyDecryptor = "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor";

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var admitted = new List<XElement>();
        foreach (var element in inner.GetAllElements())
        {
            var verdict = Verdict(element);
            if (verdict is null)
                admitted.Add(element);
            else
                logger.LogWarning("Data Protection key ring element {Element} {Id} skipped: {Reason}", element.Name.LocalName, (string)element.Attribute("id") ?? "?", verdict);
        }

        return admitted;
    }

    public void StoreElement(XElement element, string friendlyName) => inner.StoreElement(element, friendlyName);

    /// <summary>Null when the element may enter the key ring, otherwise why not.</summary>
    public string Verdict(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.Name.LocalName == "revocation")
            return null;
        if (element.Name.LocalName != "key")
            return "not a key or a revocation";

        var secrets = element.Descendants().Where(e => e.Name.LocalName == "encryptedSecret").ToList();
        if (secrets.Count != 1)
            return "its master key is not encrypted";

        var decryptor = (string)secrets[0].Attribute("decryptorType") ?? string.Empty;
        var typeName = decryptor.Split(',')[0].Trim();
        if (string.Equals(typeName, OurDecryptor, StringComparison.Ordinal))
            return null;
        if (string.Equals(typeName, LegacyDecryptor, StringComparison.Ordinal))
        {
            return legacyFormatUntil is { } until && time.GetUtcNow() < until
                ? null
                : "it is in the older EncryptedXml format and DataProtection:LegacyFormatUntil does not admit it";
        }

        return $"its master key is encrypted by {typeName}, which Ariva does not use";
    }
}
