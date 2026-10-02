using System.Buffers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Ariva.Api.Common.Security;
using Ariva.Api.Common.Settings;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Sensing;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Ariva.Api.Ingest.Mqtt;

/// <summary>
/// The MQTT side of the ingest (ARV-024): an MQTTnet broker that only takes device data in.
/// <list type="bullet">
/// <item>CONNECT: the user name is the device code and the password its credential, checked exactly like an HTTPS push
/// (prefix lookup, constant-time hash, allowed networks, pinned client certificate) and only for devices registered on
/// the MQTT transport; the client id is the device code (or, with MQTT 5, empty and then assigned), so one device cannot
/// take over another's session. Attempts are limited per client address before any lookup. A will message is refused
/// before the CONNECT reaches the broker (<see cref="MqttPacketLimit"/>).</item>
/// <item>PUBLISH: only to <c>ariva/v1/devices/&lt;own code&gt;/&lt;dialect&gt;</c> (canonical, xovis or declarative), for
/// the device's own zone; anything else closes the connection. The device's access is checked again on every message, so
/// a rotated or retired credential, narrowed networks or a new certificate pin stop a live connection. Messages are never
/// routed or retained: each goes through the same ingest as an HTTPS push, under a per-device rate limit of the same
/// size (a device registered on MQTT cannot use the HTTPS push endpoints). A message the ingest refuses is acknowledged
/// with PayloadFormatInvalid (MQTT 5); when the events cannot be stored the connection is closed without an
/// acknowledgement, so the device sends the message again.</item>
/// <item>SUBSCRIBE: refused.</item>
/// </list>
/// </summary>
public sealed class MqttDeviceBroker : IHostedService, IDisposable
{
    public const string TopicPrefix = "ariva/v1/devices/";
    private const string DeviceItem = "ariva:device";
    private const string PrefixItem = "ariva:prefix";
    private const string AddressItem = "ariva:address";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<MqttDeviceBroker> _logger;
    private readonly PartitionedRateLimiter<string> _connects;
    private readonly PartitionedRateLimiter<Guid> _messages;
    private readonly bool _requireTls;
    private readonly MqttConnectionLimits _limits;

    public MqttDeviceBroker(MqttServer server, IServiceScopeFactory scopes, IOptions<MqttSettings> settings, IOptions<RateLimitingSettings> limits,
        TimeProvider time, ILogger<MqttDeviceBroker> logger, MqttConnectionLimits connections = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _requireTls = settings.Value.RequireTls;
        _limits = connections;
        var perAddress = settings.Value.ConnectsPerAddressPerMinute;
        _connects = PartitionedRateLimiter.Create<string, string>(address => RateLimitPartition.GetFixedWindowLimiter(address,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perAddress, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        var device = limits.Value.Device;
        _messages = PartitionedRateLimiter.Create<Guid, Guid>(id => RateLimitPartition.GetFixedWindowLimiter(id,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = device.PermitLimit, Window = TimeSpan.FromSeconds(device.WindowSeconds), QueueLimit = 0 }));

        server.ValidatingConnectionAsync += ValidateAsync;
        server.InterceptingPublishAsync += InterceptPublishAsync;
        server.InterceptingSubscriptionAsync += e =>
        {
            e.ProcessSubscription = false;
            e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
            return Task.CompletedTask;
        };
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _connects.Dispose();
        _messages.Dispose();
    }

    /// <summary>The outcome of a CONNECT: the reason code, and the device with its credential prefix when accepted.</summary>
    public sealed record Connect(MqttConnectReasonCode Code, DeviceCredentialRecord Device, string Prefix, string AssignedClientId);

    /// <summary>Where a connection came from, kept to check the device's access again on every message.</summary>
    public sealed record Origin(IPAddress Address, string CertificateSha256);

    /// <summary>The outcome of a PUBLISH: the acknowledgement's reason (MQTT 5), or a closed connection.</summary>
    public sealed record Publish(MqttPubAckReasonCode Code, string Reason, bool Close, DeviceCredentialRecord Device = null);

    private async Task ValidateAsync(ValidatingConnectionEventArgs e)
    {
        var address = (e.RemoteEndPoint as IPEndPoint)?.Address;
        Connect outcome;
        try
        {
            outcome = await AuthenticateAsync(e.ClientId, e.UserName, e.Password, address, e.ClientCertificate, e.IsSecureConnection, e.CancellationToken,
                assignable: e.ProtocolVersion == MQTTnet.Formatter.MqttProtocolVersion.V500);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // MQTTnet's own logger is off; a failure here is logged and the connection refused, never let through.
            _logger.LogError(exception, "MQTT connection from {Address} could not be checked", address);
            outcome = new Connect(MqttConnectReasonCode.ServerUnavailable, null, null, null);
        }

        e.ReasonCode = outcome.Code;
        if (outcome.Code != MqttConnectReasonCode.Success)
        {
            // MQTTnet replaces any refusal of a client without an id by ClientIdentifierNotValid; a placeholder keeps the
            // real reason. No session is created for a refused connection.
            if (string.IsNullOrEmpty(e.ClientId))
                e.AssignedClientIdentifier = "refused";
            return;
        }

        e.SessionItems[DeviceItem] = outcome.Device;
        e.SessionItems[PrefixItem] = outcome.Prefix;
        e.SessionItems[AddressItem] = Of(address, e.ClientCertificate);
        if (outcome.AssignedClientId is not null)
            e.AssignedClientIdentifier = outcome.AssignedClientId;
        _limits?.Accept(e.RemoteEndPoint);
    }

    /// <summary>The connection's address (IPv4 when mapped) and the SHA-256 of its client certificate, if any.</summary>
    public static Origin Of(IPAddress address, X509Certificate2 certificate) => new(
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address,
        certificate is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(certificate.RawData)));

