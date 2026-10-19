using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// TEMPORARY, until ARV-089a. The login freeze (owner, in force since 2026-10-14: no change to sign-in, login, password,
/// TOTP or authentication code or behaviour) keeps the authentication endpoints on the JSON and query parsing they had
/// before ARV-089: unknown members skipped, a member sent twice read as before, numbers also from strings, repeated query
/// parameters not refused. A host names those controllers in its Program.cs (<see cref="WithLenientInput{TController}"/>)
/// so that their own files stay untouched; every other action reads strictly (<see cref="StrictJson"/>,
/// <see cref="RepeatedQueryFilter"/>). ARV-089a removes the calls and this file.
/// </summary>
public static class LenientInputExtensions
{
    #region Services

    /// <summary>
    /// Keeps the actions of <typeparamref name="TController"/> on the lenient parsing until ARV-089a: their endpoints carry
    /// <see cref="LenientInputMetadata"/>, a JSON input formatter with the pre-ARV-089 settings reads their bodies (the same
    /// options otherwise: depth, converters, no reader messages in a 400), and <see cref="RepeatedQueryFilter"/> skips them.
    /// </summary>
    /// <typeparam name="TController">An authentication controller of the host.</typeparam>
    /// <param name="builder">The host's MVC builder (<see cref="ControllerExtensions.AddAppControllers"/>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMvcBuilder WithLenientInput<TController>(this IMvcBuilder builder)
        where TController : ControllerBase
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<LenientInputOptions>(options => options.Controllers.Add(typeof(TController)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Transient<IPostConfigureOptions<MvcOptions>, LenientInputSetup>());
        return builder;
    }

    #endregion
}

/// <summary>TEMPORARY until ARV-089a: the controllers that keep the lenient parsing (<see cref="LenientInputExtensions"/>).</summary>
public sealed class LenientInputOptions
{
    /// <summary>The controller types.</summary>
    public ISet<Type> Controllers { get; } = new HashSet<Type>();
}

/// <summary>
/// TEMPORARY until ARV-089a: endpoint metadata of an action that keeps the lenient parsing. Its presence on an endpoint is
/// the whole exemption, so a test can list every such endpoint (StrictInputTests).
/// </summary>
public sealed class LenientInputMetadata
{
    /// <summary>The one instance.</summary>
    public static readonly LenientInputMetadata Instance = new();

    private LenientInputMetadata()
    {
    }
}

/// <summary>
/// TEMPORARY until ARV-089a: adds <see cref="LenientInputMetadata"/> to the actions of the named controllers, and the lenient
/// JSON input formatter before the strict one.
/// </summary>
/// <param name="lenient">The controllers named by the host.</param>
/// <param name="json">Builds a fresh copy of the controllers' configured JSON options.</param>
/// <param name="loggers">For the formatter's logger.</param>
internal sealed class LenientInputSetup(IOptions<LenientInputOptions> lenient, IOptionsFactory<JsonOptions> json, ILoggerFactory loggers)
    : IPostConfigureOptions<MvcOptions>
{
    public void PostConfigure(string name, MvcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name != Options.DefaultName || lenient.Value.Controllers.Count == 0 || options.InputFormatters.OfType<LenientJsonInputFormatter>().Any())
            return;

        options.Conventions.Add(new LenientInputConvention(lenient.Value.Controllers));

        // The controllers' JSON options as configured (depth, converters, AllowInputFormatterExceptionMessages false), built
        // afresh so that the strict instance stays as it is, with the three strict settings taken back.
        var relaxed = json.Create(Options.DefaultName);
        StrictJson.Relax(relaxed.JsonSerializerOptions);
        options.InputFormatters.Insert(0, new LenientJsonInputFormatter(relaxed, loggers.CreateLogger<SystemTextJsonInputFormatter>()));
    }
}

/// <summary>TEMPORARY until ARV-089a: marks the actions of the named controllers.</summary>
/// <param name="controllers">The controller types.</param>
internal sealed class LenientInputConvention(ISet<Type> controllers) : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (!controllers.Contains(controller.ControllerType.AsType()))
            return;

        foreach (var selector in controller.Actions.SelectMany(action => action.Selectors))
            selector.EndpointMetadata.Add(LenientInputMetadata.Instance);
    }
}

/// <summary>
/// TEMPORARY until ARV-089a: System.Text.Json input with the pre-ARV-089 settings, for the endpoints with
/// <see cref="LenientInputMetadata"/> only; for every other endpoint it declines, so the strict formatter after it reads.
/// It adds no content type to the API description, so the OpenAPI documents stay as they are.
/// </summary>
/// <param name="options">The relaxed copy of the controllers' JSON options.</param>
/// <param name="logger">The formatter's logger.</param>
internal sealed class LenientJsonInputFormatter(JsonOptions options, ILogger<SystemTextJsonInputFormatter> logger)
    : SystemTextJsonInputFormatter(options, logger)
{
    public override bool CanRead(InputFormatterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsLenient(context.HttpContext) && base.CanRead(context);
    }

    public override IReadOnlyList<string> GetSupportedContentTypes(string contentType, Type objectType) => null;

    /// <summary>True when the request's endpoint keeps the lenient parsing.</summary>
    internal static bool IsLenient(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<LenientInputMetadata>() is not null;
}
