using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Monitoring;
using Ariva.UnitTests.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-207 (crypto inventory section 5 item 9): every dated key and credential has a recorded age (<c>ariva.keys.age</c>), the
/// kinds with a cryptoperiod count down to it (<c>ariva.keys.rotation_due_in</c>, negative once passed), and the key ring's states
/// come from the key lifecycle job's last run (<c>ariva.keyring.keys</c>). Tags are the class's kind constants only; the database's
/// part is reported only while the run is fresh; cryptoperiods outside their bounds stop the host.
/// </summary>
public sealed class KeyAgeMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ages_Should_CountFromEachLoadedCertificatesStart_When_TheHostLoadedThem()
    {
        var clock = new ManualClock(Now);
        using var current = Wrapping(Now.AddDays(-100));
        using var previous = Wrapping(Now.AddDays(-800));
        using var signer = KeyRingSigningTests.SigningCertificate();
        using var metrics = new KeyAgeMetrics(timeProvider: clock, dataProtection: DataProtectionCertificates.From(current, previous),
            signing: KeyRingSigning.From(signer));
        using var reader = new InstrumentReader(metrics.Meter);

        var ages = Values(reader, KeyAgeMetrics.AgeName);
        var due = Values(reader, KeyAgeMetrics.RotationDueInName);

        ages[KeyAgeMetrics.DataProtectionCertificate].Should().BeApproximately(TimeSpan.FromDays(100).TotalSeconds, 1);
        ages[KeyAgeMetrics.PreviousDataProtectionCertificate].Should().BeApproximately(TimeSpan.FromDays(800).TotalSeconds, 1);
        ages.Should().ContainKey(KeyAgeMetrics.SigningCertificate);
        due[KeyAgeMetrics.DataProtectionCertificate].Should().BeApproximately(TimeSpan.FromDays(730 - 100).TotalSeconds, 1, "2 years, Proposed");
        due.Should().NotContainKey(KeyAgeMetrics.PreviousDataProtectionCertificate, "a previous certificate is kept to unwrap; its destruction has its own rule");
        reader.Observe(KeyAgeMetrics.KeyRingName).Should().BeEmpty("only the key lifecycle job reports the key ring");

        clock.Advance(TimeSpan.FromDays(640));
        Values(reader, KeyAgeMetrics.RotationDueInName)[KeyAgeMetrics.DataProtectionCertificate].Should().BeApproximately(-TimeSpan.FromDays(10).TotalSeconds, 1,
            "negative once the cryptoperiod has passed");
    }

    [Fact]
    public void Report_Should_GiveTheDatabasesAgesAndTheKeyRingForADayAndTwoHours_When_TheJobRan()
    {
        var clock = new ManualClock(Now);
        using var metrics = new KeyAgeMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);

        metrics.Reported(new KeyLifecycleRun(new KeyRingRetention(2, 1, 1, 3), new Dictionary<string, DateTime>
        {
            [KeyAgeMetrics.DeviceCredential] = Now.UtcDateTime.AddDays(-400),
            [KeyAgeMetrics.OutboundSecret] = Now.UtcDateTime.AddDays(-50),
            [KeyAgeMetrics.DataProtectionKey] = Now.UtcDateTime.AddDays(-120),
            ["passport 123"] = Now.UtcDateTime
        }), KeyAgeMetrics.RunFreshFor);

        var ages = Values(reader, KeyAgeMetrics.AgeName);
        ages.Keys.Should().BeEquivalentTo([KeyAgeMetrics.DeviceCredential, KeyAgeMetrics.OutboundSecret, KeyAgeMetrics.DataProtectionKey],
            "only the class's kinds become tags");
        Values(reader, KeyAgeMetrics.RotationDueInName).Should().ContainSingle().Which.Value.Should().BeApproximately(-TimeSpan.FromDays(35).TotalSeconds, 1,
            "a device credential's year has passed; an outbound secret's cadence is the partner's, so it has no due date");
        Values(reader, KeyAgeMetrics.KeyRingName).Should().BeEquivalentTo(new Dictionary<string, double>
        {
            [KeyAgeMetrics.Active] = 1, [KeyAgeMetrics.Kept] = 1, [KeyAgeMetrics.Held] = 3
        });

        clock.Advance(KeyAgeMetrics.RunFreshFor + TimeSpan.FromMinutes(1));
        reader.Observe(KeyAgeMetrics.AgeName).Should().BeEmpty("a report older than a day and two hours lapses");
        reader.Observe(KeyAgeMetrics.KeyRingName).Should().BeEmpty();
    }

    [Fact]
    public void Problems_Should_ReportTheValuesUnderUnsignedKeysApartFromTheRun_When_TheUpgradeCheckRan()
    {
        // The ARV-211 review (finding N1): a host that starts with leftovers reports them (outcome unsigned) for a day and two hours;
        // the key lifecycle job's run refreshes them; the run's own outcome stays apart.
        var clock = new ManualClock(Now);
        using var metrics = new KeyAgeMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);

        metrics.UpgradeCheckReported(new KeyRingUpgradeFinding(2, 0, 0, RetiredSigner: 3), KeyAgeMetrics.RunFreshFor);
        Outcomes(reader).Should().BeEquivalentTo(new Dictionary<string, double> { [KeyAgeMetrics.Unsigned] = 2, [KeyAgeMetrics.RetiredSigner] = 3 },
            "values under keys from before ARV-211 and under a retired signer's keys need different fixes, so they are reported apart");

        metrics.Reported(new KeyLifecycleRun(new KeyRingRetention(0, 1, 0, 0), new Dictionary<string, DateTime>(), new KeyRingUpgradeFinding(0, 1, 0, 30)),
            KeyAgeMetrics.RunFreshFor);
        Outcomes(reader).Should().BeEquivalentTo(new Dictionary<string, double> { [KeyAgeMetrics.Run] = 0, [KeyAgeMetrics.Unsigned] = 1, [KeyAgeMetrics.RetiredSigner] = 0 });

        metrics.RunFailed(KeyAgeMetrics.RunFreshFor);
        Outcomes(reader).Should().BeEquivalentTo(new Dictionary<string, double> { [KeyAgeMetrics.Run] = 1, [KeyAgeMetrics.Unsigned] = 1, [KeyAgeMetrics.RetiredSigner] = 0 },
            "a failed run leaves the last counts as they were");

        clock.Advance(KeyAgeMetrics.RunFreshFor + TimeSpan.FromMinutes(1));
        reader.Observe(KeyAgeMetrics.ProblemsName).Should().BeEmpty("both lapse a day and two hours after they were reported");
    }

    private static Dictionary<string, double> Outcomes(InstrumentReader reader) =>
        reader.Observe(KeyAgeMetrics.ProblemsName).ToDictionary(m => m.Tags["outcome"], m => m.Value, StringComparer.Ordinal);

    [Fact]
    public void Cryptoperiods_Should_TakeTheConfiguredDaysAndRefuseOthers_When_TheHostStarts()
    {
        var clock = new ManualClock(Now);
        using var metrics = new KeyAgeMetrics(timeProvider: clock, configuration: Configuration(("Monitoring:Cryptoperiods:Device-Credential", "500")));
        metrics.CryptoperiodDays(KeyAgeMetrics.DeviceCredential).Should().Be(500, "configuration keys are read ignoring case");
        metrics.CryptoperiodDays(KeyAgeMetrics.IntegrationClientSecret).Should().Be(365);
        metrics.CryptoperiodDays(KeyAgeMetrics.OutboundSecret).Should().BeNull();

        FluentActions.Invoking(() => new KeyAgeMetrics(configuration: Configuration(("Monitoring:Cryptoperiods:device-credential", "10")))).Should()
            .Throw<InvalidOperationException>().WithMessage("*30 to 3,650*");
        FluentActions.Invoking(() => new KeyAgeMetrics(configuration: Configuration(("Monitoring:Cryptoperiods:token-signing-key", "90")))).Should()
            .Throw<InvalidOperationException>().WithMessage("*not a kind with a cryptoperiod*");
    }

    [Fact]
    public void DefaultCryptoperiods_Should_BeTheKeyManagementPolicysProposedPeriods_When_Read()
    {
        // key-management.md section 3.4 item 4: K3 2 years; K12, K13 (with K12), K14 and K16 1 year; the signing certificate as K3.
        KeyAgeMetrics.DefaultCryptoperiodDays.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [KeyAgeMetrics.DataProtectionCertificate] = 730,
            [KeyAgeMetrics.SigningCertificate] = 730,
            [KeyAgeMetrics.IntegrationClientSecret] = 365,
            [KeyAgeMetrics.IntegrationClientSeed] = 365,
            [KeyAgeMetrics.DeviceCredential] = 365,
            [KeyAgeMetrics.DisplayCredential] = 365
        });
    }

    private static Dictionary<string, double> Values(InstrumentReader reader, string instrument) =>
        reader.Observe(instrument).ToDictionary(m => m.Tags["kind"], m => m.Value, StringComparer.Ordinal);

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static X509Certificate2 Wrapping(DateTimeOffset notBefore)
    {
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        using var created = new CertificateRequest("CN=Ariva DP (age test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(notBefore, notBefore.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }
}