    /// <summary>
    /// The CONNECT checks. <paramref name="assignable"/>: the protocol lets the server assign a client id (MQTT 5); with
    /// MQTT 3.1.1 the client id must be the device code.
    /// </summary>
    public async Task<Connect> AuthenticateAsync(string clientId, string userName, string password, IPAddress address, X509Certificate2 certificate, bool secure,
        CancellationToken ct, bool assignable = true)
    {
        var mapped = address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
        using var lease = _connects.AttemptAcquire(mapped?.ToString() ?? "unknown");
        if (!lease.IsAcquired)
            return Refused(null, mapped, "too many connection attempts from the address", MqttConnectReasonCode.ConnectionRateExceeded);
        if (_requireTls && !secure)
            return Refused(null, mapped, "not over TLS", MqttConnectReasonCode.NotAuthorized);
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
            return Refused(null, mapped, "no user name or password", MqttConnectReasonCode.BadUserNameOrPassword);

        await using var scope = _scopes.CreateAsyncScope();
        var check = await DeviceCredentialCheck.VerifyAsync(scope.ServiceProvider.GetRequiredService<ISvcDeviceGateway>(), password, userName, mapped,
            _ => Task.FromResult(certificate), ct);
        if (check.Device is null)
            return Refused(check.Prefix, mapped, check.Refusal, MqttConnectReasonCode.BadUserNameOrPassword);
        if (!string.Equals(check.Device.Transport, nameof(DeviceTransport.Mqtt), StringComparison.Ordinal))
            return Refused(check.Prefix, mapped, "the device is not registered for the MQTT transport", MqttConnectReasonCode.NotAuthorized);
        if (string.IsNullOrEmpty(clientId) ? !assignable : !string.Equals(clientId, check.Device.Code, StringComparison.Ordinal))
            return Refused(check.Prefix, mapped, "the client id is not the device code", MqttConnectReasonCode.ClientIdentifierNotValid);

        _logger.LogInformation("MQTT device {Device} connected from {Address}", check.Device.Code, mapped);
        return new Connect(MqttConnectReasonCode.Success, check.Device, check.Prefix, string.IsNullOrEmpty(clientId) ? check.Device.Code : null);
    }

    private Connect Refused(string prefix, IPAddress address, string reason, MqttConnectReasonCode code)
    {
        _logger.LogWarning("MQTT device connection refused for {CredentialPrefix} from {Address}: {Reason}", prefix ?? "(none)", address, reason);
        return new Connect(code, null, prefix, null);
    }

