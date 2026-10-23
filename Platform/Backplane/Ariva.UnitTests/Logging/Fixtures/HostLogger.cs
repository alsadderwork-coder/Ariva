using Ariva.Api.Common.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;

namespace Ariva.UnitTests.Logging.Fixtures;

/// <summary>
/// A logger built as a host builds it (<c>AddAppLogging</c>): the collector clients registered first, then the logger with the
/// host's services, so a collector behind a site CA (ARV-200) or reached over https (ARV-226) gets its pinned client. The logger
/// is disposed before the services, as the host does, so its last batch still goes out.
/// </summary>
internal sealed class HostLogger : IDisposable
{
    #region Fields

    private readonly ServiceProvider _services;

    #endregion

    #region Constructors

    private HostLogger(ServiceProvider services, Logger logger)
    {
        _services = services;
        Logger = logger;
    }

    #endregion

    #region Properties

    public Logger Logger { get; }

    #endregion

    #region Methods

    /// <summary>The host's logger for <paramref name="configuration"/> in <paramref name="environment"/>; its refusals are the host's.</summary>
    public static HostLogger Create(IConfiguration configuration, string environment)
    {
        var collection = new ServiceCollection();
        ArivaLogging.AddCollectorClients(collection, configuration);
        var services = collection.BuildServiceProvider();
        try
        {
            return new HostLogger(services, ArivaLogging.Configure(new LoggerConfiguration(), configuration, environment, null, services).CreateLogger());
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    /// <summary>Builds the host's logger and disposes it at once: what a test of the refusals needs.</summary>
    public static void Build(IConfiguration configuration, string environment)
    {
        using var logger = Create(configuration, environment);
    }

    public void Dispose()
    {
        Logger.Dispose();
        _services.Dispose();
    }

    #endregion
}
