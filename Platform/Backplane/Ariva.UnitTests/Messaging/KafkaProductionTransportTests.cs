using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-083b, on the owner's decision of 2026-11-10 (a site's brokers always use the internal CA; CWE-295, CWE-287, CWE-501; ASVS
/// 5.0 V12.3.4, V13.2.1): a k8s-prd host with Kafka on does not start unless it reaches the brokers over SaslSsl, verified
/// against the internal CA whose file is there (<c>Kafka:SslCaLocation</c>, as ARV-082a pins PostgreSQL and Redis), and signs
/// in with a SASL mechanism as its own principal (<c>Kafka:SaslUsername</c> and <c>Kafka:SaslPassword</c>). The topics job
/// refuses the same. No message carries the password; elsewhere, and with Kafka off, the settings are taken as they are.
/// </summary>
public sealed class KafkaProductionTransportTests : IDisposable
{
    #region Fields

    private const string KafkaPassword = "kafka-secret-value-0123";
    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-kafka-ca-").FullName;
    private readonly string _ca;

    public KafkaProductionTransportTests()
    {
        _ca = Path.Combine(_folder, "ca.crt");
        File.WriteAllText(_ca, "test CA");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    #endregion

    #region Cases

    /// <summary>The setting left out (or "missing-file" for a CA path with no file), and the message expected; null: accepted.</summary>
    public static TheoryData<string, string> ProductionCases => new()
    {
        { "Kafka:SslCaLocation", "Kafka:SslCaLocation must name the internal CA's PEM file in k8s-prd*" },
        { "missing-file", "Kafka:SslCaLocation names a file that does not exist*" },
        { "Kafka:SaslMechanism", "Kafka:SaslMechanism must be set in k8s-prd*" },
        { "Kafka:SaslUsername", "Kafka:SaslUsername and Kafka:SaslPassword must be set in k8s-prd*" },
        { "Kafka:SaslPassword", "Kafka:SaslUsername and Kafka:SaslPassword must be set in k8s-prd*" },
        { "nothing", null }
    };

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(ProductionCases))]
    public void AddArivaMessaging_Should_RefuseKafkaWithoutTheInternalCaOrItsOwnPrincipal_When_InProduction(string left, string message)
    {
        var configuration = Configuration("k8s-prd", left);

        var add = () => new ServiceCollection().AddArivaMessaging(configuration);

        if (message is null)
        {
            add.Should().NotThrow("SaslSsl to the internal CA, signed in as the host's own principal");
            return;
        }

        add.Should().Throw<InvalidOperationException>().WithMessage(message)
            .Which.Message.Contains(KafkaPassword, StringComparison.Ordinal).Should().BeFalse("no message carries the password");
    }

    [Theory]
    [InlineData("k8s-dev", "Kafka:SslCaLocation")]
    [InlineData("k8s-demo", "Kafka:SaslUsername")]
    [InlineData("vm-local", "missing-file")]
    public void AddArivaMessaging_Should_TakeTheSettingsAsTheyAre_When_NotInProduction(string environment, string left)
    {
        var add = () => new ServiceCollection().AddArivaMessaging(Configuration(environment, left));

        add.Should().NotThrow("the internal CA and the principals are k8s-prd's rule; dev and demo may run plaintext");
    }

    [Fact]
    public void EnsureProductionTransport_Should_AskNothing_When_KafkaIsOff()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Application:Environment"] = "k8s-prd" }).Build();

        var check = () => MessagingExtensions.EnsureProductionTransport(new KafkaSettings { Enabled = false }, configuration, _ => false);

        check.Should().NotThrow("no host uses Kafka, so none signs in");
    }

    [Fact]
    public async Task TopicsJob_Should_Refuse_When_ProductionKafkaHasNoInternalCa()
    {
        var code = await Ariva.Di.TopicProvisioning.RunAsync(Configuration("k8s-prd", "Kafka:SslCaLocation"), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        code.Should().Be(1, "the job signs in to the same brokers as the hosts, so it refuses what they refuse");
    }

    #endregion

    #region Helpers

    /// <summary>A k8s-prd-ready Kafka section for <paramref name="environment"/> with <paramref name="left"/> left out.</summary>
    private IConfiguration Configuration(string environment, string left)
    {
        var values = new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Kafka:SecurityProtocol"] = "SaslSsl",
            ["Kafka:SaslMechanism"] = "ScramSha512",
            ["Kafka:SslCaLocation"] = left == "missing-file" ? Path.Combine(_folder, "absent.crt") : _ca,
            ["Kafka:SaslUsername"] = "ariva-main",
            ["Kafka:SaslPassword"] = KafkaPassword
        };
        values.Remove(left);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    #endregion
}
