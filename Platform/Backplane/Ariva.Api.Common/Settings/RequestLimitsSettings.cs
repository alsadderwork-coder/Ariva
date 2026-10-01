namespace Ariva.Api.Common.Settings;

/// <summary>
/// Input size limits applied to every host (CWE-120), bound from <c>Security:Limits</c>. The defaults are the
/// production values; per endpoint classes (sensor pushes 256 KB, streamed file imports 20 MB) override the body
/// limit on their own endpoints with <c>RequestSizeLimit</c> metadata.
/// </summary>
public sealed class RequestLimitsSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Security:Limits";

    /// <summary>Largest request body Kestrel accepts, in bytes. Default 1 MB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    /// <summary>Largest request line (method, path and query), in bytes. Default 8 KB.</summary>
    public int MaxRequestLineBytes { get; set; } = 8_192;

    /// <summary>Largest total size of all request headers, in bytes. Default 32 KB.</summary>
    public int MaxRequestHeadersTotalBytes { get; set; } = 32_768;

    /// <summary>Most request headers accepted. Default 64.</summary>
    public int MaxRequestHeaderCount { get; set; } = 64;

    /// <summary>Most form values accepted. Default 1024.</summary>
    public int MaxFormValueCount { get; set; } = 1_024;

    /// <summary>Largest single form value, in bytes. Default 64 KB.</summary>
    public int MaxFormValueLengthBytes { get; set; } = 65_536;

    /// <summary>Largest form key, in bytes. Default 2 KB.</summary>
    public int MaxFormKeyLengthBytes { get; set; } = 2_048;

    /// <summary>Deepest JSON nesting accepted by controllers and minimal APIs. Default 32.</summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>Time allowed to receive the request headers, in seconds (slow header attacks). Default 15.</summary>
    public int RequestHeadersTimeoutSeconds { get; set; } = 15;
}
