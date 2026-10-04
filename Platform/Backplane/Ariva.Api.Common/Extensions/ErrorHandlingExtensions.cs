using Ariva.Api.Common.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Error responses as RFC 9457 ProblemDetails, without exception details outside vm-local (CWE-209, information
/// exposure through error messages).
/// </summary>
public static class ErrorHandlingExtensions
{
    #region Constants

    private const string ExceptionExtension = "exception";

    #endregion

    #region Services

    /// <summary>
    /// Registers the ProblemDetails service. Unhandled exceptions become 500 (or the status of a
    /// <see cref="BadHttpRequestException"/>, for example 413; 503 with Retry-After when PostgreSQL or Redis is
    /// unavailable, <see cref="DependencyOutageHandler"/>) with a generic title and a trace id; only in
    /// vm-local the exception message and stack trace are added, for developers.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppErrorHandling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails(options => options.CustomizeProblemDetails = CustomizeProblemDetails);
        // ARV-072: a dependency outage answers 503 with Retry-After, not 500 (DependencyOutageHandler).
        services.AddExceptionHandler<DependencyOutageHandler>();

        return services;
    }

    #endregion

    #region Middlewares

    /// <summary>
    /// Adds the exception handler and the status code pages middleware, both writing ProblemDetails. Status code
    /// pages give empty 401, 403, 404, 405, 413 and 429 answers a ProblemDetails body without stack traces.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseErrorHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError
        });
        app.UseStatusCodePages();

        return app;
    }

    #endregion

    #region Helpers

    private static void CustomizeProblemDetails(ProblemDetailsContext context)
    {
        context.ProblemDetails.Extensions.Remove(ExceptionExtension);

        if (context.Exception is null)
        {
            return;
        }

        var environment = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (environment.IsVmLocal())
        {
            context.ProblemDetails.Detail = context.Exception.Message;
            context.ProblemDetails.Extensions[ExceptionExtension] = context.Exception.ToString();
            return;
        }

        context.ProblemDetails.Detail = null;
    }

    #endregion
}
