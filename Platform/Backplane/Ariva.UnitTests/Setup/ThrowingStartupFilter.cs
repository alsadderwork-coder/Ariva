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

    /// <summary>ARV-072: paths that throw what a dependency outage throws (PostgreSQL wrapped by NHibernate, Redis, a timeout), and a ReDoS guard's timeout.</summary>
    public const string OutagePath = "/tests/outage/";

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

            if (context.Request.Path.StartsWithSegments(OutagePath.TrimEnd('/'), out var rest))
            {
                throw rest.Value switch
                {
                    "/postgres" => new NHibernate.Exceptions.GenericADOException(SecretMessage,
                        new Npgsql.NpgsqlException(SecretMessage, new System.Net.Sockets.SocketException(111))),
                    "/redis" => new StackExchange.Redis.RedisConnectionException(StackExchange.Redis.ConnectionFailureType.UnableToConnect, SecretMessage),
                    "/timeout" => new TimeoutException(SecretMessage),
                    "/regex" => new System.Text.RegularExpressions.RegexMatchTimeoutException(SecretMessage),
                    _ => new InvalidOperationException(SecretMessage)
                };
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    };

    #endregion
}
