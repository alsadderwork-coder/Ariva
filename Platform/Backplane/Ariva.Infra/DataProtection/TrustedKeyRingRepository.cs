using System.Globalization;
using System.Xml;
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
/// <item>ARV-211 (script 0073, the review's finding M1): every row a host stores carries a signature by the key ring signing
/// certificate over its friendly name and exact text (<see cref="KeyRingSigning.SignRow"/>), checked before the row is parsed.
/// A row the current signer signed is admitted as it is; one a previous signer signed, only as an expired copy; any other
/// signature, a retired signer's among them, is refused. So a key's id, dates and descriptor, and every revocation, are the
/// ones a host wrote. A row without a signature was written before ARV-211: it is admitted only while
/// <c>DataProtection:Signing:CertificateSignedUntil</c> is open, only in a format from before ARV-211 (an element that names a
/// signer is a copy of a signed row's), and a key only as an expired copy.</item>
/// <item>A key is admitted only in the exact shape Data Protection writes with Ariva's settings: an id and version 1, its three
/// dates and one descriptor read by <c>AuthenticatedEncryptorDescriptorDeserializer</c>, whose inner descriptor holds the
/// payload algorithm (AES-256-GCM, or AES-256-CBC with HMAC-SHA256 for a key from before ARV-080) and a single
/// <c>encryptedSecret</c> where the master key was, and no <c>masterKey</c> or <c>value</c> element anywhere else. A
/// plain-text master key beside a genuine encrypted blob copied from another row (a decoy) is therefore refused, and so is a
/// genuine encrypted secret moved under another descriptor or algorithm.</item>
/// <item>That <c>encryptedSecret</c> must name <see cref="OaepGcmXmlDecryptor"/> and hold one element whose signature verifies
/// here, before the key enters the ring (ARV-211, <see cref="OaepGcmXml.Check"/>): signed by the current key ring signing
/// certificate, the key is admitted as it is; signed by a previous signing certificate, or by the Data Protection certificate
/// that wraps it while <c>DataProtection:Signing:CertificateSignedUntil</c> admits that format (an installation's keys from
/// before ARV-211), only as an expired copy, so it unprotects and never protects; signed by anything else, a retired signing
/// key among them, it is skipped.</item>
/// <item>A key in the older format (<c>EncryptedXmlDecryptor</c>) is admitted only before
/// <c>DataProtection:LegacyFormatUntil</c>, a migration window set in configuration, and only as an expired copy: it
/// still decrypts what it protected, but Data Protection never makes an expired key the default, so a key planted in
/// that format during the window protects nothing new.</item>
/// <item>Two rows with one key id (Data Protection refuses a ring that holds both): the signed row is kept, else the first.</item>
/// <item>Anything else is skipped and logged, with the element's name, id and type names only in their expected shapes.</item>
/// </list>
/// </summary>
public sealed class TrustedKeyRingRepository(IKeyRingRowStore rows, DataProtectionCertificates certificates, KeyRingSigning signing, DateTimeOffset? legacyFormatUntil,
    TimeProvider time, ILogger logger) : IXmlRepository
{
    #region Constants

    private const string DeserializerType = "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer";
    private const string DataProtectionAssembly = "Microsoft.AspNetCore.DataProtection";
    private const string LegacyDecryptor = "Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor";
    private static readonly string OurDecryptor = typeof(OaepGcmXmlDecryptor).FullName;
    private static readonly string OurAssembly = typeof(OaepGcmXmlDecryptor).Assembly.GetName().Name;

    #endregion

    #region Properties

    /// <summary>Until when keys in the older format are read; null when they are refused.</summary>
    public DateTimeOffset? LegacyFormatUntil => legacyFormatUntil;

    /// <summary>The signing certificates a row's and an element's signature are checked against (ARV-211).</summary>
    public KeyRingSigning Signing => signing;

    #endregion

    #region IXmlRepository

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var now = time.GetUtcNow();
        var admitted = new List<Admitted>();
        var unsigned = 0;
        foreach (var row in rows.GetAllRows())
        {
            var verdict = Verdict(row, now, out var element, out var onlyToUnprotect);
            if (verdict is not null)
            {
                logger.LogWarning("Data Protection key ring element {Element} {Id} skipped: {Reason}", NameOf(element), IdOf(element), verdict);
                continue;
            }

            if (row.Signature is null)
                unsigned++;
            admitted.Add(new Admitted(onlyToUnprotect ? ExpiredCopy(element, now) : element, row.Signature is not null, KeyIdOf(element)));
        }

        if (unsigned > 0)
            logger.LogWarning(
                "Data Protection key ring: {Count} row(s) written before ARV-211 admitted, only to unprotect, until {Until} (DataProtection:Signing:CertificateSignedUntil); remove the window once SecretReprotection has run",
                unsigned, signing.CertificateSignedUntil?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        return Deduplicated(admitted);
    }

    /// <summary>Stores the element with the row signature of the current signing certificate (ARV-211, script 0073).</summary>
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
        var xml = element.ToString(SaveOptions.DisableFormatting);
        rows.StoreRow(new KeyRingRow(friendlyName, xml, signing.SignRow(friendlyName, xml)));
    }

    #endregion

    #region Verdicts

    /// <summary>Null when the stored row may enter the key ring, otherwise why not (its signature first, then its element).</summary>
    public string Verdict(KeyRingRow row) => Verdict(row, time.GetUtcNow(), out _, out _);

    /// <summary>Whether the stored row enters the key ring only as an expired copy. False for a row that is refused.</summary>
    public bool AdmitsOnlyToUnprotect(KeyRingRow row) => Verdict(row, time.GetUtcNow(), out _, out var onlyToUnprotect) is null && onlyToUnprotect;

    /// <summary>
    /// Null when the element may enter the key ring as a signed row would bring it, otherwise why not: the checks of its shape,
    /// descriptor and element signature, without the row's signature. Internal: the hosts check whole rows; tests check an element's own rules with it.
    /// </summary>
    internal string Verdict(XElement element) => Verdict(element, time.GetUtcNow(), out _, out _);

    /// <summary>
    /// Whether the element enters the key ring only as an expired copy (ARV-080 and ARV-211): a key in the older format, signed
    /// by a previous signing certificate, or signed by its wrapping certificate's own key. False for an element that is refused.
    /// </summary>
    internal bool AdmitsOnlyToUnprotect(XElement element) => Verdict(element, time.GetUtcNow(), out var onlyToUnprotect, out _) is null && onlyToUnprotect;

    private string Verdict(KeyRingRow row, DateTimeOffset now, out XElement element, out bool onlyToUnprotect)
    {
        ArgumentNullException.ThrowIfNull(row);
        element = null;
        onlyToUnprotect = false;

        // The row's signature over its exact text, before anything parses it.
        var rowStanding = SignerStanding.Deactivated;
        if (row.Signature is not null)
        {
            rowStanding = signing.CheckRow(row, out var reason);
            if (rowStanding == SignerStanding.Refused)
                return reason;
        }
        else if (!signing.ReadsCertificateSigned(now))
        {
            return "its row carries no signature (written before ARV-211) and DataProtection:Signing:CertificateSignedUntil does not admit it";
        }

        try
        {
            element = PostgresXmlRepository.Parse(row.Xml ?? string.Empty);
        }
        catch (XmlException)
        {
            return "its row is not well-formed XML";
        }

        var verdict = Verdict(element, now, out onlyToUnprotect, out var namesSigner);
        if (verdict is not null)
            return verdict;
        // Before ARV-211 no element named a signer: an unsigned row holding one is a copy of a signed row's element.
        if (row.Signature is null && namesSigner)
            return "its row carries no signature but its element names a key ring signer (a copy of an element written since ARV-211)";
        if (rowStanding == SignerStanding.Deactivated && element.Name.LocalName == "key")
            onlyToUnprotect = true;
        return null;
    }

    private string Verdict(XElement element, DateTimeOffset now, out bool onlyToUnprotect, out bool namesSigner)
    {
        ArgumentNullException.ThrowIfNull(element);
        onlyToUnprotect = false;
        namesSigner = false;
        if (element.Name == XName.Get("revocation"))
            return null;
        if (element.Name != XName.Get("key"))
            return "not a key or a revocation";
        if (KeyIdOf(element) is null || (string)element.Attribute("version") != "1")
            return "it does not have a key id and version 1";

        // Data Protection reads these by their exact, unqualified names; a second or namespaced copy could make the filter
        // and Data Protection see different dates or descriptors. Nothing else is in a key Data Protection writes.
        string[] names = ["creationDate", "activationDate", "expirationDate", "descriptor"];
        foreach (var name in names)
        {
            var children = element.Elements().Where(e => e.Name.LocalName == name).ToList();
            if (children.Count != 1 || children[0].Name != XName.Get(name))
                return $"it does not have exactly one unqualified {name}";
        }

        if (element.Elements().Count() != names.Length)
            return "it holds an element Data Protection does not write in a key";

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

        var descriptorProblem = DescriptorProblem(element.Element("descriptor"));
        if (descriptorProblem is not null)
            return descriptorProblem;

        var decryptor = TypeName.Of((string)secrets[0].Attribute("decryptorType"));
        if (decryptor.Is(OurDecryptor, OurAssembly))
        {
            // Data Protection gives the decryptor the encrypted secret's one child element.
            var encrypted = secrets[0].Elements().ToList();
            if (encrypted.Count != 1)
                return "its encrypted secret does not hold exactly one encrypted key";
            namesSigner = encrypted[0].Attribute("signer") is not null;
            var standing = OaepGcmXml.Check(encrypted[0], certificates.All, signing, now, out var reason);
            if (standing == SignerStanding.Refused)
                return reason;
            onlyToUnprotect = standing == SignerStanding.Deactivated;
            return null;
        }

        if (decryptor.Is(LegacyDecryptor, DataProtectionAssembly))
        {
            var windowOpen = legacyFormatUntil is { } until && now < until;
            if (!windowOpen)
                return "it is in the older EncryptedXml format and DataProtection:LegacyFormatUntil does not admit it";
            onlyToUnprotect = true;
            return null;
        }

        return $"its master key is encrypted by {decryptor.Safe}, which Ariva does not use";
    }

    /// <summary>
    /// The review's finding M1: the descriptor Data Protection writes with Ariva's settings and nothing else, so a genuine encrypted
    /// secret cannot be moved under another deserializer (one that names its own algorithm types) or a weaker algorithm.
    /// </summary>
    private static string DescriptorProblem(XElement outer)
    {
        var deserializer = TypeName.Of((string)outer.Attribute("deserializerType"));
        if (!deserializer.Is(DeserializerType, DataProtectionAssembly))
            return $"its descriptor is read by {deserializer.Safe}, which Ariva does not use";

        var inner = outer.Elements().ToList();
        if (inner.Count != 1 || inner[0].Name != XName.Get("descriptor"))
            return "its descriptor does not hold exactly one unqualified descriptor";

        var parts = inner[0].Elements().ToList();
        var encryption = parts.Where(e => e.Name == XName.Get("encryption")).ToList();
        var validation = parts.Where(e => e.Name == XName.Get("validation")).ToList();
        var secret = parts.Where(e => e.Name.LocalName == "encryptedSecret").ToList();
        if (encryption.Count != 1 || validation.Count > 1 || secret.Count != 1 || parts.Count != 2 + validation.Count)
            return "its descriptor does not hold exactly an encryption, an optional validation and an encrypted secret";

        var algorithm = (string)encryption[0].Attribute("algorithm");
        if (algorithm == "AES_256_GCM" && validation.Count == 0)
            return null;
        if (algorithm == "AES_256_CBC" && validation.Count == 1 && (string)validation[0].Attribute("algorithm") == "HMACSHA256")
            return null;
        return "its payload algorithm is neither AES-256-GCM nor AES-256-CBC with HMAC-SHA256";
    }

    #endregion

    #region Helpers

    private sealed record Admitted(XElement Element, bool Signed, Guid? KeyId);

    /// <summary>One element per key id: the signed row's, else the first (Data Protection refuses a ring with a duplicate id).</summary>
    private static List<XElement> Deduplicated(List<Admitted> admitted)
    {
        var kept = new List<XElement>();
        var seen = new HashSet<Guid>();
        foreach (var entry in admitted.OrderByDescending(a => a.Signed))
        {
            if (entry.KeyId is { } id && !seen.Add(id))
                continue;
            kept.Add(entry.Element);
        }

        // Back in the order the rows were read, which Data Protection does not depend on but logs follow.
        return [.. admitted.Select(a => a.Element).Where(kept.Contains)];
    }

    private static Guid? KeyIdOf(XElement element) =>
        element is not null && element.Name == XName.Get("key") && Guid.TryParseExact((string)element.Attribute("id") ?? string.Empty, "D", out var id) ? id : null;

    /// <summary>The element's name as it may reach a log line (CWE-117): <c>key</c>, <c>revocation</c> or a fixed text.</summary>
    private static string NameOf(XElement element) => element?.Name.LocalName switch
    {
        null => "row",
        "key" => "key",
        "revocation" => "revocation",
        _ => "of another name"
    };

    /// <summary>The key id as it may reach a log line (CWE-117): in its GUID shape, otherwise <c>?</c>.</summary>
    private static string IdOf(XElement element) =>
        Guid.TryParseExact((string)element?.Attribute("id") ?? string.Empty, "D", out var id) ? id.ToString("D") : "?";

    /// <summary>The key with its expiration moved to now at the latest (in memory only): it decrypts, never protects.</summary>
    private static XElement ExpiredCopy(XElement key, DateTimeOffset now)
    {
        var copy = new XElement(key);
        // The exact name Data Protection reads (XmlKeyManager: keyElement.Element("expirationDate")).
        var expiration = copy.Element("expirationDate");
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

    /// <summary>An assembly-qualified type name as stored: the type and the assembly's simple name.</summary>
    private sealed record TypeName(string Type, string Assembly)
    {
        public static TypeName Of(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new TypeName(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : string.Empty);
        }

        public bool Is(string type, string assembly) =>
            string.Equals(Type, type, StringComparison.Ordinal) && string.Equals(Assembly, assembly, StringComparison.Ordinal);

        /// <summary>The type name as it may reach a log line (CWE-117): in a .NET type name's shape, otherwise a fixed text.</summary>
        public string Safe => Type.Length is > 0 and <= 200 && Type.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`')
            ? Type
            : "a type name that is not in a .NET type name's shape";
    }

    #endregion
}
