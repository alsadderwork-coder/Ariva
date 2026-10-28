using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Core.Flights;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Flights;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Sensing;
using Ariva.UnitTests.Setup;
using FluentAssertions;

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
}
