using System.Net;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Validation;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-104g, first security review, M1 (CWE-400, CWE-770, ASVS V15.2.2): the validation results' limits over HTTP on Ariva.Api.Main.
/// The <c>validation-results</c> concurrency policy, saturated (one permit, none waiting), answers 429 with Retry-After (a
/// concurrency lease carries no retry time, so the host gives a fixed one); a computation that takes longer than the request
/// timeout answers 503 with Retry-After. The results service is a fake that holds its answer until told, so no database is needed.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ValidationResultsLimitsTests
{
    private const string Path = "/api/v1/sites/AMM/validation/campaigns/0199a000-0000-7000-8000-00000000a1a1/results";

    private static IArivaHost Main(HeldResults results, params (string Key, string Value)[] settings) =>
        ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                TestAuthenticationHandler.Register(services);
                FakeAdministration.Register(services);
                services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => new EverySiteScope()));
                services.Replace(ServiceDescriptor.Scoped<ISvcValidationResults>(_ => results));
            });
        });

    private static HttpRequestMessage Request()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "validation-manager");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.BorderShiftSupervisor);
        return request;
    }

    [Fact]
    public async Task Get_Should_Answer429WithRetryAfter_When_TheValidationResultsPolicyIsSaturated()
    {
        var results = new HeldResults();
        await using var app = Main(results, ("Security:RateLimiting:ValidationResults:PermitLimit", "1"), ("Security:RateLimiting:ValidationResults:QueueLimit", "0"));
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var holding = Request();
        var first = client.SendAsync(holding, ct);
        await results.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var refusedRequest = Request();
        using var refused = await client.SendAsync(refusedRequest, ct);
        results.Release.TrySetResult();
        using var answered = await first;

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Header("Retry-After").Should().Be(Ariva.Api.Common.Extensions.RateLimitingExtensions.ConcurrencyRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        answered.StatusCode.Should().Be(HttpStatusCode.OK, "the request holding the permit is answered once its computation ends");
        results.Calls.Should().Be(1, "the refused request never reached the service");
    }

    [Fact]
    public async Task Get_Should_Answer503WithRetryAfter_When_TheComputationOutlastsTheRequestTimeout()
    {
        var results = new HeldResults();
        await using var app = Main(results, ("Validation:Results:RequestTimeout", "00:00:05"));
        using var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var request = Request();
        using var answer = await client.SendAsync(request, ct);

        answer.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        answer.Header("Retry-After").Should().Be("30");
        results.Cancelled.Should().BeTrue("the request's wait ends with the timeout");
        results.Release.TrySetResult();
    }

    /// <summary>A results service that answers only when released; it notes its calls and whether its caller stopped waiting.</summary>
    private sealed class HeldResults : ISvcValidationResults
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }

        public async Task<Fluentx.Result<ValidationResultsJson>> GetAsync(string siteCode, Guid campaignId, int? revision = null, CancellationToken ct = default)
        {
            Calls++;
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

            return new Fluentx.Result<ValidationResultsJson>(new ValidationResultsJson("{}"u8.ToArray(), null, null));
        }

        public Task<Fluentx.Result<ValidationResultsJson>> RecomputeAsync(string siteCode, Guid campaignId, RecomputeValidationResultsRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class EverySiteScope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) => Task.FromResult(SiteAccess.Everything);
    }
}
