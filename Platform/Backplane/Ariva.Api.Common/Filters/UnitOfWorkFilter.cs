using System.Text.Json;
using Ariva.Api.Common.Logging;
using Ariva.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
    /// <summary>The model state key that replaces a JSON path (<c>$.x</c>, <c>$['x']</c>) in a 400 answer.</summary>
    public const string BodyErrorKey = "body";

    /// <summary>
    /// MVC controllers with the Ariva filters: no NUL character in any input (ARV-063, <see cref="NulCharacterFilter"/>
    /// and <see cref="NulRejectingStringConverter"/>), no form read for an action that binds none (ARV-096c,
    /// <see cref="UnboundFormFilter"/>), no query parameter sent twice (ARV-089, <see cref="RepeatedQueryFilter"/>;
    /// the JSON itself is read strictly through <see cref="StrictJson"/>, set with the request limits), the unit of work, and
    /// 400 answers that never repeat what a caller sent: model binding messages without the attempted value
    /// (<see cref="WithoutAttemptedValues"/>), JSON reader messages replaced by a fixed text
    /// (<c>AllowInputFormatterExceptionMessages</c> false: System.Text.Json's message quotes the path, and a path holds the
    /// caller's member names) and JSON paths replaced by <see cref="BodyErrorKey"/> (<see cref="WithoutBodyPaths"/>).
    /// </summary>
    public static IMvcBuilder AddAppControllers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddControllers(options =>
            {
                // Instances, so MVC takes each filter's own order (a filter added by type is wrapped in one of order 0).
                options.Filters.Add(new RepeatedQueryFilter());
                options.Filters.Add(new UnboundFormFilter());
                options.Filters.Add(new NulCharacterFilter());
                // TEMPORARY until ARV-089a: the frozen endpoints' NUL check at its earlier position (order 0, before the unit of work).
                options.Filters.Add(new NulCharacterFilter(lenientInput: true));
                options.Filters.Add<UnitOfWorkFilter>();
                WithoutAttemptedValues(options.ModelBindingMessageProvider);
            })
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.Converters.Add(new NulRejectingStringConverter());
                json.AllowInputFormatterExceptionMessages = false;
            })
            .ConfigureApiBehaviorOptions(api => api.InvalidModelStateResponseFactory = ValidationProblemWithoutBodyPaths);
    }

    /// <summary>
    /// The 400 answer of an invalid model state (<c>[ApiController]</c>), as ASP.NET Core's default builds it (ProblemDetails
    /// from the host's factory, <c>application/problem+json</c>), with the state's JSON paths replaced
    /// (<see cref="WithoutBodyPaths"/>). ARV-091: the refusal is a security event, <see cref="SecurityEvents.StrictJsonRefused"/>
    /// when the JSON reader refused the body (an unknown or repeated member, a number as text, JSON that does not parse; ARV-089),
    /// <see cref="SecurityEvents.NulCharacterRefused"/> for a NUL character in a JSON string (ARV-063) and
    /// <see cref="SecurityEvents.InvalidModelRefused"/> otherwise, with no key, message or value of the state.
    /// </summary>
    public static IActionResult ValidationProblemWithoutBodyPaths(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.RequestServices?.GetService<SecurityEventLog>() is { } events)
        {
            if (RefusedByTheJsonReader(context.ModelState))
                events.StrictJson(context.HttpContext);
            else if (HoldsNulRefusal(context.ModelState))
                events.NulCharacter(context.HttpContext);
            else
                events.InvalidModel(context.HttpContext);
        }

        var factory = context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, WithoutBodyPaths(context.ModelState));
        ObjectResult result = problem.Status == StatusCodes.Status400BadRequest
            ? new BadRequestObjectResult(problem)
            : new ObjectResult(problem) { StatusCode = problem.Status };
        result.ContentTypes.Add("application/problem+json");
        result.ContentTypes.Add("application/problem+xml");
        return result;
    }

    /// <summary>True when an error of the state is the JSON reader's own (System.Text.Json's <see cref="JsonException"/>).</summary>
    public static bool RefusedByTheJsonReader(ModelStateDictionary state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Values.Any(entry => entry.Errors.Any(error => error.Exception is JsonException));
    }

    /// <summary>True when an error of the state is the NUL refusal of a JSON string (<see cref="NulRejectingStringConverter"/>, a fixed text).</summary>
    public static bool HoldsNulRefusal(ModelStateDictionary state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Values.Any(entry => entry.Errors.Any(error => string.Equals(error.ErrorMessage, NulRejectingStringConverter.NulMessage, StringComparison.Ordinal)));
    }

    /// <summary>
    /// ARV-104b, first security review (CWE-501, CWE-79): System.Text.Json names a body error by its JSON path, and a path
    /// quotes the member names the caller sent (<c>$['&lt;script&gt;']</c>), so every key that starts with <c>$</c> becomes
    /// <see cref="BodyErrorKey"/>. Messages stay as they are: the reader's own are already the fixed "The input was not valid."
    /// (<c>AllowInputFormatterExceptionMessages</c> false), and Ariva's converters only say which rule failed.
    /// </summary>
    public static ModelStateDictionary WithoutBodyPaths(ModelStateDictionary state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var safe = new ModelStateDictionary(state.MaxAllowedErrors);
        foreach (var (key, entry) in state)
        {
            var target = key.StartsWith('$') ? BodyErrorKey : key;
            foreach (var error in entry.Errors)
            {
                if (string.IsNullOrEmpty(error.ErrorMessage) && error.Exception is not null)
                    safe.TryAddModelException(target, error.Exception);
                else
                    safe.TryAddModelError(target, error.ErrorMessage);
            }
        }

        return safe;
    }

    /// <summary>
    /// ARV-104b (CWE-501, CWE-79): ASP.NET Core's default binding messages quote the value that failed to bind ("The value
    /// '&lt;script&gt;' is not valid for ZoneId."), so a query or route value that is not a number, a GUID or a date came
    /// back in the 400 answer. These messages name the field only; field names come from the request models, never from
    /// the caller.
    /// </summary>
    public static void WithoutAttemptedValues(Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.DefaultModelBindingMessageProvider messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        messages.SetAttemptedValueIsInvalidAccessor((_, field) => $"The value is not valid for {field}.");
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => "The value is not valid.");
        messages.SetValueIsInvalidAccessor(_ => "The value is invalid.");
        messages.SetValueMustNotBeNullAccessor(_ => "The value is invalid.");
    }
}
