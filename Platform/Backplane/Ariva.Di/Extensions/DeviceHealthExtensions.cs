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

/// <summary>The raw sensing event archive (ARV-026).</summary>
public static class SensingArchiveExtensions
{
    public static IServiceCollection AddArivaSensingArchive(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<Ariva.Core.Sensing.ISensingArchive, SensingArchive>();
        services.AddSingleton<Ariva.Core.Sensing.IDeviceHealthArchive, DeviceHealthArchive>();
        return services;
    }

    /// <summary>
    /// Archives the four sensing topics and the device health topic (ARV-036), one consumer group each
    /// (<c>ariva-stream.sensing-archive-*</c>, <c>ariva-stream.health-archive</c>).
    /// </summary>
    public static ArivaMessagingBuilder ConsumeSensingArchive(this ArivaMessagingBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        return messaging
            .Consume<Ariva.Core.Sensing.TrackSampleBatch, SensingArchiveConsumer<Ariva.Core.Sensing.TrackSampleBatch>>(Ariva.Core.Messaging.KafkaTopics.DeviceTrackSample, "sensing-archive-tracks")
            .Consume<Ariva.Core.Sensing.VendorLineCrossingBatch, SensingArchiveConsumer<Ariva.Core.Sensing.VendorLineCrossingBatch>>(Ariva.Core.Messaging.KafkaTopics.DeviceVendorLineCrossing, "sensing-archive-crossings")
            .Consume<Ariva.Core.Sensing.ZoneOccupancyBatch, SensingArchiveConsumer<Ariva.Core.Sensing.ZoneOccupancyBatch>>(Ariva.Core.Messaging.KafkaTopics.DeviceZoneOccupancy, "sensing-archive-occupancy")
            .Consume<Ariva.Core.Sensing.IntervalCountBatch, SensingArchiveConsumer<Ariva.Core.Sensing.IntervalCountBatch>>(Ariva.Core.Messaging.KafkaTopics.DeviceIntervalCount, "sensing-archive-intervals")
            .Consume<Ariva.Core.Sensing.DeviceHealthReported, DeviceHealthArchiveConsumer>(Ariva.Core.Messaging.KafkaTopics.DeviceHealth, "health-archive");
    }
}
