using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ariva.Simulation.Api.Emulators.Integration;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>Where an observer's sign-in left it.</summary>
public enum ObserverSignIn
{
    /// <summary>Signed in with its permissions (not a pending account).</summary>
    SignedIn,

    /// <summary>Ariva refused the user name, password or code (one answer for every reason, as Ariva gives it).</summary>
    Refused,

    /// <summary>The account has an authenticator and no TOTP seed is configured for it.</summary>
    SecondFactorRequired,

    /// <summary>The account must still change its password or enrol an authenticator: it holds no permission yet.</summary>
    Pending,

    /// <summary>Ariva's sign-in limit for this address answered 429.</summary>
    RateLimited,

    /// <summary>Ariva could not be reached, or answered something else (a sign-in answer without a usable token among them).</summary>
    Unreachable
}

/// <summary>
/// What Ariva answered one call: the HTTP status (null when Ariva could not be reached) and the problem's title, if any;
/// <paramref name="ObserverOut"/> when the observer is out of the rehearsal (<see cref="ArivaObserverClient.TakenOut"/>), and
/// <paramref name="NotSent"/> when the call was never sent for that reason.
/// </summary>
internal sealed record ArivaAnswer(int? Status, string Title, bool ObserverOut = false, bool NotSent = false)
{
    public bool Succeeded => Status is >= 200 and < 300;

    /// <summary>
    /// Worth sending again with the same key and body: no answer, 408, 429 or a server error; never when the observer is out of
    /// the rehearsal (its sign-in failed, or Ariva refused a token it had just issued), so a disabled account costs no more sign-ins.
    /// </summary>
    public bool Retryable => !ObserverOut && (Status is null or 408 or 429 or >= 500);
}

/// <summary>
/// One emulated validation observer's client of Ariva.Api.Main (ARV-104i). It signs in as a normal Ariva account through the
/// normal sign-in (<c>POST api/auth/login</c> with the user name, the password and, when the account has an authenticator, a
/// TOTP code of a step it has not used yet, as Ariva's replay guard requires), keeps the access token in memory until a minute
/// before it expires, and calls the capture API with it. It has no other way in: no key, no header, no shortcut, and every
/// server check of the capture API applies to what it sends (CWE-287, CWE-306). The refresh cookie is never kept (cookies are
/// off); the password, the seed, the codes and the tokens are never logged.
/// <para>
/// Sign-ins are bounded so that an account Ariva no longer accepts (disabled, reset, locked) costs at most two sign-ins per
/// rehearsal and never holds Ariva's sign-in limit for the simulator's address or pushes the account into lockout: besides the
/// rehearsal's first sign-in (<see cref="SignInAsync"/>), one more while sending (a token about to expire, or a 401 to a token
/// issued earlier). When that sign-in fails, when the observer would need a third, or when Ariva answers 401 to a token it has
/// just issued, the token is dropped and the observer is taken out for the rest of the rehearsal (<see cref="TakenOut"/>): its
/// remaining items are not sent. <see cref="BeginRehearsal"/> gives it its chances back for the next rehearsal.
/// </para>
/// </summary>
internal sealed class ArivaObserverClient
{
    private const string PendingScope = "pending";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ArivaAnswer NotSentAnswer = new(null, null, ObserverOut: true, NotSent: true);
    private readonly ObserverCredentials _credentials;
    private readonly Func<HttpClient> _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly byte[] _seed;
    private readonly string _account;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _steps;
    private string _token;
    private DateTimeOffset _refreshAt;
    private bool _signedInAgain;

    // The token was issued and Ariva has not yet answered a call with it other than 401: a 401 to it is not tried again.
    private bool _unproven;

