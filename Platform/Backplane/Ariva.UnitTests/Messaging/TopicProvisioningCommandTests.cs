using System.Diagnostics;
using Ariva.Di;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-062: the <c>--provision-topics</c> command the Helm topics Job runs. It must never pass a release whose topics
/// were not created (exit 1), must refuse production settings the hosts would refuse, and must leave the flag out of
/// the configuration. The broker-facing path is covered by Ariva.IntegrationTests TopicProvisioningTests.
/// </summary>
public sealed class TopicProvisioningCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Flag_Should_BeRecognisedAndRemoved_When_Present()
    {
        string[] args = ["--provision-topics", "--Kafka:Enabled=true"];

        TopicProvisioning.IsRequested(args).Should().BeTrue();
        TopicProvisioning.IsRequested(["--migrate"]).Should().BeFalse();
        TopicProvisioning.IsRequested(null).Should().BeFalse();
        TopicProvisioning.WithoutFlag(args).Should().Equal("--Kafka:Enabled=true");
    }

    [Fact]
    public async Task Run_Should_SucceedWithoutContactingABroker_When_KafkaIsOff()
    {
        var code = await TopicProvisioning.RunAsync(Configuration(("Kafka:Enabled", "false"), ("Kafka:BootstrapServers", "127.0.0.1:1")), TimeSpan.FromSeconds(1), Ct);

        code.Should().Be(0, "no host uses Kafka, so the release needs no topics");
    }

    [Fact]
    public async Task Run_Should_Refuse_When_ProductionKafkaIsNotSaslSsl()
    {
        var code = await TopicProvisioning.RunAsync(Configuration(
            ("Application:Environment", "k8s-prd"), ("Kafka:Enabled", "true"), ("Kafka:SecurityProtocol", "Plaintext"), ("Kafka:BootstrapServers", "127.0.0.1:1")),
            TimeSpan.FromSeconds(1), Ct);

        code.Should().Be(1, "the hosts refuse plaintext Kafka in k8s-prd (CWE-501), so the job must too");
    }

    [Fact]
    public async Task Run_Should_Fail_When_TheSecurityProtocolIsUnknown()
    {
        var code = await TopicProvisioning.RunAsync(Configuration(("Kafka:Enabled", "true"), ("Kafka:SecurityProtocol", "Bogus"), ("Kafka:BootstrapServers", "127.0.0.1:1")),
            TimeSpan.FromSeconds(1), Ct);

        code.Should().Be(1);
    }

    [Fact]
    public async Task Run_Should_FailWithinItsDeadline_When_TheBrokerIsUnreachable()
    {
        var clock = Stopwatch.StartNew();

        var code = await TopicProvisioning.RunAsync(Configuration(("Kafka:Enabled", "true"), ("Kafka:BootstrapServers", "127.0.0.1:1")), TimeSpan.FromSeconds(2), Ct);

        code.Should().Be(1, "a release must not go ahead without its topics");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the job ends at its deadline instead of waiting for the broker forever");
    }
}
