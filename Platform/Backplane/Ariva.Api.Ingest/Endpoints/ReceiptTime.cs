namespace Ariva.Api.Ingest.Endpoints;

/// <summary>
/// The receipt time of a request (F19, ARV-064): Ariva's clock when the request arrived, taken by the first middleware,
/// before client certificates, authentication and the body. A device's clock offset is its send time against this, so
/// the time Ingest spends checking a credential (slow the first time, before the cache holds it) is never read as the
/// device's clock running behind, which would keep its readings Unreliable for the next ten pushes.
/// </summary>
public static class ReceiptTime
{
    private sealed record Feature(DateTime ReceivedUtc);

    /// <summary>Records the arrival time of every request.</summary>
    public static IApplicationBuilder UseReceiptTime(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var time = app.ApplicationServices.GetService<TimeProvider>() ?? TimeProvider.System;
        return app.Use((context, next) =>
        {
            context.Features.Set(new Feature(time.GetUtcNow().UtcDateTime));
            return next(context);
        });
    }

    /// <summary>When the request arrived, or null when the middleware did not run.</summary>
    public static DateTime? Of(HttpContext context) => context?.Features.Get<Feature>()?.ReceivedUtc;
}
