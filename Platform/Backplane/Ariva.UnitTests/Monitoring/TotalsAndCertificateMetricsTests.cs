using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Core.Flights;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Flights;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Sensing;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-125a: the device and feed totals, which only the replica that wins a sweep computes, are reported only while that
/// sweep is fresh, so a dashboard never adds up stale copies from other replicas or hosts; <c>ariva.devices.monitored</c>
/// sits beside <c>ariva.devices.offline</c>; and <c>ariva.certificates.expires_in</c> keeps the end of validity of the
/// certificates the host loaded, nothing else of them.
/// </summary>
public sealed class TotalsAndCertificateMetricsTests
{
    [Fact]
    public void DeviceTotals_Should_BeReportedOnlyWhileTheSweepIsFresh_When_ThisReplicaSwept()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new DeviceHealthMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        string[] totals = ["ariva.devices.monitored", "ariva.devices.offline", "ariva.devices.degraded", "ariva.zones.degraded_now", "ariva.devices.clock_unreliable"];
        totals.Should().OnlyContain(name => reader.Observe(name).Count == 0, "a host or replica that never swept reports no total, not zeros");

        metrics.Snapshot(12, 3, 1, 2, 4, TimeSpan.FromSeconds(30));

        totals.Select(name => reader.Observe(name).Single().Value).Should().Equal(12, 3, 1, 2, 4);
        clock.Advance(TimeSpan.FromSeconds(30));
        reader.Observe("ariva.devices.offline").Should().ContainSingle("still within two sweep intervals");
        clock.Advance(TimeSpan.FromSeconds(1));
        totals.Should().OnlyContain(name => reader.Observe(name).Count == 0, "another replica swept since: this copy is stale and is not reported");
        metrics.Current.Should().Be((12, 3, 1, 2, 4), "the last sweep is still known to the process");
    }

    [Fact]
    public void FeedTotal_Should_BeReportedOnlyWhileTheSweepIsFresh_When_ThisReplicaSwept()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new FlightMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe("ariva.feeds.stale").Should().BeEmpty("a host that registers the metrics without sweeping reports nothing");

        metrics.Snapshot(2, TimeSpan.FromMinutes(2));
        reader.Observe("ariva.feeds.stale").Should().ContainSingle().Which.Value.Should().Be(2);

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        reader.Observe("ariva.feeds.stale").Should().BeEmpty();
        metrics.Items("legs", "DMO", "api-aodb", [new FlightItemResult(0, "EK0001", true, [], [])]);
        reader.Sum("ariva.flights.items", ("site", "DMO"), ("outcome", "applied")).Should().Be(1, "the counters are not affected");
    }

    [Fact]
    public void ExpiresIn_Should_CountDownToTheDataProtectionCertificatesEnd_When_TheHostLoadedIt()
    {
        var now = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(now);
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        using var certificate = new CertificateRequest("CN=Ariva Data Protection (test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        using var metrics = new CertificateMetrics(timeProvider: clock, dataProtection: DataProtectionCertificates.From(certificate));
        using var reader = new InstrumentReader(metrics.Meter);

        var measured = reader.Observe(CertificateMetrics.ExpiresInName).Should().ContainSingle().Subject;
        measured.Tags.Should().Equal(new Dictionary<string, string> { ["certificate"] = CertificateMetrics.DataProtection }, "its name only, nothing of the certificate");
        measured.Value.Should().BeApproximately(TimeSpan.FromDays(30).TotalSeconds, 1);

        clock.Advance(TimeSpan.FromDays(31));
        reader.Observe(CertificateMetrics.ExpiresInName).Single().Value.Should().BeApproximately(-TimeSpan.FromDays(1).TotalSeconds, 1, "negative once its end has passed");
    }

    [Fact]
    public void Track_Should_RefuseACertificateArivaDoesNotReport_When_AskedForAnother()
    {
        using var metrics = new CertificateMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(CertificateMetrics.ExpiresInName).Should().BeEmpty("no certificate loaded");

        FluentActions.Invoking(() => metrics.Track("ingress CN=airport.example", DateTime.UtcNow)).Should().Throw<ArgumentOutOfRangeException>(
            "the tag is one of Ariva's names, never a certificate's subject");
        metrics.Track(CertificateMetrics.MqttServer, DateTime.UtcNow.AddDays(10));
        reader.Observe(CertificateMetrics.ExpiresInName).Should().ContainSingle().Which.Tags["certificate"].Should().Be(CertificateMetrics.MqttServer);
    }

    [Fact]
    public void WebCertificate_Should_FollowTheKubeletsSwapAndReadZeroWhenUnreadable_When_TheGaugeIsCollected()
    {
        // ARV-198: api-main reports the web pods' certificate from its public half (the chart mounts tls.crt alone), laid out as the
        // kubelet lays out a projected secret: tls.crt links to ..data/tls.crt and ..data to a dated folder, which a renewal replaces
        // by pointing ..data elsewhere (the link's own time and length never change). A configured file it cannot read reads 0.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The kubelet's layout is symbolic links on Linux.");
        var folder = Directory.CreateTempSubdirectory("ariva-web-tls-").FullName;
        try
        {
            void Dated(string name, string content)
            {
                Directory.CreateDirectory(Path.Combine(folder, name));
                File.WriteAllText(Path.Combine(folder, name, "tls.crt"), content);
            }
            void Swap(string name)
            {
                var temporary = Path.Combine(folder, "..data_tmp");
                File.CreateSymbolicLink(temporary, name);
                // rename(2) over the old link, as the kubelet's atomic writer does (File.Replace checks with lstat; File.Move would
                // follow the link to its folder).
                File.Replace(temporary, Path.Combine(folder, "..data"), destinationBackupFileName: null);
            }
            var file = Path.Combine(folder, "tls.crt");
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { [CertificateMetrics.WebCertificatePathSetting] = file }).Build();
            using var metrics = new CertificateMetrics(configuration: configuration);
            using var reader = new InstrumentReader(metrics.Meter);
            HoursLeft(reader).Should().Be(0, "a configured file that is not there reads 0, so certificateExpiry fires rather than reading nothing");

            Dated("..2026_11_09_a", Pem(hours: 10));
            File.CreateSymbolicLink(Path.Combine(folder, "..data"), "..2026_11_09_a");
            File.CreateSymbolicLink(file, "..data/tls.crt");
            HoursLeft(reader).Should().BeApproximately(10, 0.1);

            Dated("..2026_11_09_b", Pem(hours: 50));
            Swap("..2026_11_09_b");
            HoursLeft(reader).Should().BeApproximately(50, 0.1, "the renewal the kubelet swapped in is read through the links");

            Dated("..2026_11_09_c", "not a certificate");
            Swap("..2026_11_09_c");
            HoursLeft(reader).Should().Be(0, "a file without a certificate reads 0, and nothing throws");

            Dated("..2026_11_09_d", Pem(hours: 30) + new string('A', 70_000));
            Swap("..2026_11_09_d");
            HoursLeft(reader).Should().Be(0, "a file over 64 KB, measured on the target, is not read");
            FluentActions.Invoking(() => metrics.TrackFile("ingress CN=airport.example", file)).Should().Throw<ArgumentOutOfRangeException>();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExpiresIn_Should_ReportTheSigningCertificateAndEachCaFileTheHostUses_When_Configured()
    {
        // ARV-207: the key ring's signing certificate (ARV-211) and the CA files a host verifies its services against, each by its
        // own constant; a CA bundle reports its earliest end (during a rotation the older CA is the one to act on); a setting the
        // host does not use (Redis off) is not reported.
        var folder = Directory.CreateTempSubdirectory("ariva-ca-").FullName;
        try
        {
            var bundle = Path.Combine(folder, "ca.crt");
            // A bundle as a CA file holds it: one certificate per PEM block, each block on lines of its own (ExportCertificatePem
            // ends without a line break, so two exports glued together would read as no certificate at all).
            File.WriteAllText(bundle, Pem(hours: 500) + "\n" + Pem(hours: 20) + "\n");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Database:UseEncryption"] = "true",
                ["Database:RootCertificate"] = bundle,
                ["Redis:Enabled"] = "false",
                ["Redis:CaCertificatePath"] = bundle,
                ["Kafka:SslCaLocation"] = Path.Combine(folder, "missing.crt"),
                ["Otlp:RootCertificate"] = bundle
            }).Build();
            using var signer = Ariva.UnitTests.Security.KeyRingSigningTests.SigningCertificate();
            using var metrics = new CertificateMetrics(configuration: configuration, signing: KeyRingSigning.From(signer));
            using var reader = new InstrumentReader(metrics.Meter);

            var measured = reader.Observe(CertificateMetrics.ExpiresInName).ToDictionary(m => m.Tags["certificate"], m => m.Value / 3600, StringComparer.Ordinal);

            measured.Keys.Should().BeEquivalentTo([CertificateMetrics.DataProtectionSigning, CertificateMetrics.DatabaseCa, CertificateMetrics.KafkaCa, CertificateMetrics.OtlpCa]);
            measured[CertificateMetrics.DatabaseCa].Should().BeApproximately(20, 0.1, "the earliest end in the bundle");
            measured[CertificateMetrics.KafkaCa].Should().Be(0, "a configured CA file that is not there reads 0, so certificateExpiry fires");
            measured[CertificateMetrics.DataProtectionSigning].Should().BeApproximately((signer.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalHours, 0.1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static double HoursLeft(InstrumentReader reader) =>
        reader.Observe(CertificateMetrics.ExpiresInName).Single(m => m.Tags["certificate"] == CertificateMetrics.WebTls).Value / 3600;

    private static string Pem(int hours)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=web-service", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(hours));
        return certificate.ExportCertificatePem();
    }
}
