using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Api.Common.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-063 (CWE-20): no NUL character (U+0000) reaches a service. PostgreSQL refuses NUL in text, so a NUL that reached a
/// query or an insert answered 500; the dynamic scan found it with <c>siteCode=%00</c>. Kestrel already refuses NUL in
/// the path; this filter answers 400 for one in the query string, a route value or a form field, and
/// <see cref="NulRejectingStringConverter"/> makes one in a JSON string a model binding error (400 through
/// <c>[ApiController]</c>). Runs after authentication and authorization, so an anonymous caller still gets 401. Registered as an
/// instance (ARV-096c), so MVC takes its <see cref="Order"/>: registered by type it was wrapped in a filter of order 0 and ran after
/// <c>[ApiController]</c>'s invalid model filter (order -2000), which answered a NUL in an otherwise invalid upload as an invalid model.
/// </summary>
/// <param name="lenientInput">
/// TEMPORARY until ARV-089a: true for the instance that serves only the endpoints the login freeze keeps as they are
/// (<see cref="LenientInputMetadata"/>), at the position the filter had before ARV-096c (order 0), so their answers do not change;
/// false for the instance that serves every other endpoint, before every other action filter.
/// </param>
public sealed class NulCharacterFilter(bool lenientInput = false) : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>
    /// Before every other action filter (the site scope filter and <c>[ApiController]</c>'s invalid model filter among them); the
    /// frozen endpoints' instance at order 0, as before ARV-096c.
    /// </summary>
    public int Order => lenientInput ? 0 : int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.ActionDescriptor.EndpointMetadata.OfType<LenientInputMetadata>().Any() != lenientInput)
        {
            await next();
            return;
        }

        if (await HoldsNulAsync(context.HttpContext.Request))
        {
            // ARV-091: the refusal is a security event, without where the character was or the value around it.
            context.HttpContext.RequestServices?.GetService<SecurityEventLog>()?.NulCharacter(context.HttpContext);
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad request",
                Detail = "The request contains a NUL character."
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" }
            };
            return;
        }

        await next();
    }

    internal static async Task<bool> HoldsNulAsync(HttpRequest request)
    {
        if (request.Query.Any(pair => pair.Key.Contains('\0', StringComparison.Ordinal) || pair.Value.Any(Holds)))
            return true;
        if (request.RouteValues.Values.OfType<string>().Any(Holds))
            return true;
        if (request.HasFormContentType)
        {
            // Model binding has read the form already when an action takes it; the form limits apply either way.
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            if (form.Any(pair => pair.Key.Contains('\0', StringComparison.Ordinal) || pair.Value.Any(Holds)) ||
                form.Files.Any(file => Holds(file.FileName) || Holds(file.Name)))
                return true;
        }

        return false;
    }

    private static bool Holds(string value) => value is not null && value.Contains('\0', StringComparison.Ordinal);
}

/// <summary>
/// Reads JSON strings like the default converter, but refuses one that holds a NUL character (ARV-063): the request
/// model is then invalid and the controller answers 400 before any service runs. The NUL refusal is an
/// <see cref="InputFormatterException"/>, the framework's mark of a message safe to return (its text is fixed), so it
/// survives <c>AllowInputFormatterExceptionMessages</c> false (ARV-104b), which turns every other JSON reader message, the
/// wrong token type here included, into the fixed "The input was not valid.".
/// </summary>
public sealed class NulRejectingStringConverter : JsonConverter<string>
{
    /// <summary>The answer to a NUL character in a JSON string.</summary>
    public const string NulMessage = "A string must not contain a NUL character.";

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string, got {reader.TokenType}.");
        var value = reader.GetString();
        if (value is not null && value.Contains('\0', StringComparison.Ordinal))
            throw new InputFormatterException(NulMessage);
        return value;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}
