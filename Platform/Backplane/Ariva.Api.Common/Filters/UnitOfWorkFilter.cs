using Ariva.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// Ends the request's unit of work when the action has run (AMAN's unit of work filter): commits what a command
/// promised, rolls back on an exception. Running before the response is written means a failed commit becomes a 500
/// instead of a 200 for data that was never saved.
/// </summary>
public sealed class UnitOfWorkFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var executed = await next();
        var unitOfWork = context.HttpContext.RequestServices.GetService<IUnitOfWork>();
        if (unitOfWork is null)
            return;

        if (executed.Exception is not null && !executed.ExceptionHandled)
            await unitOfWork.RollbackAsync();
        else
            await unitOfWork.EndAsync(CancellationToken.None); // a commit is not cancelled by a client that went away
    }
}

public static class ControllerExtensions
{
    /// <summary>
    /// MVC controllers with the Ariva filters: no NUL character in any input (ARV-063, <see cref="NulCharacterFilter"/>
    /// and <see cref="NulRejectingStringConverter"/>) and the unit of work.
    /// </summary>
    public static IMvcBuilder AddAppControllers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddControllers(options =>
            {
                options.Filters.Add<NulCharacterFilter>();
                options.Filters.Add<UnitOfWorkFilter>();
            })
            .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new NulRejectingStringConverter()));
    }
}
