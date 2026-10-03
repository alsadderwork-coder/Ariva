using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Messaging;
using Ariva.Core.Services.Border;
using Ariva.Infra.Border;
using Ariva.Infra.Messaging.Inbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>The AMAN feed and immigration endpoints (ARV-048), in Ariva.Api.Integration.</summary>
public static class BorderExtensions
{
    /// <summary>The immigration intake, its settings (<c>Border:Feed</c>, checked at start), metrics and the AMAN records' inbox keys.</summary>
    public static IServiceCollection AddArivaBorderFeed(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(BorderFeedSettings.SectionName).Get<BorderFeedSettings>() ?? new BorderFeedSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BorderMetrics>();
        services.TryAddScoped<ISvcImmigrationIntake, Ariva.Infra.Services.Border.SvcImmigrationIntake>();
        services.TryAddSingleton<AmanInboxKeys>();
        services.TryAddSingleton<IInboxKey<DeskSessionChanged>>(provider => provider.GetRequiredService<AmanInboxKeys>());
        services.TryAddSingleton<IInboxKey<DeskIntervalStats>>(provider => provider.GetRequiredService<AmanInboxKeys>());
        services.TryAddSingleton<IInboxKey<EGateIntervalStats>>(provider => provider.GetRequiredService<AmanInboxKeys>());
        services.TryAddSingleton<IInboxKey<InboundFlightLaneDemand>>(provider => provider.GetRequiredService<AmanInboxKeys>());
        return services;
    }

    /// <summary>Consumes AMAN's four feed topics (<c>aman.feed.*.v1</c>), each with its own consumer group, read strictly.</summary>
    public static ArivaMessagingBuilder ConsumeAmanFeed(this ArivaMessagingBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        return messaging
            .Consume<DeskSessionChanged, AmanDeskSessionConsumer>(KafkaTopics.AmanDeskSessionChanged, "aman-desk-sessions", new AmanFeedJson<DeskSessionChanged>())
            .Consume<DeskIntervalStats, AmanDeskIntervalConsumer>(KafkaTopics.AmanDeskIntervalStats, "aman-desk-intervals", new AmanFeedJson<DeskIntervalStats>())
            .Consume<EGateIntervalStats, AmanEgateIntervalConsumer>(KafkaTopics.AmanEgateIntervalStats, "aman-egate-intervals", new AmanFeedJson<EGateIntervalStats>())
            .Consume<InboundFlightLaneDemand, AmanLaneDemandConsumer>(KafkaTopics.AmanInboundFlightLaneDemand, "aman-lane-demand",
                new AmanFeedJson<InboundFlightLaneDemand>());
    }
}
