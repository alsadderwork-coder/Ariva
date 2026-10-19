using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-089 (ASVS 5.0 V15.3.7, HTTP parameter pollution; CWE-20): a query parameter sent more than once answers 400 before
/// model binding, unless the action binds a collection from the query under that name (a <c>[FromQuery]</c> parameter by
/// its name or binder name, or a property of a complex query model, with or without the parameter's prefix). Model binding
/// takes the first of repeated values for a single value while a proxy, a log or another reader may take the last, so a
/// repetition is refused instead of being read one way here and another way there. Names compare ignoring case, as the
/// query collection does. The answer is a fixed text that never repeats the key or a value. It runs after authentication
/// and authorization (endpoint routing), so an anonymous caller still gets 401 and a caller without the permission 403.
/// The endpoints the login freeze keeps lenient until ARV-089a (<see cref="LenientInputMetadata"/>) are left alone.
/// </summary>
public sealed class RepeatedQueryFilter : IResourceFilter, IOrderedFilter
{
    #region Fields

    /// <summary>The 400 answer's detail; it names no parameter.</summary>
    public const string Message = "A query parameter appears more than once.";

    /// <summary>How deep a complex query model's properties are followed.</summary>
    private const int MaxModelDepth = 4;

    #endregion

    #region Properties

    /// <summary>Before every other resource filter.</summary>
    public int Order => int.MinValue;

    #endregion

    #region IResourceFilter

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActionDescriptor.EndpointMetadata.OfType<LenientInputMetadata>().Any())
            return;

        var query = context.HttpContext.Request.Query;
        HashSet<string> collections = null;
        foreach (var (key, values) in query)
        {
            if (values.Count <= 1)
                continue;
            collections ??= QueryCollectionNames(context.ActionDescriptor, context.HttpContext.RequestServices?.GetService<IModelMetadataProvider>());
            if (collections.Contains(key))
                continue;

            context.Result = Refusal();
            return;
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to do after the action.
    }

    #endregion

    #region Helpers

    /// <summary>The 400 ProblemDetails answer, as <see cref="NulCharacterFilter"/> gives it.</summary>
    public static ObjectResult Refusal() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad request",
            Detail = Message
        })
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };

    /// <summary>
    /// The query names under which the action binds a collection: each collection parameter that can come from the query,
    /// by its binder name or its name, and each collection property of a complex parameter that can, with and without the
    /// parameter's prefix (model binding falls back to no prefix when none of the query keys has it).
    /// </summary>
    /// <param name="action">The action about to run.</param>
    /// <param name="metadata">The host's model metadata provider; without one only parameter types are looked at.</param>
    /// <returns>The names, compared ignoring case.</returns>
    public static HashSet<string> QueryCollectionNames(ActionDescriptor action, IModelMetadataProvider metadata)
    {
        ArgumentNullException.ThrowIfNull(action);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in action.Parameters)
        {
            if (!FromQuery(parameter.BindingInfo?.BindingSource))
                continue;

            var name = parameter.BindingInfo?.BinderModelName ?? parameter.Name;
            if (IsCollection(parameter.ParameterType))
            {
                names.Add(name);
                continue;
            }

            if (metadata is null)
                continue;
            var model = metadata.GetMetadataForType(parameter.ParameterType);
            if (!model.IsComplexType)
                continue;
            foreach (var path in CollectionPaths(model, 0, []))
            {
                names.Add(path);
                names.Add($"{name}.{path}");
            }
        }

        return names;
    }

    /// <summary>The dotted paths of a complex model's collection properties that bind from the query.</summary>
    private static IEnumerable<string> CollectionPaths(ModelMetadata model, int depth, HashSet<Type> seen)
    {
        if (depth >= MaxModelDepth || !seen.Add(model.ModelType))
            yield break;

        foreach (var property in model.Properties)
        {
            if (!property.IsBindingAllowed || !FromQuery(property.BindingSource))
                continue;
            var name = property.BinderModelName ?? property.PropertyName;
            if (property.IsEnumerableType)
            {
                yield return name;
                continue;
            }

            if (!property.IsComplexType)
                continue;
            foreach (var path in CollectionPaths(property, depth + 1, seen))
                yield return $"{name}.{path}";
        }

        seen.Remove(model.ModelType);
    }

    /// <summary>True when a value of this source may come from the query: unspecified, the query, or a source that takes it.</summary>
    private static bool FromQuery(BindingSource source) => source is null || source.CanAcceptDataFrom(BindingSource.Query);

    /// <summary>A collection binds repeated keys; a string, though enumerable, is one value.</summary>
    private static bool IsCollection(Type type) => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    #endregion
}
