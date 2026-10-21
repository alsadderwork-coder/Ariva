using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-096c (CWE-400, CWE-770, ASVS 5.0 V15.2.2): a form sent to an action that binds no form field or file answers 415 before
/// anything reads it. MVC's form value providers read a form sent to any action with a parameter (a <c>CancellationToken</c>
/// counts), whatever the action binds, without the request's token: up to the endpoint's body limit, file parts over 64 KB into the
/// pod's <c>/tmp</c> until the request ends, under no concurrency policy (<c>docs/security/costly-operations.md</c> section 15 item
/// 13). This resource filter runs before model binding and looks at the Content-Type header only, so such a form is never read; an
/// action that binds a form (<see cref="BindsForm"/>) is under the <c>upload</c> policy and reads it steadily
/// (<see cref="SteadyUploadAttribute"/>, held by <c>UnboundFormTests</c>' inventory). The answer is a fixed text. It runs after
/// authentication and authorization (endpoint routing), so an anonymous caller still gets 401 and a caller without the permission
/// 403. The endpoints the login freeze keeps as they are until ARV-089a (<see cref="LenientInputMetadata"/>: the sign-in endpoints
/// and the Integration token exchange) are left alone.
/// </summary>
public sealed class UnboundFormFilter : IResourceFilter, IOrderedFilter
{
    #region Fields

    /// <summary>The 415 answer's detail.</summary>
    public const string Message = "This endpoint takes no form.";

    /// <summary>How deep a complex parameter's properties are followed.</summary>
    private const int MaxModelDepth = 4;

    #endregion

    #region Properties

    /// <summary>
    /// Right after <see cref="RepeatedQueryFilter"/> (which reads the query only) and before every other resource filter, so no
    /// other filter reads the form first.
    /// </summary>
    public int Order => int.MinValue + 1;

    #endregion

    #region IResourceFilter

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActionDescriptor.EndpointMetadata.OfType<LenientInputMetadata>().Any() || !context.HttpContext.Request.HasFormContentType)
            return;
        if (BindsForm(context.ActionDescriptor, context.HttpContext.RequestServices?.GetService<IModelMetadataProvider>()))
            return;

        context.Result = Refusal();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to do after the action.
    }

    #endregion

    #region Helpers

    /// <summary>The 415 ProblemDetails answer, of fixed text.</summary>
    public static ObjectResult Refusal() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status415UnsupportedMediaType,
            Title = "Unsupported media type",
            Detail = Message
        })
        {
            StatusCode = StatusCodes.Status415UnsupportedMediaType,
            ContentTypes = { "application/problem+json" }
        };

    /// <summary>
    /// True when the action may bind a value from a form: a parameter or bound property from the form or its files, one whose source
    /// is not given or may take the form (model binding's own, a custom binder), or a property of a complex parameter that names
    /// such a source. When in doubt it answers true, so a form an action may read is never refused here.
    /// </summary>
    /// <param name="action">The action about to run.</param>
    /// <param name="metadata">The host's model metadata provider; without one only the parameters' own sources are looked at.</param>
    /// <returns>Whether the action binds a form field or file.</returns>
    public static bool BindsForm(ActionDescriptor action, IModelMetadataProvider metadata)
    {
        ArgumentNullException.ThrowIfNull(action);
        IEnumerable<ParameterDescriptor> parameters = action.Parameters;
        if (action is ControllerActionDescriptor controller)
            parameters = parameters.Concat(controller.BoundProperties);

        foreach (var parameter in parameters)
        {
            var source = parameter.BindingInfo?.BindingSource;
            if (source is null || TakesForm(source))
                return true;
            if (metadata is null || source.IsGreedy)
                continue;
            var model = metadata.GetMetadataForType(parameter.ParameterType);
            if (model.IsComplexType && PropertyTakesForm(model, 0, []))
                return true;
        }

        return false;
    }

    /// <summary>True when a property of a complex model, at any depth, names a source that may take the form.</summary>
    private static bool PropertyTakesForm(ModelMetadata model, int depth, HashSet<Type> seen)
    {
        if (depth >= MaxModelDepth || !seen.Add(model.ModelType))
            return false;

        var takes = model.Properties.Any(property => property.IsBindingAllowed &&
            ((property.BindingSource is { } source && TakesForm(source)) || (property.IsComplexType && PropertyTakesForm(property, depth + 1, seen))));
        seen.Remove(model.ModelType);
        return takes;
    }

    /// <summary>True when a value of this source may come from a form: the form, its files, model binding's own source or a custom binder.</summary>
    private static bool TakesForm(BindingSource source) =>
        source == BindingSource.Custom || source.CanAcceptDataFrom(BindingSource.Form) || source.CanAcceptDataFrom(BindingSource.FormFile);

    #endregion
}
