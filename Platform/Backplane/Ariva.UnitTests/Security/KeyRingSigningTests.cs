using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-211 (NIST SP 800-57 Part 1 section 5.2, ASVS V11.1.1, V11.2.3, CWE-320, CWE-327): the Data Protection key ring is signed
/// with a key of its own, an ECDSA P-256 certificate apart from the wrapping certificates. A key signed by a retired signing key is
/// refused while a key wrapped under a previous certificate still unwraps; a previous certificate's own key can no longer make a
/// key the hosts admit; a previous signer only verifies, and the keys it signed only unprotect, so the hosts make a new default key
/// signed by the current signer; an installation's keys from before ARV-211 (signed with the wrapping certificate's key) keep
/// reading inside DataProtection:Signing:CertificateSignedUntil, only to unprotect, and are refused after it.
/// </summary>
public sealed class KeyRingSigningTests : IDisposable
{
    private const string Purpose = "Ariva.UnitTests.KeyRingSigning";
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var directory in _folders.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }

    #region Signers and the element format

    [Fact]
    public void Decrypt_Should_RefuseAKey_When_ARetiredSigningKeySignedIt()
    {
        using var wrapping = WrappingCertificate();
        using var retired = SigningCertificate();
        using var current = SigningCertificate();
        var element = OaepGcmXml.Encrypt(new XElement("masterKey", "material"), wrapping, retired);
        var now = DateTimeOffset.UtcNow;

        var signing = KeyRingSigning.From(current);
        var standing = OaepGcmXml.Check(element, [wrapping], signing, now, out var reason);

        standing.Should().Be(SignerStanding.Refused, "the signing key that signed it is configured neither as current nor as previous");
        reason.Should().Contain("retired signing key");
        FluentActions.Invoking(() => OaepGcmXml.Decrypt(element, [wrapping], signing, now)).Should().Throw<CryptographicException>()
            .WithMessage("*retired signing key*", "nothing is unwrapped before the signer is accepted");
        OaepGcmXml.Check(element, [wrapping], KeyRingSigning.From(current, [PublicOnly(retired)]), now, out _).Should().Be(SignerStanding.Deactivated,
            "while it is a previous signer it verifies, and its keys only unprotect");
    }

    [Fact]
    public void Decrypt_Should_UnwrapAKey_When_APreviousCertificateWrapsItAndTheCurrentSignerSignedIt()
    {
        using var previous = WrappingCertificate();
        using var current = WrappingCertificate();
        using var signer = SigningCertificate();
        var original = new XElement("masterKey", new XElement("value", "material"));
        var element = OaepGcmXml.Encrypt(original, previous, signer);
        var signing = KeyRingSigning.From(signer);

        var decrypted = OaepGcmXml.Decrypt(element, DataProtectionCertificates.From(current, previous).All, signing, DateTimeOffset.UtcNow);

        XNode.DeepEquals(decrypted, original).Should().BeTrue("a wrapping certificate is kept for recovery and still unwraps the keys it wrapped");
        OaepGcmXml.Check(element, DataProtectionCertificates.From(current, previous).All, signing, DateTimeOffset.UtcNow, out _).Should().Be(SignerStanding.Current,
            "who wraps a key does not decide whether it is admitted: who signed it does");
    }

    [Fact]
    public void Check_Should_RefuseAKey_When_APreviousCertificatesOwnKeySignedIt()
    {
        // The deviation ARV-211 closes: whoever holds a previous certificate (kept for recovery) could sign a key the hosts admitted.
        using var previous = WrappingCertificate();
        using var current = WrappingCertificate();
        using var signer = SigningCertificate();
        var certificates = DataProtectionCertificates.From(current, previous).All;
        var now = DateTimeOffset.UtcNow;
        var oldFormat = OaepGcmXml.EncryptSignedWithCertificate(new XElement("masterKey", "planted"), previous);
        var claimed = OaepGcmXml.EncryptSignedWithCertificate(new XElement("masterKey", "planted"), previous);
        claimed.SetAttributeValue("signer", KeyRingSigning.SignerOf(signer));
        claimed.SetAttributeValue("signatureAlgorithm", KeyRingSigning.Algorithm);

        OaepGcmXml.Check(oldFormat, certificates, KeyRingSigning.From(signer), now, out var reason).Should().Be(SignerStanding.Refused,
            "outside the upgrade window a key signed with a wrapping certificate's key is refused");
        reason.Should().Contain("CertificateSignedUntil");
        OaepGcmXml.Check(claimed, certificates, KeyRingSigning.From(signer), now, out reason).Should().Be(SignerStanding.Refused,
            "naming the signing certificate does not make the certificate's own signature its signature");
        reason.Should().Contain("does not verify");
        OaepGcmXml.Check(oldFormat, certificates, KeyRingSigning.From(signer, certificateSignedUntil: now.AddDays(1)), now, out _).Should().Be(SignerStanding.Deactivated,
            "inside the window it is read, and only to unprotect");
    }

    [Fact]
    public void Check_Should_RefuseAKey_When_ItsSignerOrSignatureWasChanged()
    {
        using var wrapping = WrappingCertificate();
        using var signer = SigningCertificate();
        using var other = SigningCertificate();
        var signing = KeyRingSigning.From(signer, [PublicOnly(other)]);
        var now = DateTimeOffset.UtcNow;
        var moved = OaepGcmXml.Encrypt(new XElement("masterKey", "material"), wrapping, signer);
        moved.SetAttributeValue("signer", KeyRingSigning.SignerOf(other));
        var relabelled = OaepGcmXml.Encrypt(new XElement("masterKey", "material"), wrapping, signer);
        relabelled.SetAttributeValue("signatureAlgorithm", "RS256");
        var unsigned = OaepGcmXml.Encrypt(new XElement("masterKey", "material"), wrapping, signer);
        unsigned.Element(OaepGcmXml.Namespace + "signature")?.Remove();

        OaepGcmXml.Check(moved, [wrapping], signing, now, out _).Should().Be(SignerStanding.Refused, "the signature binds the signer it names");
        OaepGcmXml.Check(relabelled, [wrapping], signing, now, out _).Should().Be(SignerStanding.Refused, "only ES256 is read");
        OaepGcmXml.Check(unsigned, [wrapping], signing, now, out _).Should().Be(SignerStanding.Refused, "a missing signature is refused, never thrown past the filter");
        var written = OaepGcmXml.Encrypt(new XElement("masterKey", "material"), wrapping, signer);
        ((string)written.Attribute("signer")).Should().Be(KeyRingSigning.SignerOf(signer));
        ((string)written.Attribute("signatureAlgorithm")).Should().Be(KeyRingSigning.Algorithm);
        ((string)written.Attribute("algorithm")).Should().Be(OaepGcmXml.Algorithm, "the wrapping label, which ariva-db.sh keyring reads, is unchanged");
    }

    #endregion

    #region The key ring as the hosts read it

    [Fact]
    public void Rotation_Should_MakeANewKeySignedByTheNewSigner_When_ThePreviousSignerOnlyVerifies()
    {
        using var wrapping = WrappingCertificate();
        using var first = SigningCertificate();
        using var second = SigningCertificate();
        var repository = new MemoryKeyRing();
        string before;
        using (var host = Host(repository, wrapping, KeyRingSigning.From(first)))
            before = Protector(host).Protect("totp-seed");

        using var rotating = Host(repository, wrapping, KeyRingSigning.From(second, [PublicOnly(first)]));
        var after = Protector(rotating).Protect("outbound-secret");
        var read = Unprotected(rotating, before);

        KeyId(after).Should().NotBe(KeyId(before), "the key the previous signer signed is only an expired copy, so a new default key is made");
        read.Plaintext.Should().Be("totp-seed", "the previous signer still verifies what it signed");
        read.RequiresMigration.Should().BeTrue("so the re-protection job moves it to the new key");
        Signer(repository, KeyId(after)).Should().Be(KeyRingSigning.SignerOf(second), "the new key is signed by the current signer");

        using var retired = Host(repository, wrapping, KeyRingSigning.From(second));
        FluentActions.Invoking(() => Protector(retired).Unprotect(before)).Should().Throw<CryptographicException>(
            "once the previous signer is removed, a key it signed is refused");
        (Protector(retired).Unprotect(after) == "outbound-secret").Should().BeTrue("the new key needs only the current signer");
    }

    [Fact]
    public void Upgrade_Should_KeepReadingTheInstallationsKeysOnlyInsideTheWindow_When_TheyWereSignedWithTheSharedKey()
    {
        using var wrapping = WrappingCertificate();
        using var signer = SigningCertificate();
        var repository = new MemoryKeyRing();
        string before;
        using (var installed = BeforeArv211Host(repository, wrapping))
            before = Protector(installed).Protect("totp-seed");
        repository.GetAllElements().Single().Descendants(OaepGcmXml.Namespace + "encryptedKey").Single().Attribute("signer").Should()
            .BeNull("the fixture is a key written before ARV-211, signed with the wrapping certificate's key");

        var now = DateTimeOffset.UtcNow;
        using var upgraded = Host(repository, wrapping, KeyRingSigning.From(signer, certificateSignedUntil: now.AddDays(7)));
        var read = Unprotected(upgraded, before);
        var after = Protector(upgraded).Protect("outbound-secret");

        read.Plaintext.Should().Be("totp-seed", "a running installation keeps reading its keys while the window is open");
        read.RequiresMigration.Should().BeTrue("the old key only unprotects, so the re-protection job moves the value");
        KeyId(after).Should().NotBe(KeyId(before), "nothing new is protected with a key signed by the shared key");
        Signer(repository, KeyId(after)).Should().Be(KeyRingSigning.SignerOf(signer));

        using var closed = Host(repository, wrapping, KeyRingSigning.From(signer));
        FluentActions.Invoking(() => Protector(closed).Unprotect(before)).Should().Throw<CryptographicException>(
            "after the window a key signed with a wrapping certificate's own key is refused");
        (Protector(closed).Unprotect(after) == "outbound-secret").Should().BeTrue();
    }

    [Fact]
    public void KeyRing_Should_NeverProtectWithAKeyAPreviousCertificateSigned_When_OneIsPlantedInsideTheWindow()
    {
        // Inside the upgrade window a writer holding a previous certificate's key plants a key in the old format, activated after the
        // genuine one: it is admitted only as an expired copy, so it never becomes the default key.
        using var previous = WrappingCertificate();
        using var current = WrappingCertificate();
        using var signer = SigningCertificate();
        var repository = new MemoryKeyRing();
        var signing = KeyRingSigning.From(signer, certificateSignedUntil: DateTimeOffset.UtcNow.AddDays(7));
        using (var host = Host(repository, current, signing, previous))
            Protector(host).Protect("creates the genuine key");
        using (var planter = BeforeArv211Host(repository, previous))
        {
            Thread.Sleep(20);
            planter.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        }

        using var reader = Host(repository, current, signing, previous);
        var payload = Protector(reader).Protect("totp-seed");

        Signer(repository, KeyId(payload)).Should().Be(KeyRingSigning.SignerOf(signer), "the default key is the genuine one, never the planted one");
        var filter = new TrustedKeyRingRepository(repository, DataProtectionCertificates.From(current, previous), signing, null, TimeProvider.System, NullLogger.Instance);
        repository.GetAllRows().Count(filter.AdmitsOnlyToUnprotect).Should().Be(1, "the planted key is admitted only to unprotect");
    }

    [Fact]
    public void Verdict_Should_SkipAKey_When_ARetiredSignerSignedIt()
    {
        using var wrapping = WrappingCertificate();
        using var retired = SigningCertificate();
        using var current = SigningCertificate();
        var repository = new MemoryKeyRing();
        using (var host = Host(repository, wrapping, KeyRingSigning.From(retired)))
            Protector(host).Protect("anything");
        var key = repository.GetAllElements().Single();

        var filter = new TrustedKeyRingRepository(repository, DataProtectionCertificates.From(wrapping), KeyRingSigning.From(current), null, TimeProvider.System,
            NullLogger.Instance);

        filter.Verdict(key).Should().Contain("retired signing key");
        filter.GetAllElements().Should().BeEmpty("the filter refuses it before Data Protection sees it");
    }

    #endregion

    #region Row signatures and descriptors (the review's finding M1)

    [Fact]
    public void Verdict_Should_RefuseAGenuineEncryptedSecret_When_ItIsMovedUnderAnotherDescriptorOrAlgorithm()
    {
        using var wrapping = WrappingCertificate();
        using var signer = SigningCertificate();
        var repository = new MemoryKeyRing();
        var signing = KeyRingSigning.From(signer);
        using (var host = Host(repository, wrapping, signing))
            Protector(host).Protect("anything");
        var genuine = repository.GetAllElements().Single();
        var filter = new TrustedKeyRingRepository(repository, DataProtectionCertificates.From(wrapping), signing, null, TimeProvider.System, NullLogger.Instance);
        const string ManagedDeserializer =
            "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.ManagedAuthenticatedEncryptorDescriptorDeserializer, Microsoft.AspNetCore.DataProtection";

        XElement Variant(Action<XElement> change)
        {
            var copy = new XElement(genuine);
            change(copy);
            return copy;
        }

        XElement Inner(XElement key) => key.Element("descriptor")?.Element("descriptor");
        var variants = new Dictionary<string, XElement>
        {
            ["another deserializer"] = Variant(k => k.Element("descriptor")?.SetAttributeValue("deserializerType", ManagedDeserializer)),
            ["the deserializer from another assembly"] = Variant(k => k.Element("descriptor")?.SetAttributeValue("deserializerType",
                "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer, Evil")),
            ["a weaker algorithm"] = Variant(k => Inner(k)?.Element("encryption")?.SetAttributeValue("algorithm", "AES_128_GCM")),
            ["CBC without a MAC"] = Variant(k => Inner(k)?.Element("encryption")?.SetAttributeValue("algorithm", "AES_256_CBC")),
            ["CBC with another MAC"] = Variant(k =>
            {
                Inner(k)?.Element("encryption")?.SetAttributeValue("algorithm", "AES_256_CBC");
                Inner(k)?.Add(new XElement("validation", new XAttribute("algorithm", "HMACSHA512")));
            }),
            ["GCM with a validation"] = Variant(k => Inner(k)?.Add(new XElement("validation", new XAttribute("algorithm", "HMACSHA256")))),
            ["another element in the descriptor"] = Variant(k => Inner(k)?.Add(new XElement("other"))),
            ["a second descriptor"] = Variant(k => k.Element("descriptor")?.Add(new XElement("descriptor"))),
            ["another element in the key"] = Variant(k => k.Add(new XElement("note"))),
            ["another version"] = Variant(k => k.SetAttributeValue("version", "2")),
            ["an id that is not a key id"] = Variant(k => k.SetAttributeValue("id", "not-a-key-id"))
        };

        filter.Verdict(genuine).Should().BeNull("the key a host wrote is admitted");
        foreach (var (name, variant) in variants)
            filter.Verdict(variant).Should().NotBeNull(name);
        var beforeArv080 = Variant(k =>
        {
            Inner(k)?.Element("encryption")?.SetAttributeValue("algorithm", "AES_256_CBC");
            Inner(k)?.Add(new XElement("validation", new XAttribute("algorithm", "HMACSHA256")));
        });
        filter.Verdict(beforeArv080).Should().BeNull("a key from before ARV-080 (AES-256-CBC with HMAC-SHA256) keeps reading until it expires");
    }

    [Fact]
    public void Rows_Should_BeRefused_When_TheirIdDatesNameOrDescriptorWereChanged()
    {
        using var wrapping = WrappingCertificate();
        using var signer = SigningCertificate();
        var repository = new MemoryKeyRing();
        var signing = KeyRingSigning.From(signer, certificateSignedUntil: DateTimeOffset.UtcNow.AddDays(7));
        string payload;
        using (var host = Host(repository, wrapping, signing))
            payload = Protector(host).Protect("totp-seed");
        var row = repository.GetAllRows().Single();
        row.Signature.Should().MatchRegex("^ES256:[0-9A-F]{64}:[A-Za-z0-9+/]{86}==$", "the shape script 0073 holds");
        var filter = new TrustedKeyRingRepository(repository, DataProtectionCertificates.From(wrapping), signing, null, TimeProvider.System, NullLogger.Instance);
        var id = KeyId(payload).ToString();
        var other = Guid.NewGuid().ToString();
        // A writer with INSERT alone adds each under a name of its own (the friendly name is the table's key).
        string Fresh() => $"key-{Guid.NewGuid()}";

        var copies = new Dictionary<string, KeyRingRow>
        {
            ["re-identified"] = new($"key-{other}", row.Xml.Replace(id, other, StringComparison.Ordinal), row.Signature),
            ["re-dated"] = new(Fresh(), Regex.Replace(row.Xml, "<expirationDate>[^<]*</expirationDate>", "<expirationDate>2099-01-01T00:00:00Z</expirationDate>"), row.Signature),
            ["renamed"] = row with { FriendlyName = Fresh() },
            ["another algorithm"] = new(Fresh(), row.Xml.Replace("AES_256_GCM", "AES_256_CBC", StringComparison.Ordinal), row.Signature),
            ["another row's signature"] = row with { FriendlyName = Fresh(), Signature = signing.SignRow("key-other", "<key/>") },
            ["a retired signer"] = row with { FriendlyName = Fresh(), Signature = KeyRingSigning.From(SigningCertificate()).SignRow(row.FriendlyName, row.Xml) },
            ["malformed"] = row with { FriendlyName = Fresh(), Signature = "ES256:not-a-signer:AAAA" }
        };

        filter.Verdict(row).Should().BeNull("the row the host wrote is admitted");
        foreach (var (name, copy) in copies)
            filter.Verdict(copy).Should().NotBeNull(name).And.Contain("row", name);
        foreach (var copy in copies.Values)
            repository.Add(copy);
        repository.GetAllRows().Should().HaveCount(copies.Count + 1);
        var admitted = filter.GetAllElements();
        admitted.Should().ContainSingle("only the row the host wrote enters the ring");
        ((string)admitted.Single().Attribute("id")).Should().Be(id);
        using var reader = Host(repository, wrapping, signing);
        (Protector(reader).Unprotect(payload) == "totp-seed").Should().BeTrue();
    }

    [Fact]
    public void Rows_Should_RefuseAnUnsignedRow_When_ItCopiesAnElementWrittenSinceArv211OrTheWindowIsClosed()
    {
        using var wrapping = WrappingCertificate();
        using var signer = SigningCertificate();
        var repository = new MemoryKeyRing();
        var closed = KeyRingSigning.From(signer);
        var open = KeyRingSigning.From(signer, certificateSignedUntil: DateTimeOffset.UtcNow.AddDays(7));
        string payload;
        using (var host = Host(repository, wrapping, closed))
            payload = Protector(host).Protect("totp-seed");
        var genuine = repository.GetAllRows().Single();
        var other = Guid.NewGuid().ToString();
        // A writer with INSERT alone: the genuine element under another id without a row signature, and a revocation of the key.
        var copy = new KeyRingRow($"key-{other}", genuine.Xml.Replace(KeyId(payload).ToString(), other, StringComparison.Ordinal), null);
        var revocation = new KeyRingRow("revocation-planted", string.Create(CultureInfo.InvariantCulture,
            $"<revocation version=\"1\"><revocationDate>{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}</revocationDate><key id=\"{KeyId(payload)}\" /><reason>planted</reason></revocation>"), null);
        repository.Add(copy);
        repository.Add(revocation);
        TrustedKeyRingRepository Filter(KeyRingSigning signing) =>
            new(repository, DataProtectionCertificates.From(wrapping), signing, null, TimeProvider.System, NullLogger.Instance);

        Filter(closed).Verdict(copy).Should().Contain("no signature").And.Contain("CertificateSignedUntil");
        Filter(open).Verdict(copy).Should().Contain("names a key ring signer", "no element written before ARV-211 named a signer");
        Filter(closed).Verdict(revocation).Should().Contain("no signature", "outside the window every revocation is one a host signed");
        Filter(open).Verdict(revocation).Should().BeNull("inside the window a revocation from before ARV-211 is still read (an availability risk only)");
        using (var reader = Host(repository, wrapping, closed))
            (Protector(reader).Unprotect(payload) == "totp-seed").Should().BeTrue("the planted revocation does not reach the ring");

        // A revocation a host writes is signed, and it is admitted.
        using (var revoking = Host(repository, wrapping, closed))
            revoking.GetRequiredService<IKeyManager>().RevokeKey(KeyId(payload), "ARV-211 test");
        var signed = repository.GetAllRows().Single(r => r.FriendlyName != revocation.FriendlyName && r.Xml.StartsWith("<revocation", StringComparison.Ordinal));
        Filter(closed).Verdict(signed).Should().BeNull();
        using var revoked = Host(repository, wrapping, closed);
        FluentActions.Invoking(() => Protector(revoked).Unprotect(payload)).Should().Throw<CryptographicException>("the signed revocation is honoured");
    }

    [Fact]
    public void KeyRing_Should_KeepOneRowPerKeyId_When_ARowIsCopiedUnderAnotherName()
    {
        // Data Protection refuses a ring holding one key id twice; inside the upgrade window a writer could add an unsigned copy of a
        // key from before ARV-211 under another friendly name (with any dates) to take the ring down.
        using var wrapping = WrappingCertificate();
        using var signer = SigningCertificate();
        var repository = new MemoryKeyRing();
        string before;
        using (var installed = BeforeArv211Host(repository, wrapping))
            before = Protector(installed).Protect("totp-seed");
        var original = repository.GetAllRows().Single();
        repository.Add(new KeyRingRow(original.FriendlyName + "-copy",
            Regex.Replace(original.Xml, "<expirationDate>[^<]*</expirationDate>", "<expirationDate>2099-01-01T00:00:00Z</expirationDate>"), null));
        var signing = KeyRingSigning.From(signer, certificateSignedUntil: DateTimeOffset.UtcNow.AddDays(7));
        var filter = new TrustedKeyRingRepository(repository, DataProtectionCertificates.From(wrapping), signing, null, TimeProvider.System, NullLogger.Instance);

        filter.GetAllElements().Where(e => (string)e.Attribute("id") == KeyId(before).ToString()).Should().ContainSingle("the first row of a key id is kept");
        using var upgraded = Host(repository, wrapping, signing);
        (Protector(upgraded).Unprotect(before) == "totp-seed").Should().BeTrue("the ring still loads");
    }

    #endregion

    #region Configuration

    [Fact]
    public void Load_Should_RefuseTheHost_When_NoSigningCertificateIsConfigured()
    {
        using var wrapping = WrappingCertificate();

        var load = () => KeyRingSigning.Load(new ConfigurationBuilder().Build(), DataProtectionCertificates.From(wrapping));

        load.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:Signing:CertificatePath*");
    }

    [Fact]
    public void Load_Should_CreateTheDevelopmentSigningCertificateOnceOwnerOnly_When_DevelopmentCertificateIsOn()
    {
        var directory = Folder();
        var configuration = Configuration(new()
        {
            ["DataProtection:UseDevelopmentCertificate"] = "true",
            ["DataProtection:DevelopmentCertificateDirectory"] = directory
        });
        var wrapping = DataProtectionCertificates.Load(configuration);

        var first = KeyRingSigning.Load(configuration, wrapping);
        var second = KeyRingSigning.Load(configuration, wrapping);

        second.Signer.Should().Be(first.Signer, "keys signed yesterday still verify today");
        first.Current.HasPrivateKey.Should().BeTrue();
        first.Current.GetECDsaPublicKey().Should().NotBeNull();
        first.Signer.Should().NotBe(wrapping.Current.GetCertHashString(HashAlgorithmName.SHA256), "a certificate of its own, beside the wrapping one");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(directory, "dataprotection-signing-dev.pem")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.GetFiles(directory, "*.tmp").Should().BeEmpty("the host's own file was moved into place");
    }

    [Fact]
    public async Task Load_Should_GiveEveryHostTheSameDevelopmentSigningCertificate_When_TheyStartTogether()
    {
        // The hosts of a developer machine start at once: each that finds no file makes its own and only one is moved into place.
        var directory = Folder();
        var configuration = Configuration(new()
        {
            ["DataProtection:UseDevelopmentCertificate"] = "true",
            ["DataProtection:DevelopmentCertificateDirectory"] = directory
        });
        var wrapping = DataProtectionCertificates.Load(configuration);

        var signers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => KeyRingSigning.Load(configuration, wrapping).Signer, TestContext.Current.CancellationToken)));

        signers.Distinct(StringComparer.Ordinal).Should().ContainSingle("every host signs with the one certificate that won");
        Directory.GetFiles(directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Load_Should_ReadThePreviousSignersAndTheWindow_When_ARotationIsConfigured()
    {
        var directory = Folder();
        using var current = SigningCertificate();
        using var previous = SigningCertificate();
        var currentPath = Write(directory, "current.crt", current.ExportCertificatePem());
        var keyPath = Write(directory, "current.key", current.GetECDsaPrivateKey()?.ExportPkcs8PrivateKeyPem());
        var previousPath = Write(directory, "previous.crt", previous.ExportCertificatePem());
        using var wrapping = WrappingCertificate();

        var loaded = KeyRingSigning.Load(Configuration(new()
        {
            ["DataProtection:Signing:CertificatePath"] = currentPath,
            ["DataProtection:Signing:KeyPath"] = keyPath,
            ["DataProtection:Signing:Previous:0:CertificatePath"] = previousPath,
            ["DataProtection:Signing:CertificateSignedUntil"] = "2027-01-10"
        }), DataProtectionCertificates.From(wrapping));

        loaded.Signer.Should().Be(KeyRingSigning.SignerOf(current));
        loaded.Previous.Should().ContainSingle().Which.HasPrivateKey.Should().BeFalse("a previous signer is its certificate alone");
        loaded.Standing(KeyRingSigning.SignerOf(previous), out _).Should().Be(SignerStanding.Deactivated);
        loaded.CertificateSignedUntil.Should().Be(new DateTimeOffset(2027, 1, 10, 0, 0, 0, TimeSpan.Zero));
        FluentActions.Invoking(() => KeyRingSigning.Load(Configuration(new()
        {
            ["DataProtection:Signing:CertificatePath"] = currentPath,
            ["DataProtection:Signing:KeyPath"] = keyPath,
            ["DataProtection:Signing:Previous:0:CertificatePath"] = previousPath,
            ["DataProtection:Signing:Previous:0:KeyPath"] = keyPath
        }), DataProtectionCertificates.From(wrapping))).Should().Throw<InvalidOperationException>().WithMessage("*only verifies*",
            "a previous signer's private key is never mounted");
        FluentActions.Invoking(() => KeyRingSigning.From(current, [previous])).Should().Throw<InvalidOperationException>().WithMessage("*only verifies*");
    }

    [Fact]
    public void CertificateSignedUntil_Should_BeRefused_When_ItIsMoreThan90DaysAhead()
    {
        // The review's finding L1: the upgrade window is a short migration step, never a standing setting.
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        IConfiguration Window(string value) => Configuration(new() { ["DataProtection:Signing:CertificateSignedUntil"] = value });

        KeyRingSigning.CertificateSignedUntilOf(Window("2027-02-08"), now).Should().Be(new DateTimeOffset(2027, 2, 8, 0, 0, 0, TimeSpan.Zero), "90 days ahead");
        KeyRingSigning.CertificateSignedUntilOf(Window("2026-01-01"), now).Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "a window that has passed is closed");
        KeyRingSigning.CertificateSignedUntilOf(Configuration(new()), now).Should().BeNull();
        FluentActions.Invoking(() => KeyRingSigning.CertificateSignedUntilOf(Window("2027-02-09"), now)).Should().Throw<InvalidOperationException>()
            .WithMessage("*more than 90 days ahead*wiki 04 section 6.2*");
        FluentActions.Invoking(() => KeyRingSigning.CertificateSignedUntilOf(Window("someday"), now)).Should().Throw<InvalidOperationException>().WithMessage("*not a date*");

        var vmLocal = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Api.Common", "appsettings.base.vm-local.json"))
            .Build();
        var developer = KeyRingSigning.CertificateSignedUntilOf(vmLocal, DateTimeOffset.UtcNow);
        developer.Should().NotBeNull("a developer machine reads its keys from before ARV-211 for a while");
        developer.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddDays(KeyRingSigning.MaxCertificateSignedDays));
    }

    [Fact]
    public void Load_Should_RefuseAPreviousSignersFile_When_ItHoldsAPrivateKey()
    {
        // The review's finding L2: the previous signer's tls.crt alone; a file that holds its key too (a whole PEM bundle) is refused.
        var directory = Folder();
        using var current = SigningCertificate();
        using var previous = SigningCertificate();
        var currentPath = Write(directory, "current.crt", current.ExportCertificatePem());
        var keyPath = Write(directory, "current.key", current.GetECDsaPrivateKey()?.ExportPkcs8PrivateKeyPem());
        var bundle = Write(directory, "previous.pem", previous.ExportCertificatePem() + "\n" + previous.GetECDsaPrivateKey()?.ExportECPrivateKeyPem());
        using var wrapping = WrappingCertificate();

        var load = () => KeyRingSigning.Load(Configuration(new()
        {
            ["DataProtection:Signing:CertificatePath"] = currentPath,
            ["DataProtection:Signing:KeyPath"] = keyPath,
            ["DataProtection:Signing:Previous:0:CertificatePath"] = bundle
        }), DataProtectionCertificates.From(wrapping));

        load.Should().Throw<InvalidOperationException>().WithMessage("*holds a private key*");
        KeyRingSigning.HoldsPrivateKey(previous.ExportCertificatePem()).Should().BeFalse();
        KeyRingSigning.HoldsPrivateKey(previous.GetECDsaPrivateKey()?.ExportPkcs8PrivateKeyPem()).Should().BeTrue();
        KeyRingSigning.HoldsPrivateKey(previous.GetECDsaPrivateKey()?.ExportEncryptedPkcs8PrivateKeyPem("pw".AsSpan(),
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000))).Should().BeTrue();
    }

    [Fact]
    public void Load_Should_LeaveNoPrivateKeyFileBehind_When_TheDevelopmentCertificateCannotBeMovedIntoPlace()
    {
        // The review's finding L6: a move that fails for another reason than a file already in place removes the host's own file.
        var directory = Folder();
        Directory.CreateDirectory(Path.Combine(directory, "dataprotection-signing-dev.pem"));
        var configuration = Configuration(new()
        {
            ["DataProtection:UseDevelopmentCertificate"] = "true",
            ["DataProtection:DevelopmentCertificateDirectory"] = directory
        });
        var wrapping = DataProtectionCertificates.Load(configuration);

        FluentActions.Invoking(() => KeyRingSigning.Load(configuration, wrapping)).Should().Throw<IOException>();
        Directory.GetFiles(directory, "*.tmp").Should().BeEmpty("the file that holds a private key does not stay behind");
    }

    [Fact]
    public void AddArivaDataProtection_Should_CheckTheUpgradeAtStart_When_TheHostVerifiesItsSchema()
    {
        // The review's finding H1: outside vm-local (Database:VerifySchemaOnStartup) every host runs the upgrade check at start.
        var directory = Folder();
        IServiceCollection Registered(bool verify)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddArivaDataProtection(Configuration(new()
            {
                ["DataProtection:UseDevelopmentCertificate"] = "true",
                ["DataProtection:DevelopmentCertificateDirectory"] = directory,
                ["Database:VerifySchemaOnStartup"] = verify ? "true" : "false"
            }));
            return services;
        }

        Registered(verify: true).Should().Contain(d => d.ImplementationType == typeof(KeyRingUpgradeGate));
        Registered(verify: false).Should().NotContain(d => d.ImplementationType == typeof(KeyRingUpgradeGate), "vm-local migrates its own database");
    }

    [Fact]
    public void UpgradeCheck_Should_RefuseOnlyTheNeverUpgradedCase_When_NoWindowIsOpen()
    {
        // The review's findings H1 and N1: an installation whose values all need keys from before ARV-211 (an upgrade or a restore
        // without the window) is refused; one already upgraded, with values under signed keys, only warns about its leftovers.
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var never = new KeyRingUpgradeFinding(UserSeeds: 3, ClientSeeds: 1, OutboundSecrets: 0);
        var leftovers = never with { OnSignedKeys = 40 };

        KeyRingUpgradeCheck.Problem(new KeyRingUpgradeFinding(0, 0, 0), null, now).Should().BeNull("no stored value needs a key from before ARV-211");
        KeyRingUpgradeCheck.Problem(never, now.AddDays(7), now).Should().BeNull("the window is open");
        KeyRingUpgradeCheck.Problem(never, null, now).Should().Contain("is not set").And.Contain("3 user TOTP seed(s), 1 integration client seed(s) and 0 outbound")
            .And.Contain("No stored value is under a key a configured key ring signer signed").And.Contain("wiki 04 section 6.2")
            .And.NotContain("dataProtectionSigningPreviousSecretName", "no retired signer's key is needed");
        KeyRingUpgradeCheck.Problem(never, now.AddDays(-1), now).Should().Contain("closed on 2026-11-09");
        KeyRingUpgradeCheck.Leftovers(never, null, now).Should().BeNull("the never-upgraded case is a refusal, not a warning");

        KeyRingUpgradeCheck.Problem(leftovers, null, now).Should().BeNull("an upgraded installation starts whatever its leftovers");
        KeyRingUpgradeCheck.Leftovers(leftovers, null, now).Should().Contain("40 stored value(s) are under signed keys").And.Contain("reopen the window")
            .And.Contain("wiki 10 section 4.25");
        KeyRingUpgradeCheck.Leftovers(leftovers, now.AddDays(7), now).Should().BeNull("while the window is open the hosts warn about the window instead");

        // A signing rotation whose previous signer was removed, or never configured, before any value moved (the second review's note):
        // the values are under keys a signer no host holds signed. The window does not admit those keys, so it neither excuses them
        // nor is it the fix: the message names the previous signer.
        var rotation = new KeyRingUpgradeFinding(0, 0, 0, OnSignedKeys: 0, RetiredSigner: 12);
        KeyRingUpgradeCheck.Problem(rotation, now.AddDays(7), now).Should().Contain("12 stored value(s) need keys signed by a key ring signing certificate no host has configured")
            .And.Contain("dataProtectionSigningPreviousSecretName").And.Contain("The window does not admit these keys").And.NotContain("CertificateSignedUntil is not set");
        KeyRingUpgradeCheck.Problem(rotation, null, now).Should().NotBeNull();
        KeyRingUpgradeCheck.Problem(never with { RetiredSigner = 2 }, null, now).Should().Contain("is not set").And.Contain("dataProtectionSigningPreviousSecretName",
            "each kind of key gets its own fix");
        var rotatedLeftovers = rotation with { OnSignedKeys = 5 };
        KeyRingUpgradeCheck.Problem(rotatedLeftovers, null, now).Should().BeNull("once a value is under a configured signer's key, the hosts start");
        KeyRingUpgradeCheck.Leftovers(rotatedLeftovers, now.AddDays(7), now).Should().Contain("configure the previous signer again")
            .And.Contain("dataProtectionSigningPreviousSecretName", "an open window does not excuse a retired signer's keys");
    }

    [Fact]
    public void Classify_Should_CountOnlyKeysAConfiguredSignerSigned_When_RowsArePlantedRetiredOrUnsigned()
    {
        // The hosts verify each row (the review's finding N1): a row signed by the current or a previous signer puts its values on a
        // signed key; an unsigned row, a forged signature or a retired signer's does not; a value whose key has no row is not counted.
        using var current = SigningCertificate();
        using var previous = SigningCertificate();
        var signing = KeyRingSigning.From(current, [PublicOnly(previous)]);
        var retired = KeyRingSigning.From(SigningCertificate());
        var byPrevious = KeyRingSigning.From(previous);
        Guid signedKey = Guid.NewGuid(), previousKey = Guid.NewGuid(), unsignedKey = Guid.NewGuid(), forgedKey = Guid.NewGuid(), retiredKey = Guid.NewGuid();
        KeyRingRow Row(Guid id, KeyRingSigning by)
        {
            var xml = $"<key id=\"{id}\" version=\"1\"><creationDate>2026-11-10T00:00:00Z</creationDate></key>";
            return new KeyRingRow($"key-{id}", xml, by?.SignRow($"key-{id}", xml));
        }

        KeyRingRow[] rows =
        [
            Row(signedKey, signing), Row(previousKey, byPrevious), Row(unsignedKey, null), Row(retiredKey, retired),
            Row(forgedKey, null) with { Signature = "ES256:" + KeyRingSigning.SignerOf(current) + ":" + Convert.ToBase64String(new byte[64]) }
        ];
        (string, Guid, long)[] named =
        [
            ("user", signedKey, 2), ("client", previousKey, 1), ("user", unsignedKey, 3), ("client", forgedKey, 1), ("outbound", retiredKey, 1),
            ("user", Guid.NewGuid(), 5)
        ];

        var finding = KeyRingUpgradeCheck.Classify(named, rows, signing);

        finding.OnSignedKeys.Should().Be(3, "the current signer's and the previous signer's rows verify");
        finding.UserSeeds.Should().Be(3, "the unsigned key's; the value whose key has no row is not counted");
        finding.ClientSeeds.Should().Be(1, "a forged signature that names the current signer verifies under no signer: counted with the unsigned");
        (finding.OutboundSecrets == 0).Should().BeTrue("no outbound value names a key here (a count, held as a boolean for ARV-249)");
        finding.RetiredSigner.Should().Be(1, "a retired signer is not configured: counted apart, since only it admits its keys");
        finding.Upgraded.Should().BeTrue();

        // The rotation case: every value under the previous signer's keys, which the host no longer holds. Configured again, they verify.
        (string, Guid, long)[] rotated = [("user", previousKey, 4), ("outbound", previousKey, 1)];
        var removed = KeyRingUpgradeCheck.Classify(rotated, rows, KeyRingSigning.From(current));
        removed.RetiredSigner.Should().Be(5);
        removed.Unsigned.Should().Be(0, "a retired signer's key is not one from before ARV-211");
        removed.Upgraded.Should().BeFalse();
        KeyRingUpgradeCheck.Problem(removed, DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow).Should().Contain("dataProtectionSigningPreviousSecretName");
        var configured = KeyRingUpgradeCheck.Classify(rotated, rows, signing);
        configured.OnSignedKeys.Should().Be(5);
        configured.RetiredSigner.Should().Be(0);
    }

    [Theory]
    [InlineData("rsa")]
    [InlineData("p384")]
    [InlineData("no-usage")]
    [InlineData("encipherment")]
    public void From_Should_RefuseASigningCertificate_When_ItIsNotAP256SigningCertificate(string kind)
    {
        using var certificate = kind switch
        {
            "rsa" => WrappingCertificate(),
            "p384" => SigningCertificate(ECCurve.NamedCurves.nistP384),
            "no-usage" => SigningCertificate(usage: null),
            _ => SigningCertificate(usage: X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment)
        };

        FluentActions.Invoking(() => KeyRingSigning.From(certificate)).Should().Throw<InvalidOperationException>()
            .WithMessage(kind is "rsa" or "p384" ? "*ECDSA P-256*" : "*digitalSignature*");
    }

    [Theory]
    [InlineData("appsettings.base.k8s-dev.json")]
    [InlineData("appsettings.base.k8s-demo.json")]
    [InlineData("appsettings.base.k8s-prd.json")]
    public void ClusterSettings_Should_MountTheSigningCertificateAndKeepTheWindowClosed_When_Committed(string file)
    {
        // The window is for an upgrade and is set by the operator with an end date (wiki 04 section 6.2), never shipped open.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Api.Common", "appsettings.base.json"))
            .AddJsonFile(Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Api.Common", file))
            .Build();

        configuration["DataProtection:Signing:CertificatePath"].Should().Be("/app/secrets/dataprotection-signing/tls.crt");
        configuration["DataProtection:Signing:KeyPath"].Should().Be("/app/secrets/dataprotection-signing/tls.key");
        configuration["DataProtection:Signing:CertificateSignedUntil"].Should().BeNull();
        configuration.GetSection("DataProtection:Signing:Previous").GetChildren().Should().BeEmpty("a rotation's previous signer is the operator's, for its overlap only");
    }

    [Fact]
    public void Chart_Should_MountTheSigningSecretInEveryApiHost_When_Rendered()
    {
        // The paths the cluster files name come from this mount; an API pod without it does not start (KeyRingSigning.Load).
        foreach (var host in (string[])["api-main", "api-ingest", "api-stream", "api-cronz", "api-integration"])
        {
            var template = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "Platform", "Cloud", "Ariva.K8s", "Helm", "Charts", "platform", "templates", host + ".yaml"));
            template.Should().Contain("secretName: {{ .Values.dataProtectionSigningSecretName | default \"ariva-dataprotection-signing\" }}", host)
                .And.Contain("mountPath: /app/secrets/dataprotection-signing", host);
        }
    }

    #endregion

    #region Helpers

    /// <summary>An ECDSA P-256 certificate whose key usage is digital signature alone, with its private key (the key ring's signer).</summary>
    internal static X509Certificate2 SigningCertificate(ECCurve? curve = null, X509KeyUsageFlags? usage = X509KeyUsageFlags.DigitalSignature)
    {
        using var ecdsa = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ariva key ring signing (test)", ecdsa, HashAlgorithmName.SHA256);
        if (usage is { } flags)
            request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Round trip through PKCS#12 so the private key is usable on every platform, as a mounted PEM pair would be.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 PublicOnly(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);

    private static X509Certificate2 WrappingCertificate()
    {
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        var request = new CertificateRequest("CN=Ariva DP (signing test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private static ServiceProvider Host(MemoryKeyRing repository, X509Certificate2 wrapping, KeyRingSigning signing, params X509Certificate2[] previous)
    {
        var certificates = DataProtectionCertificates.From(wrapping, previous);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(new DatabaseSettings(), certificates, signing);
        services.Configure<KeyManagementOptions>(options => options.XmlRepository =
            new TrustedKeyRingRepository(repository, certificates, signing, null, TimeProvider.System, NullLogger.Instance));
        return services.BuildServiceProvider();
    }

    /// <summary>The hosts before ARV-211: the same element format, signed with the wrapping certificate's own RSA key.</summary>
    private static ServiceProvider BeforeArv211Host(MemoryKeyRing repository, X509Certificate2 wrapping)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DataProtectionCertificates.From(wrapping));
        // Only to read its own keys back while it writes them (the decryptor is today's, inside its window).
        services.AddSingleton(KeyRingSigning.From(SigningCertificate(), certificateSignedUntil: DateTimeOffset.UtcNow.AddDays(1)));
        services.AddDataProtection().SetApplicationName(DataProtectionExtensions.ApplicationName);
        services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = repository;
            options.XmlEncryptor = new CertificateSignedEncryptor(wrapping);
        });
        return services.BuildServiceProvider();
    }

    private sealed class CertificateSignedEncryptor(X509Certificate2 certificate) : IXmlEncryptor
    {
        public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
            new(OaepGcmXml.EncryptSignedWithCertificate(plaintextElement, certificate), typeof(OaepGcmXmlDecryptor));
    }

    private static IDataProtector Protector(IServiceProvider host) =>
        host.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static (string Plaintext, bool RequiresMigration) Unprotected(IServiceProvider host, string payload)
    {
        var protector = (IPersistedDataProtector)Protector(host);
        var bytes = protector.DangerousUnprotect(WebEncoders.Base64UrlDecode(payload), ignoreRevocationErrors: false, out var requiresMigration, out _);
        return (System.Text.Encoding.UTF8.GetString(bytes), requiresMigration);
    }

    /// <summary>The key id a payload carries after its 4-byte magic header.</summary>
    private static Guid KeyId(string payload) => new(WebEncoders.Base64UrlDecode(payload).AsSpan(4, 16));

    private static string Signer(MemoryKeyRing repository, Guid keyId) =>
        (string)repository.GetAllElements().Single(e => (string)e.Attribute("id") == keyId.ToString())
            .Descendants(OaepGcmXml.Namespace + "encryptedKey").Single().Attribute("signer");

    private string Folder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ariva-dp-signing-" + Guid.CreateVersion7().ToString("N"));
        _folders.Add(directory);
        return directory;
    }

    private static string Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static IConfiguration Configuration(Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    #endregion
}
