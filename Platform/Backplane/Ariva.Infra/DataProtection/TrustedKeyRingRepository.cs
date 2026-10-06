using System.Globalization;
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
/// <item>A key is admitted only in the exact shape Data Protection writes: a single <c>encryptedSecret</c> at
/// <c>key/descriptor/descriptor</c> (where the master key was), and no <c>masterKey</c> or <c>value</c> element anywhere
/// else in the key. A plain-text master key beside a genuine encrypted blob copied from another row (a decoy) is
/// therefore refused.</item>
/// <item>That <c>encryptedSecret</c> must name <see cref="OaepGcmXmlDecryptor"/>, which verifies the element's RSA-PSS
/// signature (it needs the certificate's private key).</item>
/// <item>A key in the older format (<c>EncryptedXmlDecryptor</c>) is admitted only before
/// <c>DataProtection:LegacyFormatUntil</c>, a migration window set in configuration, and only as an expired copy: it
/// still decrypts what it protected, but Data Protection never makes an expired key the default, so a key planted in
/// that format during the window protects nothing new.</item>
/// <item>Revocations are admitted (see the security guide: a planted revocation is an availability risk). Anything else
/// is skipped and logged.</item>
/// </list>
/// </summary>
public sealed class TrustedKeyRingRepository(IXmlRepository inner, DateTimeOffset? legacyFormatUntil, TimeProvider time, ILogger logger) : IXmlRepository
{
    private static readonly string OurDecryptor = typeof(OaepGcmXmlDecryptor).FullName!;
    private const string LegacyDecryptor = "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor";

    /// <summary>Until when keys in the older format are read; null when they are refused.</summary>
    public DateTimeOffset? LegacyFormatUntil => legacyFormatUntil;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var admitted = new List<XElement>();
        foreach (var element in inner.GetAllElements())
        {
            var verdict = Verdict(element);
            if (verdict is not null)
            {
                logger.LogWarning("Data Protection key ring element {Element} {Id} skipped: {Reason}", element.Name.LocalName, (string)element.Attribute("id") ?? "?", verdict);
                continue;
            }

            admitted.Add(IsLegacy(element) ? ExpiredCopy(element) : element);
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

        // Data Protection reads these by their exact, unqualified names; a second or namespaced copy could make the filter
        // and Data Protection see different dates or descriptors.
        foreach (var name in (string[])["creationDate", "activationDate", "expirationDate", "descriptor"])
        {
            var children = element.Elements().Where(e => e.Name.LocalName == name).ToList();
            if (children.Count != 1 || children[0].Name != XName.Get(name))
                return $"it does not have exactly one unqualified {name}";
        }

        // Data Protection stores the master key as one encryptedSecret in place of the masterKey element and, when it
        // reads the key, decrypts that element back into place; a masterKey or value element anywhere else is plain text.
        var secrets = element.Descendants().Where(e => e.Name.LocalName == "encryptedSecret").ToList();
        if (secrets.Count != 1)
            return "it does not hold exactly one encrypted secret";

        var path = secrets[0].Ancestors().Select(a => a.Name.LocalName).ToList();
        if (path is not ["descriptor", "descriptor", "key"])
            return "its encrypted secret is not at key/descriptor/descriptor";

        if (element.Descendants().Any(e => e.Name.LocalName is "masterKey" or "value" && !e.Ancestors().Contains(secrets[0])))
            return "it holds a plain-text master key outside its encrypted secret";

        var typeName = ((string)secrets[0].Attribute("decryptorType") ?? string.Empty).Split(',')[0].Trim();
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

    private static bool IsLegacy(XElement element) =>
        element.Name.LocalName == "key" &&
        element.Descendants().Any(e => e.Name.LocalName == "encryptedSecret" &&
                                       ((string)e.Attribute("decryptorType") ?? string.Empty).StartsWith(LegacyDecryptor, StringComparison.Ordinal));

    /// <summary>The key with its expiration moved to now at the latest (in memory only): it decrypts, never protects.</summary>
    private XElement ExpiredCopy(XElement key)
    {
        var copy = new XElement(key);
        // The exact name Data Protection reads (XmlKeyManager: keyElement.Element("expirationDate")).
        var expiration = copy.Element("expirationDate");
        var now = time.GetUtcNow();
        if (expiration is null)
        {
            copy.Add(new XElement("expirationDate", now.ToString("O", CultureInfo.InvariantCulture)));
        }
        else if (!DateTimeOffset.TryParse(expiration.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stored) || stored > now)
        {
            expiration.Value = now.ToString("O", CultureInfo.InvariantCulture);
        }

        return copy;
    }
}
