using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Infra.Security;

namespace Ariva.Infra.Integration.Outbound;

/// <summary>
/// Retries, timeouts and the circuit breaker of one outbound endpoint (ARV-045). Each attempt has the endpoint's
/// timeout; only GET and HEAD are retried, on a connection failure, a timeout, 408, 429 or 5xx, with exponential backoff
/// and jitter (honouring a short Retry-After); a refused call is never retried. After the endpoint's number of failures
/// in a row the circuit opens for its break time, and calls fail at once; then one call is let through to try it again.
/// </summary>
public sealed class OutboundResilienceHandler(OutboundTarget target, TimeProvider time) : DelegatingHandler
{
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset? _openUntil;
    private bool _trial;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _openUntil is { } until && until > time.GetUtcNow();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trial = Admit();
        try
        {
            return await SendWithRetriesAsync(request, cancellationToken);
        }
        catch (OutboundRefusedException)
        {
            // A refused call counts as a failure of the endpoint (it is misconfigured or under attack) and ends a trial.
            Record(success: false);
            throw;
        }
        finally
        {
            // A trial that ended in neither a success nor a recorded failure (the caller cancelled) does not leave the
            // circuit waiting for a trial forever; only the call admitted as the trial ends it.
            if (trial)
            {
                lock (_gate)
                    _trial = false;
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        for (var attempt = 0;; attempt++)
        {
            HttpResponseMessage response = null;
            Exception failure = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(target.TimeoutSeconds));
                try
                {
                    response = await base.SendAsync(request, timeout.Token);
                }
                catch (OutboundRefusedException)
                {
                    throw;
                }
                catch (HttpRequestException e) when (e.InnerException is OutboundRefusedException refused)
                {
                    throw refused;
                }
                catch (HttpRequestException e)
                {
                    failure = e;
                }
                catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = new TimeoutException($"No answer within {target.TimeoutSeconds} seconds.", e);
                }
            }

            var transient = failure is not null || response!.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!transient)
            {
                Record(success: true);
                return response;
            }

            Record(success: false);
            if (!idempotent || attempt >= target.RetryCount || IsOpen)
            {
                if (failure is not null)
                    throw failure is HttpRequestException ? failure : new HttpRequestException(failure.Message, failure);
                return response;
            }

            var wait = TimeSpan.FromMilliseconds(Math.Min(5000, 500 * Math.Pow(2, attempt)) + System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 250));
            if (response?.Headers.RetryAfter?.Delta is { } after && after <= TimeSpan.FromSeconds(10))
                wait = after;
            response?.Dispose();
            await Task.Delay(wait, time, cancellationToken);
        }
    }

    // True when this call is the half-open trial.
    private bool Admit()
    {
        lock (_gate)
        {
            if (_openUntil is not { } until)
                return false;
            if (until > time.GetUtcNow() || _trial)
                throw new OutboundUnavailableException($"The circuit to {target.Code} is open after {_failures} failures in a row.");
            _trial = true; // half open: this call decides
            return true;
        }
    }

    private void Record(bool success)
    {
        lock (_gate)
        {
            if (success)
            {
                (_failures, _openUntil) = (0, null);
                return;
            }

            if (++_failures >= target.BreakerFailures)
                _openUntil = time.GetUtcNow().AddSeconds(target.BreakSeconds);
        }
    }
}

/// <summary>Every answer of 300 to 399 (but 304 Not Modified) is refused, never followed (CWE-918: a redirect could point anywhere).</summary>
public sealed class OutboundRedirectGuard : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400 && response.StatusCode != HttpStatusCode.NotModified)
        {
            response.Dispose();
            throw new OutboundRefusedException("The endpoint answered with a redirect, which Ariva does not follow.");
        }

        return response;
    }
}