    /// <summary>
    /// <paramref name="steps"/> holds the last TOTP step each account used, by the user name as Ariva matches it
    /// (<see cref="ObserverCredentials.Normalize"/>), kept by the emulator across a replacement of the observers, so an account
    /// signed in again within the same 30 seconds, however its name is written, waits for the next step rather than be refused
    /// by Ariva's replay guard.
    /// </summary>
    public ArivaObserverClient(int observer, ObserverCredentials credentials, Func<HttpClient> http, TimeProvider time, ILogger logger,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> steps = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Observer = observer;
        _credentials = credentials;
        _http = http;
        _time = time;
        _logger = logger;
        _seed = credentials.HasSecondFactor ? Totp.FromBase32(credentials.TotpSecret) : null;
        _account = ObserverCredentials.Normalize(credentials.UserName);
        _steps = steps ?? new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>The observer's number in the rehearsal reports (1 upwards); never its user name.</summary>
    public int Observer { get; }

    /// <summary>
    /// The account's user name as Ariva matches it (<see cref="ObserverCredentials.Normalize"/>), for the Idempotency-Key of what
    /// it sends (hashed there, never shown or logged): the same account gives the same keys however its name is written.
    /// </summary>
    public string Account => _account;

    public bool HasSecondFactor => _seed is not null;

    public bool SignedIn => _token is not null && _time.GetUtcNow() < _refreshAt;

    /// <summary>Why the observer was taken out of the current rehearsal (fixed text, never a secret); null while it captures.</summary>
    public string TakenOut { get; private set; }

    /// <summary>Starts a rehearsal: the observer captures again and may sign in once more while sending.</summary>
    public void BeginRehearsal()
    {
        _signedInAgain = false;
        TakenOut = null;
    }

    /// <summary>Signs in unless a token is still good; the answer says why not.</summary>
    public async Task<ObserverSignIn> SignInAsync(CancellationToken ct)
    {
        if (SignedIn)
            return ObserverSignIn.SignedIn;
        _token = null;
        string code = null;
        if (_seed is not null)
        {
            // A step newer than the last one used (Ariva accepts a step once per account): wait for the next one when needed.
            var step = Totp.Step(_time.GetUtcNow());
            var last = _steps.GetValueOrDefault(_account, -1);
            if (step <= last)
            {
                var next = DateTimeOffset.FromUnixTimeSeconds((last + 1) * Totp.StepSeconds);
                var wait = next - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait + TimeSpan.FromMilliseconds(100), _time, ct);
                step = last + 1;
            }

            _steps[_account] = step;
            code = Totp.Code(_seed, step);
        }

        try
        {
            using var response = await _http().PostAsJsonAsync("api/auth/login",
                new { userName = _credentials.UserName, password = _credentials.Password, code }, Json, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Logged(ObserverSignIn.RateLimited, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var problem = await response.Content.ReadAsStringAsync(ct);
                return Logged(problem.Contains("mfa-required", StringComparison.Ordinal) ? ObserverSignIn.SecondFactorRequired : ObserverSignIn.Refused,
                    response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
                return Logged(ObserverSignIn.Unreachable, response.StatusCode);
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = answer.RootElement;
            // Every value is checked for its type before it is read (a wrongly typed one would throw), and the token for the
            // characters of a bearer token (it goes into a header): anything else is an answer without a usable token.
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String || token.GetString() is not { } value ||
                !IsBearerToken(value) ||
                !root.TryGetProperty("expiresIn", out var expiresIn) || expiresIn.ValueKind != JsonValueKind.Number ||
                !expiresIn.TryGetInt32(out var seconds) || seconds <= 0)
                return Logged(ObserverSignIn.Unreachable, response.StatusCode);
            if (root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String &&
                string.Equals(scope.GetString(), PendingScope, StringComparison.Ordinal))
                return Logged(ObserverSignIn.Pending, response.StatusCode);
            _token = value;
            _unproven = true;
            _refreshAt = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(10, seconds - 60));
            return Logged(ObserverSignIn.SignedIn, response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Validation observer {Observer} could not reach Ariva to sign in", Observer);
            return ObserverSignIn.Unreachable;
        }
    }

    private ObserverSignIn Logged(ObserverSignIn outcome, HttpStatusCode status)
    {
        _logger.LogInformation("Validation observer {Observer} sign-in: {Outcome} ({Status})", Observer, outcome, (int)status);
        return outcome;
    }

    /// <summary>The characters of an RFC 6750 bearer token (b64token), at most 8 KB: what may go into the Authorization header.</summary>
    private static bool IsBearerToken(string value)
    {
        if (value.Length is 0 or > 8192)
            return false;
        var end = value.Length;
        while (end > 1 && value[end - 1] == '=')
            end--;
        for (var i = 0; i < end; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(value[i]) || value[i] is '-' or '.' or '_' or '~' or '+' or '/'))
                return false;
        }

        return true;
    }

