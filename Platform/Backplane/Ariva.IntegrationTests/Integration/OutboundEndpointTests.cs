using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Integration;
using Ariva.Di.Extensions;
using Ariva.Infra.Flights.Acris;
using Ariva.Infra.Integration;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.IntegrationTests.Integration;

/// <summary>
/// ARV-045 against TimescaleDB with script 0028: outbound endpoints are administered within the administrator's sites,
/// their secret is stored only protected and never shown; the ACRIS pull claims a due endpoint, calls it through the
/// guarded client, applies the site's flights as the endpoint's feed, honours Not Modified, and records refusals (a
/// redirect) as failures; the runtime role cannot delete an endpoint.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboundEndpointTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (Guid All, Guid Dmo)? _admins;

    private static readonly IConfiguration Lab = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
    {
        ["Application:Environment"] = "vm-local",
        ["Integration:Outbound:AllowLoopback"] = "true",
        ["Integration:Outbound:LabHosts:0"] = "127.0.0.1"
    }).Build();

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Outbound, configure: services =>
    {
        services.AddArivaFlights(new ConfigurationBuilder().Build(), watchFeeds: false);
        services.AddArivaBorderFeed(new ConfigurationBuilder().Build());
        services.AddArivaOutboundEndpoints(Lab, "vm-local");
        services.AddArivaOutboundCalls(Lab, "vm-local");
    });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task<(Guid All, Guid Dmo)> AdminsAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_admins is { } known)
            {
                await _host.CreateUserAsync("it.out.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var all = await _host.CreateUserAsync("it.out.all", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", all);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            (await _host.AsCallerAsync(all, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("OUT", "Outbound airport"), Ct))).HasErrors.Should().BeFalse();
            var dmo = await _host.CreateUserAsync("it.out.dmo", roles: [RoleCodes.SystemAdministrator]);
            await _host.AsCallerAsync(all, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(dmo, new SiteAccessRequest(false, ["DMO"]), Ct));
            _admins = (all, dmo);
            return _admins.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static CreateOutboundEndpointRequest ApiKey(string code, string url, string[] sites, string purpose = "Generic", string pullPath = null) =>
        new(code, "Endpoint " + code, purpose, sites,
            new OutboundConnectionRequest(url, ["127.0.0.0/8"], "ApiKeyHeader", HeaderName: "X-Api-Key", PullPath: pullPath, PollSeconds: 30, RetryCount: 0),
            new OutboundSecretRequest(ApiKey: "the-key-" + code));

    private Task<Fluentx.Result<Core.Domain.ViewModels.OutboundEndpointViewModel>> CreateAsync(Guid caller, CreateOutboundEndpointRequest request) =>
        _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcOutboundEndpoints>().CreateAsync(request, Ct));

    [Fact]
    public async Task Endpoint_Should_BeAdministeredWithinTheCallersSitesAndKeepItsSecretProtected_When_Registered()
    {
        var p = await AdminsAsync();
        var created = await CreateAsync(p.All, ApiKey("both-sites", "https://aodb.example.test/api", ["DMO", "OUT"]));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.BaseUrl.Should().Be("https://aodb.example.test/api/");

        (await _host.ReadAsync<string>("SELECT secret_protected FROM outbound_endpoint WHERE code = @secret", secret: "both-sites")).Should().NotContain("the-key-both-sites");
        System.Text.Json.JsonSerializer.Serialize(created.Data).Should().NotContain("the-key");

        (await CreateAsync(p.Dmo, ApiKey("beyond", "https://aodb.example.test/", ["OUT"]))).ErrorMessages.Should().Equal([OutboundErrors.BeyondOwnSites]);
        (await CreateAsync(p.All, ApiKey("both-sites", "https://aodb.example.test/", ["DMO"]))).ErrorMessages.Should().Equal([OutboundErrors.DuplicateCode]);
        (await _host.AsCallerAsync(p.Dmo, s => s.GetRequiredService<ISvcOutboundEndpoints>().GetAsync(created.Data.Id, Ct))).ErrorMessages.Should()
            .Equal([OutboundErrors.NotFound], "an endpoint beyond your sites does not exist for you");

        var oauth = new UpdateOutboundEndpointRequest("Renamed", ["DMO", "OUT"],
            new OutboundConnectionRequest("https://aodb.example.test/", ["10.0.0.0/8"], "OAuth2ClientCredentials", TokenPath: "/token", ClientId: "ariva"));
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcOutboundEndpoints>().UpdateAsync(created.Data.Id, oauth, Ct))).ErrorMessages.Should()
            .Contain(e => e.Contains("does not change", StringComparison.Ordinal));

        // Moving the endpoint (URL, networks, pinned CA, token path) without its secret would send the stored key elsewhere.
        var stored = await _host.ReadAsync<string>("SELECT secret_protected FROM outbound_endpoint WHERE code = @secret", secret: "both-sites");
        var move = new UpdateOutboundEndpointRequest("Moved", ["DMO", "OUT"],
            new OutboundConnectionRequest("https://attacker.example.test/", ["203.0.113.7/32"], "ApiKeyHeader", HeaderName: "X-Api-Key"));
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcOutboundEndpoints>().UpdateAsync(created.Data.Id, move, Ct))).ErrorMessages.Should()
            .Equal([Ariva.Infra.Services.Integration.SvcOutboundEndpoints.SecretAgain]);
        (await _host.ReadAsync<string>("SELECT base_url || ' ' || secret_protected FROM outbound_endpoint WHERE code = @secret", secret: "both-sites")).Should()
            .Be("https://aodb.example.test/api/ " + stored, "nothing changed");
        var rename = new UpdateOutboundEndpointRequest("Renamed only", ["DMO", "OUT"],
            new OutboundConnectionRequest("https://aodb.example.test/api", ["127.0.0.0/8"], "ApiKeyHeader", HeaderName: "X-Api-Key", RetryCount: 0, PollSeconds: 30));
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcOutboundEndpoints>().UpdateAsync(created.Data.Id, rename, Ct))).HasErrors.Should()
            .BeFalse("the same place needs no secret");
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcOutboundEndpoints>().UpdateAsync(created.Data.Id,
            move with { Secret = new OutboundSecretRequest(ApiKey: "the-new-place-key") }, Ct))).HasErrors.Should().BeFalse("moved with a secret for the new place");
        (await _host.ReadAsync<string>("SELECT secret_protected FROM outbound_endpoint WHERE code = @secret", secret: "both-sites")).Should().NotBe(stored);

        var version = await _host.ReadAsync<int>("SELECT client_version FROM outbound_endpoint WHERE code = @secret", secret: "both-sites");
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcOutboundEndpoints>().SetSecretAsync(created.Data.Id, new OutboundSecretRequest(ApiKey: "a-new-key-1"), Ct)))
            .HasErrors.Should().BeFalse();
        (await _host.ReadAsync<int>("SELECT client_version FROM outbound_endpoint WHERE code = @secret", secret: "both-sites")).Should().BeGreaterThan(version, "hosts rebuild their client");
        (await _host.AsCallerAsync(p.All, s => s.GetRequiredService<ISvcOutboundEndpoints>().DisableAsync(created.Data.Id, Ct))).Data.Status.Should().Be("Disabled");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name = @secret", secret: "both-sites")).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AcrisPull_Should_ApplyTheSitesFlightsAndRecordHowItWent_When_Due()
    {
        var p = await AdminsAsync();
        var day = Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var at = (int minutes) => Now.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var modified = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var json = $$$"""
            [{"flightNumber":{"airlineCode":"QR","trackNumber":"0401"},"originDate":"{{{day}}}","departureAirport":"DOH","arrivalAirport":"DMO",
              "arrival":{"scheduled":"{{{at(120)}}}","gate":"G9"}},
             {"flightNumber":{"airlineCode":"QR","trackNumber":"402"},"originDate":"{{{day}}}","departureAirport":"DMO","arrivalAirport":"DOH",
              "departure":{"scheduled":"{{{at(200)}}}"}},
             {"flightNumber":{"airlineCode":"QR","trackNumber":"403"},"originDate":"{{{day}}}","departureAirport":"DOH","arrivalAirport":"KWI"}]
            """;
        var calls = 0;
        await using var server = await LoopbackServer.StartAsync(async c =>
        {
            if (c.Request.Path == "/acris/moved")
            {
                c.Response.Redirect("http://169.254.169.254/latest/meta-data/");
                return;
            }

            calls++;
            if (c.Request.Headers["X-Api-Key"] != "the-key-acris-dmo")
                c.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else if (c.Request.Headers.IfModifiedSince.Count > 0)
                c.Response.StatusCode = StatusCodes.Status304NotModified;
            else
            {
                c.Response.Headers.LastModified = modified.ToString("R");
                c.Response.ContentType = "application/json";
                await c.Response.WriteAsync(json, Ct);
            }
        });
        var good = await CreateAsync(p.All, ApiKey("acris-dmo", $"http://127.0.0.1:{server.Port}/", ["DMO"], "AcrisFlights", "/acris/flights"));
        good.HasErrors.Should().BeFalse(string.Join(", ", good.ErrorMessages ?? []));
        var moved = await CreateAsync(p.All, ApiKey("acris-moved", $"http://127.0.0.1:{server.Port}/", ["DMO"], "AcrisFlights", "/acris/moved"));
        moved.HasErrors.Should().BeFalse();

        // An endpoint whose secret another key ring protected (a lost or replaced ring) fails alone; the others still pull.
        var lost = await CreateAsync(p.All, ApiKey("acris-lost", $"http://127.0.0.1:{server.Port}/", ["DMO"], "AcrisFlights", "/acris/flights"));
        lost.HasErrors.Should().BeFalse();
        var otherRing = new EphemeralDataProtectionProvider().CreateProtector(OutboundSecrets.Purpose).Protect("{\"apiKey\":\"the-key-acris-dmo\"}");
        await _host.ReadAsync<int>("UPDATE outbound_endpoint SET secret_protected = @secret WHERE code = 'acris-lost' RETURNING 1", secret: otherRing);
        // Only this test's endpoints are due (another test of the class may have registered one in the same database).
        await _host.ReadAsync<long>("WITH d AS (UPDATE outbound_endpoint SET status = 'Disabled' WHERE purpose = 'AcrisFlights' AND code NOT LIKE 'acris-%' RETURNING 1) " +
            "SELECT count(*) FROM d");

        var poller = await _host.AsCallerAsync(null, s => Task.FromResult(s.GetServices<IHostedService>().OfType<AcrisPoller>().Single()));
        (await poller.RunOnceAsync(Ct)).Should().Be(3);
        (await _host.ReadAsync<string>("SELECT last_status || ' ' || consecutive_failures FROM outbound_endpoint WHERE code = 'acris-lost'"))
            .Should().Be("The endpoint's secret cannot be read with this key ring; set the secret again. 1");
        await _host.ReadAsync<long>("WITH d AS (UPDATE outbound_endpoint SET status = 'Disabled' WHERE code = 'acris-lost' RETURNING 1) SELECT count(*) FROM d");

        (await _host.ReadAsync<string>("SELECT string_agg(flight_key || ' ' || direction || ' ' || feed, ',' ORDER BY flight_key) FROM flight_leg WHERE flight_key LIKE 'QR4%'"))
            .Should().Be($"QR401-{Now:yyyyMMdd}-A Arrival acris-acris-dmo,QR402-{Now:yyyyMMdd}-D Departure acris-acris-dmo");
        (await _host.ReadAsync<string>("SELECT last_status || ' | ' || last_modified FROM outbound_endpoint WHERE code = @secret", secret: "acris-dmo"))
            .Should().Be("Pulled 3 flights: 2 applied, 1 refused. | " + modified.ToString("R"));
        (await _host.ReadAsync<string>("SELECT last_status || ' ' || consecutive_failures FROM outbound_endpoint WHERE code = @secret", secret: "acris-moved"))
            .Should().Contain("redirect").And.EndWith(" 1");

        (await poller.RunOnceAsync(Ct)).Should().Be(0, "not due again for 30 seconds");
        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        (await poller.RunOnceAsync(Ct)).Should().Be(2);
        (await _host.ReadAsync<string>("SELECT last_status FROM outbound_endpoint WHERE code = @secret", secret: "acris-dmo")).Should().Be("Not modified.");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task AmanPull_Should_PullEveryContractWithOneTokenAndATotpCodeOnEachCall_When_Due()
    {
        var p = await AdminsAsync();
        var seed = Ariva.Infra.Security.Base32.Encode(Ariva.Infra.Security.Totp.NewSecret());
        var minute = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-2);
        var good = System.Text.Json.JsonSerializer.Serialize(new Ariva.Business.Contracts.Aman.V1.DeskIntervalStats("DMO", "IN09", new DateTimeOffset(minute), 60, 2, 3, 41,
            60, 30, "VIS", "pull-1"), Ariva.Infra.Border.AmanFeedJson<Ariva.Business.Contracts.Aman.V1.DeskIntervalStats>.Options);
        var unreadable = good.Replace("\"pull-1\"", "\"pull-2\",\"officerId\":\"OFFICER-7\"", StringComparison.Ordinal);
        // A record naming another site (one that exists): the pull feeds its endpoint's one site only (CWE-863).
        var otherSite = good.Replace("\"pull-1\"", "\"pull-3\"", StringComparison.Ordinal).Replace("\"siteCode\":\"DMO\"", "\"siteCode\":\"OUT\"", StringComparison.Ordinal);
        var exchanges = 0;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(string Contract, string After, bool Code)>();
        bool CodeOk(string code) => code is not null && Ariva.Infra.Security.Totp.Match(Ariva.Infra.Security.Base32.Decode(seed), code, _host.Clock.GetUtcNow(), null) is not null;
        await using var server = await LoopbackServer.StartAsync(async c =>
        {
            c.Response.ContentType = "application/json";
            if (c.Request.Path == "/aman/api/v1/auth" && c.Request.Method == "POST")
            {
                using var body = await System.Text.Json.JsonDocument.ParseAsync(c.Request.Body, cancellationToken: Ct);
                var r = body.RootElement;
                if (r.GetProperty("clientId").GetString() != "ariva-pull" || r.GetProperty("clientSecret").GetString() != "aman-secret-1" ||
                    !CodeOk(r.GetProperty("totpCode").GetString()))
                {
                    c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                Interlocked.Increment(ref exchanges);
                await c.Response.WriteAsync($$"""{"accessToken":"tok-1","expiresAt":"{{_host.Clock.GetUtcNow().AddMinutes(15):O}}","sessionId":"{{Guid.NewGuid()}}"}""", Ct);
                return;
            }

            var contract = c.Request.Path.Value!.Replace("/aman/api/v1/feed/", "", StringComparison.Ordinal);
            var after = c.Request.Query["after"].ToString();
            calls.Enqueue((contract, after, CodeOk(c.Request.Headers["X-TOTP-Code"])));
            if (c.Request.Headers.Authorization != "Bearer tok-1" || !CodeOk(c.Request.Headers["X-TOTP-Code"]))
            {
                c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await c.Response.WriteAsync(contract == "desk-interval-stats" && after == "0"
                ? $$"""{"data":{"items":[{{good}},{{unreadable}},{{otherSite}}],"next":3},"hasErrors":false,"errorMessages":[]}"""
                : $$"""{"data":{"items":[],"next":{{after}}},"hasErrors":false,"errorMessages":[]}""", Ct);
        });

        CreateOutboundEndpointRequest Aman(string code, string secret, string authKind = "TotpClientCredentials", string pullPath = "/feed/", string[] sites = null) =>
            new(code, "AMAN " + code, "AmanFeed", sites ?? ["DMO"],
                new OutboundConnectionRequest($"http://127.0.0.1:{server.Port}/aman/api/v1/", ["127.0.0.0/8"], authKind, TokenPath: authKind == "TotpClientCredentials" ? "/auth" : null,
                    ClientId: authKind == "TotpClientCredentials" ? "ariva-pull" : null, HeaderName: authKind == "ApiKeyHeader" ? "X-Api-Key" : null,
                    TotpPerRequest: true, PullPath: pullPath, PollSeconds: 30, RetryCount: 0),
                authKind == "TotpClientCredentials" ? new OutboundSecretRequest(ClientSecret: secret, TotpSeed: seed) : new OutboundSecretRequest(ApiKey: secret));

        // AMAN's Integration API is pulled with TOTP client credentials only, below a feed path, for one site (CWE-287).
        (await CreateAsync(p.All, Aman("aman-key", "a-key-of-sorts", authKind: "ApiKeyHeader"))).ErrorMessages.Should().Contain(e => e.Contains("TotpClientCredentials", StringComparison.Ordinal));
        (await CreateAsync(p.All, Aman("aman-path", "aman-secret-1", pullPath: "/feed"))).ErrorMessages.Should().Contain(e => e.Contains("ending in a slash", StringComparison.Ordinal));
        (await CreateAsync(p.All, Aman("aman-two", "aman-secret-1", sites: ["DMO", "OUT"]))).ErrorMessages.Should().Contain(e => e.Contains("exactly one site", StringComparison.Ordinal));
        (await CreateAsync(p.All, Aman("aman-dmo", "aman-secret-1"))).HasErrors.Should().BeFalse();
        (await CreateAsync(p.All, Aman("aman-wrong", "not-the-secret"))).HasErrors.Should().BeFalse();

        // Only this test's endpoints are due (another test of the class may have registered one in the same database).
        await _host.ReadAsync<long>("WITH d AS (UPDATE outbound_endpoint SET status = 'Disabled' WHERE purpose = 'AmanFeed' AND code NOT LIKE 'aman-%' RETURNING 1) " +
            "SELECT count(*) FROM d");
        var poller = await _host.AsCallerAsync(null, s => Task.FromResult(s.GetServices<IHostedService>().OfType<Ariva.Infra.Border.AmanPoller>().Single()));
        (await poller.RunOnceAsync(Ct)).Should().Be(2);

        exchanges.Should().Be(1, "one token for the four contracts; the wrong secret's exchange is refused");
        var dmoCalls = calls.ToList();
        dmoCalls.Select(c => c.Contract).Should().BeEquivalentTo(["desk-sessions", "desk-interval-stats", "egate-interval-stats", "inbound-lane-demand"]);
        dmoCalls.Should().OnlyContain(c => c.Code, "X-TOTP-Code on every call");
        (await _host.ReadAsync<string>("SELECT feed || ' ' || (desk_id IS NOT NULL) FROM border_desk_interval WHERE source_event_id = 'pull-1'")).Should().Be("aman-aman-dmo true");
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE source_event_id = 'pull-2'")).Should().Be(0, "an unreadable record is never stored");
        (await _host.ReadAsync<string>("SELECT last_status || ' ' || consecutive_failures FROM outbound_endpoint WHERE code = 'aman-dmo'"))
            .Should().Be("Pulled 3 AMAN records: 1 applied, 0 unchanged, 1 refused, 1 unreadable. 0").And.NotContain("OFFICER");
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE source_event_id = 'pull-3'")).Should().Be(0, "another site's record is refused");
        (await _host.ReadAsync<string>("SELECT last_status || ' ' || consecutive_failures FROM outbound_endpoint WHERE code = 'aman-wrong'"))
            .Should().Be("AMAN answered 401. 1");
        (await _host.ReadAsync<long>("SELECT after_sequence FROM aman_pull_cursor c JOIN outbound_endpoint e ON e.id = c.endpoint_id WHERE e.code = 'aman-dmo' AND c.contract = 'desk-interval-stats'"))
            .Should().Be(3);

        // The next poll continues after the position with the cached token.
        while (calls.TryDequeue(out _))
        {
        }

        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        (await poller.RunOnceAsync(Ct)).Should().Be(1, "the failing endpoint waits twice its interval before AMAN is asked again");
        exchanges.Should().Be(1, "the token is cached until shortly before it expires");
        calls.Should().Contain(("desk-interval-stats", "3", true));
        (await _host.ReadAsync<string>("SELECT last_status FROM outbound_endpoint WHERE code = 'aman-dmo'")).Should().StartWith("Pulled 0 AMAN records");
    }

    [Theory]
    [InlineData("DELETE FROM outbound_endpoint", "42501")]
    [InlineData("TRUNCATE outbound_endpoint", "42501")]
    [InlineData("UPDATE outbound_endpoint SET base_url = 'gopher://x/'", "23514")]
    [InlineData("UPDATE outbound_endpoint SET pull_path = 'http://169.254.169.254/' WHERE purpose = 'AcrisFlights'", "23514")]
    [InlineData("DELETE FROM aman_pull_cursor", "42501")]
    [InlineData("TRUNCATE aman_pull_cursor", "42501")]
    [InlineData("UPDATE outbound_endpoint SET auth_kind = 'ApiKeyHeader', header_name = 'X-Key', token_path = NULL, client_id = NULL WHERE purpose = 'AmanFeed'", "23514")]
    [InlineData("UPDATE outbound_endpoint SET totp_per_request = false WHERE purpose = 'AmanFeed'", "23514")]
    [InlineData("UPDATE outbound_endpoint SET pull_path = NULL WHERE purpose = 'AmanFeed'", "23514")]
    [InlineData("UPDATE outbound_endpoint SET pull_path = '/feed/?all=1' WHERE purpose = 'AmanFeed'", "23514")]
    [InlineData("UPDATE outbound_endpoint SET site_codes = 'DMO OUT' WHERE purpose = 'AmanFeed'", "23514")]
    public async Task Database_Should_RefuseUnsafeChanges_When_TheRuntimeRoleTries(string sql, string state)
    {
        var p = await AdminsAsync();
        if (await _host.ReadAsync<long>("SELECT count(*) FROM outbound_endpoint WHERE purpose = 'AcrisFlights'") == 0)
            (await CreateAsync(p.All, ApiKey("db-acris", "https://aodb.example.test/", ["DMO"], "AcrisFlights", "/flights"))).HasErrors.Should().BeFalse();
        if (await _host.ReadAsync<long>("SELECT count(*) FROM outbound_endpoint WHERE purpose = 'AmanFeed'") == 0)
            (await CreateAsync(p.All, new CreateOutboundEndpointRequest("db-aman", "AMAN", "AmanFeed", ["DMO"],
                new OutboundConnectionRequest("https://aman.example.test/aman/api/v1/", ["10.0.0.0/8"], "TotpClientCredentials", TokenPath: "/auth", ClientId: "ariva",
                    TotpPerRequest: true, PullPath: "/feed/", PollSeconds: 30),
                new OutboundSecretRequest(ClientSecret: "aman-secret-1", TotpSeed: Ariva.Infra.Security.Base32.Encode(Ariva.Infra.Security.Totp.NewSecret()))))).HasErrors.Should().BeFalse();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(state);
        await transaction.RollbackAsync(Ct);
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private WebApplication _app;
        public int Port { get; private set; }

        // A plain request delegate: the stand-in partner has no endpoints of Ariva's own.
        public static async Task<LoopbackServer> StartAsync(RequestDelegate handle)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Run(handle);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new LoopbackServer { _app = app, Port = new Uri(address).Port };
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
