using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-089 (ASVS 5.0 V2.2.1, V15.3.5, V15.3.7; CWE-20, CWE-501): request JSON is read strictly. A member the request model
/// does not have, a member sent twice (also when the two differ only in case, as names match ignoring case) and a number
/// sent as a string are refused; the controller then answers its usual 400 without paths or values
/// (<see cref="ControllerExtensions.ValidationProblemWithoutBodyPaths"/>). ASP.NET Core starts from
/// <see cref="JsonSerializerDefaults.Web"/>, which reads numbers from strings and skips unknown members, so the three
/// settings are set here for the controllers' and the minimal APIs' options alike
/// (<see cref="Extensions.RequestLimitsExtensions"/>). Only reading changes: <see cref="JsonNumberHandling.Strict"/> writes
/// numbers as numbers, as the web defaults did, and the other two settings apply to reading only, so every response is
/// serialized exactly as before.
/// </summary>
public static class StrictJson
{
    #region Methods

    /// <summary>Refuses unmapped members, duplicate properties and numbers sent as strings.</summary>
    /// <param name="options">Options not yet used (System.Text.Json freezes options at their first use).</param>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.AllowDuplicateProperties = false;
        options.NumberHandling = JsonNumberHandling.Strict;
    }

    /// <summary>
    /// Takes the three settings of <see cref="Apply"/> back to ASP.NET Core's web defaults (unknown members skipped,
    /// duplicates allowed, numbers also from strings) and leaves everything else (depth, converters, names) as it is.
    /// Only for the endpoints the login freeze keeps lenient until ARV-089a (<see cref="LenientInputExtensions"/>).
    /// </summary>
    /// <param name="options">Options not yet used.</param>
    public static void Relax(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UnmappedMemberHandling = JsonSerializerOptions.Web.UnmappedMemberHandling;
        options.AllowDuplicateProperties = JsonSerializerOptions.Web.AllowDuplicateProperties;
        options.NumberHandling = JsonSerializerOptions.Web.NumberHandling;
    }

    /// <summary>True when the options read as <see cref="Apply"/> sets them.</summary>
    /// <param name="options">The options to check.</param>
    /// <returns>Whether all three settings are strict.</returns>
    public static bool IsStrict(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow
               && !options.AllowDuplicateProperties
               && options.NumberHandling == JsonNumberHandling.Strict;
    }

    #endregion
}
