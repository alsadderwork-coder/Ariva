using Apizr;
using Apizr.Logging;
using Microsoft.Extensions.Logging;
using Refit;

namespace Ariva.LoadTests;

/// <summary>A device's push to Ariva.Api.Ingest (ARV-022), as a sensor sends it.</summary>
public interface IIngestApi
{
    /// <summary>The raw answer: the harness records every status, 413 and 429 included, and reads Retry-After.</summary>
    [Post("/api/v1/ingest/zones/{zone}/events")]
    [Headers("Content-Type: application/json")]
    Task<HttpResponseMessage> PushAsync(string zone, [Body] Stream body, [Header("X-Ariva-Device-Key")] string deviceKey, CancellationToken ct);
}

/// <summary>A display player's board on Ariva.Api.Main (ARV-058).</summary>
public interface IDisplayApi
{
    [Get("/api/v1/display/board")]
    Task<HttpResponseMessage> BoardAsync([AliasAs("code")] string code, [Header("X-Ariva-Display-Key")] string displayKey, CancellationToken ct);
}

/// <summary>
/// The harness's HTTP clients, built by Apizr (product owner's choice, 2026-10-04) over typed Refit interfaces instead of
/// a hand-built HttpClient. Logging is off and no resilience pipeline or cache is configured: a load client must not
/// retry, cache or hold requests, or it would hide the answers it measures. Calls go through each manager's Refit client
/// (<see cref="IApizrManager{TWebApi}.Api"/>), which carries the configured handler and timeout, rather than through
/// ExecuteAsync, whose expression handling would spend the client's CPU on the same machine as the hosts measured.
/// </summary>
public static class ArivaApis
{
    /// <summary>Concurrent connections to one host: 40 sensors, their bursts and 50 displays at once.</summary>
    private const int MaxConnectionsPerServer = 512;

    public static IIngestApi Ingest(Uri baseAddress) => Create<IIngestApi>(baseAddress);

    public static IDisplayApi Display(Uri baseAddress) => Create<IDisplayApi>(baseAddress);

    private static TApi Create<TApi>(Uri baseAddress) => ApizrBuilder.Current.CreateManagerFor<TApi>(options => options
        .WithBaseAddress(baseAddress)
        .WithHttpClientHandler(new HttpClientHandler { MaxConnectionsPerServer = MaxConnectionsPerServer, AllowAutoRedirect = false })
        .WithRequestTimeout(TimeSpan.FromSeconds(30))
        .WithLogging(HttpTracerMode.ExceptionsOnly, HttpMessageParts.None, LogLevel.None)).Api;
}
