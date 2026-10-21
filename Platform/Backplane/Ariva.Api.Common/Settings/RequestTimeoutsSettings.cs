namespace Ariva.Api.Common.Settings;

/// <summary>
/// Request timeouts of every host (ARV-096 on Ariva.Api.Main, ARV-096b on the others; ASVS 5.0 V15.2.2), bound from
/// <c>Security:RequestTimeouts</c> and refused at start when out of bounds. A request still running at its timeout has
/// <c>HttpContext.RequestAborted</c> cancelled; an endpoint that passes that token to its reads ends there, and one whose answer has
/// not started gets 503 with <c>Retry-After: </c><see cref="RetryAfterSeconds"/>. The values are Proposed (docs/product/decisions.md,
/// ARV-096). Exempt: Ariva.Api.Main's live hub (long by design), Ariva.Api.Cronz's TickerQ dashboard and its hub (a branch of the
/// pipeline before the timeouts, which a default would cut), and, while the login freeze lasts, Ariva.Api.Integration's token
/// exchange. Ariva.Api.Integration runs the timeouts before its call audit, so a batch or an AIDX body read there ends at the default.
/// </summary>
public sealed class RequestTimeoutsSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Security:RequestTimeouts";

    /// <summary>The Retry-After of a 503 for a request that outlasted its timeout, in seconds.</summary>
    public const int RetryAfterSeconds = 30;

    /// <summary>
    /// Every endpoint without its own policy (the default policy), on every host, in seconds. Default 120: the endpoints under it
    /// read bounded sets and answer in seconds (docs/security/costly-operations.md); a sensor push (256 KB at 1 KB a second at
    /// least) and an Integration batch or AIDX message (at 32 KB a second, over HTTP/1.x) arrive well within it.
    /// </summary>
    public int DefaultSeconds { get; set; } = 120;

    /// <summary>The on-demand daily report and its CSV files (the <c>reports</c> policy), in seconds. Default 60.</summary>
    public int ReportsSeconds { get; set; } = 60;

    /// <summary>
    /// Endpoints that move a large file (the <c>transfer</c> policy), in seconds: the uploads and imports under the <c>upload</c>
    /// concurrency policy (up to 20 MB, an advertisement video up to its configured size) and the stored files served for preview
    /// and to display players. Default 900 (15 minutes). MVC reads a form or a JSON body without the request's token, so this
    /// timeout alone would not end a body still arriving: every upload reads its body itself, with that token, before model
    /// binding (<c>SteadyUploadAttribute</c>, or the advertisement upload's filter, which takes the same steps), so the timeout
    /// ends the read on any protocol; and over HTTP/1.x the body must arrive at 32 KB a second or more after 10 seconds (408
    /// below), so the largest, 20 MB and the form's framing, is in within about 11 minutes. A video above about 28 MB outlasts
    /// the default at that rate: an installation that raises <c>Advertising:Videos:MaxMegabytes</c> raises this with it (about
    /// 33 s a MB).
    /// </summary>
    public int TransferSeconds { get; set; } = 900;

    /// <summary>The bounds, as text for a refusal at start; empty when every value is usable.</summary>
    public IEnumerable<string> Problems()
    {
        if (DefaultSeconds is < 5 or > 600)
            yield return "DefaultSeconds is 5 to 600.";
        if (ReportsSeconds is < 5 or > 600)
            yield return "ReportsSeconds is 5 to 600.";
        if (TransferSeconds is < 60 or > 3_600)
            yield return "TransferSeconds is 60 to 3,600.";
    }

    /// <summary>True when every value is within its bounds.</summary>
    public bool IsValid => !Problems().Any();
}