    /// <summary>Reads a JSON answer of Ariva's (at most the client's response buffer); the value is default when the call failed.</summary>
    public async Task<(ArivaAnswer Answer, T Value)> GetAsync<T>(string path, CancellationToken ct)
    {
        var (answer, body) = await SendOnceAsync(HttpMethod.Get, path, null, null, readBody: true, ct);
        if (!answer.Succeeded || body is null)
            return (answer, default);
        try
        {
            return (answer, JsonSerializer.Deserialize<T>(body, Json));
        }
        catch (JsonException)
        {
            return (answer with { Status = null, Title = "Ariva's answer is not readable" }, default);
        }
    }

    /// <summary>
    /// Sends one capture call with its Idempotency-Key, once more after a short wait when it is worth it (no answer, 408, 429 or
    /// 5xx), with the same key; <paramref name="body"/> is built again for the second send (a tracer batch reads its clock again).
    /// Nothing is sent once the observer is out of the rehearsal.
    /// </summary>
    public async Task<ArivaAnswer> SendAsync(HttpMethod method, string path, Func<object> body, string idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (TakenOut is not null)
            return NotSentAnswer;
        var (answer, _) = await SendOnceAsync(method, path, body(), idempotencyKey, readBody: false, ct);
        if (!answer.Retryable)
            return answer;
        await Task.Delay(TimeSpan.FromSeconds(1), _time, ct);
        (answer, _) = await SendOnceAsync(method, path, body(), idempotencyKey, readBody: false, ct);
        return answer;
    }

    private async Task<(ArivaAnswer Answer, string Body)> SendOnceAsync(HttpMethod method, string path, object body, string idempotencyKey, bool readBody,
        CancellationToken ct)
    {
        // At most two rounds: a 401 to a token issued earlier signs in once more; a 401 to a token just issued takes the observer out.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (TakenOut is not null)
                return (NotSentAnswer, null);
            if (!SignedIn)
            {
                // A sign-in while sending (the token is about to expire, or its session ended): one per rehearsal, tried once.
                if (_signedInAgain)
                    return (TakeOut(null, "it would need a third sign-in in one rehearsal"), null);
                _signedInAgain = true;
                var signIn = await SignInAsync(ct);
                if (signIn != ObserverSignIn.SignedIn)
                    return (TakeOut(null, $"its sign-in during the rehearsal answered {signIn}"), null);
            }

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (idempotencyKey is not null)
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);
            try
            {
                using var response = await _http().SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The token expired or its session ended: sign in again once; a token Ariva has just issued is not tried again.
                    _token = null;
                    if (_unproven)
                        return (TakeOut(401, "Ariva answered 401 to a token it had just issued"), null);
                    continue;
                }

                _unproven = false;
                var text = readBody || !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
                return (new ArivaAnswer((int)response.StatusCode, response.IsSuccessStatusCode ? null : TitleOf(text)), response.IsSuccessStatusCode ? text : null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (new ArivaAnswer(null, e is TaskCanceledException ? "timeout" : "Ariva unreachable"), null);
            }
        }

        return (TakeOut(401, "Ariva answered 401 to a token it had just issued"), null);
    }

    /// <summary>Takes the observer out of the rest of the rehearsal: its token is dropped and nothing more is sent (logged with its number and the reason only).</summary>
    private ArivaAnswer TakeOut(int? status, string reason)
    {
        _token = null;
        TakenOut = reason;
        _logger.LogWarning("Validation observer {Observer} is taken out of the rehearsal: {Reason}", Observer, reason);
        return new ArivaAnswer(status, $"The observer was taken out of the rehearsal: {reason}.", ObserverOut: true);
    }

    /// <summary>Signs out of the session, if any (best effort; the session would otherwise end when its token expires).</summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        var token = _token;
        _token = null;
        if (token is null)
            return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http().SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Validation observer {Observer} could not sign out; its session ends with its token", Observer);
        }
    }

    /// <summary>The title of Ariva's problem answer: Ariva's own fixed text (its answers never repeat a request value), cut to 200 characters.</summary>
    private static string TitleOf(string problem)
    {
        if (string.IsNullOrEmpty(problem))
            return null;
        try
        {
            using var document = JsonDocument.Parse(problem);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("title", out var title) &&
                   title.ValueKind == JsonValueKind.String && title.GetString() is { } text
                ? text.Length > 200 ? text[..200] : text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
