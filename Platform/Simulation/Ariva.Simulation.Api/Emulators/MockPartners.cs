using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Ariva.Simulation.Api.Emulators.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators;

/// <summary>
/// The mock AMAN's token service (ARV-029), for Ariva's outbound AMAN connector (ARV-050): the AMAN-style exchange of
/// client id, secret and a TOTP code for an opaque access token. The secret is compared as a SHA-256 digest in constant
/// time and hashed for an unknown client too, so the answer and its timing do not tell which part was wrong; a TOTP
/// step is accepted once per client (replay guard). Tokens are random 256-bit values held as their SHA-256 only.
/// </summary>
public sealed class MockAmanTokens(IOptionsMonitor<AmanEmulatorSettings> settings, TimeProvider time)
{
    private static readonly byte[] Dummy = SHA256.HashData("no such client"u8);
    private const string DummySeed = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly ConcurrentDictionary<string, (string ClientId, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _steps = new(StringComparer.Ordinal);

    /// <summary>A token and its expiry for valid credentials, or null.</summary>
    public (string Token, DateTimeOffset Expires)? Exchange(string clientId, string clientSecret, string totpCode)
    {
        var mock = settings.CurrentValue.Mock;
        var client = mock.Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret ?? string.Empty));
        var secretOk = CryptographicOperations.FixedTimeEquals(presented, client is null ? Dummy : Convert.FromHexString(client.SecretSha256));
        var now = time.GetUtcNow();
        // The code is checked whatever the secret gave, so the time taken does not tell which part was wrong.
        var codeOk = Totp.Verify(Totp.FromBase32(client?.TotpSecret ?? DummySeed), totpCode, now, out var step);
        if (client is null || !secretOk || !codeOk)
            return null;
        // One exchange per step: a code seen once is not accepted again.
        var accepted = false;
        _steps.AddOrUpdate(client.ClientId, _ => { accepted = true; return step; }, (_, last) =>
        {
            accepted = step > last;
            return accepted ? step : last;
        });
        if (!accepted)
            return null;

        Purge(now);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddMinutes(mock.TokenMinutes);
        _tokens[Digest(token)] = (client.ClientId, expires);
        return (token, expires);
    }

    /// <summary>The client a live token belongs to, or null.</summary>
    public MockAmanClient Validate(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128 || !_tokens.TryGetValue(Digest(token), out var entry) || entry.Expires <= time.GetUtcNow())
            return null;
        return settings.CurrentValue.Mock.Clients.FirstOrDefault(c => string.Equals(c.ClientId, entry.ClientId, StringComparison.Ordinal));
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var (key, value) in _tokens)
        {
            if (value.Expires <= now)
                _tokens.TryRemove(key, out _);
        }
    }

    private static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Authentication schemes of the mock partners: the mock AMAN's bearer token and the emulated AODB's API key.</summary>
public static class MockPartnerSchemes
{
    public const string AmanToken = "Ariva.Simulation.MockAman";
    public const string AodbKey = "Ariva.Simulation.MockAodb";
    public const string AmanPolicy = "simulation.mock-aman";
    public const string AodbPolicy = "simulation.mock-aodb";
    public const string AmanAuthLimit = "simulation.mock-aman-auth";
}

/// <summary>
/// The mock AMAN Integration API's scheme (CWE-287): <c>Authorization: Bearer</c> with a token from the exchange, and
/// <c>X-TOTP-Code</c> within one step of now on every call when the client's policy asks for it (no replay guard on
/// data calls, as AMAN and Ariva). Every failure is the same 401.
/// </summary>
internal sealed class MockAmanTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, MockAmanTokens tokens,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var client = tokens.Validate(header[7..].Trim());
        if (client is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        if (client.PerRequestTotp && !Totp.Verify(Totp.FromBase32(client.TotpSecret), Request.Headers["X-TOTP-Code"].ToString(), time.GetUtcNow(), out _))
            return Task.FromResult(AuthenticateResult.Fail("Invalid code."));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, client.ClientId)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

/// <summary>
/// The emulated AODB's ACRIS scheme (CWE-287): the API key in the configured header, hashed with SHA-256 and compared in
/// constant time with <see cref="AodbEmulatorSettings.AcrisKeySha256"/>; no key configured means nothing authenticates.
/// </summary>
internal sealed class MockAodbKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IOptionsMonitor<AodbEmulatorSettings> settings) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var current = settings.CurrentValue;
        var presented = Request.Headers[current.AcrisHeader].ToString();
        if (presented.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());
        if (string.IsNullOrEmpty(current.AcrisKeySha256) || presented.Length > 256 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), Convert.FromHexString(current.AcrisKeySha256)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid key."));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "acris")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
