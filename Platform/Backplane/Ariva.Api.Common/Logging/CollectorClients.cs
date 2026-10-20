using Ariva.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// ARV-200: the log sinks' clients for a collector whose CA file is set (<see cref="CollectorTrust.LokiSetting"/>,
/// <see cref="CollectorTrust.OtlpSetting"/>), pinned to that CA (<see cref="CollectorTrust.Pin"/>) in a client factory of their
/// own. The host's factory gives every client the standard resilience pipeline, whose telemetry logs through the host's logger
/// factory: a client taken from it while Serilog's logger is being built builds that logger again, a recursion that stopped every
/// k8s-prd host with a collector CA. This factory has no pipeline and no logger, so the logger takes its clients at once, as the
/// sinks made their own clients before (they batch and retry by themselves); the host disposes it after the logger, so the last
/// batch is still sent. It holds the pinned clients only.
/// </summary>
public sealed class CollectorClients : IDisposable
{
    #region Fields

    private readonly ServiceProvider _provider;

    #endregion

    #region Constructors

    private CollectorClients(ServiceProvider provider) => _provider = provider;

    #endregion

    #region Methods

    /// <summary>
    /// The pinned clients the configuration's CA files call for, or null when it sets none. A CA file that cannot serve stops the
    /// host here, before any sink exists (<see cref="CollectorTrust.RootsFrom"/>).
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    public static CollectorClients From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var services = new ServiceCollection();
        var pinned = false;
        foreach (var (setting, client) in new[] { (CollectorTrust.LokiSetting, CollectorTrust.LokiClient), (CollectorTrust.OtlpSetting, CollectorTrust.OtlpLogsClient) })
        {
            if (CollectorTrust.RootsFrom(configuration, setting) is not { } roots)
                continue;
            CollectorTrust.Pin(services, roots, [client]);
            pinned = true;
        }

        return pinned ? new CollectorClients(services.BuildServiceProvider()) : null;
    }

    /// <summary>True when <paramref name="client"/> is one of the pinned clients here.</summary>
    /// <param name="client">The client's name.</param>
    public bool IsPinned(string client) => CollectorTrust.IsPinned(_provider, client);

    /// <summary>The pinned client of that name; any other name is refused (a factory would hand out an unpinned one).</summary>
    /// <param name="client">The client's name.</param>
    /// <exception cref="InvalidOperationException">No client of that name is pinned here.</exception>
    public HttpClient CreateClient(string client) => _provider.GetRequiredService<IHttpClientFactory>().CreateClient(Pinned(client));

    /// <summary>The pinned handler of that name, which the factory owns; any other name is refused.</summary>
    /// <param name="client">The client's name.</param>
    /// <exception cref="InvalidOperationException">No client of that name is pinned here.</exception>
    public HttpMessageHandler CreateHandler(string client) => _provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(Pinned(client));

    public void Dispose() => _provider.Dispose();

    private string Pinned(string client) =>
        IsPinned(client) ? client : throw new InvalidOperationException($"No collector client {client} is pinned: only a pinned client is handed out.");

    #endregion
}
