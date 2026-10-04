using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Middlewares;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TickerQ.Dashboard.DependencyInjection;
using TickerQ.DependencyInjection;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>The <c>Cronz:Dashboard</c> section: the TickerQ dashboard is off unless enabled with the SHA-256 of its key.</summary>
public sealed class JobsDashboardSettings
{
    public const string SectionName = "Cronz:Dashboard";

    public bool Enabled { get; init; }

    /// <summary>The SHA-256 (64 hex digits) of the dashboard key; the key itself is never configured here.</summary>
    public string KeySha256 { get; init; }

    public string BasePath { get; init; } = "/tickerq";

    public IEnumerable<string> Problems()
    {
        if (!Enabled)
            yield break;
        if (KeySha256 is null || !Regex.IsMatch(KeySha256, "^[0-9a-fA-F]{64}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            yield return "Cronz:Dashboard:KeySha256 is the SHA-256 of the dashboard key as 64 hex digits.";
        if (BasePath is null || !Regex.IsMatch(BasePath, "^/[a-z][a-z0-9-]{1,30}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            yield return "Cronz:Dashboard:BasePath is one lower-case path segment, for example /tickerq.";
    }
}

/// <summary>
/// TickerQ in Ariva.Api.Cronz (ADR-0020, ARV-060): the schedulers in memory (each job keeps its own state in PostgreSQL),
/// and the dashboard only when <c>Cronz:Dashboard</c> enables it with a key. The dashboard has no Ariva user session (a
/// browser cannot send the bearer token on navigation), so it takes an operator key, compared as a SHA-256 digest in
/// constant time (<see cref="JobsDashboardGuard"/>), which the dashboard's browser app sends in TickerQ's host mode. The
/// guard answers every request of the branch: a short list of paths without data is open, the API needs the key, and the
/// notification hub a short-lived ticket cookie the guard issues after the key (TickerQ's own check matches paths ignoring
/// case only for its API and leaves its hub negotiation open). Its page gets a policy of its own; cross-origin calls are
/// refused. Deployments expose it on the admin network only (wiki 04).
/// </summary>
public static class JobsDashboard
{
    public static IServiceCollection AddArivaJobs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(JobsDashboardSettings.SectionName).Get<JobsDashboardSettings>() ?? new JobsDashboardSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddTickerQ(options =>
        {
            if (!settings.Enabled)
                return;
            var digest = Convert.FromHexString(settings.KeySha256);
            options.AddDashboard(dashboard =>
            {
                dashboard.SetBasePath(settings.BasePath);
                // Host mode: TickerQ trusts the request's user, which only the guard sets, and only after the key.
                dashboard.WithHostAuthentication();
                dashboard.WithSessionTimeout(30);
                // Same origin only: no cross-origin caller, credentials or not.
                dashboard.SetCorsPolicy(_ => { });
                dashboard.PreDashboardMiddleware = app => app.UseMiddleware<JobsDashboardGuard>(digest, settings.BasePath);
                // Host mode puts an authorization requirement on TickerQ's API group, evaluated against the guard's user. Only
                // for a matched endpoint: the page has none, and the guard has already decided what may reach it.
                dashboard.CustomMiddleware = app => app.UseWhen(context => context.GetEndpoint() is not null, branch => branch.UseAuthorization());
            });
        });
        return services;
    }
}

/// <summary>
/// Guards the dashboard branch. Open without the key, for GET and HEAD only: the page (at its root, and at any path
/// without a dot that is neither the API nor the hub, which serves the same page for the browser app's own routes), its
/// static files under /assets/, its icon and <c>/api/auth/info</c> (how to sign in). The API needs the key in the
/// Authorization header. The notification hub needs a ticket instead: TickerQ's browser app sends no credential to its
/// hub in host mode, so every request with the key gets back a ticket cookie (HttpOnly, SameSite=Strict, scoped to the hub
/// path, valid for <see cref="TicketLifetime"/>): an expiry and its HMAC under a key this process draws at random, never the
/// operator key, and never in a URL. The hub also refuses another origin. Paths are compared ignoring case, as ASP.NET routes them. The page is served under a content
/// security policy that allows exactly its two inline scripts: TickerQ's own preload script (hashed from the package at
/// startup) and the configuration script it writes, when that names this base path; anything else on the page is refused.
/// </summary>
public sealed partial class JobsDashboardGuard
{
    /// <summary>The page's policy before the script hashes; it keeps the API baseline's start so no layer loosens it.</summary>
    private const string PagePolicy =
        "default-src 'none'; script-src 'self' {0}; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
        "connect-src 'self'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'";

    private const string Hub = "/ticker-notification-hub";

    /// <summary>The hub ticket's cookie; Secure, so browsers keep it only from HTTPS (and from localhost).</summary>
    public const string TicketCookie = "__Secure-ariva_jobs_hub";

    /// <summary>The Authorization value the guard gives an admitted hub request (see <see cref="InvokeAsync"/>); not a credential.</summary>
    public const string HubMarker = "Ariva-Jobs-Ticket";

    /// <summary>How long a ticket stays valid; every request with the key renews it.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(30);

    private static readonly Lazy<string> PreloadScriptHash = new(() =>
    {
        using var stream = typeof(TickerQ.Dashboard.DashboardOptionsBuilder).Assembly.GetManifestResourceStream("TickerQ.Dashboard.wwwroot.dist.index.html")
                           ?? throw new InvalidOperationException("The TickerQ dashboard page is not in its package.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var match = InlineScript().Match(reader.ReadToEnd());
        return match.Success ? Hash(match.Groups[1].Value) : null;
    });

    private readonly RequestDelegate _next;
    private readonly byte[] _digest;
    private readonly string _basePath;
    private readonly ILogger<JobsDashboardGuard> _logger;
    private readonly TimeProvider _time;
    private readonly byte[] _ticketKey = RandomNumberGenerator.GetBytes(32);

    public JobsDashboardGuard(RequestDelegate next, byte[] digest, string basePath, ILogger<JobsDashboardGuard> logger, TimeProvider time)
    {
        _next = next;
        _digest = digest;
        _basePath = basePath;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True when the value is the key: as typed into TickerQ's host-mode sign-in, bare or after "Bearer ".</summary>
    public static bool Matches(string value, byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (string.IsNullOrEmpty(value) || value.Length > 512)
            return false;
        var key = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())), digest);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = (context.Request.Path.Value ?? string.Empty).ToLowerInvariant();
        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var api = path.StartsWith("/api/", StringComparison.Ordinal) || path == "/api";
        var hub = path.StartsWith(Hub, StringComparison.Ordinal);

        if (read && (path is "" or "/" or "/index.html" || (!api && !hub && !path.Contains('.', StringComparison.Ordinal))))
        {
            // The page, also for the browser app's own routes: rewritten to the root, so no other handler is reached.
            context.Request.Path = "/";
            await PageAsync(context);
            return;
        }

        if (read && (StaticFile().IsMatch(path) || path == "/api/auth/info"))
        {
            await _next(context);
            return;
        }

        var allowed = hub
            ? SameOrigin(context.Request) && context.Request.Cookies.TryGetValue(TicketCookie, out var ticket) && IsTicket(ticket)
            : Matches(KeyOf(context), _digest);
        if (!allowed)
        {
            LogRefused(_logger, path.Length > 100 ? path[..100] : path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (hub)
        {
            // TickerQ's hub refuses a request without an Authorization value before host mode looks at the user (AuthService
            // in TickerQ 10.0.2), and its browser app sends none to the hub. In host mode the value itself is never read, so
            // a fixed marker that holds nothing secret stands in; whatever the request carried is replaced.
            context.Request.Headers.Authorization = HubMarker;
        }
        else
        {
            context.Response.Cookies.Append(TicketCookie, IssueTicket(), new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = _basePath + Hub,
                MaxAge = TicketLifetime,
                IsEssential = true
            });
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "jobs-operator")], "Ariva.JobsDashboard"));
        await _next(context);
    }

