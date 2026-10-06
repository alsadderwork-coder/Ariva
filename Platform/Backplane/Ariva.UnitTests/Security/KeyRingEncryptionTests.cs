using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ariva.UnitTests.Setup;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-080 (ASVS V11.3.1 to V11.3.3, V11.2.3): the Data Protection key ring is wrapped with RSA-OAEP-SHA256 and
/// AES-256-GCM, keys written before stay readable, new keys protect payloads with AES-256-GCM, and a weak certificate
/// never protects the ring. The PostgreSQL repository is covered by Ariva.IntegrationTests DataProtectionKeyRingTests.
/// </summary>
public sealed class KeyRingEncryptionTests
{
    private const string Purpose = "Ariva.UnitTests.KeyRing";

    #region Element format

    [Fact]
    public void Decrypt_Should_ReturnTheOriginalElement_When_ItWasEncryptedWithAConfiguredCertificate()
    {
        using var certificate = Certificate(3072);
        var original = new XElement("key", new XAttribute("id", "42"), new XElement("secret", "master-key-material"));

        var encrypted = OaepGcmXml.Encrypt(original, certificate);
        var decrypted = OaepGcmXml.Decrypt(encrypted, [certificate]);

        XNode.DeepEquals(decrypted, original).Should().BeTrue();
        encrypted.ToString().Should().NotContain("master-key-material");
        ((string)encrypted.Attribute("algorithm")).Should().Be(OaepGcmXml.Algorithm);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    [InlineData("wrappedKey")]
    public void Decrypt_Should_Refuse_When_APartWasAltered(string part)
    {
        using var certificate = Certificate(3072);
        var encrypted = OaepGcmXml.Encrypt(new XElement("key", "material"), certificate);
        var element = encrypted.Element(OaepGcmXml.Namespace + part)!;
        var bytes = Convert.FromBase64String(element.Value);
        bytes[0] ^= 0x01;
        element.Value = Convert.ToBase64String(bytes);

        var decrypt = () => OaepGcmXml.Decrypt(encrypted, [certificate]);

        decrypt.Should().Throw<CryptographicException>("GCM authenticates the key and the wrapped key decrypts only unaltered");
    }

    [Fact]
    public void Decrypt_Should_Refuse_When_TheSignatureIsMissingOrAltered()
    {
        using var certificate = Certificate(3072);
        var unsigned = OaepGcmXml.Encrypt(new XElement("key", "material"), certificate);
        unsigned.Element(OaepGcmXml.Namespace + "signature")!.Remove();
        var altered = OaepGcmXml.Encrypt(new XElement("key", "material"), certificate);
        var signature = altered.Element(OaepGcmXml.Namespace + "signature")!;
        var bytes = Convert.FromBase64String(signature.Value);
        bytes[^1] ^= 0x01;
        signature.Value = Convert.ToBase64String(bytes);

        FluentActions.Invoking(() => OaepGcmXml.Decrypt(unsigned, [certificate])).Should().Throw<CryptographicException>(
            "an element made with the public certificate alone has no valid signature");
        FluentActions.Invoking(() => OaepGcmXml.Decrypt(altered, [certificate])).Should().Throw<CryptographicException>().WithMessage("*signature*");
    }

    [Fact]
    public void Decrypt_Should_Refuse_When_TheAlgorithmLabelOrCertificateIsChanged()
    {
        using var certificate = Certificate(3072);
        using var other = Certificate(3072);
        var relabelled = OaepGcmXml.Encrypt(new XElement("key", "material"), certificate);
        relabelled.SetAttributeValue("algorithm", "RSA1_5+A256CBC");
        var reassigned = OaepGcmXml.Encrypt(new XElement("key", "material"), certificate);
        reassigned.SetAttributeValue("certificate", other.GetCertHashString(HashAlgorithmName.SHA256));

        FluentActions.Invoking(() => OaepGcmXml.Decrypt(relabelled, [certificate])).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => OaepGcmXml.Decrypt(reassigned, [certificate, other])).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => OaepGcmXml.Decrypt(OaepGcmXml.Encrypt(new XElement("key"), certificate), [other]))
            .Should().Throw<CryptographicException>().WithMessage("*No Data Protection certificate*");
    }

    #endregion

    #region The key ring as the hosts configure it

    [Fact]
    public void Keys_Should_BeWrappedWithOaepAndGcm_When_TheHostsWriteThem()
    {
        using var certificate = Certificate(3072);
        var repository = new MemoryRepository();
        using var host = Host(repository, DataProtectionCertificates.From(certificate));

        var protectedValue = Protector(host).Protect("totp-seed");

        var stored = repository.GetAllElements().Select(e => e.ToString()).ToList();
        stored.Should().NotBeEmpty();
        stored.Should().AllSatisfy(xml =>
        {
            xml.Should().Contain(OaepGcmXml.Algorithm);
            xml.Should().NotContain("rsa-1_5", "PKCS#1 v1.5 key wrapping is refused (ASVS V11.3.1)");
            xml.Should().NotContain("aes256-cbc", "the ring is encrypted with AES-GCM, not CBC without a MAC (ASVS V11.3.2, V11.3.3)");
            xml.Should().NotContain("EncryptedData", "the framework's EncryptedXml format is no longer written");
            xml.Should().NotContain("<value>", "no plaintext master key may reach the repository");
        });

        using var otherHost = Host(repository, DataProtectionCertificates.From(certificate));
        Protector(otherHost).Unprotect(protectedValue).Should().Be("totp-seed", "every host decrypts with the shared certificate");
    }

    [Fact]
    public void NewKeys_Should_ProtectPayloadsWithAesGcm_When_Created()
    {
        using var certificate = Certificate(3072);
        var repository = new MemoryRepository();
        using var host = Host(repository, DataProtectionCertificates.From(certificate));

        Protector(host).Protect("anything");

        // The descriptor names the payload algorithm in the clear; only the master key value is encrypted.
        var encryption = repository.GetAllElements().Single().Descendants("encryption").Single();
        ((string)encryption.Attribute("algorithm")).Should().Be("AES_256_GCM");
    }

    [Fact]
    public void Unprotect_Should_ReadPayloadsOfAKeyWrittenBeforeArv080_When_ItsCertificateIsConfigured()
    {
        using var certificate = Certificate(3072);
        var repository = new MemoryRepository();
        string legacyPayload;
        using (var legacy = LegacyHost(repository, certificate))
            legacyPayload = Protector(legacy).Protect("outbound-secret");
        repository.GetAllElements().Single().ToString().Should().Contain("EncryptedData", "the fixture is a key in the old format");

        using var host = Host(repository, DataProtectionCertificates.From(certificate), legacyFormatUntil: DateTimeOffset.UtcNow.AddDays(1));
        using var closed = Host(repository, DataProtectionCertificates.From(certificate), legacyFormatUntil: null);

        Protector(host).Unprotect(legacyPayload).Should().Be("outbound-secret", "old keys stay readable during the migration window");
        FluentActions.Invoking(() => Protector(closed).Unprotect(legacyPayload)).Should().Throw<CryptographicException>(
            "without DataProtection:LegacyFormatUntil the older format, which anyone can write with the public certificate, is refused");
    }

    [Fact]
    public void KeyRing_Should_IgnoreAPlantedPlaintextKey_When_ItWouldBecomeTheDefault()
    {
        using var certificate = Certificate(3072);
        var repository = new MemoryRepository();
        using (var first = Host(repository, DataProtectionCertificates.From(certificate)))
            Protector(first).Protect("creates the genuine key");

        using var attacker = PlaintextKeyWriter(repository);
        // Activated after the genuine key, so it would be the default key if it were trusted.
        Thread.Sleep(20);
        attacker.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        repository.GetAllElements().Should().Contain(e => e.ToString().Contains("<value>"), "the fixture plants a plaintext key");

        using var host = Host(repository, DataProtectionCertificates.From(certificate));
        var payload = Protector(host).Protect("totp-seed");

        FluentActions.Invoking(() => Protector(attacker).Unprotect(payload)).Should().Throw<CryptographicException>(
            "the host never protects with a key someone planted in the table");
        Protector(host).Unprotect(payload).Should().Be("totp-seed");
    }

    [Fact]
    public void KeyRing_Should_IgnoreAPlantedKey_When_ItHidesAGenuineEncryptedSecretAsADecoy()
    {
        using var certificate = Certificate(3072);
        var repository = new MemoryRepository();
        using (var first = Host(repository, DataProtectionCertificates.From(certificate)))
            Protector(first).Protect("creates the genuine key");
        var genuine = repository.GetAllElements().Single().Descendants().Single(e => e.Name.LocalName == "encryptedSecret");

        // A plain-text key with a later activation, carrying a genuine signed encrypted secret copied from the real row.
        using var attacker = PlaintextKeyWriter(repository);
        // Activated after the genuine key, so it would be the default key if it were trusted.
        Thread.Sleep(20);
        attacker.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        repository.Mutate(elements =>
        {
            var planted = elements.Single(e => e.ToString().Contains("<value>"));
            planted.Descendants().Single(e => e.Name.LocalName == "masterKey").Parent!.Add(new XElement("decoy", new XElement(genuine)));
        });

        using var host = Host(repository, DataProtectionCertificates.From(certificate));
        var payload = Protector(host).Protect("totp-seed");

        FluentActions.Invoking(() => Protector(attacker).Unprotect(payload)).Should().Throw<CryptographicException>(
            "a decoy encrypted secret does not make a plain-text master key trusted");
    }

    [Fact]
    public void KeyRing_Should_NeverProtectWithALegacyKey_When_OneIsPlantedDuringTheMigrationWindow()
    {
        using var certificate = Certificate(3072);
        var repository = new MemoryRepository();
        using (var first = Host(repository, DataProtectionCertificates.From(certificate)))
            Protector(first).Protect("creates the genuine key");

        // Anyone with the public certificate can write the older format; this one activates after the genuine key.
        using var planter = LegacyHost(repository, certificate);
        Thread.Sleep(20);
        planter.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        using var host = Host(repository, DataProtectionCertificates.From(certificate), legacyFormatUntil: DateTimeOffset.UtcNow.AddDays(30));
        var payload = Protector(host).Protect("totp-seed");

        using var reader = LegacyHost(repository, certificate);
        FluentActions.Invoking(() => Protector(reader).Unprotect(payload)).Should().Throw<CryptographicException>(
            "a legacy key is admitted only to decrypt, as an expired copy, so it never becomes the default key");
    }

    [Fact]
    public void AddArivaDataProtection_Should_PutTheTrustedFilterInFrontOfPostgres_When_AHostRegistersIt()
    {
        using var certificate = Certificate(3072);
        var until = new DateTimeOffset(2027, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(new DatabaseSettings(), DataProtectionCertificates.From(certificate), until);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeyManagementOptions>>().Value;

        options.XmlRepository.Should().BeOfType<TrustedKeyRingRepository>().Which.LegacyFormatUntil.Should().Be(until);
        options.XmlEncryptor.Should().BeOfType<OaepGcmXmlEncryptor>();
    }

    [Fact]
    public void Verdict_Should_AdmitOnlyArivaKeys_When_TheRingIsRead()
    {
        var filter = new TrustedKeyRingRepository(new MemoryRepository(), null, TimeProvider.System, NullLogger.Instance);
        XElement Key(string decryptor) => new("key", new XAttribute("id", Guid.NewGuid()),
            new XElement("descriptor", new XElement("descriptor", new XElement("encryptedSecret", new XAttribute("decryptorType", decryptor)))));

        filter.Verdict(Key(typeof(OaepGcmXmlDecryptor).AssemblyQualifiedName!)).Should().BeNull();
        filter.Verdict(Key("Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor, Microsoft.AspNetCore.DataProtection")).Should().Contain("LegacyFormatUntil");
        filter.Verdict(Key("Evil.Decryptor, Evil")).Should().Contain("does not use");
        filter.Verdict(new XElement("key", new XElement("descriptor", new XElement("descriptor", new XElement("masterKey", new XElement("value", "AAAA"))))))
            .Should().Contain("exactly one encrypted secret");
        // A decoy: a plain-text master key where Data Protection reads it, and a genuine encrypted secret elsewhere.
        var decoy = Key(typeof(OaepGcmXmlDecryptor).AssemblyQualifiedName!);
        var genuine = decoy.Descendants("encryptedSecret").Single();
        genuine.Remove();
        decoy.Element("descriptor")!.Element("descriptor")!.Add(new XElement("masterKey", new XElement("value", "AAAA")), new XElement("decoy", genuine));
        filter.Verdict(decoy).Should().NotBeNull("Data Protection would read the plain-text master key and ignore the decoy");
        filter.Verdict(new XElement("revocation")).Should().BeNull("a revocation can at worst force a new key");
        filter.Verdict(new XElement("something")).Should().NotBeNull();
    }

    [Fact]
    public void DecryptorType_Should_KeepItsName_When_KeysAreStoredWithIt()
    {
        // Every stored key records this type's name: renaming or moving it would make the key ring unreadable.
        typeof(OaepGcmXmlDecryptor).FullName.Should().Be("Ariva.Infra.DataProtection.OaepGcmXmlDecryptor");
        typeof(OaepGcmXmlDecryptor).Assembly.GetName().Name.Should().Be("Ariva.Infra");
    }

    [Theory]
    [InlineData("appsettings.base.k8s-dev.json")]
    [InlineData("appsettings.base.k8s-demo.json")]
    [InlineData("appsettings.base.k8s-prd.json")]
    public void ClusterSettings_Should_NotOpenTheLegacyWindow_When_Committed(string file)
    {
        // The window is for a migration and is set by the operator with an end date, never shipped open (ARV-080).
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Api.Common", file))
            .Build();

        configuration["DataProtection:LegacyFormatUntil"].Should().BeNull();
    }

    #endregion

    #region Certificate strength

    [Theory]
    [InlineData(2048)]
    [InlineData(1024)]
    public void Certificates_Should_RefuseAnRsaKey_When_ItIsUnder3072Bits(int bits)
    {
        using var weak = Certificate(bits);

        var current = () => DataProtectionCertificates.From(weak);
        using var strong = Certificate(3072);
        var previous = () => DataProtectionCertificates.From(strong, weak);

        current.Should().Throw<InvalidOperationException>().WithMessage($"*RSA {bits} key*3072*");
        previous.Should().Throw<InvalidOperationException>("a weak previous certificate still unlocks every old key");
    }

    [Fact]
    public void Certificates_Should_RefuseAnEcKey_When_ItCannotWrapWithOaep()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=Ariva EC", ec, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var load = () => DataProtectionCertificates.From(certificate);

        load.Should().Throw<InvalidOperationException>().WithMessage("*no RSA key*");
    }

    [Fact]
    public void Load_Should_RefuseAMountedCertificate_When_ItsRsaKeyIsUnder3072Bits()
    {
        var directory = Directory.CreateTempSubdirectory("ariva-dp-weak-");
        try
        {
            using var rsa = RSA.Create(2048);
            using var created = new CertificateRequest("CN=Ariva weak", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificatePath = Path.Combine(directory.FullName, "tls.crt");
            var keyPath = Path.Combine(directory.FullName, "tls.key");
            File.WriteAllText(certificatePath, created.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["DataProtection:CertificatePath"] = certificatePath,
                ["DataProtection:KeyPath"] = keyPath
            }).Build();

            var load = () => DataProtectionCertificates.Load(configuration);

            load.Should().Throw<InvalidOperationException>().WithMessage("*RSA 2048 key*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    #endregion

    #region Helpers

    private static ServiceProvider Host(MemoryRepository repository, DataProtectionCertificates certificates, DateTimeOffset? legacyFormatUntil = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaDataProtection(new DatabaseSettings(), certificates, legacyFormatUntil);
        // After AddArivaDataProtection: this repository replaces PostgreSQL behind the same trusted key filter, and the
        // encryptor stays the hosts' one.
        services.Configure<KeyManagementOptions>(options => options.XmlRepository =
            new TrustedKeyRingRepository(repository, legacyFormatUntil, TimeProvider.System, NullLogger.Instance));
        return services.BuildServiceProvider();
    }

    /// <summary>Someone with INSERT on the key table: writes a key whose master key is in plain text.</summary>
    private static ServiceProvider PlaintextKeyWriter(MemoryRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(DataProtectionExtensions.ApplicationName);
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    /// <summary>The configuration before ARV-080: the framework's certificate encryptor (EncryptedXml).</summary>
    private static ServiceProvider LegacyHost(MemoryRepository repository, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName(DataProtectionExtensions.ApplicationName)
            .ProtectKeysWithCertificate(certificate)
            .UnprotectKeysWithAnyCertificate(certificate);
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider host) =>
        host.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static X509Certificate2 Certificate(int bits)
    {
        using var rsa = RSA.Create(bits);
        var request = new CertificateRequest($"CN=Ariva DP {bits}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Round trip through PKCS#12 so the private key is usable on every platform, as a mounted PEM pair would be.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
                return [.. _elements.Select(e => new XElement(e))];
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
                _elements.Add(new XElement(element));
        }

        /// <summary>Changes the stored rows in place, as a database writer could.</summary>
        public void Mutate(Action<List<XElement>> change)
        {
            lock (_elements)
                change(_elements);
        }
    }

    #endregion
}
