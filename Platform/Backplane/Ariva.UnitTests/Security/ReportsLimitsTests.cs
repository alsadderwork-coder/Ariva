using System.Globalization;
using System.Net;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Settings;
using Ariva.Core.Reports;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-096 (CWE-400, CWE-770, ASVS 5.0 V15.2.2): the on-demand daily report's limits over HTTP on Ariva.Api.Main, as
/// <see cref="ValidationResultsLimitsTests"/> proves the validation results'. The <c>reports</c> concurrency policy, saturated (one
/// permit, none waiting), answers 429 with Retry-After (a concurrency lease carries no retry time, so the host gives a fixed one);
/// a report that takes longer than the <c>reports</c> request timeout answers 503 with Retry-After, and the service sees its token
/// cancelled (the token the report's reads pass to the database). The report service is a fake that holds its answer until told,
/// so no database is needed.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ReportsLimitsTests
{
    private const string Daily = "/api/v1/sites/DMO/reports/daily?date=2026-10-18";
    private const string DailyCsv = "/api/v1/sites/DMO/reports/daily.csv?date=2026-10-18&section=alerts";

    private static IArivaHost MainHost(HeldReports reports, params (string Key, string Value)[] settings) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                TestAuthenticationHandler.Register(services);
                FakeAdministration.Register(services);
                services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Replace(ServiceDescriptor.Scoped<ISvcReports>(_ => reports));
            });
        });

    private static HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "report-reader");
        request.Headers.TryAddWithoutValidation("Authorization", TestAuthenticationHandler.Bearer);
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.BorderShiftSupervisor);
        return request;
    }

    [Theory]
    [InlineData(Daily)]
    [InlineData(DailyCsv)]
    public async Task Get_Should_Answer429WithRetryAfter_When_TheReportsPolicyIsSaturated(string path)
    {
        var reports = new HeldReports();
        await using var app = MainHost(reports, ("Security:RateLimiting:Reports:PermitLimit", "1"), ("Security:RateLimiting:Reports:QueueLimit", "0"));
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var holding = Request(Daily);
        var first = client.SendAsync(holding, ct);
        await reports.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var refusedRequest = Request(path);
        using var refused = await client.SendAsync(refusedRequest, ct);
        reports.Release.TrySetResult();
        using var answered = await first;

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the JSON report and its CSV share one policy");
        refused.Header("Retry-After").Should().Be(RateLimitingExtensions.ConcurrencyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
        answered.StatusCode.Should().Be(HttpStatusCode.OK, "the request holding the permit is answered once its report is built");
        reports.Calls.Should().Be(1, "the refused request never reached the service");
    }

    [Theory]
    [InlineData(Daily)]
    [InlineData(DailyCsv)]
    public async Task Get_Should_Answer409WithTheFixedText_When_TheDayIsPastTheReportsReadBounds(string path)
    {
        // ARV-096d: a day the report would read past a bound of is refused, never cut short.
        await using var app = MainHost(new HeldReports { Error = ReportErrors.TooLarge });
        using var client = app.CreateClient();
        using var request = Request(path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        HttpResponses.ParseProblem(await response.BodyAsync()).GetProperty("detail").GetString().Should().Be(ReportErrors.TooLarge);
    }

    [Fact]
    public async Task Get_Should_LetTheQueuedRequestThrough_When_ThePermitFrees()
    {
        // The Proposed 2 at once and 4 waiting, here 1 and 1: a caller waits for a turn instead of getting 429.
        var reports = new HeldReports();
        await using var app = MainHost(reports, ("Security:RateLimiting:Reports:PermitLimit", "1"), ("Security:RateLimiting:Reports:QueueLimit", "1"));
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var holding = Request(Daily);
        var first = client.SendAsync(holding, ct);
        await reports.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var waitingRequest = Request(DailyCsv);
        var waiting = client.SendAsync(waitingRequest, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        reports.Calls.Should().Be(1, "the second request waits for the permit");
        reports.Release.TrySetResult();
        using var answered = await first;
        using var queued = await waiting;

        answered.StatusCode.Should().Be(HttpStatusCode.OK);
        queued.StatusCode.Should().Be(HttpStatusCode.OK);
        reports.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(Daily)]
    [InlineData(DailyCsv)]
    public async Task Get_Should_Answer503WithRetryAfter_When_TheReportOutlastsItsRequestTimeout(string path)
    {
        var reports = new HeldReports();
        await using var app = MainHost(reports, ("Security:RequestTimeouts:ReportsSeconds", "5"));
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var request = Request(path);
        using var answer = await client.SendAsync(request, ct);

        answer.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        answer.Header("Retry-After").Should().Be(RequestTimeoutsSettings.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
        reports.Cancelled.Should().BeTrue("the report's reads get the request's token, which the timeout cancels");
        reports.Release.TrySetResult();
    }

    [Fact]
    public async Task Get_Should_Answer401WithoutWaiting_When_TheReportsPolicyIsSaturatedAndTheRequestSendsNoCredentials()
    {
        // ARV-096b: the policy runs before authentication, so a request without credentials took a slot or a place in the queue and
        // was answered 401 only after it; now it is not counted, and is refused at authentication at once.
        var reports = new HeldReports();
        await using var app = MainHost(reports, ("Security:RateLimiting:Reports:PermitLimit", "1"), ("Security:RateLimiting:Reports:QueueLimit", "0"));
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var holding = Request(Daily);
        var first = client.SendAsync(holding, ct);
        await reports.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var anonymous = Enumerable.Range(0, 5).Select(_ => client.GetAsync(Daily, ct)).ToList();
        var answers = await Task.WhenAll(anonymous).WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var refusedRequest = Request(DailyCsv);
        using var refused = await client.SendAsync(refusedRequest, ct);
        reports.Release.TrySetResult();
        using var answered = await first;

        answers.Select(a => a.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Unauthorized, "none waits for or takes the one slot");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "a signed in caller still meets the saturated policy");
        answered.StatusCode.Should().Be(HttpStatusCode.OK);
        reports.Calls.Should().Be(1);
        foreach (var answer in answers)
            answer.Dispose();
    }

    [Fact]
    public async Task Get_Should_Answer429_When_ASignedInCallerAddsAnEmptySecondAuthorizationValue()
    {
        // Security review of ARV-096b, H1, on the real JWT scheme: authentication reads the header's values joined (empty ones
        // dropped), so a valid token beside an empty second value still signs the caller in; the policy must count it the same way.
        var reports = new HeldReports();
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting("Security:RateLimiting:Reports:PermitLimit", "1");
            builder.UseSetting("Security:RateLimiting:Reports:QueueLimit", "0");
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ISessionValidator>(new FakeSessionValidator()));
                services.Replace(ServiceDescriptor.Scoped<IPermissionResolver>(_ => new Grants(Ariva.Core.Global.Defaults.Permissions.ViewReport)));
                services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Replace(ServiceDescriptor.Scoped<ISvcReports>(_ => reports));
            });
        });
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var token = app.Services.GetRequiredService<Ariva.Infra.Security.AccessTokenIssuer>()
            .Issue(Guid.CreateVersion7(), "report.reader", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], false);

        using var holding = new HttpRequestMessage(HttpMethod.Get, Daily);
        holding.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        var first = client.SendAsync(holding, ct);
        await reports.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var doubled = new HttpRequestMessage(HttpMethod.Get, DailyCsv);
        doubled.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + token, "" });
        using var refused = await client.SendAsync(doubled, ct);
        reports.Release.TrySetResult();
        using var answered = await first;

        answered.StatusCode.Should().Be(HttpStatusCode.OK, "the token signs the caller in");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the same caller is counted whatever empty value it adds");
        reports.Calls.Should().Be(1);
    }

    /// <summary>The stored grants of the signed-in caller.</summary>
    private sealed class Grants(params Permission[] granted) : IPermissionResolver
    {
        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(System.Security.Claims.ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>(granted));
    }

    /// <summary>A report service that answers only when released; it notes its calls and whether its caller stopped waiting.</summary>
    private sealed class HeldReports : ISvcReports
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);
        public bool Cancelled { get; private set; }

        /// <summary>When set, the answer at once instead of a report.</summary>
        public string Error { get; init; }

        public async Task<Fluentx.Result<DailyReport>> DailyAsync(string siteCode, DateOnly? date, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (Error is not null)
                return Fluentx.Result.Error<DailyReport>(Error);
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            var day = date ?? new DateOnly(2026, 10, 18);
            return new Fluentx.Result<DailyReport>(new DailyReport(siteCode, day, "Asia/Dubai", new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc),
                new DailyHeadline(null, null, null, 0, 0, 0, null), [], new Dictionary<string, int>(), [], []));
        }
    }

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }
}