    private async Task InterceptPublishAsync(InterceptingPublishEventArgs e)
    {
        // Device data is never routed to subscribers (there are none) and never retained.
        e.ProcessPublish = false;
        var device = e.SessionItems[DeviceItem] as DeviceCredentialRecord;
        var prefix = e.SessionItems[PrefixItem] as string;
        var origin = e.SessionItems[AddressItem] as Origin;
        var outcome = await PublishAsync(device, prefix, e.ApplicationMessage.Topic, e.ApplicationMessage.Payload, e.CancellationToken, origin);
        e.CloseConnection = outcome.Close;
        e.Response.ReasonCode = outcome.Code;
        e.Response.ReasonString = outcome.Reason;
        if (outcome.Device is not null)
            e.SessionItems[DeviceItem] = outcome.Device;
    }

    public async Task<Publish> PublishAsync(DeviceCredentialRecord device, string prefix, string topic, ReadOnlySequence<byte> payload, CancellationToken ct,
        Origin origin = null)
    {
        if (device is null || prefix is null || origin is null)
            return new Publish(MqttPubAckReasonCode.NotAuthorized, null, true);
        if (!TryDialect(topic, device.Code, out var dialect))
        {
            _logger.LogWarning("MQTT device {Device} published outside its topic; connection closed", device.Code);
            return new Publish(MqttPubAckReasonCode.NotAuthorized, null, true);
        }

        await using var scope = _scopes.CreateAsyncScope();
        // The device's access is checked again (a cached lookup, evicted on every registry change): rotating or retiring the
        // credential, moving the device off MQTT, narrowing its networks or pinning a certificate it did not present ends
        // a live connection at its next message.
        var current = await scope.ServiceProvider.GetRequiredService<ISvcDeviceGateway>().FindByPrefixAsync(prefix, ct);
        if (current is null || current.DeviceId != device.DeviceId || !string.Equals(current.CredentialHash, device.CredentialHash, StringComparison.Ordinal) ||
            !string.Equals(current.Transport, nameof(DeviceTransport.Mqtt), StringComparison.Ordinal) ||
            (current.AllowedSources.Count > 0 && !DeviceCredentialCheck.FromAllowedNetwork(origin.Address, current.AllowedSources)) ||
            (current.ClientCertificateSha256 is { } pin && !string.Equals(pin, origin.CertificateSha256, StringComparison.Ordinal)))
        {
            _logger.LogWarning("MQTT device {Device}: access no longer valid; connection closed", device.Code);
            return new Publish(MqttPubAckReasonCode.NotAuthorized, null, true);
        }

        using var lease = _messages.AttemptAcquire(current.DeviceId);
        if (!lease.IsAcquired)
            return new Publish(MqttPubAckReasonCode.QuotaExceeded, "Rate limit exceeded.", false, current);
        if (payload.Length > IngestSettings.MaxBodyBytes)
            return new Publish(MqttPubAckReasonCode.PayloadFormatInvalid, "A message is at most 256 KB.", false, current);

        var ingest = scope.ServiceProvider.GetRequiredService<SensingIngest>();
        try
        {
            var result = await ingest.IngestAsync(current, dialect, payload.ToArray(), _time.GetUtcNow().UtcDateTime, ct);
            if (result.HasErrors)
            {
                var reason = result.ErrorMessages.First();
                return new Publish(MqttPubAckReasonCode.PayloadFormatInvalid, reason.Length <= 200 ? reason : reason[..200], false, current);
            }

            return new Publish(MqttPubAckReasonCode.Success, null, false, current);
        }
        catch (SensingSinkUnavailableException e)
        {
            _logger.LogError(e, "MQTT message from device {Device} could not be stored; connection closed so it is sent again", current.Code);
            return new Publish(MqttPubAckReasonCode.ImplementationSpecificError, null, true, current);
        }
    }

    /// <summary><c>ariva/v1/devices/&lt;code&gt;/&lt;dialect&gt;</c> for this device's code, with a dialect in lower case.</summary>
    public static bool TryDialect(string topic, string code, out DeviceDialect dialect)
    {
        dialect = default;
        if (topic is null || topic.Length > 128 || !topic.StartsWith(TopicPrefix, StringComparison.Ordinal))
            return false;
        var rest = topic[TopicPrefix.Length..].Split('/');
        if (rest.Length != 2 || !string.Equals(rest[0], code, StringComparison.Ordinal))
            return false;
        (var ok, dialect) = rest[1] switch
        {
            "canonical" => (true, DeviceDialect.Canonical),
            "xovis" => (true, DeviceDialect.Xovis),
            "declarative" => (true, DeviceDialect.Declarative),
            _ => (false, default)
        };
        return ok;
    }
}
