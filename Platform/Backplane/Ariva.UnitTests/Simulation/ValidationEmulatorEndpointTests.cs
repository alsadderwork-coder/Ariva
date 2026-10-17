using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Entities;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.Simulation.Api.Emulators.Validation;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-104i, CWE-287 and CWE-306: the validation observer emulator's endpoints. Its controls need an operator key with the
/// control scope (no key 401, a read key 403); observer credentials go in and never come back out; a rehearsal signs in
/// through Ariva.Api.Main's normal sign-in (a TOTP code of a fresh step for an account with an authenticator), sends the
/// scenario's truth through the capture API with an Idempotency-Key per item and the observer's own token, and reports what
/// Ariva answered: a resend is replayed, a campaign's creator is refused by Ariva and reported as refused. Hostile values are
/// refused without being echoed (a body over the limit gets 413 from Kestrel: the E2E run), one rehearsal runs at a time,
/// rehearsals are limited per key,
/// and no log line holds a password, seed, code or token. A stand-in for Ariva.Api.Main answers the sign-in and the capture API.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ValidationEmulatorEndpointTests
{
    #region Fakes

    private const string Route = "/api/v1/simulation/validation";
    private const string ReadKey = "sim-read-3f0a9c2e8b7d4e61a5c0f9b2d8e7a6c1";
    private const string ControlKey = "sim-ctl-7b1e4d9a2c6f8e03b5a7d1c9e4f2a8b6";
    private const string Observer1 = "sim.observer1";
    private const string Password1 = "observer-one-Pass-0a1b2c3d4e5f";
    private const string Seed1 = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Observer2 = "sim.observer2";
    private const string Password2 = "observer-two-Pass-6a7b8c9d0e1f";
    private const string Observer3 = "sim.observer3";
    private const string Password3 = "observer-three-Pass-1f2e3d4c5b6a";
    private const string Creator = "sim.creator";
    private const string CreatorPassword = "creator-Pass-2b3c4d5e6f7a";
    private const string OwnCampaignTitle = "The account that created or started this campaign does not capture for it.";
    private const string DayStart = "2026-10-08T20:00:00Z";

    private static readonly Guid CampaignId = Guid.Parse("0199a000-0000-7000-8000-0000000000c1");
    private static readonly Guid VisEntry = Guid.Parse("0199a000-0000-7000-8000-00000000e001");
    private static readonly Guid VisExit = Guid.Parse("0199a000-0000-7000-8000-00000000e002");
    private static readonly Guid VisZone = Guid.Parse("0199a000-0000-7000-8000-00000000f001");
    private static readonly Guid Desk08 = Guid.Parse("0199a000-0000-7000-8000-00000000d008");
    private static readonly Guid Desk09 = Guid.Parse("0199a000-0000-7000-8000-00000000d009");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>One call Ariva.Api.Main received: method, address, bearer token, Idempotency-Key and body.</summary>
    private sealed record Call(string Method, Uri Uri, string Bearer, string Key, string Body);

    /// <summary>The simulator's clock, real time moved on by <see cref="Offset"/> (a token's lifetime passes without waiting for it).</summary>
    private sealed class OffsetClock : TimeProvider
    {
        private long _offsetTicks;

        public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    /// <summary>
    /// Ariva.Api.Main stand-in: the sign-in (password, and a TOTP code of a step newer than the last for an account with an
    /// authenticator; one 401 for every failure), the capture list of site DMO with one running campaign, and the three capture
    /// writes (201, a resend with the same key and body 200, the same key with another body 409; 403 for the campaign's creator).
    /// Tokens expire after <see cref="ExpiresIn"/> seconds of <see cref="Clock"/>; after <see cref="RevokeAfterCaptures"/> capture
    /// writes every token issued so far is revoked (an account disabled or reset mid-rehearsal), after which sign-ins can be refused
    /// (<see cref="RefuseSignInsAfterRevoke"/>) or every token answered 401 (<see cref="RefuseTokensAfterRevoke"/>).
    /// </summary>
    private sealed class FakeMain : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Password, string Seed)> _accounts = new(StringComparer.Ordinal)
        {
            [Observer1] = (Password1, Seed1),
            [Observer2] = (Password2, null),
            [Observer3] = (Password3, null),
            [Creator] = (CreatorPassword, null)
        };

        private readonly ConcurrentDictionary<string, (string User, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _lastStep = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string User, string Key), string> _stored = new();
        private int _issued;
        private int _captures;
        private volatile bool _revoked;

        public ConcurrentQueue<Call> Calls { get; } = new();

        /// <summary>When set, a sign-in waits for it (a rehearsal held in progress).</summary>
        public SemaphoreSlim Hold { get; set; }

        public TimeProvider Clock { get; init; } = TimeProvider.System;

        public int ExpiresIn { get; init; } = 900;

        public int? RevokeAfterCaptures { get; init; }

        public bool RefuseSignInsAfterRevoke { get; init; }

        public bool RefuseTokensAfterRevoke { get; init; }

        /// <summary>After this many capture writes the clock moves on by <see cref="Advance"/>.</summary>
        public int? AdvanceAfterCaptures { get; init; }

        public TimeSpan Advance { get; init; }

        public int Stored => _stored.Count;

        public int Logins(string user) =>
            Calls.Count(c => c.Uri.AbsolutePath == "/api/auth/login" && JsonDocument.Parse(c.Body).RootElement.GetProperty("userName").GetString() == user);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
            Calls.Enqueue(new Call(request.Method.Method, request.RequestUri, request.Headers.Authorization?.Parameter, key, body));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login")
            {
                if (Hold is not null)
                    await Hold.WaitAsync(ct);
                return SignIn(JsonDocument.Parse(body!).RootElement);
            }

            if (request.Headers.Authorization?.Parameter is not { } bearer || !_tokens.TryGetValue(bearer, out var issued) || issued.Expires <= Clock.GetUtcNow() ||
                (_revoked && RefuseTokensAfterRevoke))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var user = issued.User;
            if (path == "/api/auth/logout")
            {
                _tokens.TryRemove(bearer, out _);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            const string capture = "/api/v1/sites/DMO/validation/capture/campaigns";
            if (request.Method == HttpMethod.Get && path == capture)
                return Json(HttpStatusCode.OK, CampaignJson);
            if (request.Method != HttpMethod.Post || !path.StartsWith($"{capture}/{CampaignId:D}/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            try
            {
                if (user == Creator)
                    return Json(HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { title = OwnCampaignTitle, status = 403 }));
                // A tracer batch is the same request whatever clock reading it carries (Ariva's fingerprint leaves it out).
                var fingerprint = Regex.Replace(body!, "\"deviceClockUtc\":\"[^\"]*\"", string.Empty);
                if (_stored.TryGetValue((user, key), out var stored))
                    return new HttpResponseMessage(stored == fingerprint ? HttpStatusCode.OK : HttpStatusCode.Conflict) { Content = new StringContent("{}") };
                _stored[(user, key)] = fingerprint;
                return Json(HttpStatusCode.Created, "{}");
            }
            finally
            {
                var captures = Interlocked.Increment(ref _captures);
                if (captures == RevokeAfterCaptures)
                {
                    _revoked = true;
                    _tokens.Clear();
                }

                if (captures == AdvanceAfterCaptures && Clock is OffsetClock clock)
                    clock.Advance(Advance);
            }
        }

        private HttpResponseMessage SignIn(JsonElement login)
        {
            var user = login.GetProperty("userName").GetString();
            var password = login.GetProperty("password").GetString();
            var code = login.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var refused = Json(HttpStatusCode.Unauthorized, "{\"type\":\"https://ariva/problems/invalid-credentials\",\"title\":\"Sign-in failed\"}");
            if (_revoked && RefuseSignInsAfterRevoke)
                return refused;
            if (user is null || !_accounts.TryGetValue(user, out var account) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(account.Password)), SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty))))
                return refused;
            if (account.Seed is not null)
            {
                if (code is null)
                    return Json(HttpStatusCode.Unauthorized, "{\"type\":\"https://ariva/problems/mfa-required\"}");
                if (!Totp.Verify(Totp.FromBase32(account.Seed), code, Clock.GetUtcNow(), out var step) || step <= _lastStep.GetValueOrDefault(user, -1))
                    return refused;
                _lastStep[user] = step;
            }

            var token = string.Create(CultureInfo.InvariantCulture, $"tok-{user}-{Interlocked.Increment(ref _issued)}-0a1b2c3d4e5f6a7b8c9d");
            _tokens[token] = (user, Clock.GetUtcNow().AddSeconds(ExpiresIn));
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { accessToken = token, tokenType = "Bearer", expiresIn = ExpiresIn, scope = (string)null }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static readonly string CampaignJson = JsonSerializer.Serialize(new[]
    {
        new
        {
            id = CampaignId, siteCode = "DMO", name = "Rehearsal", timeZoneId = "Asia/Dubai", days = new[] { "2026-10-09" }, binMinutes = 15,
            lines = new[]
            {
                new { id = VisEntry, name = "A-VIS entry", role = "Entry", queueZone = "A-VIS" },
                new { id = VisExit, name = "A-VIS exit", role = "Exit", queueZone = "A-VIS" }
            },
            zones = new[] { new { id = VisZone, name = "A-VIS" } },
            desksIncluded = true,
            desks = new[] { new { id = Desk08, checkpoint = "IMM", code = "AR-08" }, new { id = Desk09, checkpoint = "IMM", code = "AR-09" } },
            maxClockOffsetSeconds = 300
        }
    });

    private sealed class Capture : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture capture) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                capture.Lines.Enqueue("scope: " + state);
                return null;
            }

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter) =>
                capture.Lines.Enqueue(formatter(state, exception) + " " + state + " " + exception);
        }
    }

    private static IArivaHost Host(FakeMain main, bool mainUrl = true, int perMinute = 10, Capture capture = null, int truthPerMinute = 60) =>
        ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-reader");
            builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ReadKey));
            builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "read");
            builder.UseSetting("Simulation:Control:Keys:1:Name", "demo-operator");
            builder.UseSetting("Simulation:Control:Keys:1:Sha256", Digest(ControlKey));
            builder.UseSetting("Simulation:Control:Keys:1:Scopes:0", "read");
            builder.UseSetting("Simulation:Control:Keys:1:Scopes:1", "control");
            builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty);
            builder.UseSetting("Simulation:Ariva:MainUrl", mainUrl ? "https://api-main-service" : string.Empty);
            builder.UseSetting("Simulation:Validation:RehearsalsPerMinute", perMinute.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Simulation:Validation:TruthReadsPerMinute", truthPerMinute.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(ValidationEmulator.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => main);
                // The simulator's clock is the stand-in's, so a token's lifetime can pass between two captures.
                if (main.Clock is OffsetClock clock)
                    services.AddSingleton<TimeProvider>(clock);
                if (capture is not null)
                    services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(capture);
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string key, string json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, Ct);
    }

    private static string Observers(params (string User, string Password, string Seed)[] observers) =>
        JsonSerializer.Serialize(new { observers = observers.Select(o => new { userName = o.User, password = o.Password, totpSecret = o.Seed }) });

    private static string Rehearsal(object extra = null)
    {
        var body = new Dictionary<string, object>
        {
            ["siteCode"] = "DMO", ["campaignId"] = CampaignId, ["scenarioSeed"] = 9303, ["dayStartUtc"] = DayStart, ["fromMinute"] = 1080, ["toMinute"] = 1110
        };
        foreach (var property in extra?.GetType().GetProperties() ?? [])
            body[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(extra);
        return JsonSerializer.Serialize(body);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    #endregion

    [Theory]
    [InlineData("PUT", "/observers")]
    [InlineData("POST", "/rehearsals")]
    public async Task Controls_Should_Return401WithoutAKeyAnd403ForAReadKey_When_Called(string method, string path)
    {
        await using var app = Host(new FakeMain());
        using var client = app.CreateClient();
        var json = path == "/observers" ? Observers((Observer2, Password2, null)) : Rehearsal();

        using var anonymous = await SendAsync(client, new HttpMethod(method), Route + path, null, json);
        using var reader = await SendAsync(client, new HttpMethod(method), Route + path, ReadKey, json);
        using var status = await SendAsync(client, HttpMethod.Get, Route, ReadKey);
        using var truth = await SendAsync(client, HttpMethod.Get, $"{Route}/truth?dayStartUtc={DayStart}&fromMinute=1080&toMinute=1110&queues=A-VIS", ReadKey);
        using var guessed = await SendAsync(client, HttpMethod.Get, Route, "sim-guess-00000000000000000000000000000000");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        reader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        truth.StatusCode.Should().Be(HttpStatusCode.OK);
        guessed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Observers_Should_NeverEchoACredentialAndRefuseMalformedOnes_When_Replaced()
    {
        await using var app = Host(new FakeMain());
        using var client = app.CreateClient();

        using var good = await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, Seed1), (Observer2, Password2, null)));
        var text = await good.Content.ReadAsStringAsync(Ct);

        good.StatusCode.Should().Be(HttpStatusCode.OK);
        text.Should().NotContain(Observer1).And.NotContain(Password1).And.NotContain(Seed1).And.NotContain(Observer2).And.NotContain(Password2);
        var observers = (await JsonOf(good)).GetProperty("observers");
        observers.GetArrayLength().Should().Be(2);
        observers[0].GetProperty("secondFactor").GetBoolean().Should().BeTrue();
        observers[1].GetProperty("secondFactor").GetBoolean().Should().BeFalse();
        (await (await SendAsync(client, HttpMethod.Get, Route, ReadKey)).Content.ReadAsStringAsync(Ct)).Should().NotContain(Password1).And.NotContain(Observer1);

        foreach (var bad in new[]
                 {
                     Observers(("leaked\u0007user", "leaked-password-1", null)),
                     Observers(("leaked.user", "leaked-password-2", "not base32 leaked!")),
                     Observers(("leaked.user", "", null)),
                     Observers((Observer1, "leaked-password-3", null), (Observer1, "leaked-password-4", null)),
                     Observers([.. Enumerable.Range(0, 9).Select(i => ("leaked" + i.ToString(CultureInfo.InvariantCulture), "leaked-password-5", (string)null))]),
                     "{\"observers\":[]}",
                     "{}"
                 })
        {
            using var refused = await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, bad);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().NotContain("leaked").And.NotContain("base32 leaked");
        }
    }

    [Fact]
    public async Task Rehearsal_Should_SignInThroughTheNormalSignInAndSendTheTruthOncePerItem_When_NoErrorIsInjected()
    {
        var main = new FakeMain();
        await using var app = Host(main);
        using var client = app.CreateClient();
        (await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, Seed1), (Observer2, Password2, null))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var first = await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var report = await JsonOf(first);

        report.GetProperty("outcome").GetString().Should().Be("Completed");
        report.GetProperty("scenarioSeed").GetUInt32().Should().Be(9303);
        report.GetProperty("runBy").GetString().Should().Be("demo-operator");
        report.GetProperty("observers").EnumerateArray().Select(o => o.GetProperty("state").GetString()).Should().Equal("Capturing", "Capturing");
        report.GetProperty("counts").GetProperty("planned").GetInt32().Should().Be(4, "two lines, two bins");
        report.GetProperty("counts").GetProperty("recorded").GetInt32().Should().Be(4);
        report.GetProperty("deskBatches").GetProperty("recorded").GetInt32().Should().Be(4, "AR-08 and AR-09 to the two observers, two bins");
        report.GetProperty("tracerBatches").GetProperty("recorded").GetInt32().Should().BeGreaterThan(0);
        report.GetProperty("tracerRuns").GetInt32().Should().Be(3, "one tracer every 10 minutes from 18:01 to 18:30");
        report.GetProperty("deskMinutes").GetInt32().Should().Be(60);
        report.GetProperty("refusals").GetArrayLength().Should().Be(0);
        var text = await first.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain(Observer1).And.NotContain(Password1).And.NotContain("tok-");

        var calls = main.Calls.ToList();
        calls.Should().OnlyContain(c => c.Uri.Host == "api-main-service", "the simulator calls only the configured Ariva.Api.Main");
        var logins = calls.Where(c => c.Uri.AbsolutePath == "/api/auth/login").Select(c => JsonDocument.Parse(c.Body).RootElement).ToList();
        logins.Should().HaveCount(2, "each observer signs in once through the normal sign-in");
        logins[0].GetProperty("userName").GetString().Should().Be(Observer1);
        logins[0].GetProperty("code").GetString().Should().MatchRegex("^[0-9]{6}$", "the account has an authenticator: a TOTP code goes with the password");
        logins[1].GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        var writes = calls.Where(c => c.Method == "POST" && c.Uri.AbsolutePath != "/api/auth/login").ToList();
        writes.Should().OnlyContain(c => c.Bearer.StartsWith("tok-sim.observer", StringComparison.Ordinal) && ManualCount.IsIdempotencyKey(c.Key));
        writes.Select(c => c.Key).Should().OnlyHaveUniqueItems();

        // What was counted is the truth: the scenario's entries and exits of A-VIS per bin.
        using var truthAnswer = await SendAsync(client, HttpMethod.Get, $"{Route}/truth?dayStartUtc={DayStart}&fromMinute=1080&toMinute=1110&queues=A-VIS", ReadKey);
        var minutes = (await JsonOf(truthAnswer)).GetProperty("queues")[0].GetProperty("minutes").EnumerateArray().ToList();
        var counts = writes.Where(c => c.Uri.AbsolutePath.EndsWith("/counts", StringComparison.Ordinal)).Select(c => JsonDocument.Parse(c.Body).RootElement).ToList();
        foreach (var (bin, offset) in new[] { ("2026-10-09T14:00:00.000Z", 0), ("2026-10-09T14:15:00.000Z", 15) })
        {
            var entries = minutes.Skip(offset).Take(15).Sum(m => m.GetProperty("entries").GetInt32());
            var exits = minutes.Skip(offset).Take(15).Sum(m => m.GetProperty("exits").GetInt32());
            counts.Single(c => c.GetProperty("binStartUtc").GetString() == bin && c.GetProperty("lineId").GetGuid() == VisEntry).GetProperty("crossingsIn").GetInt32()
                .Should().Be(entries).And.BeGreaterThan(0);
            counts.Single(c => c.GetProperty("binStartUtc").GetString() == bin && c.GetProperty("lineId").GetGuid() == VisExit).GetProperty("crossingsOut").GetInt32()
                .Should().Be(exits);
        }

        var desks = writes.Where(c => c.Uri.AbsolutePath.EndsWith("/desk-observations", StringComparison.Ordinal)).Select(c => JsonDocument.Parse(c.Body).RootElement).ToList();
        desks.Should().OnlyContain(d => d.GetProperty("desks").GetArrayLength() == 1 && d.GetProperty("desks")[0].GetProperty("states").GetArrayLength() == 15);
        var tracers = writes.Where(c => c.Uri.AbsolutePath.EndsWith("/tracer-runs", StringComparison.Ordinal)).Select(c => JsonDocument.Parse(c.Body).RootElement).ToList();
        tracers.SelectMany(t => t.GetProperty("runs").EnumerateArray()).Select(r => r.GetProperty("tracerCode").GetString()).Order(StringComparer.Ordinal)
            .Should().Equal("T-01", "T-02", "T-03");
        tracers.Should().OnlyContain(t => DateTime.Parse(t.GetProperty("deviceClockUtc").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) > DateTime.UtcNow.AddMinutes(-5),
            "the device clock is read when the batch is sent");

        // Sent again: every item is the same request under the same key, so Ariva returns what it stored; nothing is stored twice.
        var stored = main.Stored;
        using var again = await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
        var replayed = await JsonOf(again);
        replayed.GetProperty("counts").GetProperty("replayed").GetInt32().Should().Be(4);
        replayed.GetProperty("counts").GetProperty("recorded").GetInt32().Should().Be(0);
        replayed.GetProperty("deskBatches").GetProperty("replayed").GetInt32().Should().Be(4);
        main.Stored.Should().Be(stored);
        main.Calls.Count(c => c.Uri.AbsolutePath == "/api/auth/login").Should().Be(2, "the tokens are kept until a minute before they expire");

        var status = await JsonOf(await SendAsync(client, HttpMethod.Get, Route, ReadKey));
        status.GetProperty("last").GetProperty("counts").GetProperty("replayed").GetInt32().Should().Be(4);
        status.GetProperty("observers").EnumerateArray().Should().OnlyContain(o => o.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task Rehearsal_Should_ReportArivasRefusalWithoutWorkingAroundIt_When_TheObserverCreatedTheCampaign()
    {
        var main = new FakeMain();
        await using var app = Host(main);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Creator, CreatorPassword, null)));

        var report = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));

        report.GetProperty("outcome").GetString().Should().Be("Completed");
        report.GetProperty("counts").GetProperty("refused").GetInt32().Should().Be(4);
        report.GetProperty("counts").GetProperty("recorded").GetInt32().Should().Be(0);
        report.GetProperty("deskBatches").GetProperty("refused").GetInt32().Should().Be(2, "one observer logs both desks, one batch per bin");
        report.GetProperty("tracerBatches").GetProperty("refused").GetInt32().Should().Be(1);
        report.GetProperty("tracerRuns").GetInt32().Should().Be(0);
        report.GetProperty("refusals").EnumerateArray().Should().OnlyContain(r => r.GetProperty("status").GetInt32() == 403 && r.GetProperty("title").GetString() == OwnCampaignTitle);
        main.Stored.Should().Be(0);
        main.Calls.Where(c => c.Method == "POST" && c.Uri.AbsolutePath != "/api/auth/login").Should()
            .OnlyContain(c => c.Bearer.StartsWith("tok-sim.creator", StringComparison.Ordinal), "the refused account's own token, nothing else");
    }

    /// <summary>The report's tallies of the three kinds of call, summed: (planned, recorded, refused, failed, not sent).</summary>
    private static (int Planned, int Recorded, int Refused, int Failed, int NotSent) Totals(JsonElement report)
    {
        var kinds = new[] { "counts", "tracerBatches", "deskBatches" }.Select(k => report.GetProperty(k)).ToList();
        int Sum(string name) => kinds.Sum(k => k.GetProperty(name).GetInt32());
        return (Sum("planned"), Sum("recorded"), Sum("refused"), Sum("failed"), Sum("notSent"));
    }

    [Fact]
    public async Task Rehearsal_Should_SignInExactlyOnceMorePerObserverAndRecordTheRest_When_AriVaEndsTheSessionsMidRehearsal()
    {
        // CWE-287: Ariva ends every session after the third capture (a restart of its session store, say); each observer signs in
        // once more on its next call and carries on.
        var main = new FakeMain { RevokeAfterCaptures = 3 };
        await using var app = Host(main);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null), (Observer3, Password3, null)));

        var report = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));

        report.GetProperty("outcome").GetString().Should().Be("Completed");
        report.GetProperty("observers").EnumerateArray().Select(o => o.GetProperty("state").GetString()).Should().Equal("Capturing", "Capturing");
        var totals = Totals(report);
        totals.Recorded.Should().Be(totals.Planned, "every item is recorded under the observer's new session");
        (totals.Refused, totals.Failed, totals.NotSent).Should().Be((0, 0, 0));
        report.GetProperty("refusals").GetArrayLength().Should().Be(0);
        main.Logins(Observer2).Should().Be(2, "its first sign-in and exactly one more after Ariva ended its session");
        main.Logins(Observer3).Should().Be(2);
        main.Stored.Should().Be(totals.Planned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rehearsal_Should_TakeTheObserverOutAfterOneMoreSignIn_When_AriVaNoLongerAcceptsTheAccount(bool refuseSignIn)
    {
        // CWE-287: the accounts are disabled after the third capture. Either the sign-in is refused, or Ariva answers 401 even to
        // the token it has just issued. Each observer tries one more sign-in, then is out for the rest of the rehearsal: no
        // further sign-in, so the account is never pushed into lockout and the simulator's address never holds the sign-in limit.
        var main = new FakeMain { RevokeAfterCaptures = 3, RefuseSignInsAfterRevoke = refuseSignIn, RefuseTokensAfterRevoke = !refuseSignIn };
        await using var app = Host(main);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null), (Observer3, Password3, null)));

        var report = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));

        report.GetProperty("outcome").GetString().Should().Be("Completed");
        report.GetProperty("observers").EnumerateArray().Select(o => o.GetProperty("state").GetString()).Should().Equal("TakenOut", "TakenOut");
        var totals = Totals(report);
        totals.Recorded.Should().Be(3, "what was sent before the accounts were disabled");
        totals.Failed.Should().Be(2, "the call that took each observer out");
        totals.NotSent.Should().Be(totals.Planned - 5, "nothing more is sent for an observer taken out");
        totals.Refused.Should().Be(0);
        report.GetProperty("refusals").EnumerateArray().Should().HaveCount(2).And
            .OnlyContain(r => r.GetProperty("title").GetString()!.StartsWith("The observer was taken out of the rehearsal", StringComparison.Ordinal));
        main.Logins(Observer2).Should().Be(2, "its first sign-in and one more, never a third");
        main.Logins(Observer3).Should().Be(2);
        // Two sign-ins and campaign reads, three captures, then per observer: the 401, the sign-in and (when it was let in) the 401 to its new token.
        main.Calls.Count.Should().Be(refuseSignIn ? 11 : 13);
        var status = await JsonOf(await SendAsync(client, HttpMethod.Get, Route, ReadKey));
        status.GetProperty("observers").EnumerateArray().Should().OnlyContain(o => !o.GetProperty("signedIn").GetBoolean(), "the refused token is dropped");

        // The next rehearsal gives each observer its chances again, still bounded: one sign-in each, refused, or let in and its
        // token answered 401 at once (a token Ariva has just issued is not signed in for again).
        var again = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));
        again.GetProperty("outcome").GetString().Should().Be("NoObserverCanCapture");
        again.GetProperty("observers").EnumerateArray().Select(o => o.GetProperty("state").GetString()).Should()
            .Equal(refuseSignIn ? ["Refused", "Refused"] : ["TakenOut", "TakenOut"]);
        main.Logins(Observer2).Should().Be(3, "two in the first rehearsal, one in the second");
        main.Logins(Observer3).Should().Be(3);
    }

    [Fact]
    public async Task Rehearsal_Should_RenewATokenNearItsExpiryWithANewTotpStep_When_ItsLifetimePassesMidRehearsal()
    {
        // Ariva's token lives 120 seconds; the simulator keeps it until a minute before. After the second capture 61 seconds pass:
        // the observer signs in again before its next call, with the code of a newer step, and the rest goes with the new token.
        var clock = new OffsetClock();
        var main = new FakeMain { Clock = clock, ExpiresIn = 120, AdvanceAfterCaptures = 2, Advance = TimeSpan.FromSeconds(61) };
        await using var app = Host(main);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, Seed1)));

        var report = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));

        report.GetProperty("outcome").GetString().Should().Be("Completed");
        var totals = Totals(report);
        totals.Recorded.Should().Be(totals.Planned);
        report.GetProperty("refusals").GetArrayLength().Should().Be(0);
        var calls = main.Calls.ToList();
        var logins = calls.Select((c, i) => (Call: c, Index: i)).Where(c => c.Call.Uri.AbsolutePath == "/api/auth/login").ToList();
        logins.Should().HaveCount(2, "the first sign-in and the renewal");
        var key = Totp.FromBase32(Seed1);
        long StepOf(Call login)
        {
            var code = JsonDocument.Parse(login.Body).RootElement.GetProperty("code").GetString();
            var now = Totp.Step(clock.GetUtcNow());
            return Enumerable.Range(0, 20).Select(back => now + 1 - back).First(step => Totp.Code(key, step) == code);
        }

        StepOf(logins[1].Call).Should().BeGreaterThan(StepOf(logins[0].Call), "Ariva accepts each step once per account: the renewal uses a newer one");
        var writes = calls.Select((c, i) => (Call: c, Index: i)).Where(c => c.Call.Method == "POST" && c.Call.Uri.AbsolutePath.Contains("/capture/", StringComparison.Ordinal)).ToList();
        writes.Where(w => w.Index < logins[1].Index).Should().HaveCount(2).And.OnlyContain(w => w.Call.Bearer.StartsWith("tok-sim.observer1-1-", StringComparison.Ordinal));
        writes.Where(w => w.Index > logins[1].Index).Should().NotBeEmpty().And.OnlyContain(w => w.Call.Bearer.StartsWith("tok-sim.observer1-2-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rehearsal_Should_ReportTheObserversState_When_SignInIsRefusedOrNeedsASecondFactor()
    {
        var main = new FakeMain();
        await using var app = Host(main);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, "wrong-password-0000", Seed1), (Observer1 + "x", Password1, null)));

        var report = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));
        report.GetProperty("outcome").GetString().Should().Be("NoObserverCanCapture");
        report.GetProperty("observers").EnumerateArray().Select(o => o.GetProperty("state").GetString()).Should().Equal("Refused", "Refused");

        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, null)));
        var second = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal()));
        second.GetProperty("observers")[0].GetProperty("state").GetString().Should().Be("SecondFactorRequired");

        // The site is Ariva's to check against the account (SiteScopeTests: the simulator only forwards it): another site is 404 there.
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null)));
        var elsewhere = await JsonOf(await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal(new { siteCode = "XS2" })));
        elsewhere.GetProperty("outcome").GetString().Should().Be("NoObserverCanCapture");
        elsewhere.GetProperty("observers")[0].GetProperty("state").GetString().Should().Be("SiteNotVisible");
        main.Calls.Should().Contain(c => c.Uri.AbsolutePath == "/api/v1/sites/XS2/validation/capture/campaigns");
        main.Stored.Should().Be(0);
    }

    /// <summary>The SQL line comment, built so that no double hyphen appears in the source text (as tests/support/payloads.ts does).</summary>
    private static readonly string SqlComment = new('-', 2);

    private static IEnumerable<string> HostileRehearsals()
    {
        foreach (var payload in new[] { "' OR 1=1 " + SqlComment, "<script>alert(1)</script>", "DMO'; DROP TABLE manual_count; " + SqlComment, "dmo", "ABCDEFGHIJ-KLMNOPQRS" })
            yield return Rehearsal(new { siteCode = payload });
        yield return Rehearsal(new { campaignId = Guid.Empty });
        yield return Rehearsal(new { scenarioSite = "<img src=x onerror=alert(1)>" });
        yield return Rehearsal(new { scenarioSeed = -1 });
        foreach (var payload in new[] { "2026-10-08T20:00:00", "<script>alert(1)</script>", "1999-12-31T20:00:00Z", "2026-13-40T20:00:00Z", "' OR 1=1 " + SqlComment + "Z" })
            yield return Rehearsal(new { dayStartUtc = payload });
        yield return Rehearsal(new { fromMinute = 1440 });
        yield return Rehearsal(new { toMinute = 1080 });
        yield return Rehearsal(new { fromMinute = 600, toMinute = 841 });
        yield return Rehearsal(new { countErrorPercent = 51 });
        yield return Rehearsal(new { countErrorLines = new[] { "<script>alert(1)</script>\n" } });
        yield return Rehearsal(new { countErrorLines = new[] { "<script>alert(1)</script>" } });
        yield return Rehearsal(new { countErrorLines = new[] { "A-VIS entry\" onmouseover=\"alert(1)" } });
        yield return Rehearsal(new { tracerErrorMinutes = -31 });
        yield return Rehearsal(new { missedBinsPercent = 101 });
        yield return Rehearsal(new { tracerEveryMinutes = 0 });
        yield return Rehearsal(new { seed = 4294967296L });
        yield return Rehearsal(new { campaignId = "<script>alert(1)</script>" });
        yield return "{\"<script>alert(1)</script>\": ";
        yield return "[1,2,3]";
    }

    [Fact]
    public async Task Rehearsal_Should_Return400WithoutEchoingTheValue_When_ARequestValueIsHostile()
    {
        var main = new FakeMain();
        await using var app = Host(main, perMinute: 1);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null)));

        foreach (var json in HostileRehearsals())
        {
            using var refused = await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, json);
            var text = await refused.Content.ReadAsStringAsync(Ct);

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, json);
            text.Should().NotContain("<script").And.NotContain("onerror").And.NotContain("onmouseover").And.NotContain("OR 1=1").And.NotContain("DROP TABLE").And.NotContain("Ariva.Simulation");
        }

        main.Calls.Should().BeEmpty("nothing is sent to Ariva for a refused request");
        using var valid = await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
        valid.StatusCode.Should().Be(HttpStatusCode.OK, "a refused request does not use the key's rehearsal of the minute");
    }

    [Fact]
    public async Task Rehearsal_Should_Answer409_When_ItCannotStart()
    {
        await using (var app = Host(new FakeMain(), mainUrl: false))
        {
            using var client = app.CreateClient();
            await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null)));
            (await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal())).StatusCode.Should().Be(HttpStatusCode.Conflict, "no Ariva address");
        }

        await using var other = Host(new FakeMain());
        using var second = other.CreateClient();
        (await SendAsync(second, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal())).StatusCode.Should().Be(HttpStatusCode.Conflict, "no observer");
        await SendAsync(second, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null)));
        (await SendAsync(second, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal(new { scenarioSeed = 7 }))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "the reference day runs with seed 9303");
    }

    [Fact]
    public async Task Rehearsal_Should_RunOneAtATimeAndAFewAMinutePerKey_When_RequestsOverlap()
    {
        var main = new FakeMain { Hold = new SemaphoreSlim(0) };
        await using var app = Host(main, perMinute: 2);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, Password2, null)));

        var running = SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
        for (var i = 0; i < 100 && !main.Calls.Any(c => c.Uri.AbsolutePath == "/api/auth/login"); i++)
            await Task.Delay(50, Ct);

        using var overlapping = await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
        using var replace = await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, Seed1)));
        (await JsonOf(await SendAsync(client, HttpMethod.Get, Route, ReadKey))).GetProperty("running").GetBoolean().Should().BeTrue();
        main.Hold.Release(10);
        using var finished = await running;
        using var limited = await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());

        overlapping.StatusCode.Should().Be(HttpStatusCode.Conflict);
        replace.StatusCode.Should().Be(HttpStatusCode.Conflict, "observers are not replaced under a running rehearsal");
        finished.StatusCode.Should().Be(HttpStatusCode.OK);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "two rehearsals a minute for this key, the overlapping one counted");
        limited.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Truth_Should_AnswerTheEveningWithS17AndRefuseBadWindowsWithoutEchoingThem_When_Read()
    {
        await using var app = Host(new FakeMain());
        using var client = app.CreateClient();

        using var answer = await SendAsync(client, HttpMethod.Get, $"{Route}/truth?site=DMO&dayStartUtc={DayStart}&fromMinute=1080&toMinute=1170&queues=A-VIS,A-RES", ReadKey);
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        var truth = await JsonOf(answer);
        truth.GetProperty("scenarioSeed").GetUInt32().Should().Be(9303);
        truth.GetProperty("queues").EnumerateArray().Select(q => q.GetProperty("queue").GetString()).Should().Equal("A-RES", "A-VIS");
        truth.GetProperty("queues")[1].GetProperty("minutes").GetArrayLength().Should().Be(90);
        truth.GetProperty("queues")[1].GetProperty("minutes")[0].GetProperty("startUtc").GetString().Should().Be("2026-10-09T14:00:00Z");
        truth.GetProperty("desks").EnumerateArray().Select(d => d.GetProperty("desk").GetString()).Should().Contain(["AR-05", "AR-08", "AR-22"]);
        truth.GetProperty("desks").EnumerateArray().Should().OnlyContain(d => d.GetProperty("states").GetArrayLength() == 90);
        var outage = truth.GetProperty("outages").EnumerateArray().Single();
        (outage.GetProperty("sensor").GetString(), outage.GetProperty("queueZone").GetString(), outage.GetProperty("fromUtc").GetString(), outage.GetProperty("toUtc").GetString())
            .Should().Be(("S-17", "A-VIS", "2026-10-09T14:20:00Z", "2026-10-09T14:30:00Z"));

        foreach (var query in new[]
                 {
                     $"dayStartUtc={DayStart}&fromMinute=%3Cscript%3E&toMinute=1170",
                     $"dayStartUtc={DayStart}&fromMinute=1080&toMinute=1441%27%20OR%201%3D1",
                     $"dayStartUtc={DayStart}&fromMinute=600&toMinute=961",
                     "dayStartUtc=%3Cscript%3Ealert(1)%3C%2Fscript%3E&fromMinute=1080&toMinute=1170",
                     $"dayStartUtc={DayStart}&fromMinute=1080&toMinute=1170&queues=" + Uri.EscapeDataString(new string('Q', 40))
                 })
        {
            using var refused = await SendAsync(client, HttpMethod.Get, $"{Route}/truth?{query}", ReadKey);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().NotContain("script").And.NotContain("OR 1=1").And.NotContain("QQQQ");
        }

        using var unknown = await SendAsync(client, HttpMethod.Get, $"{Route}/truth?site=%3Cscript%3E&dayStartUtc={DayStart}&fromMinute=1080&toMinute=1170", ReadKey);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await unknown.Content.ReadAsStringAsync(Ct)).Should().NotContain("script");
    }

    [Fact]
    public async Task Truth_Should_Answer429WithRetryAfter_When_AKeyReadsMoreThanItsShareOfTheMinute()
    {
        await using var app = Host(new FakeMain(), truthPerMinute: 2);
        using var client = app.CreateClient();
        var path = $"{Route}/truth?dayStartUtc={DayStart}&fromMinute=1080&toMinute=1090&queues=A-VIS";

        using var first = await SendAsync(client, HttpMethod.Get, path, ReadKey);
        using var second = await SendAsync(client, HttpMethod.Get, path, ReadKey);
        using var third = await SendAsync(client, HttpMethod.Get, path, ReadKey);
        using var otherKey = await SendAsync(client, HttpMethod.Get, path, ControlKey);

        (first.StatusCode, second.StatusCode).Should().Be((HttpStatusCode.OK, HttpStatusCode.OK));
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.Should().NotBeNull();
        otherKey.StatusCode.Should().Be(HttpStatusCode.OK, "each key has its own share");
    }

    [Fact]
    public async Task ArivaMainClient_Should_FollowNoRedirectKeepNoCookieAndUseNoProxy_When_BuiltByTheHost()
    {
        // CWE-918: the chain the host builds for the observers' client (resilience and all), down to its primary handler.
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty);
            builder.UseSetting("Simulation:Ariva:MainUrl", "https://api-main-service");
        });

        var handler = app.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ValidationEmulator.HttpClientName);
        var chain = new List<string>();
        while (handler is DelegatingHandler delegating)
        {
            chain.Add(delegating.GetType().Name);
            handler = delegating.InnerHandler;
        }

        var primary = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        primary.AllowAutoRedirect.Should().BeFalse("a redirect could send the password or a token elsewhere");
        primary.UseCookies.Should().BeFalse("Ariva's refresh cookie is never kept");
        primary.UseProxy.Should().BeFalse("no environment proxy sees an in-cluster call");
        chain.Should().NotBeEmpty();
        using var named = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ValidationEmulator.HttpClientName);
        named.BaseAddress.Should().Be(new Uri("https://api-main-service/"));
    }

    [Fact]
    public async Task LogFile_Should_HoldTheSimulatorsLinesAsJsonWithoutASecret_When_LogFilePathIsSet()
    {
        // LogFile:Path, as Ariva's hosts honour it, so that the E2E run's log scan reads the simulator's lines (CWE-532).
        var directory = Directory.CreateTempSubdirectory("ariva-sim-log-");
        var path = Path.Combine(directory.FullName, "Ariva.Simulation.Api.log");
        try
        {
            var main = new FakeMain();
            await using (var app = ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
                         {
                             builder.UseSetting("LogFile:Path", path);
                             builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-operator");
                             builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ControlKey));
                             builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "read");
                             builder.UseSetting("Simulation:Control:Keys:0:Scopes:1", "control");
                             builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty);
                             builder.UseSetting("Simulation:Ariva:MainUrl", "https://api-main-service");
                             builder.ConfigureTestServices(services => services.AddHttpClient(ValidationEmulator.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => main));
                         }))
            {
                using var client = app.CreateClient();
                await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, Seed1)));
                await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
            }

            string text;
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                text = await reader.ReadToEndAsync(Ct);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().NotBeEmpty();
            lines.Select(l => JsonDocument.Parse(l).RootElement).Should().OnlyContain(e => e.GetProperty("Properties").GetProperty("SourceContext").GetString()!.Length > 0);
            lines.Should().Contain(l => l.Contains("Validation rehearsal by demo-operator", StringComparison.Ordinal));
            lines.Should().NotContain(l => l.Contains("\"SourceContext\":\"Microsoft.AspNetCore.Hosting", StringComparison.Ordinal) && !l.Contains("\"Level\":\"Warning", StringComparison.Ordinal) &&
                                           !l.Contains("\"Level\":\"Error", StringComparison.Ordinal) && !l.Contains("\"Level\":\"Critical", StringComparison.Ordinal));
            foreach (var secret in new[] { Password1, Seed1, ControlKey, "tok-", Observer1 })
                text.Should().NotContain(secret);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // The host may still hold the file on some platforms; the temporary directory is left to the system.
            }
        }
    }

    [Fact]
    public async Task Logs_Should_NeverHoldAPasswordSeedCodeOrToken_When_ARehearsalRuns()
    {
        var main = new FakeMain();
        var capture = new Capture();
        await using var app = Host(main, capture: capture);
        using var client = app.CreateClient();
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer1, Password1, Seed1), (Observer2, Password2, null)));
        await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());
        await SendAsync(client, HttpMethod.Put, Route + "/observers", ControlKey, Observers((Observer2, "wrong-password-0000", null)));
        await SendAsync(client, HttpMethod.Post, Route + "/rehearsals", ControlKey, Rehearsal());

        var codes = main.Calls.Where(c => c.Uri.AbsolutePath == "/api/auth/login").Select(c => JsonDocument.Parse(c.Body).RootElement)
            .Where(l => l.GetProperty("code").ValueKind == JsonValueKind.String).Select(l => l.GetProperty("code").GetString()).ToList();
        codes.Should().NotBeEmpty();
        capture.Lines.Should().Contain(l => l.Contains("Validation rehearsal by demo-operator", StringComparison.Ordinal));
        var everything = string.Join("\n", capture.Lines);
        foreach (var secret in new[] { Password1, Password2, Seed1, "tok-", Observer1, Observer2, "wrong-password-0000" })
            everything.Should().NotContain(secret);
        // A code counts only as a number of its own (inside a longer run of digits, such as a duration, it is a coincidence).
        foreach (var code in codes)
            Regex.IsMatch(everything, $"(?<![0-9]){code}(?![0-9])").Should().BeFalse("the TOTP code is never logged");
    }
}
