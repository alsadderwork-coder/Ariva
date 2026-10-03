using System.Collections.Concurrent;
using Ariva.Core.Domain.Enums;

namespace Ariva.Infra.Integration.Outbound;

/// <summary>
/// The HttpClient of each outbound endpoint (ARV-045): built from its record, and rebuilt when the record changes
/// (<see cref="OutboundTarget.Version"/>), so a URL comes only from the registry, never from a caller. The chain is
/// resilience, redirect refusal, authentication, then the guarded transport (<see cref="OutboundTransport"/>); every
/// answer is capped at <see cref="OutboundSettings.MaxResponseBytes"/>. Secrets are unprotected here, in the calling host only.
/// </summary>
public sealed class OutboundClients(OutboundSecrets secrets, OutboundSettings settings, IOutboundResolver resolver, TimeProvider time) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, (int Version, HttpClient Client, OutboundResilienceHandler Resilience, IDisposable Certificate)> _clients = new();
    private readonly Lock _building = new();

    public HttpClient For(OutboundTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_clients.TryGetValue(target.Id, out var known) && known.Version == target.Version)
            return known.Client;
        lock (_building)
        {
            if (_clients.TryGetValue(target.Id, out known) && known.Version == target.Version)
                return known.Client;
            var built = Build(target);
            _clients[target.Id] = built;
            if (known.Client is { } replaced)
                Retire(replaced, known.Certificate);
            return built.Client;
        }
    }

    // A replaced client (the endpoint changed) finishes its calls, then goes with its handler, certificate and secret.
    private void Retire(HttpClient client, IDisposable certificate) =>
        _ = Task.Delay(client.Timeout + TimeSpan.FromSeconds(5), time).ContinueWith(_ =>
        {
            client.Dispose();
            certificate?.Dispose();
        }, TaskScheduler.Default);

    /// <summary>Whether the endpoint's circuit is open (for status reporting).</summary>
    public bool IsOpen(Guid id) => _clients.TryGetValue(id, out var known) && known.Resilience.IsOpen;

    private (int Version, HttpClient Client, OutboundResilienceHandler Resilience, IDisposable Certificate) Build(OutboundTarget target)
    {
        // The calling host checks the scheme again: plain HTTP only to a lab host it lists itself (a record changed in the
        // database cannot make it call in clear).
        if (target.BaseUrl.Scheme != Uri.UriSchemeHttps && !(target.BaseUrl.Scheme == Uri.UriSchemeHttp && settings.LabHostSet.Contains(target.BaseUrl.IdnHost)))
            throw new OutboundRefusedException($"The endpoint {target.Code} is not HTTPS.");
        var secret = secrets.Read(target.SecretProtected);
        var certificate = target.AuthKind == OutboundAuthKind.MutualTls ? secret.Certificate() : null;
        HttpMessageHandler inner = OutboundTransport.Create(target, resolver, settings.AllowLoopback, certificate);
        inner = target.AuthKind switch
        {
            OutboundAuthKind.TotpClientCredentials or OutboundAuthKind.OAuth2ClientCredentials => new TokenHandler(target, secret, time) { InnerHandler = inner },
            OutboundAuthKind.ApiKeyHeader => new ApiKeyHeaderHandler(target.HeaderName, secret.ApiKey) { InnerHandler = inner },
            OutboundAuthKind.HmacSignature => new HmacSignatureHandler(target.KeyId, Convert.FromBase64String(secret.HmacKey), time) { InnerHandler = inner },
            _ => inner
        };
        var resilience = new OutboundResilienceHandler(target, time) { InnerHandler = new OutboundRedirectGuard { InnerHandler = inner } };
        var client = new HttpClient(resilience, disposeHandler: true)
        {
            BaseAddress = target.BaseUrl,
            Timeout = TimeSpan.FromSeconds(target.TimeoutSeconds * (target.RetryCount + 1) + 15),
            MaxResponseContentBufferSize = settings.MaxResponseBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ariva/1.0");
        return (target.Version, client, resilience, certificate);
    }

    public void Dispose()
    {
        foreach (var (_, client, _, certificate) in _clients.Values)
        {
            client.Dispose();
            certificate?.Dispose();
        }
        _clients.Clear();
    }
}
