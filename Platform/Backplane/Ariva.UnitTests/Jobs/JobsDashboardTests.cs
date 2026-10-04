using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ariva.Api.Cronz.Jobs;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-060 on Ariva.Api.Cronz in process: TickerQ runs the report deliveries; its dashboard exists only when enabled with
/// the SHA-256 of a key, answers its API only with that key and its hub only with the ticket cookie a keyed request
/// returns (same origin only), and serves its page under a policy of its own that allows only its own scripts and its
/// inline scripts by hash.
/// </summary>
public sealed class JobsDashboardTests
{
    private const string Key = "dashboard-key-for-tests-0123456789abcdef";
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key)));

    private static IArivaHost Cronz(bool enabled = true, string digest = null) => ArivaHosts.Create(ArivaHosts.Cronz, configure: builder =>
    {
        builder.UseSetting("Cronz:Dashboard:Enabled", enabled ? "true" : "false");
        builder.UseSetting("Cronz:Dashboard:KeySha256", digest ?? Digest);
    });

    private static HttpRequestMessage Get(string path, string key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    [Fact]
    public async Task Api_Should_AnswerOnlyWithTheKey_When_TheDashboardIsOn()
    {
        await using var host = Cronz();
        using var client = host.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        (await client.SendAsync(Get("/tickerq/api/cron-tickers"), ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no key");
        (await client.SendAsync(Get("/tickerq/api/cron-tickers", "not-the-key"), ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a wrong key");
        (await client.SendAsync(Get("/tickerq/api/cron-tickers?access_token=" + Key), ct)).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized, "never the key in the address");
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1"))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the hub too, negotiation included");
        using (var validate = new HttpRequestMessage(HttpMethod.Post, "/tickerq/api/auth/validate"))
            (await client.SendAsync(validate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Routing ignores case, so the guard does too (the review's bypass: upper-case paths were taken for the page).
        (await client.SendAsync(Get("/tickerq/API/options"), ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an upper-case API path");
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/tickerq/Ticker-Notification-Hub/negotiate?negotiateVersion=1"))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an upper-case hub path");
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1&access_token=" + Key))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "never the key in the address");
        (await client.SendAsync(Get("/tickerq/report.json"), ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an unknown file is not open");
        using (var post = new HttpRequestMessage(HttpMethod.Post, "/tickerq/"))
            (await client.SendAsync(post, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "only reading the page is open");
        using (var negotiate = Get("/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1", Key))
        {
            negotiate.Method = HttpMethod.Post;
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the hub takes the ticket, not the key");
        }

        using var answer = await client.SendAsync(Get("/tickerq/api/cron-tickers", Key), ct);
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        (await answer.Content.ReadAsStringAsync(ct)).Should().Contain(ReportJobs.Deliveries).And.Contain("0 */5 * * * *");

        // A keyed request returns the hub ticket: HttpOnly, Secure, SameSite=Strict, only for the hub's path, never the key.
        var setCookie = answer.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(JobsDashboardGuard.TicketCookie + "=", StringComparison.Ordinal));
        setCookie.Should().Contain("path=/tickerq/ticker-notification-hub").And.Contain("secure").And.Contain("samesite=strict").And.Contain("httponly")
            .And.NotContain(Key);
        var cookie = setCookie.Split(';')[0];
        HttpRequestMessage Negotiate(string path, string origin = null, string withCookie = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("Cookie", withCookie ?? cookie);
            if (origin is not null)
                request.Headers.Add("Origin", origin);
            return request;
        }

        using (var negotiate = Negotiate("/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1", origin: "http://localhost"))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the hub with the ticket, from its own origin");
        using (var negotiate = Negotiate("/tickerq/Ticker-Notification-Hub/negotiate?negotiateVersion=1"))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.OK, "any case of the hub path");
        using (var negotiate = Negotiate("/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1", origin: "https://attacker.example"))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "another origin, even with the operator's cookie");
        var tampered = cookie[..^2] + (cookie[^2] == 'A' ? "B" : "A") + cookie[^1];
        using (var negotiate = Negotiate("/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1", withCookie: tampered))
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a ticket that is not signed");
        // The marker the guard gives an admitted hub request is no credential anywhere.
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/tickerq/ticker-notification-hub/negotiate?negotiateVersion=1"))
        {
            negotiate.Headers.TryAddWithoutValidation("Authorization", JobsDashboardGuard.HubMarker);
            (await client.SendAsync(negotiate, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the hub marker without a ticket");
        }

        using (var api = new HttpRequestMessage(HttpMethod.Get, "/tickerq/api/cron-tickers"))
        {
            api.Headers.TryAddWithoutValidation("Authorization", JobsDashboardGuard.HubMarker);
            (await client.SendAsync(api, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the hub marker on the API");
        }

        using (var api = Get("/tickerq/api/cron-tickers"))
        {
            api.Headers.Add("Cookie", cookie);
            (await client.SendAsync(api, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the ticket opens the hub only, never the API");
        }
        (await client.SendAsync(Get("/tickerq/api/auth/info"), ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the page asks how to sign in");
    }

    [Fact]
    public async Task Page_Should_AllowOnlyItsOwnScriptsByHash_When_Served()
    {
        await using var host = Cronz();
        using var client = host.CreateClient();
        using var page = await client.SendAsync(Get("/tickerq/"), TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var policy = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        policy.Should().StartWith("default-src 'none'").And.Contain("frame-ancestors 'none'").And.Contain("script-src 'self' 'sha256-")
            .And.NotContain("unsafe-eval").And.NotContain("script-src 'self' 'unsafe-inline'");
        var inline = System.Text.RegularExpressions.Regex.Matches(html, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
        inline.Should().HaveCountGreaterThanOrEqualTo(2, "the page's preload script and TickerQ's configuration script");
        foreach (System.Text.RegularExpressions.Match script in inline)
            policy.Should().Contain($"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script.Groups[1].Value)))}'");
        page.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        System.Text.RegularExpressions.Regex.Matches(policy, "'sha256-").Count.Should().Be(2, "exactly the preload and the configuration script");

        // The browser app's own routes get the same page, never another handler.
        using var route = await client.SendAsync(Get("/tickerq/Cron-Tickers"), TestContext.Current.CancellationToken);
        route.StatusCode.Should().Be(HttpStatusCode.OK);
        route.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        string.Join(";", route.Headers.GetValues("Content-Security-Policy")).Should().Be(policy);
    }

    [Theory]
    [InlineData("/tickerq", "host", true)]
    [InlineData("/elsewhere", "host", false)]
    [InlineData("/tickerq", "none", false)]
    public void ConfigScript_Should_BeAllowedOnlyForThisBasePathInHostMode(string basePath, string mode, bool allowed)
    {
        var script = $$$"""

                (function() {
                try {
                    // Expose config
                    window.TickerQConfig = {"basePath":"{{{basePath}}}","backendDomain":null,"auth":{"mode":"{{{mode}}}","enabled":true,"sessionTimeout":30}};

                    // Derive dynamic base for vite-plugin-dynamic-base
                    window.__dynamic_base__ = window.TickerQConfig.basePath;
                } catch (e) { console.error('Runtime config injection failed:', e); }
                })();
                """;
        JobsDashboardGuard.IsConfigScript(script, "/tickerq").Should().Be(allowed);
        JobsDashboardGuard.IsConfigScript(script + "alert(1);", "/tickerq").Should().BeFalse("anything more is not TickerQ's script");
    }

    [Fact]
    public async Task Dashboard_Should_NotExist_When_ItIsOff()
    {
        await using var host = Cronz(enabled: false);
        using var client = host.CreateClient();

        (await client.SendAsync(Get("/tickerq/api/cron-tickers", Key), TestContext.Current.CancellationToken)).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized, "no branch: the default deny answers, and a dashboard key is no Ariva token");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void Settings_Should_RefuseToStart_When_TheDigestIsNotOne(string digest)
    {
        new JobsDashboardSettings { Enabled = true, KeySha256 = digest }.Problems().Should().NotBeEmpty();
        new JobsDashboardSettings { Enabled = true, KeySha256 = Digest, BasePath = "/x/../y" }.Problems().Should().NotBeEmpty();
        new JobsDashboardSettings { Enabled = false, KeySha256 = digest }.Problems().Should().BeEmpty("off needs nothing");
    }

    [Fact]
    public void Key_Should_MatchOnlyItsDigest()
    {
        var digest = Convert.FromHexString(Digest);
        JobsDashboardGuard.Matches("Bearer " + Key, digest).Should().BeTrue();
        JobsDashboardGuard.Matches(Key, digest).Should().BeTrue("TickerQ's host mode sends the key as typed");
        JobsDashboardGuard.Matches(Key + "x", digest).Should().BeFalse();
        JobsDashboardGuard.Matches(null, digest).Should().BeFalse();
        JobsDashboardGuard.Matches(new string('a', 600), digest).Should().BeFalse();
    }

    [Fact]
    public async Task Ticket_Should_ExpireAndBelongToItsProcess()
    {
        var time = new Clock(new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero));
        var digest = Convert.FromHexString(Digest);
        var guard = new JobsDashboardGuard(_ => Task.CompletedTask, digest, "/tickerq", Microsoft.Extensions.Logging.Abstractions.NullLogger<JobsDashboardGuard>.Instance, time);
        var other = new JobsDashboardGuard(_ => Task.CompletedTask, digest, "/tickerq", Microsoft.Extensions.Logging.Abstractions.NullLogger<JobsDashboardGuard>.Instance, time);

        var ticket = guard.IssueTicket();
        guard.IsTicket(ticket).Should().BeTrue();
        other.IsTicket(ticket).Should().BeFalse("another process (a restart) draws another ticket key");
        guard.IsTicket(null).Should().BeFalse();
        guard.IsTicket("not base64 at all!").Should().BeFalse();
        guard.IsTicket(new string('A', 54)).Should().BeFalse();
        time.Now += JobsDashboardGuard.TicketLifetime;
        guard.IsTicket(ticket).Should().BeFalse("expired");
        await Task.CompletedTask;
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