/// <summary>A named header carrying the endpoint's API key.</summary>
public sealed class ApiKeyHeaderHandler(string headerName, string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Signs each request with the shared key (HMAC-SHA256 over method, path and query, Unix timestamp and the body's
/// SHA-256 in hex, joined by line feeds), in <c>X-Ariva-Key-Id</c>, <c>X-Ariva-Timestamp</c> and <c>X-Ariva-Signature</c> (base64).
/// </summary>
public sealed class HmacSignatureHandler(string keyId, byte[] key, TimeProvider time) : DelegatingHandler
{
    public static string Sign(byte[] key, string method, string pathAndQuery, long timestamp, byte[] body)
    {
        var canonical = string.Join('\n', method.ToUpperInvariant(), pathAndQuery, timestamp.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(SHA256.HashData(body ?? [])));
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var name in new[] { "X-Ariva-Key-Id", "X-Ariva-Timestamp", "X-Ariva-Signature" })
            request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation("X-Ariva-Key-Id", keyId);
        request.Headers.TryAddWithoutValidation("X-Ariva-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Ariva-Signature", Sign(key, request.Method.Method, request.RequestUri!.PathAndQuery, timestamp, body));
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// A bearer token from the endpoint's own token path, cached until 60 seconds before it expires and dropped when the
/// endpoint answers 401: AMAN-style TOTP client credentials (client id, secret and a fresh TOTP code; <c>X-TOTP-Code</c>
/// on every call when the endpoint needs it) or OAuth 2.0 client credentials (HTTP Basic client authentication, RFC 6749).
/// The token request goes to the same origin as the calls, through the same guarded transport. A TOTP exchange is tried
/// at most once per TOTP step (ARV-050, CWE-287): AMAN accepts one exchange per step and client (a replay guard) and
/// counts refusals toward its lockout, so a second attempt in the same step (a 401 that dropped the token, a retry)
/// fails here without a call and the next step tries again.
/// </summary>
public sealed class TokenHandler(OutboundTarget target, OutboundSecret secret, TimeProvider time) : DelegatingHandler
{
    private const int MaxTokenAnswerBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _token;
    private DateTimeOffset _expires;
    private long _lastExchangeStep = -1;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await TokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (target.AuthKind == Core.Domain.Enums.OutboundAuthKind.TotpClientCredentials && target.TotpPerRequest)
        {
            request.Headers.Remove("X-TOTP-Code");
            request.Headers.TryAddWithoutValidation("X-TOTP-Code", Code());
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_token == token)
                    _token = null;
            }
            finally
            {
                _gate.Release();
            }
        }

        return response;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && time.GetUtcNow() < _expires)
                return _token;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(target.BaseUrl, target.TokenPath.TrimStart('/')));
            if (target.AuthKind == Core.Domain.Enums.OutboundAuthKind.TotpClientCredentials)
            {
                var step = Totp.StepAt(time.GetUtcNow());
                if (step <= _lastExchangeStep)
                    throw new OutboundUnavailableException($"The token of {target.Code} was already requested in this TOTP step; the next step tries again.");
                _lastExchangeStep = step;
                request.Content = JsonContent.Create(new { clientId = target.ClientId, clientSecret = secret.ClientSecret, totpCode = Totp.Code(Base32.Decode(secret.TotpSeed), step) });
            }
            else
            {
                var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
                if (!string.IsNullOrEmpty(target.Scope))
                    form["scope"] = target.Scope;
                request.Content = new FormUrlEncodedContent(form);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(target.ClientId) + ":" + Uri.EscapeDataString(secret.ClientSecret))));
            }

            using var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The token request to {target.Code} was refused ({(int)response.StatusCode}).", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaxTokenAnswerBytes)
                throw new HttpRequestException($"The token answer of {target.Code} is too large.");
            try
            {
                // Bounded while reading, also when the answer has no length (chunked or compressed).
                await response.Content.LoadIntoBufferAsync(MaxTokenAnswerBytes, ct);
            }
            catch (HttpRequestException e)
            {
                throw new HttpRequestException($"The token answer of {target.Code} is too large.", e);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var (token, expires) = Parse(bytes);
            _token = token;
            _expires = expires - TimeSpan.FromSeconds(60) is var early && early > time.GetUtcNow().AddSeconds(30) ? early : time.GetUtcNow().AddSeconds(30);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    // AMAN answers { accessToken, expiresAt }; OAuth 2.0 { access_token, expires_in }.
    private (string Token, DateTimeOffset Expires) Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            var token = (root.TryGetProperty("accessToken", out var a) ? a : root.TryGetProperty("access_token", out var b) ? b : default) is { ValueKind: JsonValueKind.String } t
                ? t.GetString()
                : null;
            if (string.IsNullOrEmpty(token) || token.Length > 8192 || token.Any(c => c is < '!' or > '~'))
                throw new HttpRequestException($"The token answer of {target.Code} has no usable token.");
            var now = time.GetUtcNow();
            if (root.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var expiresAt))
                return (token, expiresAt);
            if (root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s) && s is > 0 and <= 86400)
                return (token, now.AddSeconds(s));
            return (token, now.AddMinutes(5));
        }
        catch (JsonException e)
        {
            throw new HttpRequestException($"The token answer of {target.Code} is not JSON.", e);
        }
    }

    private string Code() => Totp.Code(Base32.Decode(secret.TotpSeed), Totp.StepAt(time.GetUtcNow()));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _gate.Dispose();
        base.Dispose(disposing);
    }
}
