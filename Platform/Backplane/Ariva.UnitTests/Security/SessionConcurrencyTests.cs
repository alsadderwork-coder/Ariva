using System.Threading.RateLimiting;
using Ariva.Api.Common.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-096b (CWE-400, CWE-770, ASVS 5.0 V15.2.2): the user concurrency policies (backtest, validation results, reports) run
/// before authentication and count every request that sends credentials, read as authentication reads them; a request that sends
/// none shares an unlimited partition, since authentication refuses it at once. The upload policy's places are taken after
/// authorization since ARV-265 (<see cref="UploadLimitTests"/>). <see cref="ReportsLimitsTests"/> proves it over HTTP on a saturated policy.
/// </summary>
public sealed class SessionConcurrencyTests
{
    // A row's arguments are its display name, which the TRX keeps and CI checks for secrets (ARV-252): a row names its made-up
    // token by a placeholder, replaced here, and the failure message quotes the row as written.
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "" }, false)]
    [InlineData(new[] { "   " }, false)]
    [InlineData(new[] { "", "" }, false)]
    [InlineData(new[] { "Bearer {jwt}" }, true)]
    [InlineData(new[] { "bearer {jwt}" }, true)]
    [InlineData(new[] { "Bearer {jwt}", "" }, true)]
    [InlineData(new[] { "", "Bearer {jwt}" }, true)]
    [InlineData(new[] { "Bearer one", "Bearer two" }, true)]
    [InlineData(new[] { "Bearer " }, true)]
    [InlineData(new[] { "Bearer {device}" }, true)]
    [InlineData(new[] { "Basic dXNlcjpwYXNz" }, true)]
    [InlineData(new[] { "Token eyJhbGciOiJFUzI1NiJ9" }, true)]
    public void PresentsCredentials_Should_ReadTheHeaderAsAuthenticationDoes_When_ItDecidesWhoIsCounted(string[] rows, bool counted)
    {
        // Security review H1: JWT authentication reads the values joined, empty ones dropped (Headers.Authorization.ToString()); a
        // token beside an empty second value must be counted, or a signed-in caller would go uncounted and still be authenticated.
        var values = rows.Select(v => v.Replace("{jwt}", "eyJhbGciOiJFUzI1NiJ9.e30.c2ln", StringComparison.Ordinal)
            .Replace("{device}", "ardk_0123456789abcdef", StringComparison.Ordinal)).ToArray();
        var context = new DefaultHttpContext();
        if (values.Length > 0)
            context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(values);

        RateLimitingExtensions.PresentsCredentials(context).Should().Be(counted, string.Join(" | ", rows.Select(v => $"[{v}]")));
    }

    [Fact]
    public void SessionConcurrency_Should_GiveNoSlot_When_TheRequestSendsNoCredentials()
    {
        var context = new DefaultHttpContext();

        var partition = RateLimitingExtensions.SessionConcurrency(context, RateLimitingExtensions.BacktestPolicy, 1, 0);
        using var limiter = partition.Factory(partition.PartitionKey);
        var leases = Enumerable.Range(0, 50).Select(_ => limiter.AttemptAcquire()).ToList();

        partition.PartitionKey.Should().Be(RateLimitingExtensions.WithoutCredentialsPartition);
        leases.Should().OnlyContain(lease => lease.IsAcquired, "a request refused 401 at authentication must not wait for a slot");
        leases.ForEach(lease => lease.Dispose());
    }

    [Fact]
    public void SessionConcurrency_Should_ShareThePoliciesSlots_When_TheRequestSendsCredentials()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer eyJhbGciOiJFUzI1NiJ9.e30.c2ln";

        var partition = RateLimitingExtensions.SessionConcurrency(context, RateLimitingExtensions.ReportsPolicy, 1, 0);
        using var limiter = partition.Factory(partition.PartitionKey);
        using var first = limiter.AttemptAcquire();
        using var second = limiter.AttemptAcquire();

        partition.PartitionKey.Should().Be(RateLimitingExtensions.ReportsPolicy, "one partition per host process, as before");
        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeFalse("one permit and no queue");
        limiter.Should().BeOfType<ConcurrencyLimiter>();
    }
}
