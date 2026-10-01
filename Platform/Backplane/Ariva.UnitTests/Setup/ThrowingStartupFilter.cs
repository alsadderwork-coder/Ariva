using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Appends a terminal middleware after the host's own pipeline that throws for <see cref="Path"/>, so tests can prove
/// what an unhandled exception looks like to a client. Requests reach it only when no endpoint matched and the
/// caller passed authorization.
/// </summary>
public sealed class ThrowingStartupFilter : IStartupFilter
{
    #region Constants

    /// <summary>The path that throws.</summary>
    public const string Path = "/tests/throw";

    /// <summary>Exception message that looks like a leaked secret; it must never reach a client outside vm-local.</summary>
    public const string SecretMessage = "Host=timescaledb;Username=ariva;Password=not-for-clients";

    #endregion

    #region IStartupFilter

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.Run(context =>
        {
            if (context.Request.Path == Path)
            {
                throw new InvalidOperationException(SecretMessage);
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    };

    #endregion
}
