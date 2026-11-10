using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ariva.Infra.Messaging.Kafka;
using Ariva.UnitTests.Setup;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-267: a counter an increase rule reads is primed at 0 only once the host's meter provider listens, or the 0 never
/// reaches the collector and SigNoz still drops the first real increase (a series' first point). In a host built as the Ariva
/// hosts are (OpenTelemetry registered before the messaging), the producer's fatal error counter primed in its lifetime's
/// <c>StartAsync</c> is exported at 0 for both kinds, while a measurement taken in a hosted service's dependency's
/// constructor (all hosted services are built before the first one starts) is exported by nobody: the reason none of the
/// priming is done in a constructor. Read through OpenTelemetry's own SDK with an exporter of the test's, in the host
/// collection, which runs alone, so no other test's meter of the same name adds points.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class MetricPrimingExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrimedCounters_Should_BeExportedAtZero_When_PrimedAsTheHostStartsAndNotInAConstructor()
    {
        var exporter = new RecordingExporter();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddMetrics();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(KafkaProducerMetrics.MeterName, ConstructorPrimed.MeterName)
            .AddReader(new BaseExportingMetricReader(exporter)));
        builder.Services.AddSingleton<KafkaProducerMetrics>();
        builder.Services.AddSingleton(services => new KafkaProduce(_ => new Mock<IProducer<string, byte[]>>().Object, TimeProvider.System,
            NullLogger.Instance, TimeSpan.FromMinutes(5), services.GetRequiredService<KafkaProducerMetrics>()));
        builder.Services.AddHostedService<KafkaProducerLifetime>();
        builder.Services.AddSingleton<ConstructorPrimed>();
        builder.Services.AddHostedService<ConstructorPrimedService>();
        using var host = builder.Build();

        await host.StartAsync(Ct);
        host.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000).Should().BeTrue();

        exporter.Points(KafkaProducerMetrics.FatalName).Should().OnlyContain(point => point.Value == 0)
            .And.Subject.Select(point => point.Tags["kind"]).Should().BeEquivalentTo([KafkaProducerMetrics.FatalErrorKind, KafkaProducerMetrics.RecreatedKind],
                "the lifetime's StartAsync primes both kinds after the meter provider started");
        exporter.Points(ConstructorPrimed.CounterName).Should().BeEmpty(
            "a measurement taken while the hosted services are built, before the meter provider starts, reaches no exporter");
        await host.StopAsync(Ct);
    }

    /// <summary>A dependency of a hosted service that measures a 0 in its constructor, as ARV-267's first draft did.</summary>
    private sealed class ConstructorPrimed : IDisposable
    {
        public const string MeterName = "Ariva.UnitTests.ConstructorPrimed";
        public const string CounterName = "ariva.unittests.constructor_primed";

        private readonly Meter _meter;

        public ConstructorPrimed(IMeterFactory factory)
        {
            _meter = factory.Create(MeterName);
            _meter.CreateCounter<long>(CounterName).Add(0, new KeyValuePair<string, object>("kind", "constructor"));
        }

        public void Dispose() => _meter.Dispose();
    }

    private sealed class ConstructorPrimedService(ConstructorPrimed primed) : IHostedService
    {
        public ConstructorPrimed Primed { get; } = primed;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Keeps every long sum point it is given, with its tags.</summary>
    private sealed class RecordingExporter : BaseExporter<Metric>
    {
        private readonly ConcurrentQueue<(string Name, long Value, Dictionary<string, string> Tags)> _points = new();

        public IReadOnlyList<(long Value, Dictionary<string, string> Tags)> Points(string name) =>
            [.. _points.Where(point => point.Name == name).Select(point => (point.Value, point.Tags))];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.LongSum)
                    continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var tag in point.Tags)
                        tags[tag.Key] = tag.Value?.ToString();
                    _points.Enqueue((metric.Name, point.GetSumLong(), tags));
                }
            }

            return ExportResult.Success;
        }
    }
}
