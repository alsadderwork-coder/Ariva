using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.UnitTests.Logging.Fixtures;

/// <summary>
/// The address a certificate names for its issuer (the authority information access extension), on loopback over plain HTTP,
/// for the certificate download tests (ARV-200, CWE-918): it keeps the path of every request, so a test can tell whether
/// anything fetched it, and answers 404 (it hands out no certificate, so nothing a chain fetched is kept anywhere).
/// </summary>
public sealed class IssuerSite : IAsyncDisposable
{
    #region Fields

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _requests = new();

    #endregion

    #region Constructors

    private IssuerSite(WebApplication app)
    {
        _app = app;
        _app.Run(context =>
        {
            _requests.Enqueue(context.Request.Path.ToString());
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    #endregion

    #region Properties

    /// <summary>The address of the issuer's certificate, for example <c>http://127.0.0.1:41234/issuer.cer</c>.</summary>
    public Uri Address { get; private set; }

    /// <summary>The paths of the requests it received.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    #endregion

    #region Methods

    public static async Task<IssuerSite> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var site = new IssuerSite(builder.Build());
        await site._app.StartAsync(cancellationToken);
        site.Address = new Uri($"http://127.0.0.1:{new Uri(site._app.Urls.First()).Port}/issuer.cer");
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    #endregion
}
