using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-063 (CWE-20): no NUL character (U+0000) reaches a service. PostgreSQL refuses NUL in text, so a NUL that reached a
/// query or an insert answered 500; the dynamic scan found it with <c>siteCode=%00</c>. Kestrel already refuses NUL in
/// the path; this filter answers 400 for one in the query string, a route value or a form field, and
/// <see cref="NulRejectingStringConverter"/> makes one in a JSON string a model binding error (400 through
/// <c>[ApiController]</c>). Runs after authentication and authorization, so an anonymous caller still gets 401.
/// </summary>
public sealed class NulCharacterFilter : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>Before every other action filter (the site scope filter among them).</summary>
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (await HoldsNulAsync(context.HttpContext.Request))
        {
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
/// model is then invalid and the controller answers 400 before any service runs.
/// </summary>
public sealed class NulRejectingStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string, got {reader.TokenType}.");
        var value = reader.GetString();
        if (value is not null && value.Contains('\0', StringComparison.Ordinal))
            throw new JsonException("A string must not contain a NUL character.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}
