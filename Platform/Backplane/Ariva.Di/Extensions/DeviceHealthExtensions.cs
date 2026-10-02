using Ariva.Infra.Sensing;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Di.Extensions;

/// <summary>The heartbeat monitor of Ariva.Api.Main (ARV-025): sweeps for devices that lost their heartbeat.</summary>
public static class DeviceHealthExtensions
{
    public static IServiceCollection AddArivaDeviceHealthMonitor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<DeviceHeartbeatMonitor>();
        return services;
    }

    /// <summary>Consumes <c>ariva.device.health.v1</c> into device heartbeats (ARV-025).</summary>
    public static ArivaMessagingBuilder ConsumeDeviceHealth(this ArivaMessagingBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        return messaging.Consume<Ariva.Core.Sensing.DeviceHealthReported, DeviceHealthConsumer>(Ariva.Core.Messaging.KafkaTopics.DeviceHealth, "device-health");
    }
}