    /// <summary>The Authorization header, when there is exactly one.</summary>
    private static string KeyOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization;
        return header.Count == 1 ? header[0] : null;
    }

    /// <summary>A new hub ticket: its expiry (Unix seconds) and the HMAC of both under this process's ticket key, base64url.</summary>
    public string IssueTicket()
    {
        var expiry = _time.GetUtcNow().Add(TicketLifetime).ToUnixTimeSeconds();
        var body = BitConverter.GetBytes(expiry);
        return Base64Url(body.Concat(Sign(body)).ToArray());
    }

    /// <summary>True for a ticket this process issued that has not expired.</summary>
    public bool IsTicket(string ticket)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 64)
            return false;
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(ticket.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (ticket.Length % 4)) % 4));
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != 8 + 32 || !CryptographicOperations.FixedTimeEquals(raw.AsSpan(8), Sign(raw[..8])))
            return false;
        return BitConverter.ToInt64(raw, 0) > _time.GetUtcNow().ToUnixTimeSeconds();
    }

    /// <summary>
    /// No Origin (not a browser's cross-origin call), or the Origin's host and port are this request's: a page elsewhere
    /// cannot open the hub with the operator's cookie (cross-site WebSocket hijacking, CWE-1385), on top of SameSite=Strict.
    /// </summary>
    public static bool SameOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = request.Headers.Origin;
        if (origin.Count == 0)
            return true;
        return origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var uri) &&
               string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private byte[] Sign(byte[] body) => HMACSHA256.HashData(_ticketKey, (byte[])[.. "ariva-jobs-hub"u8, .. body]);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Buffers the HTML page to put the hashes of its two expected inline scripts in its policy before it starts.</summary>
    private async Task PageAsync(HttpContext context)
    {
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            var allowed = new List<string>();
            foreach (Match script in InlineScript().Matches(Encoding.UTF8.GetString(buffer.ToArray())))
            {
                var hash = Hash(script.Groups[1].Value);
                if (hash == PreloadScriptHash.Value || IsConfigScript(script.Groups[1].Value, _basePath))
                    allowed.Add(hash);
                else
                    LogUnexpectedScript(_logger);
            }

            context.Response.Headers.ContentSecurityPolicy = string.Format(CultureInfo.InvariantCulture, PagePolicy, string.Join(' ', allowed.Distinct(StringComparer.Ordinal)));
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = buffer.Length;
        }

        await buffer.CopyToAsync(original, context.RequestAborted);
    }

    /// <summary>TickerQ's configuration script, exactly as it writes it, for this base path, host mode and no other domain.</summary>
    public static bool IsConfigScript(string script, string basePath)
    {
        var match = ConfigScript().Match(script);
        if (!match.Success)
            return false;
        try
        {
            using var config = JsonDocument.Parse(match.Groups["json"].Value);
            var root = config.RootElement;
            return root.TryGetProperty("basePath", out var path) && path.GetString() == basePath &&
                   (!root.TryGetProperty("backendDomain", out var domain) || domain.ValueKind == JsonValueKind.Null) &&
                   root.TryGetProperty("auth", out var auth) && auth.TryGetProperty("mode", out var mode) && mode.GetString() == "host";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Hash(string script) => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    [GeneratedRegex("<script>(.*?)</script>", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"^\s*\(function\(\) \{\s*try \{\s*// Expose config\s*window\.TickerQConfig = (?<json>\{[^<>]*\});\s*// Derive dynamic base for vite-plugin-dynamic-base\s*window\.__dynamic_base__ = window\.TickerQConfig\.basePath;\s*\} catch \(e\) \{ console\.error\('Runtime config injection failed:', e\); \}\s*\}\)\(\);\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConfigScript();

    [GeneratedRegex(@"^(/assets/[a-z0-9._-]+\.(js|css|woff2?|ttf|eot|svg|png|ico)|/[a-z0-9-]+\.(svg|ico|png))$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StaticFile();

    [LoggerMessage(EventId = 9601, Level = LogLevel.Warning, Message = "Jobs dashboard request refused without the key: {Path}")]
    private static partial void LogRefused(ILogger logger, string path);

    [LoggerMessage(EventId = 9602, Level = LogLevel.Warning, Message = "Jobs dashboard page held an unexpected inline script; it is not allowed to run")]
    private static partial void LogUnexpectedScript(ILogger logger);
}
