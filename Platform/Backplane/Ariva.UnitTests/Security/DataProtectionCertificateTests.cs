using Ariva.Infra.Caching;
using Ariva.Infra.DataProtection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Security;

/// <summary>ARV-008: where the key-protection certificate comes from, per environment, and the cache settings.</summary>
public sealed class DataProtectionCertificateTests
{
    public static TheoryData<string> ClusterFiles =>
    [
        "appsettings.base.k8s-dev.json",
        "appsettings.base.k8s-demo.json",
        "appsettings.base.k8s-prd.json"
    ];

    [Fact]
    public void Load_Should_Refuse_When_NoCertificateIsConfigured()
    {
        var act = () => DataProtectionCertificates.Load(new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>().WithMessage("*CertificatePath*");
    }

    [Fact]
    public void Load_Should_CreateDevelopmentCertificateOnceAndReuseIt_When_DevelopmentCertificateIsOn()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ariva-dp-" + Guid.CreateVersion7().ToString("N"));
        var configuration = Configuration(new()
        {
            ["DataProtection:UseDevelopmentCertificate"] = "true",
            ["DataProtection:DevelopmentCertificateDirectory"] = directory
        });

        var first = DataProtectionCertificates.Load(configuration);
        var second = DataProtectionCertificates.Load(configuration);

        first.Current.HasPrivateKey.Should().BeTrue();
        second.Current.Thumbprint.Should().Be(first.Current.Thumbprint, "keys written yesterday must still decrypt today");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(Path.Combine(directory, "dataprotection-dev.key"))
                .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Load_Should_IncludePreviousCertificates_When_RotationIsConfigured()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ariva-dp-" + Guid.CreateVersion7().ToString("N"));
        DataProtectionCertificates.Load(Configuration(new()
        {
            ["DataProtection:UseDevelopmentCertificate"] = "true",
            ["DataProtection:DevelopmentCertificateDirectory"] = directory
        }));
        var certificate = Path.Combine(directory, "dataprotection-dev.crt");
        var key = Path.Combine(directory, "dataprotection-dev.key");

        var loaded = DataProtectionCertificates.Load(Configuration(new()
        {
            ["DataProtection:CertificatePath"] = certificate,
            ["DataProtection:KeyPath"] = key,
            ["DataProtection:Previous:0:CertificatePath"] = certificate,
            ["DataProtection:Previous:0:KeyPath"] = key
        }));

        loaded.All.Should().HaveCount(2);
        loaded.All[0].Should().BeSameAs(loaded.Current);
    }

    [Theory]
    [MemberData(nameof(ClusterFiles))]
    public void ClusterSettings_Should_UseMountedCertificateAndRedis_When_Read(string file)
    {
        var configuration = Layered(file);

        configuration["DataProtection:CertificatePath"].Should().Be("/app/secrets/dataprotection/tls.crt");
        configuration["DataProtection:KeyPath"].Should().Be("/app/secrets/dataprotection/tls.key");
        configuration["DataProtection:UseDevelopmentCertificate"].Should().NotBe("true", "a cluster never generates its own certificate");
        RedisSettings.From(configuration).Enabled.Should().BeTrue("pods share cache evictions through the Redis backplane");
    }

    [Fact]
    public void VmLocalSettings_Should_UseDevelopmentCertificate_When_Read()
    {
        var configuration = Layered("appsettings.base.vm-local.json");

        configuration["DataProtection:UseDevelopmentCertificate"].Should().Be("true");
        RedisSettings.From(configuration).Enabled.Should().BeFalse("dev-up turns Redis on in appsettings.local.json; E2E runs without it");
    }

    private static IConfiguration Configuration(Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration Layered(string file) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, file))
            .Build();
}
