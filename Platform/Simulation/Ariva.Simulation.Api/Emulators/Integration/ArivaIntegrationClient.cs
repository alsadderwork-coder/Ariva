using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ariva.Simulation.Api.Emulators.Integration;

/// <summary>
/// The credentials Ariva issued to an emulated system's integration client (ARV-042): client id, client secret and
/// TOTP seed, shown once when an administrator registers the client, and whether the client's policy asks for
/// <c>X-TOTP-Code</c> on every call. Secrets: held in memory only, never returned or logged.
/// </summary>
public sealed partial class IntegrationClientSettings
{
    public string ClientId { get; set; }
    public string ClientSecret { get; set; }
    public string TotpSecret { get; set; }
    public bool PerRequestTotp { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(ClientId);

    /// <summary>What is wrong with the credentials, never quoting them.</summary>
    public IEnumerable<string> Problems(string name)
    {
        if (!IsConfigured)
            yield break;
        if (!ClientIdPattern().IsMatch(ClientId))
            yield return $"{name}: the client id is the one Ariva issued (ic_ and 26 characters).";
        if (ClientSecret is null || !SecretPattern().IsMatch(ClientSecret))
            yield return $"{name}: the client secret is the one Ariva issued (ics_ and 43 characters).";
        if (Totp.FromBase32(TotpSecret) is null)
            yield return $"{name}: the TOTP secret is the Base32 seed Ariva issued.";
    }

    [GeneratedRegex("^ic_[A-Za-z0-9]{26}\\z")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex("^ics_[A-Za-z0-9_-]{43}\\z")]
    private static partial Regex SecretPattern();
}

/// <summary>What one call to Ariva gave: the HTTP status (null when Ariva could not be reached) and a short reason.</summary>
public sealed record IntegrationCallResult(int? Status, string Error)
{
    public bool Succeeded => Status is >= 200 and < 300;
}

/// <summary>
/// An emulated system's client of Ariva's Integration API (ARV-029), as AMAN and an AODB call it: the token exchange
/// (<c>POST api/v1/auth</c> with client id, secret and a fresh TOTP code), the access token kept until 60 seconds
/// before it expires, <c>X-TOTP-Code</c> on every call when the client's policy asks for it, and the
/// <c>Idempotency-Key</c> of each batch. A refused call drops the token so the next one exchanges again; an exchange is
/// only tried with a TOTP step newer than the last one used, as Ariva's replay guard requires, so a burst never locks
/// the client. Thread safe.
/// </summary>
public sealed class ArivaIntegrationClient(string name, Func<HttpClient> http, TimeProvider time, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private IntegrationClientSettings _credentials;
    private byte[] _seed;
    private string _token;
    private DateTimeOffset _refreshAt;
    private long _lastStep = -1;
    private int _generation;

    public string Name { get; } = name;

    public bool IsConfigured => Volatile.Read(ref _credentials)?.IsConfigured == true;

    /// <summary>Replaces the credentials (checked by the caller); the token goes with the old ones.</summary>
    public void Use(IntegrationClientSettings credentials)
    {
        // Not during an exchange, so a token of the old credentials is never stored for the new ones.
        _exchange.Wait();
        try
        {
            Replace(credentials);
        }
        finally
        {
            _exchange.Release();
        }
    }

    private void Replace(IntegrationClientSettings credentials)
    {
        Volatile.Write(ref _seed, credentials?.IsConfigured == true ? Totp.FromBase32(credentials.TotpSecret) : null);
        Volatile.Write(ref _credentials, credentials);
        // A token being exchanged with the old credentials is not kept for the new ones.
        Interlocked.Increment(ref _generation);
        _token = null;
        _lastStep = -1;
    }

    /// <summary>Sends one call with the client's token, exchanging first when there is none.</summary>
    public async Task<IntegrationCallResult> SendAsync(HttpMethod method, string path, HttpContent content, string idempotencyKey, CancellationToken ct)
    {
        var credentials = Volatile.Read(ref _credentials);
        if (credentials?.IsConfigured != true)
            return new IntegrationCallResult(null, "no client configured");
        if (http().BaseAddress is null)
            return new IntegrationCallResult(null, "no Ariva address configured (Simulation:Ariva:IntegrationUrl)");
        var token = await TokenAsync(credentials, ct);
        if (token.Error is not null)
            return token.Error;

        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        if (credentials.PerRequestTotp)
            request.Headers.Add("X-TOTP-Code", Totp.Code(Volatile.Read(ref _seed), time.GetUtcNow()));
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        try
        {
            using var response = await http().SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                _token = null;
            return response.IsSuccessStatusCode
                ? new IntegrationCallResult((int)response.StatusCode, null)
                : new IntegrationCallResult((int)response.StatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new IntegrationCallResult(null, e is TaskCanceledException ? "timeout" : "Ariva unreachable");
        }
    }

    private async Task<(string Value, IntegrationCallResult Error)> TokenAsync(IntegrationClientSettings credentials, CancellationToken ct)
    {
        if (_token is { } current && time.GetUtcNow() < _refreshAt)
            return (current, null);
        await _exchange.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_token is { } fresh && now < _refreshAt)
                return (fresh, null);
            var generation = Volatile.Read(ref _generation);
            var step = Totp.Step(now);
            if (step <= _lastStep)
                return (null, new IntegrationCallResult(null, "waiting for a new TOTP step"));
            _lastStep = step;
            var seed = Volatile.Read(ref _seed);
            using var response = await http().PostAsJsonAsync("api/v1/auth",
                new { clientId = credentials.ClientId, clientSecret = credentials.ClientSecret, totpCode = Totp.Code(seed, step) }, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Client}: Ariva refused the token exchange ({Status})", Name, (int)response.StatusCode);
                return (null, new IntegrationCallResult((int)response.StatusCode, $"token exchange answered {(int)response.StatusCode}"));
            }

            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!answer.RootElement.TryGetProperty("accessToken", out var accessToken) || accessToken.GetString() is not { Length: > 0 } value ||
                !answer.RootElement.TryGetProperty("expiresAt", out var expiresAt) || !expiresAt.TryGetDateTimeOffset(out var expires))
                return (null, new IntegrationCallResult((int)response.StatusCode, "the token exchange answer is not readable"));
            if (generation != Volatile.Read(ref _generation))
                return (null, new IntegrationCallResult(null, "the credentials changed during the exchange"));
            _token = value;
            _refreshAt = expires - TimeSpan.FromSeconds(60);
            return (value, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return (null, new IntegrationCallResult(null, e is TaskCanceledException ? "timeout" : "Ariva unreachable"));
        }
        finally
        {
            _exchange.Release();
        }
    }
}
