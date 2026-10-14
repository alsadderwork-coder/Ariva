using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>The queue engine worker of Ariva.Api.Stream (ARV-034).</summary>
public static class StreamExtensions
{
    /// <summary>
    /// Registers the stream store, the geometry source and, when <c>Stream:Enabled</c> and Kafka are on, the queue stream
    /// worker. Call after <c>RegisterArivaServices</c> with <see cref="StreamQueueZones"/> in its messaging builder.
    /// </summary>
    public static IServiceCollection AddArivaQueueStream(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(StreamSettings.SectionName).Get<StreamSettings>() ?? new StreamSettings();
        // ARV-148: the zone settings every site runs under too (the worker's are the defaults), so a host with a site the
        // engine would refuse does not start.
        var problems = settings.Problems().Concat(settings.ZoneSettingsProblems(new Ariva.Core.Queueing.ZoneProcessorSettings())).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<StreamStore>();
        services.AddSingleton<ZoneGeometrySource>();
        services.AddSingleton<DeskTermSource>();
        // ARV-125a: the worker's consumer lag (meter Ariva.Stream).
        services.TryAddSingleton<StreamMetrics>();
        var kafka = configuration.GetSection(KafkaSettings.SectionName).Get<KafkaSettings>() ?? new KafkaSettings();
        if (settings.Enabled && kafka.Enabled)
            services.AddHostedService<QueueStreamWorker>();
        return services;
    }

    /// <summary>The sensing topics the queue worker reads raw, so that their dead-letter producers exist.</summary>
    public static ArivaMessagingBuilder StreamQueueZones(this ArivaMessagingBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        foreach (var topic in QueueStreamWorker.Topics)
            messaging.StreamFrom(topic);
        return messaging;
    }
}
